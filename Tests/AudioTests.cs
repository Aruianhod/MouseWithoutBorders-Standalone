using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Audio;

namespace MouseWithoutBorders.UnitTests;

[TestClass]
[DoNotParallelize]
public sealed class AudioTests
{
    private const string PairingKey = "AudioFixturePairingCode2026";

    [TestMethod]
    public async Task AudioAcceptsEitherAuthenticatedAddressButRejectsOtherPeersAndDisconnectedSockets()
    {
        var previous = MouseWithoutBorders.Core.Common.Sk;
        var v4 = new TcpListener(IPAddress.Loopback, 0); var v6 = new TcpListener(IPAddress.IPv6Loopback, 0);
        v4.Start(); v6.Start();
        try
        {
            using var c4 = new TcpClient(AddressFamily.InterNetwork); using var c6 = new TcpClient(AddressFamily.InterNetworkV6);
            var a4 = v4.AcceptTcpClientAsync(); var a6 = v6.AcceptTcpClientAsync();
            await c4.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)v4.LocalEndpoint).Port);
            await c6.ConnectAsync(IPAddress.IPv6Loopback, ((IPEndPoint)v6.LocalEndpoint).Port);
            using var s4 = await a4; using var s6 = await a6;
            var tcp4 = new MouseWithoutBorders.Class.TcpSk(true, c4.Client, MouseWithoutBorders.Class.SocketStatus.Connected, "PEER");
            var tcp6 = new MouseWithoutBorders.Class.TcpSk(false, c6.Client, MouseWithoutBorders.Class.SocketStatus.Connected, "PEER");
            var sockets = (MouseWithoutBorders.Class.SocketStuff)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(MouseWithoutBorders.Class.SocketStuff));
            sockets.TcpSocketsLock = new();
            typeof(MouseWithoutBorders.Class.SocketStuff).GetProperty("TcpSockets", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(sockets, new List<MouseWithoutBorders.Class.TcpSk> { tcp4, tcp6 });
            MouseWithoutBorders.Core.Common.Sk = sockets;
            Assert.IsTrue(AudioService.ConnectedAddressMatches("peer", IPAddress.IPv6Loopback));
            Assert.IsTrue(AudioService.ConnectedAddressMatches("peer", IPAddress.Parse("::ffff:127.0.0.1")));
            Assert.IsFalse(AudioService.ConnectedAddressMatches("OTHER", IPAddress.Loopback));
            Assert.IsFalse(AudioService.ConnectedAddressMatches("peer", IPAddress.Parse("192.0.2.10")));
            tcp6.Status = MouseWithoutBorders.Class.SocketStatus.Error;
            Assert.IsFalse(AudioService.ConnectedAddressMatches("peer", IPAddress.IPv6Loopback));
        }
        finally { MouseWithoutBorders.Core.Common.Sk = previous; v4.Stop(); v6.Stop(); }
    }

    [TestMethod]
    public void AudioWindowRefreshesSynchronizedOptionsButPreservesUnsavedLocalEdits()
    {
        Exception failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                using var form = new AudioSettingsForm(new AudioOptions());
                var remote = new AudioOptions { Port = 16002, LowLatency = false };
                Assert.IsTrue(form.RefreshAppliedOptions(remote, false));
                Assert.AreEqual(16002, form.ReadOptions().Port); Assert.IsFalse(form.ReadOptions().LowLatency);
                var network = form.Controls.OfType<System.Windows.Forms.TableLayoutPanel>().Single().Controls
                    .OfType<System.Windows.Forms.FlowLayoutPanel>().Single(p => p.Controls.OfType<System.Windows.Forms.CheckBox>().Any());
                network.Controls.OfType<System.Windows.Forms.CheckBox>().Single().Checked = true;
                remote.Port = 17002;
                Assert.IsFalse(form.RefreshAppliedOptions(remote, false));
                Assert.AreEqual(16002, form.ReadOptions().Port); Assert.IsTrue(form.ReadOptions().LowLatency);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [TestMethod]
    public void AudioIsOptInAndConfigurationSurvivesReloadIncludingDevicesAndVolumes()
    {
        string root = Path.Combine(Path.GetTempPath(), "MWB-Audio-Test-" + Guid.NewGuid());
        try
        {
            var settings = AudioOptions.Load(root);
            Assert.IsFalse(settings.SendSystem.Enabled || settings.SendMicrophone.Enabled || settings.Receives);
            settings.SendMicrophone = new() { Enabled = true, Peer = "PC-B", DeviceId = "microphone-id", Volume = 65 };
            settings.ReceiveSystem = new() { Enabled = true, Peer = "PC-B", DeviceId = "headphone-id", Volume = 80 };
            settings.Port = 15200;
            settings.BufferMilliseconds = 240;
            settings.Validate("PC-A");
            settings.Save(root);
            var reloaded = AudioOptions.Load(root);
            Assert.AreEqual(JsonSerializer.Serialize(settings), JsonSerializer.Serialize(reloaded));
            reloaded.SendMicrophone.Enabled = false;
            reloaded.Save(root);
            Assert.IsFalse(AudioOptions.Load(root).SendMicrophone.Enabled);
            Assert.IsTrue(File.Exists(Path.Combine(root, "audio.json.bak")));
            Assert.IsFalse(File.Exists(Path.Combine(root, "settings.json")), "Audio settings must not overwrite mouse pairing.");
            File.WriteAllText(Path.Combine(root, "audio.json"), "{damaged");
            Assert.ThrowsException<JsonException>(() => AudioOptions.Load(root));
            Assert.AreEqual("{damaged", File.ReadAllText(Path.Combine(root, "audio.json")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void AudioConfigurationRejectsInvalidPeerPortsAndImplicitMicrophonePlayback()
    {
        var options = new AudioOptions();
        options.Validate("LOCAL");
        options.SendSystem.Enabled = true;
        Assert.ThrowsException<ArgumentException>(() => options.Validate("LOCAL"));
        options.SendSystem.Peer = "local";
        Assert.ThrowsException<ArgumentException>(() => options.Validate("LOCAL"));
        options.SendSystem.Peer = "REMOTE";
        foreach (int port in new[] { 0, 1023, 15100, 15101, 65536 })
        {
            options.Port = port;
            Assert.ThrowsException<ArgumentException>(() => options.Validate("LOCAL"));
        }
        options.Port = 15102;
        options.ReceiveMicrophone = new() { Enabled = true, Peer = "REMOTE" };
        Assert.ThrowsException<ArgumentException>(() => options.Validate("LOCAL"));
        options.ReceiveMicrophone.DeviceId = "explicit-virtual-cable-playback-device";
        options.Validate("LOCAL");
        options.ReceiveMicrophone.Volume = 101;
        Assert.ThrowsException<ArgumentException>(() => options.Validate("LOCAL"));
    }

    [TestMethod]
    public void AudioProofBindsRoleCertificateChallengeAndMetadata()
    {
        byte[] certificate = RandomNumberGenerator.GetBytes(80);
        byte[] challenge = RandomNumberGenerator.GetBytes(32);
        byte[] metadata = "PC-A|PC-B|system|nonce"u8.ToArray();
        byte[] expected = AudioWire.Proof(PairingKey, "sender", certificate, challenge, metadata);
        Assert.AreEqual(32, expected.Length);
        CollectionAssert.AreEqual(expected, AudioWire.Proof(PairingKey, "sender", certificate, challenge, metadata));
        foreach (byte[] changed in new[]
        {
            AudioWire.Proof("DifferentPairingCode2026", "sender", certificate, challenge, metadata),
            AudioWire.Proof(PairingKey, "receiver", certificate, challenge, metadata),
            AudioWire.Proof(PairingKey, "sender", RandomNumberGenerator.GetBytes(80), challenge, metadata),
            AudioWire.Proof(PairingKey, "sender", certificate, RandomNumberGenerator.GetBytes(32), metadata),
            AudioWire.Proof(PairingKey, "sender", certificate, challenge, "PC-A|PC-B|microphone|nonce"u8.ToArray()),
        }) Assert.IsFalse(CryptographicOperations.FixedTimeEquals(expected, changed));
    }

    [TestMethod]
    [DataRow(1, "127.0.0.1")]
    [DataRow(2, "127.0.0.1")]
    [DataRow(1, "::ffff:127.0.0.1")]
    [DataRow(2, "::ffff:127.0.0.1")]
    [DataRow(1, "::1")]
    [DataRow(2, "::1")]
    public async Task AudioPcmAndHeartbeatTravelOverAuthenticatedTlsAndReconnect(int streamKind, string peerIp)
    {
        var kind = (AudioKind)streamKind;
        using var certificate = AudioWire.CreateCertificate();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var peerAddress = IPAddress.Parse(peerIp);
        var listenAddress = peerAddress.IsIPv4MappedToIPv6 ? peerAddress.MapToIPv4() : peerAddress;
        var listener = new TcpListener(listenAddress, 0);
        listener.Start();
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                int channels = kind == AudioKind.System ? 2 : 1;
                byte[] frame = new byte[960 * 4 * channels];
                for (int i = 0; i < frame.Length; i += 4)
                    BinaryPrimitives.WriteSingleLittleEndian(frame.AsSpan(i), (float)Math.Sin(i * 0.01) * .25f);
                var receiving = Task.Run(async () =>
                {
                    using var remote = await listener.AcceptTcpClientAsync(timeout.Token);
                    using var tls = new SslStream(remote.GetStream(), false);
                    var hello = await AudioWire.AuthenticateReceiver(tls, certificate, PairingKey,
                        h => h.Sender == "PC-A" && h.Receiver == "PC-B" && h.Kind == kind, timeout.Token);
                    Assert.AreEqual(kind, hello.Kind);
                    Assert.IsTrue(tls.IsEncrypted);
                    CollectionAssert.AreEqual(frame, await AudioWire.ReadPacket(tls, AudioWire.MaxFrame, timeout.Token));
                    Assert.AreEqual(0, (await AudioWire.ReadPacket(tls, AudioWire.MaxFrame, timeout.Token)).Length);
                    await AudioWire.WritePacket(tls, frame, timeout.Token);
                });
                using var client = await AudioService.ConnectPeerAsync(peerAddress, ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token);
                Assert.AreEqual(listenAddress.AddressFamily, client.Client.AddressFamily);
                using var secure = new SslStream(client.GetStream(), false);
                try { await AudioWire.AuthenticateSender(secure, new() { Sender = "PC-A", Receiver = "PC-B", Kind = kind }, PairingKey, timeout.Token); }
                catch { await receiving; throw; }
                Assert.IsTrue(secure.IsEncrypted);
                await AudioWire.WritePacket(secure, frame, timeout.Token);
                await AudioWire.WritePacket(secure, Array.Empty<byte>(), timeout.Token);
                CollectionAssert.AreEqual(frame, await AudioWire.ReadPacket(secure, AudioWire.MaxFrame, timeout.Token));
                await receiving;
            }
        }
        finally { listener.Stop(); }
    }

    [TestMethod]
    [DataRow(false, true)]
    [DataRow(true, false)]
    public async Task AudioRejectsWrongPairingCodeAndUnselectedPeer(bool correctKey, bool allowedPeer)
    {
        using var certificate = AudioWire.CreateCertificate();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var receiving = Task.Run(async () =>
            {
                using var remote = await listener.AcceptTcpClientAsync(timeout.Token);
                using var tls = new SslStream(remote.GetStream(), false);
                var error = await Assert.ThrowsExceptionAsync<System.Security.Authentication.AuthenticationException>(() =>
                    AudioWire.AuthenticateReceiver(tls, certificate, PairingKey, _ => allowedPeer, timeout.Token));
                StringAssert.Contains(error.Message, "来源未授权或配对码不匹配");
            });
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token);
            using var secure = new SslStream(client.GetStream(), false);
            bool authenticated = false;
            try
            {
                await AudioWire.AuthenticateSender(secure, new() { Sender = "PC-A", Receiver = "PC-B", Kind = AudioKind.System },
                    correctKey ? PairingKey : "IncorrectFixturePairingCode2026", timeout.Token);
                authenticated = true;
            }
            catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException) { }
            Assert.IsFalse(authenticated);
            await receiving;
        }
        finally { listener.Stop(); }
    }

    [TestMethod]
    public async Task AudioFramesRejectOversizeAndTruncationWithoutUnboundedAllocation()
    {
        foreach (int size in new[] { -1, AudioWire.MaxFrame + 1, int.MaxValue })
        {
            byte[] header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, size);
            using var stream = new MemoryStream(header);
            await Assert.ThrowsExceptionAsync<InvalidDataException>(() => AudioWire.ReadPacket(stream, AudioWire.MaxFrame, CancellationToken.None));
        }
        byte[] truncated = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(truncated, 16);
        using var partial = new MemoryStream(truncated);
        await Assert.ThrowsExceptionAsync<EndOfStreamException>(() => AudioWire.ReadPacket(partial, AudioWire.MaxFrame, CancellationToken.None));
    }

    [TestMethod]
    public void AudioSamplesClampVolumeAndRejectMalformedFrames()
    {
        float[] samples = { -2, -0.5f, 0, 0.5f, 2, float.NaN, float.PositiveInfinity, float.NegativeInfinity };
        byte[] bytes = new byte[samples.Length * 4];
        for (int i = 0; i < samples.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), samples[i]);
        AudioWire.ScaleSamples(bytes, 2, 50);
        float[] expected = { -.5f, -.25f, 0, .25f, .5f, 0, 0, 0 };
        for (int i = 0; i < samples.Length; i++) Assert.AreEqual(expected[i], BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 4)));
        AudioWire.ScaleSamples(bytes, 2, 0);
        Assert.IsTrue(bytes.All(b => b == 0 || b == 128)); // negative zero is a valid muted sample
        Assert.ThrowsException<InvalidDataException>(() => AudioWire.ScaleSamples(new byte[7], 2, 100));
        Assert.ThrowsException<InvalidDataException>(() => AudioWire.ScaleSamples(new byte[4], 0, 100));
    }

    [TestMethod]
    public void AudioPreventsDigitalFeedbackOnSamePlaybackEndpoint()
    {
        AudioDevices.ValidateNoFeedback("speakers", "virtual-cable");
        Assert.ThrowsException<InvalidOperationException>(() => AudioDevices.ValidateNoFeedback("speakers", "SPEAKERS"));
    }

    [TestMethod]
    public async Task AudioPendingReadCancelsWithoutWaitingForRemotePeer()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var client = new TcpClient();
            var accept = listener.AcceptTcpClientAsync();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            using var server = await accept;
            using var stop = new CancellationTokenSource();
            var pending = AudioWire.ReadPacket(server.GetStream(), AudioWire.MaxFrame, stop.Token);
            stop.Cancel();
            try
            {
                await pending.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Fail("A disabled channel must cancel its outstanding read.");
            }
            catch (OperationCanceledException) { }
        }
        finally { listener.Stop(); }
    }

    [TestMethod]
    public async Task AudioSenderRejectsTlsServerWithoutPairingProof()
    {
        using var certificate = AudioWire.CreateCertificate();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var serverTask = Task.Run(async () =>
            {
                using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
                using var tls = new SslStream(connection.GetStream(), false);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, timeout.Token);
                await tls.WriteAsync(RandomNumberGenerator.GetBytes(32), timeout.Token);
                await AudioWire.ReadPacket(tls, 4096, timeout.Token);
                await tls.ReadExactlyAsync(new byte[32], timeout.Token);
                await tls.WriteAsync(new byte[32], timeout.Token); // encrypted but unauthenticated server
            });
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token);
            using var secure = new SslStream(client.GetStream(), false);
            await Assert.ThrowsExceptionAsync<System.Security.Authentication.AuthenticationException>(() =>
                AudioWire.AuthenticateSender(secure, new() { Sender = "PC-A", Receiver = "PC-B", Kind = AudioKind.Microphone }, PairingKey, timeout.Token));
            await serverTask;
        }
        finally { listener.Stop(); }
    }

    [TestMethod]
    public void AudioSettingsUiPreservesEveryRouteAndRendersBothTabs()
    {
        Exception? failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                var settings = new AudioOptions
                {
                    SendMicrophone = new() { Enabled = true, Peer = "副电脑", Volume = 75 },
                    ReceiveSystem = new() { Enabled = true, Peer = "副电脑", Volume = 85 },
                };
                using var form = new AudioSettingsForm(settings);
                // Real WinForms layout/painting, with an invisible test-only window.
                // No live service, sound capture or user clipboard is accessed.
                form.Opacity = 0;
                form.ShowInTaskbar = false;
                form.Show();
                Assert.AreEqual(JsonSerializer.Serialize(settings), JsonSerializer.Serialize(form.ReadOptions()));
                var root = form.Controls.OfType<System.Windows.Forms.TableLayoutPanel>().Single();
                var tabs = root.Controls.OfType<System.Windows.Forms.TabControl>().Single();
                Assert.AreEqual(2, tabs.TabPages.Count);
                var network = root.Controls.OfType<System.Windows.Forms.FlowLayoutPanel>()
                    .Single(p => p.Controls.OfType<System.Windows.Forms.CheckBox>().Any());
                var lowLatency = network.Controls.OfType<System.Windows.Forms.CheckBox>().Single();
                lowLatency.Checked = false;
                Assert.IsFalse(form.ReadOptions().LowLatency);
                lowLatency.Checked = true;
                Directory.CreateDirectory("test-results");
                foreach (int index in new[] { 0, 1 })
                {
                    tabs.SelectedIndex = index;
                    form.PerformLayout();
                    using var bitmap = new System.Drawing.Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
                    bitmap.Save($"test-results/audio-settings-{index}.png", System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)));
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
