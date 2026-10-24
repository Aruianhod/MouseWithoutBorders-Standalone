using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Core;
using MouseWithoutBorders.Files;

namespace MouseWithoutBorders.UnitTests;

[TestClass][DoNotParallelize]
public sealed class FileTransferTests
{
    private string root = null!;
    [TestInitialize] public void Setup() { root = Path.Combine(Path.GetTempPath(), "MWB-Files-Test-" + Guid.NewGuid()); Directory.CreateDirectory(root); }
    [TestCleanup] public void Cleanup() { if (Path.GetFileName(root).StartsWith("MWB-Files-Test-", StringComparison.Ordinal)) Directory.Delete(root, true); }

    [DataTestMethod][DataRow(0)][DataRow(1)][DataRow(15)][DataRow(16)][DataRow(1024)][DataRow(200001)]
    public async Task EncryptedDuplexTransferAcknowledgesVerifiedFile(int size)
    {
        string oldKey = Encryption.MyKey; Encryption.InitEncryption(); Encryption.MyKey = "TestFileTransferPairingKey12345";
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            byte[] payload = RandomNumberGenerator.GetBytes(size);
            using var client = new TcpClient(); var accepting = listener.AcceptTcpClientAsync();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            using var server = await accepting;
            client.ReceiveTimeout = client.SendTimeout = server.ReceiveTimeout = server.SendTimeout = 8000;
            var receiving = Task.Run(() =>
            {
                var output = Encryption.GetEncryptedStream(server.GetStream());
                var input = Encryption.GetDecryptedStream(server.GetStream());
                return FileTransferWire.Receive(output, input, root, CancellationToken.None, null);
            });
            var sending = Task.Run(() =>
            {
                var output = Encryption.GetEncryptedStream(client.GetStream());
                var input = Encryption.GetDecryptedStream(client.GetStream());
                using var file = new MemoryStream(payload);
                FileTransferWire.Send(file, "中文文件.bin", output, input, CancellationToken.None, null);
            });
            await Task.WhenAll(sending, receiving).WaitAsync(TimeSpan.FromSeconds(15));
            CollectionAssert.AreEqual(payload, File.ReadAllBytes(await receiving));
            Assert.AreEqual(1, Directory.GetFiles(root).Length);
        }
        finally { listener.Stop(); Encryption.MyKey = oldKey; }
    }

    [DataTestMethod][DataRow("../escape")][DataRow("C:\\escape")][DataRow("file:stream")][DataRow("CON.txt")][DataRow("a.")][DataRow("folder/file")]
    public void MaliciousNamesCannotEscapeInbox(string name) => Assert.ThrowsExactly<InvalidDataException>(() => FileTransferWire.Validate(new FileOffer { Name = name }));

    [TestMethod]
    public void NegativeAndOversizeLengthsAreRejected()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => FileTransferWire.Validate(new FileOffer { Name = "a.bin", Length = -1 }));
        Assert.ThrowsExactly<InvalidDataException>(() => FileTransferWire.Validate(new FileOffer { Name = "a.bin", Length = FileTransferWire.MaximumFileSize + 1 }));
    }

    [TestMethod]
    public void DuplicateFilesAreRenamedWithoutOverwriting()
    {
        File.WriteAllText(Path.Combine(root, "a.txt"), "original");
        using var incoming = Incoming("a.txt", new byte[] { 1, 2, 3 }, false); using var replies = new MemoryStream();
        string saved = FileTransferWire.Receive(replies, incoming, root, CancellationToken.None, null);
        Assert.AreEqual("a (1).txt", Path.GetFileName(saved));
        Assert.AreEqual("original", File.ReadAllText(Path.Combine(root, "a.txt")));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(saved));
        replies.Position = 0;
        Assert.AreEqual("READY", FileTransferWire.ReadFrame<string>(replies));
        Assert.AreEqual("SAVED:a (1).txt", FileTransferWire.ReadFrame<string>(replies));
    }

    [TestMethod]
    public void HashMismatchLeavesNoFileOrPartialFile()
    {
        using var incoming = Incoming("a.txt", new byte[] { 1, 2, 3 }, true); using var replies = new MemoryStream();
        Assert.ThrowsExactly<InvalidDataException>(() => FileTransferWire.Receive(replies, incoming, root, CancellationToken.None, null));
        Assert.AreEqual(0, Directory.GetFiles(root).Length);
    }

    [TestMethod]
    public void TruncatedAndCancelledTransfersCleanUpPartialFiles()
    {
        using var incoming = new MemoryStream(); FileTransferWire.WriteFrame(incoming, new FileOffer { Name = "a.txt", Length = 100 }); incoming.WriteByte(1); incoming.Position = 0;
        using var replies = new MemoryStream();
        Assert.ThrowsExactly<EndOfStreamException>(() => FileTransferWire.Receive(replies, incoming, root, CancellationToken.None, null));
        Assert.AreEqual(0, Directory.GetFiles(root).Length);
        using var complete = Incoming("a.txt", new byte[] { 1, 2, 3 }, false); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.ThrowsAsync<OperationCanceledException>(() => Task.Run(() => FileTransferWire.Receive(replies, complete, root, cancelled.Token, null))).GetAwaiter().GetResult();
        Assert.AreEqual(0, Directory.GetFiles(root).Length);
    }

    private static MemoryStream Incoming(string name, byte[] bytes, bool corrupt)
    {
        var stream = new MemoryStream(); FileTransferWire.WriteFrame(stream, new FileOffer { Name = name, Length = bytes.Length });
        stream.Write(bytes); stream.Write(new byte[(16 - bytes.Length % 16) % 16]);
        FileTransferWire.WriteFrame(stream, corrupt ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(bytes)));
        stream.Position = 0; return stream;
    }
}
