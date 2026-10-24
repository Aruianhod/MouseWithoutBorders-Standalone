using System.IO.Pipes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StreamJsonRpc;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.IO.Compression;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;
using Clip = MouseWithoutBorders.Core.Clipboard;

namespace MouseWithoutBorders.UnitTests;

[TestClass][DoNotParallelize]
public sealed class ClipboardPipelineTests
{
    [TestMethod]
    public async Task ProductionClipboardSenderEmitsDecodableEncryptedPackets()
    {
        var previousSockets = Common.Sk; var matrix = MachineStuff.MachineMatrix;
        string previousKey = Encryption.MyKey; uint magic = Encryption.MagicNumber;
        bool share = Setting.Values.ShareClipboard;
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            Encryption.InitEncryption(); Encryption.MyKey = "ClipboardPipelineSyntheticKey123";
            Encryption.MagicNumber = Encryption.Get24BitHash(Encryption.MyKey);
            Setting.Values.ShareClipboard = true; MachineStuff.MachineMatrix = new[] { "CLIP-TEST", "", "", "" };
            using var client = new TcpClient(); var accepting = listener.AcceptTcpClientAsync();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            using var server = await accepting; server.ReceiveTimeout = 5000;
            var sender = new TcpSk(true, client.Client, SocketStatus.Connected, "CLIP-TEST") { MachineId = (uint)Common.MachineID + 1 };
            var receiver = new TcpSk(false, server.Client, SocketStatus.Connected, "SOURCE");
            var sockets = (SocketStuff)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SocketStuff));
            sockets.TcpSocketsLock = new object();
            typeof(SocketStuff).GetProperty("TcpSockets", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(sockets, new List<TcpSk> { sender });
            Common.Sk = sockets;
            var received = Task.Run(() =>
            {
                using var bytes = new MemoryStream();
                for (int i = 0; i < 2000; i++)
                {
                    var packet = SocketStuff.TcpReceiveData(receiver, out _);
                    if (packet.Type == PackageType.ClipboardDataEnd) return bytes.ToArray();
                    Assert.AreEqual(PackageType.ClipboardText, packet.Type);
                    bytes.Write(packet.Bytes, 16, 48);
                }
                throw new IOException("Clipboard end marker never arrived.");
            });
            await Task.Delay(1100);
            const string text = "TXT跨电脑复制测试🌏CaseSensitive{4CFF57F7-BEDD-43d5-AE8F-27A61E886F2F}";
            Assert.IsTrue(Clip.CheckClipboardEx(new ByteArrayOrString(text), false));
            using var compressed = new MemoryStream(await received.WaitAsync(TimeSpan.FromSeconds(8)));
            using var inflate = new DeflateStream(compressed, CompressionMode.Decompress);
            using var decoded = new MemoryStream(); inflate.CopyTo(decoded);
            Assert.AreEqual(text, System.Text.Encoding.Unicode.GetString(decoded.ToArray()));
        }
        finally { listener.Stop(); Common.Sk = previousSockets; MachineStuff.MachineMatrix = matrix; Encryption.MyKey = previousKey; Encryption.MagicNumber = magic; Setting.Values.ShareClipboard = share; }
    }

    public sealed class CaptureClipboard : IClipboardHelper
    {
        public TaskCompletionSource<ByteArrayOrString> Received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void SendLog(string log) { }
        public void SendDragFile(string fileName) { }
        public void SendClipboardData(ByteArrayOrString data, bool isFilePath) => Received.TrySetResult(data);
    }

    [TestMethod]
    public async Task ActualHelperProxyPreservesTextAndImagePayloads()
    {
        var name = "MWB-Clipboard-Pipeline-" + Guid.NewGuid();
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var connected = server.WaitForConnectionAsync(); await client.ConnectAsync(5000); await connected;
        var capture = new CaptureClipboard(); using var serverRpc = JsonRpc.Attach(server, capture);
        var proxy = JsonRpc.Attach<IClipboardHelper>(client);
        proxy.SendClipboardData(new ByteArrayOrString("TXT剪贴板诊断TEST"), false);
        var text = await capture.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(text.IsString); Assert.AreEqual("TXT剪贴板诊断TEST", text.GetString());
        capture.Received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        byte[] bytes = { 0, 1, 2, 255 }; proxy.SendClipboardData(new ByteArrayOrString(bytes), false);
        var image = await capture.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(image.IsByteArray); CollectionAssert.AreEqual(bytes, image.GetByteArray());
    }
}
