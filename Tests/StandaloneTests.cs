using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.PowerToys.Settings.UI.Library;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests;

[TestClass]
[DoNotParallelize]
public sealed class StandaloneTests
{
    public sealed class EchoService
    {
        public string Echo(string value) => value;
        public ByteArrayOrString EchoClipboard(ByteArrayOrString value) => value;
    }

    [TestMethod]
    public void CustomPairingCodePreservesCaseAndReloadsWithoutAcceptingInvalidChanges()
    {
        string root = Path.Combine(Path.GetTempPath(), "MWB-Standalone-Test-" + Guid.NewGuid());
        string oldKey = Encryption.MyKey;
        uint oldHash = Encryption.MagicNumber;
        bool oldGenerated = Encryption.GeneratedKey;
        bool oldCorrupted = Encryption.KeyCorrupted;
        try
        {
            var disk = new SettingsUtils(root);
            var settings = new Class.Settings(disk, new MouseWithoutBordersSettings(), new PassthroughPairingKeyCodec());
            Assert.IsTrue(PairingKeyEditor.TrySave(settings, "River Paper Lantern 2026", out _));
            Assert.AreEqual("RiverPaperLantern2026", Encryption.MyKey);
            Assert.IsTrue(PairingKeyEditor.TrySave(settings, "riverPaperLantern2026", out _));
            Assert.AreEqual("riverPaperLantern2026", Encryption.MyKey);
            Assert.IsTrue(Encryption.GeneratedKey);
            Assert.AreEqual(Encryption.Get24BitHash(Encryption.MyKey), Encryption.MagicNumber);
            string persisted = File.ReadAllText(Path.Combine(root, "settings.json"));
            foreach (string invalid in new[] { "short", "                ", "mwb-dpapi:v1:AQID" })
            {
                Assert.IsFalse(PairingKeyEditor.TrySave(settings, invalid, out var error));
                Assert.IsFalse(string.IsNullOrEmpty(error));
                Assert.AreEqual("riverPaperLantern2026", Encryption.MyKey);
                Assert.AreEqual(persisted, File.ReadAllText(Path.Combine(root, "settings.json")));
            }

            var stored = disk.GetSettingsOrDefault<MouseWithoutBordersSettings>(MouseWithoutBordersSettings.ModuleName);
            var reloaded = new Class.Settings(disk, stored, new PassthroughPairingKeyCodec());
            Assert.AreEqual("riverPaperLantern2026", reloaded.MyKey);
            PairingKeyEditor.EnsureExistingOrNewKey(reloaded);
            PairingKeyEditor.EnsureExistingOrNewKey(reloaded);
            Assert.AreEqual("riverPaperLantern2026", reloaded.MyKey, "Reopening setup must retain the remembered code, including its case.");
            Assert.AreEqual("riverPaperLantern2026", Encryption.MyKey);
        }
        finally
        {
            Encryption.MyKey = oldKey;
            Encryption.MagicNumber = oldHash;
            Encryption.GeneratedKey = oldGenerated;
            Encryption.KeyCorrupted = oldCorrupted;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void FirstSetupGeneratesCodeOnlyOnce()
    {
        string root = Path.Combine(Path.GetTempPath(), "MWB-Standalone-Test-" + Guid.NewGuid());
        string oldKey = Encryption.MyKey;
        uint oldHash = Encryption.MagicNumber;
        bool oldGenerated = Encryption.GeneratedKey;
        bool oldCorrupted = Encryption.KeyCorrupted;
        try
        {
            var disk = new SettingsUtils(root);
            var settings = new Class.Settings(disk, new MouseWithoutBordersSettings(), new PassthroughPairingKeyCodec());
            PairingKeyEditor.EnsureExistingOrNewKey(settings);
            string generated = settings.MyKey;
            Assert.IsTrue(Encryption.IsKeyValid(generated, out _));
            var reloaded = new Class.Settings(disk, disk.GetSettingsOrDefault<MouseWithoutBordersSettings>(MouseWithoutBordersSettings.ModuleName), new PassthroughPairingKeyCodec());
            PairingKeyEditor.EnsureExistingOrNewKey(reloaded);
            Assert.AreEqual(generated, reloaded.MyKey);
        }
        finally
        {
            Encryption.MyKey = oldKey;
            Encryption.MagicNumber = oldHash;
            Encryption.GeneratedKey = oldGenerated;
            Encryption.KeyCorrupted = oldCorrupted;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    [DataRow("PC-A", "PC-B", "PC-A,PC-B,,", "PC-A:123,PC-B:456,:,:")]
    [DataRow("PC-B", "PC-A", "PC-B,PC-A,,", "PC-B:456,PC-A:123,:,:")]
    public void AuthenticatedConnectionPersistsWithoutCompletingWizard(string local, string peer, string layout, string pool)
    {
        string root = Path.Combine(Path.GetTempPath(), "MWB-Standalone-Test-" + Guid.NewGuid());
        try
        {
            var disk = new SettingsUtils(root);
            var settings = new Class.Settings(disk, new MouseWithoutBordersSettings(), new PassthroughPairingKeyCodec());
            settings.MyKey = "RememberThisPairingKey2026";
            Assert.IsTrue(settings.FirstRun);
            Assert.IsTrue(settings.RememberAuthenticatedPeer(local, peer, pool));
            // No wizard completion and no explicit SaveSettings: reconnect data must already be on disk.
            var reloaded = new Class.Settings(disk, disk.GetSettingsOrDefault<MouseWithoutBordersSettings>(MouseWithoutBordersSettings.ModuleName), new PassthroughPairingKeyCodec());
            Assert.IsFalse(reloaded.FirstRun);
            Assert.AreEqual(layout, reloaded.MachineMatrixString);
            Assert.AreEqual(pool, reloaded.MachinePoolString);
            Assert.AreEqual("RememberThisPairingKey2026", reloaded.MyKey);
            Assert.IsFalse(reloaded.RememberAuthenticatedPeer(local.ToLowerInvariant(), peer.ToLowerInvariant(), pool));
            Assert.AreEqual(layout, reloaded.MachineMatrixString, "Reconnect must not duplicate or reorder existing computers.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RememberedConnectionKeepsUserArrangementAndOtherComputers()
    {
        string root = Path.Combine(Path.GetTempPath(), "MWB-Standalone-Test-" + Guid.NewGuid());
        try
        {
            var disk = new SettingsUtils(root);
            var settings = new Class.Settings(disk, new MouseWithoutBordersSettings(), new PassthroughPairingKeyCodec());
            settings.MachineMatrixString = "OTHER,REMOTE,,LOCAL";
            Assert.IsFalse(settings.RememberAuthenticatedPeer("LOCAL", "REMOTE", "LOCAL:1,REMOTE:2,OTHER:3,:"));
            Assert.AreEqual("OTHER,REMOTE,,LOCAL", settings.MachineMatrixString);
            Assert.IsTrue(settings.RememberAuthenticatedPeer("LOCAL", "NEW", "LOCAL:1,REMOTE:2,OTHER:3,NEW:4"));
            Assert.AreEqual("OTHER,REMOTE,NEW,LOCAL", settings.MachineMatrixString);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ClipboardSharingDefaultsOnAndRemembersOptOutAndOptInImmediately()
    {
        string root = Path.Combine(Path.GetTempPath(), "MWB-Standalone-Test-" + Guid.NewGuid());
        try
        {
            var disk = new SettingsUtils(root);
            var settings = new Class.Settings(disk, new MouseWithoutBordersSettings(), new PassthroughPairingKeyCodec());
            Assert.IsTrue(settings.ShareClipboard);
            settings.ShareClipboard = false;
            var reloaded = new Class.Settings(disk, disk.GetSettingsOrDefault<MouseWithoutBordersSettings>(MouseWithoutBordersSettings.ModuleName), new PassthroughPairingKeyCodec());
            Assert.IsFalse(reloaded.ShareClipboard);
            reloaded.ShareClipboard = true;
            Assert.IsTrue(disk.GetSettingsOrDefault<MouseWithoutBordersSettings>(MouseWithoutBordersSettings.ModuleName).Properties.ShareClipboard);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void WizardStartsWithFirstComputerChoiceOnNormalDesktop()
    {
        Exception? failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            string root = Path.Combine(Path.GetTempPath(), "MWB-Standalone-Test-" + Guid.NewGuid());
            var previousSettings = Setting.Values;
            bool previousNoAdmin = Common.RunWithNoAdminRight;
            string previousName = Common.MachineName;
            try
            {
                Setting.Values = new Class.Settings(new SettingsUtils(root), new MouseWithoutBordersSettings(), new PassthroughPairingKeyCodec());
                Setting.Values.MachineMatrixString = "REMOTE,TEST-PC,,";
                MachineStuff.MachineMatrix = null;
                Common.RunWithNoAdminRight = true;
                Common.MachineName = "TEST-PC";
                using var form = new SettingsForm();
                Assert.IsInstanceOfType<SetupPage1>(form.GetCurrentPage());
                Assert.AreEqual("REMOTE,TEST-PC,,", Setting.Values.MachineMatrixString, "Opening setup must retain remembered computers and their order.");
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                Setting.Values = previousSettings;
                MachineStuff.MachineMatrix = null;
                Common.RunWithNoAdminRight = previousNoAdmin;
                Common.MachineName = previousName;
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [TestMethod]
    public async Task ClipboardPipeCanReconnectAfterServerCancellation()
    {
        var pipeName = "MWB-Test-" + Guid.NewGuid().ToString("N");
        for (int i = 0; i < 3; i++)
        {
            using var cancellation = new CancellationTokenSource();
            IpcChannel<EchoService>.StartIpcServer(pipeName, cancellation.Token);
            using var client = new System.IO.Pipes.NamedPipeClientStream(".", pipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
            await client.ConnectAsync(5000);
            using var rpc = StreamJsonRpc.JsonRpc.Attach(client);
            Assert.AreEqual("hello", await rpc.InvokeAsync<string>("Echo", "hello").WaitAsync(TimeSpan.FromSeconds(5)));
            string text = "跨电脑剪贴板\r\n第二行：你好 🌏\tCaseSensitive";
            var textResult = await rpc.InvokeAsync<ByteArrayOrString>("EchoClipboard", new ByteArrayOrString(text)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(textResult.IsString);
            Assert.AreEqual(text, textResult.GetString());
            byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a8l8AAAAASUVORK5CYII=");
            var imageResult = await rpc.InvokeAsync<ByteArrayOrString>("EchoClipboard", new ByteArrayOrString(png)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(imageResult.IsByteArray);
            CollectionAssert.AreEqual(png, imageResult.GetByteArray());
            cancellation.Cancel();
            // Cancellation must also break an already-connected RPC wait.
            try { await rpc.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (System.IO.IOException) { }
        }
    }

    [TestMethod]
    public void NewInstallShowsPairingAndTrayWithoutService()
    {
        var settings = new MouseWithoutBordersSettings();
        Assert.IsTrue(settings.Properties.FirstRun);
        Assert.IsTrue(settings.Properties.ShowOriginalUI);
        Assert.IsFalse(settings.Properties.UseService);
        Assert.AreEqual(15100, settings.Properties.TCPPort.Value);
    }

    [TestMethod]
    public void SettingsPersistImmediatelyAndReloadPairingAndLayout()
    {
        string root = Path.Combine(Path.GetTempPath(), "MWB-Standalone-Test-" + Guid.NewGuid());
        try
        {
            var disk = new SettingsUtils(root);
            var settings = new Class.Settings(disk, new MouseWithoutBordersSettings(), new PassthroughPairingKeyCodec());
            settings.MyKey = "StandaloneTestKey123456789";
            settings.MachineMatrixString = "PC-A,PC-B,,";
            settings.FirstRun = false;
            settings.ShareClipboard = false;
            settings.SaveSettings();
            var stored = disk.GetSettingsOrDefault<MouseWithoutBordersSettings>(MouseWithoutBordersSettings.ModuleName);
            var reloaded = new Class.Settings(disk, stored, new PassthroughPairingKeyCodec());
            Assert.AreEqual("StandaloneTestKey123456789", reloaded.MyKey);
            Assert.AreEqual("PC-A,PC-B,,", reloaded.MachineMatrixString);
            Assert.IsFalse(reloaded.FirstRun);
            Assert.IsFalse(reloaded.ShareClipboard);
            Assert.IsTrue(File.Exists(Path.Combine(root, "settings.json.bak")));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MalformedSettingsAreNotSilentlyReplaced()
    {
        string root = Path.Combine(Path.GetTempPath(), "MWB-Standalone-Test-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "settings.json"), "{broken");
            var disk = new SettingsUtils(root);
            Assert.ThrowsException<IOException>(() => disk.GetSettingsOrDefault<MouseWithoutBordersSettings>("MouseWithoutBorders"));
            Assert.AreEqual("{broken", File.ReadAllText(Path.Combine(root, "settings.json")));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task CurrentProtocolTransfersEncryptedDataOverRealLoopbackSocket()
    {
        var oldKey = Encryption.MyKey;
        Encryption.InitEncryption();
        Encryption.MyKey = "StandaloneLoopbackTestKey12345";
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var client = new TcpClient();
            var accepting = listener.AcceptTcpClientAsync();
            await client.ConnectAsync(IPAddress.Loopback, port);
            using var server = await accepting;
            byte[] payload = RandomNumberGenerator.GetBytes(128);
            var receive = Task.Run(() =>
            {
                using var decrypted = Encryption.GetDecryptedStream(server.GetStream());
                var data = new byte[payload.Length];
                decrypted.ReadExactly(data);
                return data;
            });
            using (var encrypted = (CryptoStream)Encryption.GetEncryptedStream(client.GetStream()))
            {
                encrypted.Write(payload);
                encrypted.FlushFinalBlock();
                CollectionAssert.AreEqual(payload, await receive.WaitAsync(TimeSpan.FromSeconds(10)));
            }
        }
        finally { listener.Stop(); Encryption.MyKey = oldKey; }
    }
}
