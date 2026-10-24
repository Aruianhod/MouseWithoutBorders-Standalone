using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.Audio;

internal sealed class AudioQuickSetupForm : System.Windows.Forms.Form
{
    private readonly ComboBox peer = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox role = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly CheckBox setDefault = new() { Text = "将远端麦克风设为本机默认麦克风（含通话默认设备）", Checked = true, AutoSize = true };
    private readonly TextBox state = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly Button apply = new() { Text = "一键配置", AutoSize = true };
    private readonly Button restore = new() { Text = "还原一键配置", AutoSize = true };
    private bool busy;
    private readonly QuickAudioState pending;

    internal AudioQuickSetupForm(string[] peers, QuickAudioState pending = null, bool preview = false)
    {
        this.pending = pending;
        Text = "一键配置 · 耳机与麦克风";
        Name = "AudioQuickSetupForm";
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(680, 500);
        MinimumSize = new Size(640, 530);
        StartPosition = FormStartPosition.CenterParent;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 8 };
        foreach (int height in new[] { 54, 32, 32, 34, 74, 32 }) root.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        root.Controls.Add(new Label { Text = "两台各配置一次，之后自动连接。\r\n选择另一台电脑，再选择耳机和麦克风插在哪一边。", Dock = DockStyle.Fill }, 0, 0);
        peer.Items.AddRange(peers.Cast<object>().ToArray());
        if (peer.Items.Count > 0) peer.SelectedIndex = 0;
        if (pending != null)
        {
            if (!peer.Items.Contains(pending.Peer)) peer.Items.Add(pending.Peer);
            peer.SelectedItem = pending.Peer;
        }
        root.Controls.Add(peer, 0, 1);
        role.Items.AddRange(new object[] { "耳机和麦克风插在这台电脑（主电脑）", "这台使用另一台的耳机和麦克风（副电脑）" });
        role.SelectedIndex = pending == null ? 0 : (int)pending.Role;
        root.Controls.Add(role, 0, 2);
        setDefault.Checked = pending?.SetDefaultMicrophone ?? true;
        setDefault.Enabled = role.SelectedIndex == 1;
        role.SelectedIndexChanged += (_, _) => setDefault.Enabled = role.SelectedIndex == 1;
        root.Controls.Add(setDefault, 0, 3);
        root.Controls.Add(new Label
        {
            Text = "主电脑自动发送麦克风、接收声音；副电脑自动发送声音、接收麦克风。\r\n副电脑缺少虚拟设备时，会启动官方驱动安装，需要管理员授权，可能需要重启。\r\n默认设备会先备份；重启后程序会继续配置。不会自动重启或卸载已有驱动。",
            Dock = DockStyle.Fill,
        }, 0, 4);
        var license = new LinkLabel { Text = "虚拟设备：VB-CABLE / VB-Audio（捐赠软件，欢迎捐赠或购买许可）", AutoSize = true };
        license.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo("https://vb-audio.com/Services/licensing.htm") { UseShellExecute = true });
        root.Controls.Add(license, 0, 5);
        root.Controls.Add(state, 0, 6);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        actions.Controls.Add(apply); actions.Controls.Add(restore);
        root.Controls.Add(actions, 0, 7);
        Controls.Add(root);
        state.Text = "点击“一键配置”会保存本机的两条音频通道；副电脑勾选上方选项时，还会切换 Windows 默认麦克风。\r\n会议/游戏软件如果指定了独立输入设备，需改为“系统默认”。";
        apply.Click += async (_, _) => await Run(false);
        restore.Click += async (_, _) =>
        {
            if (busy) return;
            ToggleBusy(true);
            try { await AudioQuickSetup.RestoreAsync(); state.Text = "已还原先前音频配置及仍由本程序指定的默认设备。保留已安装的 VB-CABLE。"; }
            catch (Exception ex) { state.Text = "还原未完成，备份仍保留：" + ex.Message; }
            finally { ToggleBusy(false); }
        };
        FormClosing += (_, e) => { if (busy) e.Cancel = true; };
        if (pending?.Pending == true && !preview) Shown += async (_, _) => await Run(true);
    }

    private void ToggleBusy(bool value)
    {
        busy = value;
        apply.Enabled = restore.Enabled = peer.Enabled = role.Enabled = !value;
        setDefault.Enabled = !value && role.SelectedIndex == 1;
    }
    private async System.Threading.Tasks.Task Run(bool resume)
    {
        if (busy) return;
        ToggleBusy(true);
        try
        {
            if (peer.SelectedItem == null) throw new InvalidOperationException("尚无其他已配对电脑，请先完成鼠标配对。");
            await AudioQuickSetup.ApplyAsync(peer.SelectedItem.ToString(), (QuickAudioRole)role.SelectedIndex,
                setDefault.Checked, message => state.Text = message, resume);
        }
        catch (Exception ex) { state.Text = "本次配置尚未完成：" + ex.Message; }
        finally { ToggleBusy(false); }
    }

    internal static void ShowForCurrentComputer(IWin32Window owner = null, QuickAudioState pending = null)
    {
        string[] peers = MachineStuff.MachineMatrix.Where(n => !string.IsNullOrWhiteSpace(n)
            && !string.Equals(n, Common.MachineName, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        using var form = new AudioQuickSetupForm(peers, pending);
        form.ShowDialog(owner);
    }

    internal static void ResumePending(IWin32Window owner)
    {
        try
        {
            var state = AudioQuickSetup.LoadState();
            if (state?.Pending == true) ShowForCurrentComputer(owner, state);
        }
        catch (Exception ex) { MessageBox.Show(owner, "无法恢复上次音频配置：" + ex.Message, "一键配置", MessageBoxButtons.OK, MessageBoxIcon.Information); }
    }
}
