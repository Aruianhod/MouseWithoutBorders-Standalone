using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests;
[TestClass]
public sealed class OrderedPacketWriterTests
{
    private static DATA Packet(PackageType type, int id, int x = 0, int flags = 0) => new()
    { Type = type, Id = id, Src = (ID)1, Des = (ID)2, Md = new() { X = x, Y = x, dwFlags = flags } };
    [TestMethod]
    public async Task BlockedSocketDoesNotBlockInputAndOnlyAdjacentAbsoluteMovesCoalesce()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var results = new ConcurrentQueue<DATA>();
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var writer = new OrderedPacketWriter(bytes =>
        {
            if (results.IsEmpty) { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); }
            var packet = new DATA(bytes); results.Enqueue(packet);
            if (packet.Id == 999) complete.TrySetResult();
        }, ex => complete.TrySetException(ex));
        var first = Packet(PackageType.Keyboard, 1); writer.Enqueue(first, first.Bytes);
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(2)));
        var producer = Task.Run(() =>
        {
            for (int i = 2; i <= 201; i++) { var p = Packet(PackageType.Mouse, i, i, WM.WM_MOUSEMOVE); writer.Enqueue(p, p.Bytes); }
            foreach (var p in new[] { Packet(PackageType.Keyboard, 300), Packet(PackageType.Mouse, 301, 100001, WM.WM_MOUSEMOVE),
                Packet(PackageType.Mouse, 302, 100002, WM.WM_MOUSEMOVE), Packet(PackageType.Mouse, 303, 200, WM.WM_LBUTTONUP),
                Packet(PackageType.ClipboardText, 400), Packet(PackageType.ClipboardDataEnd, 401), Packet(PackageType.Keyboard, 999) })
                writer.Enqueue(p, p.Bytes);
        });
        try { await producer.WaitAsync(TimeSpan.FromSeconds(1)); }
        finally { release.Set(); }
        await complete.Task.WaitAsync(TimeSpan.FromSeconds(5));
        CollectionAssert.AreEqual(new[] { 1, 201, 300, 301, 302, 303, 400, 401, 999 }, results.Select(p => p.Id).ToArray());
        Assert.AreEqual(201, results.ElementAt(1).Md.X);
        Assert.AreEqual(0, writer.PendingBytes);
    }
    [TestMethod]
    public async Task FailedOrOverflowedWriterStopsOnceAndDoesNotReplayPendingKeys()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        int writes = 0, failures = 0;
        using var writer = new OrderedPacketWriter(bytes => { Interlocked.Increment(ref writes); entered.Set(); release.Wait(TimeSpan.FromSeconds(3)); },
            ex => Interlocked.Increment(ref failures), 32);
        var packet = Packet(PackageType.Keyboard, 1); writer.Enqueue(packet, packet.Bytes);
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(2)));
        writer.Enqueue(packet, packet.Bytes);
        try { Assert.ThrowsException<IOException>(() => writer.Enqueue(packet, packet.Bytes)); }
        finally { release.Set(); }
        Assert.AreEqual(1, failures); Assert.AreEqual(0, writer.PendingBytes);
        Assert.ThrowsException<IOException>(() => writer.Enqueue(packet, packet.Bytes));
        await Task.Delay(40); Assert.AreEqual(1, writes);
        var error = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var broken = new OrderedPacketWriter(_ => throw new IOException("synthetic socket failure"), ex => error.TrySetResult(ex));
        broken.Enqueue(packet, packet.Bytes);
        Assert.IsInstanceOfType<IOException>(await error.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.ThrowsException<IOException>(() => broken.Enqueue(packet, packet.Bytes));
    }
}
