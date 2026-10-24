using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Class;

namespace MouseWithoutBorders.UnitTests;

[TestClass]
[DoNotParallelize]
public sealed class PeerConnectionTests
{
    [TestMethod]
    public void CacheIsBoundToKeyNormalizesAddressAndExpires()
    {
        string root = Path.Combine(Path.GetTempPath(), "MWB-Connection-" + Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        const string key = "test-only-key-never-a-real-pairing-code";
        try
        {
            PeerAddressCache.Remember(root, "PC-B", key, IPAddress.Parse("::ffff:192.0.2.8"), now);
            Assert.AreEqual(IPAddress.Parse("192.0.2.8"), PeerAddressCache.Get(root, "pc-b", key, now));
            Assert.IsNull(PeerAddressCache.Get(root, "PC-B", "changed-key", now));
            Assert.IsNull(PeerAddressCache.Get(root, "PC-C", key, now));
            Assert.IsNull(PeerAddressCache.Get(root, "PC-B", key, now.AddDays(31)));
            Assert.IsNull(PeerAddressCache.Get(root, "PC-B", key, now.AddDays(-1)));
            Assert.IsFalse(File.ReadAllText(Path.Combine(root, "connection-cache.json")).Contains(key));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void InboundIpv6DoesNotOverwriteKnownWorkingOutboundLanAddress()
    {
        string root = Path.Combine(Path.GetTempPath(), "MWB-Connection-" + Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        try
        {
            PeerAddressCache.Remember(root, "PEER", "key", IPAddress.Parse("2001:db8::8"), now, outgoing: false);
            PeerAddressCache.Remember(root, "PEER", "key", IPAddress.Parse("192.0.2.8"), now.AddSeconds(1), outgoing: true);
            PeerAddressCache.Remember(root, "PEER", "key", IPAddress.Parse("2001:db8::8"), now.AddSeconds(2), outgoing: false);
            Assert.AreEqual(IPAddress.Parse("192.0.2.8"), PeerAddressCache.Get(root, "PEER", "key", now.AddSeconds(3)));
            PeerAddressCache.Remember(root, "PEER", "key", IPAddress.Parse("192.0.2.9"), now.AddSeconds(4), outgoing: true);
            Assert.AreEqual(IPAddress.Parse("192.0.2.9"), PeerAddressCache.Get(root, "PEER", "key", now.AddSeconds(5)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ChangedAddressReplacesOldAndStaleFailureDoesNotDeleteNewAddress()
    {
        string root = Path.Combine(Path.GetTempPath(), "MWB-Connection-" + Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        try
        {
            PeerAddressCache.Remember(root, "PEER", "key", IPAddress.Parse("192.0.2.8"), now);
            PeerAddressCache.Remember(root, "PEER", "key", IPAddress.Parse("192.0.2.9"), now.AddSeconds(1));
            PeerAddressCache.Forget(root, "PEER", IPAddress.Parse("192.0.2.8"));
            Assert.AreEqual(IPAddress.Parse("192.0.2.9"), PeerAddressCache.Get(root, "PEER", "key", now.AddSeconds(2)));
            PeerAddressCache.Forget(root, "PEER", IPAddress.Parse("::ffff:192.0.2.9"));
            Assert.IsNull(PeerAddressCache.Get(root, "PEER", "key", now.AddSeconds(3)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void CorruptCacheDoesNotPreventFreshAuthenticatedAddressAndSizeIsBounded()
    {
        string root = Path.Combine(Path.GetTempPath(), "MWB-Connection-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var now = DateTimeOffset.UtcNow;
        try
        {
            File.WriteAllText(Path.Combine(root, "connection-cache.json"), "{corrupt");
            Assert.IsNull(PeerAddressCache.Get(root, "PC", "key", now));
            for (int i = 0; i < 10; i++) PeerAddressCache.Remember(root, "PC" + i, "key", IPAddress.Parse("192.0.2." + (i + 2)), now.AddSeconds(i));
            Assert.AreEqual(4, System.Text.Json.JsonSerializer.Deserialize<List<PeerAddressCache.Entry>>(File.ReadAllText(Path.Combine(root, "connection-cache.json")))!.Count);
            Assert.IsNull(PeerAddressCache.Get(root, "PC0", "key", now.AddMinutes(1)));
            Assert.IsNotNull(PeerAddressCache.Get(root, "PC9", "key", now.AddMinutes(1)));
            PeerAddressCache.Remember(root, "LOCAL", "key", IPAddress.Loopback, now);
            Assert.IsNull(PeerAddressCache.Get(root, "LOCAL", "key", now));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow("127.0.0.1")]
    [DataRow("::ffff:127.0.0.1")]
    [DataRow("::1")]
    public async Task RealConnectionTransfersDataAndIncomingLabelNeedsNoDns(string address)
    {
        var ip = PeerAddressCache.Normalize(IPAddress.Parse(address));
        using var listener = new TcpListener(ip, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accepting = listener.AcceptSocketAsync(deadline.Token);
        using var client = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        var watch = Stopwatch.StartNew();
        await PeerConnection.ConnectAsync(client, IPAddress.Parse(address), ((IPEndPoint)listener.LocalEndpoint).Port, deadline.Token);
        using var server = await accepting;
        Assert.IsTrue(client.NoDelay);
        Assert.AreEqual(server.RemoteEndPoint!.ToString(), PeerConnection.IncomingLabel(server));
        await client.SendAsync(new byte[] { 42 }, SocketFlags.None, deadline.Token);
        byte[] data = new byte[1];
        Assert.AreEqual(1, await server.ReceiveAsync(data, SocketFlags.None, deadline.Token));
        Assert.AreEqual(42, data[0]);
        Console.WriteLine($"Real {address} connect/accept/data: {watch.ElapsedMilliseconds} ms");
    }

    [TestMethod]
    public async Task DeadlineCancelsBlockedAttemptAndShutdownPreservesCancellation()
    {
        bool cancelled = false;
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsExceptionAsync<TimeoutException>(() => PeerConnection.WithDeadlineAsync(async token =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { cancelled = token.IsCancellationRequested; }
        }, CancellationToken.None, 100));
        Assert.IsTrue(cancelled);
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(2));
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        try { await PeerConnection.WithDeadlineAsync(token => Task.Delay(Timeout.Infinite, token), stop.Token, 3000); Assert.Fail("Expected cancellation"); }
        catch (OperationCanceledException) { }
    }

    [TestMethod]
    public void CachedAddressAuthenticationFailureDoesNotBlockFreshDnsButNormalKeyFailureStillDoes()
    {
        string root = Path.Combine(Path.GetTempPath(), "MWB-Connection-" + Guid.NewGuid());
        string? previousRoot = Environment.GetEnvironmentVariable("MWB_STANDALONE_DATA");
        bool oldInvalid = SocketStuff.InvalidKeyFound, oldClientInvalid = SocketStuff.InvalidKeyFoundOnClientSocket;
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            Environment.SetEnvironmentVariable("MWB_STANDALONE_DATA", root);
            var address = IPAddress.Parse("192.0.2.8");
            PeerAddressCache.Remember(root, "PEER", "test-key", address, DateTimeOffset.UtcNow);
            var tcp = new TcpSk(true, socket, SocketStatus.Connecting, "PEER", address) { FromAddressCache = true };
            var sockets = (SocketStuff)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SocketStuff));
            sockets.TcpSocketsLock = new object();
            typeof(SocketStuff).GetProperty("TcpSockets", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(sockets, new List<TcpSk> { tcp });
            var update = typeof(SocketStuff).GetMethod("UpdateTcpSockets", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            SocketStuff.InvalidKeyFound = SocketStuff.InvalidKeyFoundOnClientSocket = false;
            update.Invoke(sockets, new object[] { tcp, SocketStatus.InvalidKey });
            Assert.IsFalse(SocketStuff.InvalidKeyFound);
            Assert.IsFalse(SocketStuff.InvalidKeyFoundOnClientSocket);
            Assert.AreEqual(SocketStatus.Timeout, tcp.Status);
            Assert.IsNull(PeerAddressCache.Get(root, "PEER", "test-key", DateTimeOffset.UtcNow));
            tcp.FromAddressCache = false; tcp.Status = SocketStatus.Connecting;
            update.Invoke(sockets, new object[] { tcp, SocketStatus.InvalidKey });
            Assert.IsTrue(SocketStuff.InvalidKeyFound);
            Assert.IsTrue(SocketStuff.InvalidKeyFoundOnClientSocket);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MWB_STANDALONE_DATA", previousRoot);
            SocketStuff.InvalidKeyFound = oldInvalid; SocketStuff.InvalidKeyFoundOnClientSocket = oldClientInvalid;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void RetryContinuesAfterStartupAndThroughoutLongOfflinePeriod()
    {
        Assert.IsFalse(PeerConnection.RetryDue(0));
        Assert.IsFalse(PeerConnection.RetryDue(19));
        Assert.IsTrue(PeerConnection.RetryDue(20));
        Assert.IsTrue(PeerConnection.RetryDue(40));
        Assert.IsTrue(PeerConnection.RetryDue(36000));
        Assert.AreEqual(300, Enumerable.Range(1, 6000).Count(tick => PeerConnection.RetryDue(tick)));
    }
}
