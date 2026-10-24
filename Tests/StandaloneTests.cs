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
    public sealed class EchoService { public string Echo(string value) => value; }

    [TestMethod]
    public void WizardStartsWithFirstComputerChoiceOnNormalDesktop()
    {
        Exception? failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                Common.RunWithNoAdminRight = true;
                Common.MachineName = "TEST-PC";
                using var form = new SettingsForm();
                Assert.IsInstanceOfType<SetupPage1>(form.GetCurrentPage());
            }
            catch (Exception ex) { failure = ex; }
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
