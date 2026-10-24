using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace MouseWithoutBorders.Updates;

internal sealed class UpdateForm : System.Windows.Forms.Form
{
    private readonly TextBox folder = new() { Dock = DockStyle.Fill };
    private readonly CheckBox auto = new() { Text = "启动后及每 6 小时自动检查，只提醒，不自动重启", AutoSize = true };
    private readonly TextBox status = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly Button check = new() { Text = "保存并检查更新", AutoSize = true };
    private readonly Button install = new() { Text = "下载并重启更新", AutoSize = true, Enabled = false };
    private readonly Button cancel = new() { Text = "取消下载", AutoSize = true, Enabled = false };
    private UpdateCandidate candidate;
    private bool busy;
    private CancellationTokenSource cancellation;

    internal UpdateForm()
    {
        Text = "无界鼠标 · 软件更新";
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(700, 460);
        MinimumSize = new Size(650, 480);
        StartPosition = FormStartPosition.CenterScreen;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 7 };
        foreach (int height in new[] { 35, 24, 34, 36, 58 }) root.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.Controls.Add(new Label { Text = "当前版本：" + UpdateClient.CurrentVersion, AutoSize = true }, 0, 0);
        root.Controls.Add(new Label { Text = "更新文件夹（共享路径或本机路径）", AutoSize = true }, 0, 1);
        root.Controls.Add(folder, 0, 2); root.Controls.Add(auto, 0, 3);
        root.Controls.Add(new Label { Text = "更新会短暂断开鼠标和声音共享，并重新启动程序。配对及音频设置会保留。\r\n只接受本项目发布密钥签名的更新；旧程序文件夹会保留为 .previous-… 备份。\r\n驱动单独安装；本更新不会更改虚拟声卡或 Windows 默认音频设备。", Dock = DockStyle.Fill }, 0, 4);
        root.Controls.Add(status, 0, 5);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        actions.Controls.Add(install); actions.Controls.Add(cancel); actions.Controls.Add(check);
        root.Controls.Add(actions, 0, 6); Controls.Add(root);
        try { var options = UpdateOptions.Load(); folder.Text = options.Folder; auto.Checked = options.AutoCheck; }
        catch (Exception ex) { folder.Text = new UpdateOptions().Folder; status.Text = "更新设置读取失败，可重新保存：" + ex.Message; }
        folder.TextChanged += (_, _) => { candidate = null; install.Enabled = false; };
        auto.CheckedChanged += (_, _) =>
        {
            try { new UpdateOptions { Folder = folder.Text.Trim(), AutoCheck = auto.Checked }.Save(); }
            catch (Exception ex) { status.Text = "保存失败：" + ex.Message; }
        };
        check.Click += async (_, _) =>
        {
            if (busy) return;
            candidate = null; SetBusy(true);
            cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                var options = new UpdateOptions { Folder = folder.Text.Trim(), AutoCheck = auto.Checked };
                options.Save(); status.Text = "正在检查更新…";
                candidate = await UpdateClient.CheckAsync(options.Folder, cancellation.Token);
                status.Text = UpdatePackage.IsNewer(candidate.Release, UpdateClient.CurrentVersion)
                    ? "发现新版本：" + candidate.Release.Version + "\r\n" + candidate.Release.Notes
                    : "当前已是此更新源提供的最新版本。";
            }
            catch (Exception ex) { status.Text = "检查未完成：" + ex.Message + "\r\n可先在资源管理器中打开共享路径，确认能正常访问。"; }
            finally { cancellation.Dispose(); cancellation = null; SetBusy(false); }
        };
        cancel.Click += (_, _) => cancellation?.Cancel();
        install.Click += async (_, _) =>
        {
            if (busy || candidate == null) return;
            if (MessageBox.Show(this, "下载完成后将关闭并重启无界鼠标，鼠标、剪贴板和音频连接会短暂中断。\r\n现在更新吗？", "更新到 " + candidate.Release.Version,
                MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
            SetBusy(true); cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10)); cancel.Enabled = true;
            var progress = new Progress<string>(text => status.Text = text);
            try
            {
                if (Audio.AudioQuickSetup.LoadState()?.Pending == true) throw new IOException("请先完成或取消待进行的音频驱动配置，再更新程序。");
                if (Files.FileTransferService.IsBusy) throw new IOException("请先完成或取消文件传输，再更新程序。");
                UpdatePackage.ValidateInstall(AppContext.BaseDirectory, Microsoft.PowerToys.Settings.UI.Library.SettingsUtils.DataDirectory);
                string job = await UpdateClient.DownloadAsync(candidate, progress, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                cancel.Enabled = false;
                await UpdateClient.PrepareAndRestartAsync(job, progress);
            }
            catch (OperationCanceledException) { status.Text = "已取消下载，当前程序继续运行。"; }
            catch (Exception ex) { status.Text = "更新未完成：" + ex.Message; }
            finally { cancellation.Dispose(); cancellation = null; SetBusy(false); }
        };
        FormClosing += (_, e) => { if (busy) e.Cancel = true; };
    }

    private void SetBusy(bool value)
    {
        busy = value; folder.Enabled = auto.Enabled = check.Enabled = !value;
        install.Enabled = !value && candidate != null && UpdatePackage.IsNewer(candidate.Release, UpdateClient.CurrentVersion);
        cancel.Enabled = false;
    }
}
