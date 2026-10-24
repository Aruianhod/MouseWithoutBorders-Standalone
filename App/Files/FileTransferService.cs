using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.Files;

internal static class FileTransferService
{
    private static readonly SemaphoreSlim sendGate = new(1, 1);
    private static readonly SemaphoreSlim receiveGate = new(1, 1);
    private static int active;
    internal static bool IsBusy => Volatile.Read(ref active) != 0;
    internal static string Inbox => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "MouseWithoutBorders");

    internal static async Task SendAsync(string peer, string[] paths, IProgress<string> progress, CancellationToken token)
    {
        if (!await sendGate.WaitAsync(0, token)) throw new IOException("另一个文件发送任务正在进行。");
        Interlocked.Increment(ref active);
        try
        {
            await Task.Run(async () =>
            {
                foreach (string path in paths)
                {
                    token.ThrowIfCancellationRequested();
                    if (!Setting.Values.ShareClipboard || !Setting.Values.TransferFile || Setting.Values.OneWayClipboardMode)
                        throw new IOException("请启用共享剪贴板和传输文件，并关闭单向剪贴板模式。");
                    if (Directory.Exists(path)) throw new IOException("文件夹请先压缩为 ZIP 再拖入。已传完的文件会保留。");
                    using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    FileTransferWire.Validate(new FileOffer { Name = Path.GetFileName(path), Length = file.Length });
                    var address = Audio.AudioService.ConnectedAddress(peer) ?? throw new IOException("对方尚未连接，请先完成鼠标配对。");
                    using var client = new TcpClient(AddressFamily.InterNetworkV6);
                    client.Client.DualMode = true;
                    using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(token); connectTimeout.CancelAfter(10000);
                    progress?.Report("正在连接 " + peer + "：" + Path.GetFileName(path));
                    await client.ConnectAsync(address, Common.Sk.TcpPort, connectTimeout.Token);
                    using var cancelSocket = token.Register(() => client.Dispose());
                    string authenticatedPeer = peer;
                    bool push = true; var action = ClipboardPostAction.FileInbox;
                    if (!Core.Clipboard.ShakeHand(ref authenticatedPeer, client.Client, out var encrypted, out var decrypted, ref push, ref action)
                        || !authenticatedPeer.Equals(peer, StringComparison.OrdinalIgnoreCase)) throw new IOException("文件连接验证失败。");
                    client.SendTimeout = client.ReceiveTimeout = 30000;
                    long lastReport = 0;
                    FileTransferWire.Send(file, Path.GetFileName(path), encrypted, decrypted, token, (sent, total) =>
                    {
                        if (Environment.TickCount64 - lastReport < 150 && sent != total) return;
                        lastReport = Environment.TickCount64;
                        if (!Setting.Values.TransferFile || Audio.AudioService.ConnectedAddress(peer) == null) throw new IOException("文件传输已关闭或对方已断开。");
                        progress?.Report($"{Path.GetFileName(path)}：{sent / 1048576.0:F1} / {total / 1048576.0:F1} MB（{(total == 0 ? 100 : sent * 100 / total)}%）");
                    });
                }
            }, token);
        }
        finally { Interlocked.Decrement(ref active); sendGate.Release(); }
    }

    internal static void Receive(string peer, Socket socket, Stream encrypted, Stream decrypted)
    {
        if (!Setting.Values.ShareClipboard || !Setting.Values.TransferFile || Common.RunOnLogonDesktop || !receiveGate.Wait(0))
        { socket.Close(); return; }
        Interlocked.Increment(ref active);
        try
        {
            socket.SendTimeout = socket.ReceiveTimeout = 30000;
            long lastCheck = 0;
            string file = FileTransferWire.Receive(encrypted, decrypted, Inbox, CancellationToken.None, (received, total) =>
            {
                if (Environment.TickCount64 - lastCheck < 300 && received != total) return;
                lastCheck = Environment.TickCount64;
                if (!Setting.Values.TransferFile || Audio.AudioService.ConnectedAddress(peer) == null) throw new IOException("文件传输关闭或鼠标连接已断开。");
            });
            Common.ShowToolTip("已收到 " + peer + " 的文件：" + Path.GetFileName(file) + "\r\n保存在桌面的 MouseWithoutBorders 文件夹。", 5000, ToolTipIcon.Info);
        }
        catch (Exception ex) { Common.ShowToolTip("文件接收未完成：" + ex.Message, 5000, ToolTipIcon.Warning); }
        finally { socket.Close(); Interlocked.Decrement(ref active); receiveGate.Release(); }
    }
}
