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
    private readonly ComboBox playback = new() { Name = "PlaybackMode", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
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
        ClientSize = new Size(720, 600);
        MinimumSize = new Size(720, 630);
        StartPosition = FormStartPosition.CenterParent;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 9 };
        foreach (int height in new[] { 54, 32, 32, 34, 36, 108, 32 }) root.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
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
        role.SelectedIndexChanged += (_, _) => setDefault.Enabled = playback.Enabled = role.SelectedIndex == 1;
        root.Controls.Add(setDefault, 0, 3);
        playback.Items.AddRange(new object[] { "播放位置：两边同时播放（副电脑扬声器也响）", "播放位置：仅主电脑播放（副电脑扬声器不响）" });
        playback.SelectedIndex = pending == null ? 1 : (int)pending.PlaybackMode;
        playback.Enabled = role.SelectedIndex == 1;
        root.Controls.Add(playback, 0, 4);
        root.Controls.Add(new Label
        {
            Text = "主电脑发送麦克风、接收声音；播放位置请在副电脑选择。\r\n仅主电脑播放：自动下载并安装 VB-Audio Hi-Fi Cable，和麦克风通道分开。\r\n首次需联网并完成官方 Install 窗口，可能需要重启；不需要打开 ASIO Bridge。\r\n默认设备会先备份，可切回两边播放或还原。固定使用扬声器的应用需改为系统默认。",
            Dock = DockStyle.Fill,
        }, 0, 5);
        var license = new LinkLabel { Text = "VB-CABLE / Hi-Fi Cable · VB-Audio 捐赠软件（来源与许可）", AutoSize = true };
        license.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo("https://vb-audio.com/Services/licensing.htm") { UseShellExecute = true });
        root.Controls.Add(license, 0, 6);
        root.Controls.Add(state, 0, 7);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        actions.Controls.Add(apply); actions.Controls.Add(restore);
        root.Controls.Add(actions, 0, 8);
        Controls.Add(root);
        state.Text = "点击“一键配置”保存两条音频通道及所选播放位置；副电脑还会按选项切换默认播放设备和麦克风。\r\n会议/游戏软件如果指定了独立输入设备，需改为“系统默认”。";
        apply.Click += async (_, _) => await Run(false);
        restore.Click += async (_, _) =>
        {
            if (busy) return;
            ToggleBusy(true);
            try { await AudioQuickSetup.RestoreAsync(); state.Text = "已还原先前音频配置及仍由本程序指定的默认设备。保留已安装的虚拟音频驱动。"; }
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
        playback.Enabled = !value && role.SelectedIndex == 1;
    }
    private async System.Threading.Tasks.Task Run(bool resume)
    {
        if (busy) return;
        ToggleBusy(true);
        string stage = "检查所选电脑";
        state.Clear();
        try
        {
            if (peer.SelectedItem == null) throw new InvalidOperationException("尚无其他已配对电脑，请先完成鼠标配对。");
            await AudioQuickSetup.ApplyAsync(peer.SelectedItem.ToString(), (QuickAudioRole)role.SelectedIndex,
                setDefault.Checked, message => { stage = message; state.AppendText(message + Environment.NewLine); }, resume, (RemotePlaybackMode)playback.SelectedIndex);
        }
        catch (Exception ex)
        {
            state.AppendText(Environment.NewLine + AudioQuickSetup.DescribeFailure(ex, stage));
            Logger.Log("Audio quick setup failed at: " + stage);
            Logger.Log(ex);
        }
        finally { ToggleBusy(false); }
    }

    internal static void ShowForCurrentComputer(IWin32Window owner = null, QuickAudioState pending = null)
    {
        string[] peers = MachineStuff.MachineMatrix.Where(n => !string.IsNullOrWhiteSpace(n)
            && !string.Equals(n, Common.MachineName, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        using var form = new AudioQuickSetupForm(peers, pending ?? AudioQuickSetup.LoadState());
        DialogWindows.Show(form, owner);
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
