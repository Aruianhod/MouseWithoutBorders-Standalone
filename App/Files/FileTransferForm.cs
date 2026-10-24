using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MouseWithoutBorders.Class;

namespace MouseWithoutBorders.Files;

internal sealed class FileTransferForm : System.Windows.Forms.Form
{
    private readonly ComboBox peers = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly Label drop = new() { Text = "将一个或多个文件拖到这里发送\r\n也可以点击“选择文件”", TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, AllowDrop = true };
    private readonly TextBox status = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
    private readonly Button choose = new() { Text = "选择文件", AutoSize = true };
    private readonly Button refresh = new() { Text = "刷新电脑", AutoSize = true };
    private readonly Button cancel = new() { Text = "取消发送", AutoSize = true, Enabled = false };
    private readonly CheckBox enabled = new() { Text = "启用文件传输（需要共享剪贴板）", AutoSize = true };
    private CancellationTokenSource cancellation;

    internal FileTransferForm()
    {
        Text = "无界鼠标 · 拖拽传文件"; Font = new Font("Microsoft YaHei UI", 9F); AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(660, 470); MinimumSize = new Size(620, 490); StartPosition = FormStartPosition.CenterScreen;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 7 };
        foreach (int height in new[] { 30, 36, 56, 130, 28 }) root.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        enabled.Checked = Setting.Values.ShareClipboard && Setting.Values.TransferFile;
        enabled.Enabled = !Setting.Values.TransferFileIsGpoConfigured && !Setting.Values.ShareClipboardIsGpoConfigured;
        enabled.CheckedChanged += (_, _) => { if (enabled.Checked) Setting.Values.ShareClipboard = true; Setting.Values.TransferFile = enabled.Checked; };
        root.Controls.Add(enabled, 0, 0); root.Controls.Add(peers, 0, 1);
        root.Controls.Add(new Label { Text = "选择已连接的接收电脑。双方需使用 .7 或更新版本。\r\n单文件上限 10 GB；支持多文件，文件夹请先压缩为 ZIP。\r\n收到的文件保存在对方桌面的 MouseWithoutBorders 文件夹，同名自动改名。", Dock = DockStyle.Fill }, 0, 2);
        root.Controls.Add(drop, 0, 3);
        root.Controls.Add(new Label { Text = "也保留原版跨屏拖单个文件的方式；不支持拖进任意应用窗口。", AutoSize = true }, 0, 4);
        root.Controls.Add(status, 0, 5);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var open = new Button { Text = "打开接收文件夹", AutoSize = true };
        buttons.Controls.Add(cancel); buttons.Controls.Add(choose); buttons.Controls.Add(refresh); buttons.Controls.Add(open);
        root.Controls.Add(buttons, 0, 6); Controls.Add(root);
        refresh.Click += (_, _) => RefreshPeers(); RefreshPeers();
        open.Click += (_, _) => { Directory.CreateDirectory(FileTransferService.Inbox); Process.Start(new ProcessStartInfo(FileTransferService.Inbox) { UseShellExecute = true }); };
        choose.Click += async (_, _) => { using var picker = new OpenFileDialog { Multiselect = true, Title = "选择要发送的文件" }; if (picker.ShowDialog(this) == DialogResult.OK) await Send(picker.FileNames); };
        drop.DragEnter += (_, args) => args.Effect = cancellation == null && args.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        drop.DragDrop += async (_, args) => { if (args.Data.GetData(DataFormats.FileDrop) is string[] files) await Send(files); };
        cancel.Click += (_, _) => cancellation?.Cancel();
        FormClosing += (_, args) => { if (cancellation != null) { cancellation.Cancel(); args.Cancel = true; } };
    }

    private void RefreshPeers()
    {
        peers.Items.Clear();
        foreach (string peer in Core.MachineStuff.MachineMatrix.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
            if (Audio.AudioService.ConnectedAddress(peer) != null) peers.Items.Add(peer);
        if (peers.Items.Count > 0) peers.SelectedIndex = 0;
        else status.Text = "尚无已连接电脑。先配对并连接另一台电脑，再点击刷新。";
    }

    private async Task Send(string[] paths)
    {
        if (cancellation != null) return;
        if (!enabled.Checked || peers.SelectedItem == null) { status.Text = "请启用文件传输并选择已连接的接收电脑。"; return; }
        cancellation = new CancellationTokenSource(); choose.Enabled = refresh.Enabled = peers.Enabled = enabled.Enabled = false; cancel.Enabled = true;
        try
        {
            if (paths.Length == 0 || paths.Length > 1000) throw new IOException("一次请选择 1 至 1000 个文件。");
            foreach (string path in paths) { if (Directory.Exists(path)) throw new IOException("文件夹请先压缩为 ZIP 再发送。"); }
            await FileTransferService.SendAsync(peers.SelectedItem.ToString(), paths, new Progress<string>(text => status.Text = text), cancellation.Token);
            status.Text = "发送完成，对方已确认保存 " + paths.Length + " 个文件。";
        }
        catch (Exception ex) { status.Text = cancellation.IsCancellationRequested ? "已取消；已传完的文件保留，未传完的文件由接收方清理。" : "发送未完成：" + ex.Message + "\r\n已传完的文件保留；重试会另存同名文件，不会覆盖。"; }
        finally { cancellation.Dispose(); cancellation = null; choose.Enabled = refresh.Enabled = peers.Enabled = true; enabled.Enabled = !Setting.Values.TransferFileIsGpoConfigured && !Setting.Values.ShareClipboardIsGpoConfigured; cancel.Enabled = false; }
    }
}
