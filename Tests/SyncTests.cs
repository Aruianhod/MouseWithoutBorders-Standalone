using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Audio;
using MouseWithoutBorders.Sync;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests;
[TestClass]
[DoNotParallelize]
public sealed class SyncTests
{
    private const string Key = "Synthetic-settings-pairing-test-key";
    [TestMethod]
    public void LiveSettingsApplicationAcknowledgesRemoteEditAndPublishesLaterLocalEditWithoutEcho()
    {
        Exception failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
            var stateField = typeof(SyncService).GetField("state", flags)!;
            var uiField = typeof(SyncService).GetField("ui", flags)!;
            var apply = typeof(SyncService).GetMethod("Apply", flags)!;
            var oldState = stateField.GetValue(null); var oldUi = uiField.GetValue(null);
            string oldDirectory = Environment.GetEnvironmentVariable("MWB_STANDALONE_DATA");
            bool clipboard = MouseWithoutBorders.Class.Setting.Values.ShareClipboard;
            try
            {
                string directory = Path.Combine(oldDirectory ?? Path.GetTempPath(), "service-sync-" + Guid.NewGuid().ToString("N"));
                Environment.SetEnvironmentVariable("MWB_STANDALONE_DATA", directory);
                using var form = new System.Windows.Forms.Form { Opacity = 0, ShowInTaskbar = false };
                var local = new SyncState(); stateField.SetValue(null, local); uiField.SetValue(null, form);
                form.Shown += async (_, _) =>
                {
                    try
                    {
                        await SyncService.Configure(true, true, false);
                        var document = new SyncDocument { Owner = "REMOTE", Revision = new(100, new string('2', 32)), Shared = SharedSettings.Capture() };
                        document.Shared.Clipboard = !clipboard;
                        var reply = await (Task<SyncReply>)apply.Invoke(null, new object[] { document, (Action)(() => { }) })!;
                        Assert.IsTrue(reply.Applied);
                        Assert.AreEqual(!clipboard, MouseWithoutBorders.Class.Setting.Values.ShareClipboard);
                        Assert.AreEqual(document.Revision, local.Revision);
                        await SyncService.Configure(true, true, false);
                        Assert.AreEqual(document.Revision, local.Revision, "Received configuration must not become a new local edit.");
                        MouseWithoutBorders.Class.Setting.Values.ShareClipboard = clipboard;
                        await SyncService.Configure(true, true, false);
                        Assert.AreEqual(101L, local.Revision.Counter);
                        reply = await (Task<SyncReply>)apply.Invoke(null, new object[] { document, (Action)(() => { }) })!;
                        Assert.IsFalse(reply.Applied);
                        Assert.AreEqual(clipboard, MouseWithoutBorders.Class.Setting.Values.ShareClipboard);
                        document.Revision = new(200, new string('2', 32));
                        await Assert.ThrowsExceptionAsync<IOException>(async () => await (Task<SyncReply>)apply.Invoke(null,
                            new object[] { document, (Action)(() => throw new IOException("pairing revoked")) })!);
                        Assert.AreEqual(101L, local.Revision.Counter);
                        var saved = JsonSerializer.Deserialize<SyncState>(File.ReadAllText(Path.Combine(directory, "sync.json")))!;
                        Assert.AreEqual(local.Revision, saved.Revision);
                    }
                    catch (Exception ex) { failure = ex; }
                    finally { form.Close(); }
                };
                System.Windows.Forms.Application.Run(form);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                stateField.SetValue(null, oldState); uiField.SetValue(null, oldUi);
                Environment.SetEnvironmentVariable("MWB_STANDALONE_DATA", oldDirectory);
                MouseWithoutBorders.Class.Setting.Values.ShareClipboard = clipboard;
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    [TestMethod]
    public void SettingsDialogRendersWithVisibleControls()
    {
        Exception failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                using var form = new SyncSettingsForm { Opacity = 0, ShowInTaskbar = false };
                form.Show(); form.PerformLayout();
                var panel = form.Controls.OfType<System.Windows.Forms.TableLayoutPanel>().Single();
                Assert.AreEqual(2, panel.Controls.OfType<System.Windows.Forms.CheckBox>().Count());
                var note = panel.Controls.OfType<System.Windows.Forms.Label>().Single();
                Assert.IsTrue(note.Height >= note.GetPreferredSize(new System.Drawing.Size(note.Width, 0)).Height);
                Assert.IsTrue(panel.Controls.OfType<System.Windows.Forms.TextBox>().Single().Height >= 100);
                Directory.CreateDirectory("test-results");
                using var image = new System.Drawing.Bitmap(form.Width, form.Height);
                form.DrawToBitmap(image, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
                image.Save("test-results/settings-sync.png");
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)));
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    [TestMethod]
    public void ClipboardSequenceSuppressesRemoteEchoesButAllowsRapidLocalCopiesAndCaseChanges()
    {
        var changes = new ClipboardChangeTracker();
        changes.MarkRemote(1); changes.MarkRemote(2);
        Assert.IsFalse(changes.TryLocal(1)); Assert.IsFalse(changes.TryLocal(2));
        Assert.IsTrue(changes.TryLocal(3)); Assert.IsFalse(changes.TryLocal(3));
        Assert.IsTrue(changes.TryLocal(4)); Assert.IsTrue(changes.TryLocal(5));
    }
    [TestMethod]
    public void ConcurrentEditsConvergeWithoutEchoAndLaterEditFromEitherSideWins()
    {
        var a = new SyncState { Identity = new string('1',32) }; var b = new SyncState { Identity = new string('2',32) };
        a.Observe("a"); b.Observe("b");
        Assert.IsTrue(b.Revision.CompareTo(a.Revision) > 0);
        a.Adopt(b.Revision, "b");
        for (int i = 0; i < 20; i++) { Assert.IsFalse(a.Observe("b")); Assert.IsFalse(b.Observe("b")); }
        Assert.AreEqual(a.Revision, b.Revision);
        a.Observe("a later edit"); Assert.IsTrue(a.Revision.CompareTo(b.Revision) > 0);
        b.Adopt(a.Revision, "a later edit"); b.Observe("b later edit");
        Assert.IsTrue(b.Revision.CompareTo(a.Revision) > 0);
        var reload = JsonSerializer.Deserialize<SyncState>(JsonSerializer.Serialize(b))!;
        Assert.IsFalse(reload.Observe("b later edit")); Assert.AreEqual(b.Revision, reload.Revision);
    }
    [TestMethod]
    public void AudioMirrorInvertsDirectionsAndPreservesLocalDevicesAndGain()
    {
        var source = new AudioOptions { SendMicrophone = new() { Enabled = true, Peer = "B", DeviceId = "A microphone", Volume = 70 },
            ReceiveSystem = new() { Enabled = true, Peer = "B", DeviceId = "A headset" }, Port = 16002, LowLatency = false };
        var target = new AudioOptions { ReceiveMicrophone = new() { DeviceId = "B virtual cable", Volume = 85 }, SendSystem = new() { DeviceId = "B speaker", Volume = 90 } };
        var plan = SharedAudio.Capture(source).Mirror("A", "B", target, () => throw new Exception("Must keep selected endpoint"));
        Assert.IsTrue(plan.ReceiveMicrophone.Enabled); Assert.IsTrue(plan.SendSystem.Enabled);
        Assert.AreEqual("A", plan.SendSystem.Peer); Assert.AreEqual("B virtual cable", plan.ReceiveMicrophone.DeviceId);
        Assert.AreEqual(85, plan.ReceiveMicrophone.Volume); Assert.AreEqual(90, plan.SendSystem.Volume);
        Assert.AreEqual("B speaker", plan.SendSystem.DeviceId); Assert.IsFalse(plan.LowLatency); Assert.AreEqual(16002, plan.Port);
        source.SendMicrophone.Enabled = false;
        Assert.IsFalse(SharedAudio.Capture(source).Mirror("A", "B", plan, () => null).ReceiveMicrophone.Enabled);
    }
    [TestMethod]
    public void MissingVirtualCableAndBusyThirdPeerDoNotPartiallyChangeAudio()
    {
        var source = new AudioOptions { SendMicrophone = new() { Enabled = true, Peer = "B" } };
        var target = new AudioOptions(); string before = JsonSerializer.Serialize(target);
        Assert.ThrowsException<IOException>(() => SharedAudio.Capture(source).Mirror("A", "B", target, () => null));
        Assert.AreEqual(before, JsonSerializer.Serialize(target));
        target.ReceiveMicrophone = new() { Enabled = true, Peer = "C", DeviceId = "cable" };
        Assert.ThrowsException<IOException>(() => SharedAudio.Capture(source).Mirror("A", "B", target, () => "cable"));
        var plan = SharedAudio.Capture(source).Mirror("A", "C", new(), () => null);
        Assert.IsFalse(plan.Receives);
    }
    [TestMethod]
    public void SyncAuthenticationRejectsTamperingWrongPairingReplayAndRoleConfusion()
    {
        var reply = new SyncReply(true, "applied");
        var envelope = SyncWire.Seal(reply, Key, "reply", "A", "B", "nonce-one");
        Assert.AreEqual(reply, SyncWire.Open<SyncReply>(envelope, Key, "reply", "A", "B", "nonce-one"));
        foreach (var parameters in new[] { new[] { "wrong-key", "reply", "A", "B", "nonce-one" }, new[] { Key, "request", "A", "B", "nonce-one" },
            new[] { Key, "reply", "C", "B", "nonce-one" }, new[] { Key, "reply", "A", "C", "nonce-one" }, new[] { Key, "reply", "A", "B", "nonce-two" } })
            Assert.ThrowsException<InvalidDataException>(() => SyncWire.Open<SyncReply>(envelope, parameters[0], parameters[1], parameters[2], parameters[3], parameters[4]));
        Assert.ThrowsException<InvalidDataException>(() => SyncWire.Open<SyncReply>(envelope with { Json = envelope.Json.Replace("applied", "changed") }, Key, "reply", "A", "B", "nonce-one"));
    }
    [TestMethod]
    public void InvalidDocumentsAndOversizedOrTruncatedFramesAreRejected()
    {
        Assert.ThrowsException<InvalidDataException>(() => new SyncRevision(-1, new string('1',32)).Validate());
        Assert.ThrowsException<InvalidDataException>(() => new SharedSettings { Layout = new[] { "A", "a" } }.Validate());
        Assert.ThrowsException<InvalidDataException>(() => new SyncDocument { Owner = "A", Revision = new(1,new string('1',32)), Shared = new() }.Validate("B"));
        using var output = new MemoryStream();
        Assert.ThrowsException<InvalidDataException>(() => SyncWire.Write(output, new string('x', SyncWire.FrameSize)));
        var invalid = new byte[SyncWire.FrameSize]; BinaryPrimitives.WriteInt32LittleEndian(invalid, int.MaxValue);
        Assert.ThrowsException<InvalidDataException>(() => SyncWire.Read<SyncReply>(new MemoryStream(invalid)));
        Assert.ThrowsException<EndOfStreamException>(() => SyncWire.Read<SyncReply>(new MemoryStream(new byte[3])));
    }
    [TestMethod]
    public async Task SignedSettingsAndReceiptTravelOverExistingEncryptedTransport()
    {
        // Actual streams use the existing pairing stream cipher and aligned framing.
        var previousKey = Encryption.MyKey;
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            Encryption.InitEncryption(); Encryption.MyKey = Key;
            using var client = new TcpClient(); var accept = listener.AcceptTcpClientAsync();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            using var server = await accept; client.ReceiveTimeout = server.ReceiveTimeout = 5000;
            var receiving = Task.Run(() =>
            {
                var network = server.GetStream(); var enc = Encryption.GetEncryptedStream(network); var dec = Encryption.GetDecryptedStream(network);
                Common.SendOrReceiveARandomDataBlockPerInitialIV(enc); Common.SendOrReceiveARandomDataBlockPerInitialIV(dec, false);
                SyncWire.Write(enc, "test-nonce");
                var result = SyncWire.Open<SyncReply>(SyncWire.Read<SyncEnvelope>(dec), Key, "request", "A", "B", "test-nonce");
                SyncWire.Write(enc, SyncWire.Seal(result, Key, "reply", "B", "A", "test-nonce"));
            });
            var network = client.GetStream(); var encrypted = Encryption.GetEncryptedStream(network); var decrypted = Encryption.GetDecryptedStream(network);
            Common.SendOrReceiveARandomDataBlockPerInitialIV(encrypted); Common.SendOrReceiveARandomDataBlockPerInitialIV(decrypted, false);
            string nonce = SyncWire.Read<string>(decrypted);
            SyncWire.Write(encrypted, SyncWire.Seal(new SyncReply(true, "received"), Key, "request", "A", "B", nonce));
            var reply = SyncWire.Open<SyncReply>(SyncWire.Read<SyncEnvelope>(decrypted), Key, "reply", "B", "A", nonce);
            Assert.IsTrue(reply.Applied); await receiving;
        }
        finally { listener.Stop(); Encryption.MyKey = previousKey; }
    }
}
