using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MouseWithoutBorders.Core;
using NAudio.CoreAudioApi;

namespace MouseWithoutBorders.Audio;

internal sealed class AudioSettingsForm : System.Windows.Forms.Form
{
    private readonly RouteEditor sendSystem;
    private readonly RouteEditor receiveSystem;
    private readonly RouteEditor sendMicrophone;
    private readonly RouteEditor receiveMicrophone;
    private readonly NumericUpDown port = new() { Minimum = 1024, Maximum = 65535, Width = 90 };
    private readonly NumericUpDown buffer = new() { Minimum = 60, Maximum = 500, Increment = 20, Width = 80 };
    private readonly CheckBox lowLatency = new() { Text = "低延迟（局域网推荐；声音断续时可关闭）", AutoSize = true };
    private readonly TextBox state = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly Button apply = new() { Text = "保存并应用", AutoSize = true };
    private readonly Button stop = new() { Text = "停止全部音频共享", AutoSize = true };
    private readonly Button refresh = new() { Text = "刷新设备", AutoSize = true };
    private readonly Button quick = new() { Text = "一键配置…", AutoSize = true };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private bool applying;

    internal AudioSettingsForm(AudioOptions initial = null)
    {
        Text = "声音与麦克风共享 · 无界鼠标";
        Name = "AudioSettingsForm";
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(780, 710);
        MinimumSize = new Size(720, 720);
        StartPosition = FormStartPosition.CenterParent;
        var settings = initial ?? AudioService.Instance.Options;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(12) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 126));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "点击下方“一键配置”：耳机插在哪台，哪台选主电脑；另一台选副电脑。\r\n两边配置后先确认鼠标能跨屏，再在副电脑播放声音测试。等待鼠标连接时音频不会传输。",
        }, 0, 0);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        sendSystem = new RouteEditor("把本机系统声音发送到另一台电脑", settings.SendSystem, DataFlow.Render);
        receiveSystem = new RouteEditor("接收另一台电脑的系统声音", settings.ReceiveSystem, DataFlow.Render);
        sendMicrophone = new RouteEditor("把本机麦克风发送到另一台电脑", settings.SendMicrophone, DataFlow.Capture);
        receiveMicrophone = new RouteEditor("接收另一台电脑的麦克风", settings.ReceiveMicrophone, DataFlow.Render, true);
        AddTab(tabs, "系统声音", sendSystem, receiveSystem,
            "想让副电脑扬声器不响：在副电脑点“一键配置”，选择“仅主电脑播放”。发送与麦克风接收请使用不同通道，避免串音。");
        AddTab(tabs, "麦克风", sendMicrophone, receiveMicrophone,
            "供会议/游戏使用：接收输出选虚拟音频线的播放端（如 CABLE Input），应用的麦克风选其录音端（CABLE Output）。需要自行安装虚拟音频设备；选耳机则为监听。");
        root.Controls.Add(tabs, 0, 1);
        var network = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
        port.Value = settings.Port;
        buffer.Value = settings.BufferMilliseconds;
        network.Controls.Add(new Label { Text = "音频端口（两台相同）", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        network.Controls.Add(port);
        network.Controls.Add(new Label { Text = "接收缓冲上限（毫秒）", AutoSize = true, Padding = new Padding(16, 6, 0, 0) });
        network.Controls.Add(buffer);
        network.SetFlowBreak(buffer, true);
        lowLatency.Checked = settings.LowLatency;
        buffer.Enabled = !lowLatency.Checked;
        lowLatency.CheckedChanged += (_, _) => buffer.Enabled = !lowLatency.Checked;
        network.Controls.Add(lowLatency);
        root.Controls.Add(network, 0, 2);
        root.Controls.Add(state, 0, 3);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 6, 0, 0) };
        actions.Controls.Add(apply);
        actions.Controls.Add(stop);
        actions.Controls.Add(refresh);
        actions.Controls.Add(quick);
        root.Controls.Add(actions, 0, 4);
        Controls.Add(root);
        apply.Click += async (_, _) => await Save(false);
        stop.Click += async (_, _) => await Save(true);
        refresh.Click += (_, _) => RefreshDevices();
        quick.Click += (_, _) =>
        {
            AudioQuickSetupForm.ShowForCurrentComputer(this);
            var updated = AudioService.Instance.Options;
            sendSystem.LoadRoute(updated.SendSystem); receiveSystem.LoadRoute(updated.ReceiveSystem);
            sendMicrophone.LoadRoute(updated.SendMicrophone); receiveMicrophone.LoadRoute(updated.ReceiveMicrophone);
            port.Value = updated.Port; buffer.Value = updated.BufferMilliseconds; lowLatency.Checked = updated.LowLatency;
            RefreshDevices();
        };
        timer.Tick += (_, _) => { if (!applying) state.Text = AudioService.Instance.Status; };
        Shown += (_, _) =>
        {
            // An explicit model also supports offline UI previews without device access.
            if (initial != null) return;
            RefreshDevices(); state.Text = AudioService.Instance.Status; timer.Start();
        };
        FormClosing += (_, e) => { if (applying) e.Cancel = true; };
        FormClosed += (_, _) => timer.Dispose();
    }

    internal AudioOptions ReadOptions() => new()
    {
        SendSystem = sendSystem.Read(), ReceiveSystem = receiveSystem.Read(),
        SendMicrophone = sendMicrophone.Read(), ReceiveMicrophone = receiveMicrophone.Read(),
        Port = (int)port.Value, BufferMilliseconds = (int)buffer.Value, LowLatency = lowLatency.Checked,
    };

    private void RefreshDevices()
    {
        try { foreach (var editor in new[] { sendSystem, receiveSystem, sendMicrophone, receiveMicrophone }) editor.RefreshDevices(); }
        catch (Exception ex) { MessageBox.Show(this, "读取音频设备失败：" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information); }
    }

    private async System.Threading.Tasks.Task Save(bool disable)
    {
        try
        {
            applying = true;
            apply.Enabled = stop.Enabled = refresh.Enabled = quick.Enabled = false;
            var value = ReadOptions();
            if (disable)
            {
                AudioService.Instance.Stop();
                AudioQuickSetup.CancelPending();
                AudioQuickSetup.RestorePlaybackOnStop();
                // Emergency stop also works while other unsaved fields are invalid.
                value = AudioService.Instance.Options;
                foreach (var route in new[] { value.SendSystem, value.SendMicrophone, value.ReceiveSystem, value.ReceiveMicrophone }) route.Enabled = false;
                foreach (var editor in new[] { sendSystem, receiveSystem, sendMicrophone, receiveMicrophone }) editor.EnabledChoice.Checked = false;
            }
            else
            {
                value.Validate(Common.MachineName, Class.Setting.Values.TcpPort);
                foreach (AudioKind kind in Enum.GetValues<AudioKind>())
                {
                    var sender = value.Send(kind);
                    if (sender.Enabled)
                    {
                        using var device = AudioDevices.Open(sender.DeviceId, kind == AudioKind.System ? DataFlow.Render : DataFlow.Capture);
                    }
                    var receiver = value.Receive(kind);
                    if (receiver.Enabled)
                    {
                        using var device = AudioDevices.Open(receiver.DeviceId, DataFlow.Render);
                        AudioDevices.CheckFeedback(value, device.ID);
                    }
                }
            }
            state.Text = "正在应用，音频连接会按新设置重建……";
            await AudioService.Instance.ApplyAsync(value);
            state.Text = AudioService.Instance.Status;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法应用音频设置", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        finally { applying = false; apply.Enabled = stop.Enabled = refresh.Enabled = quick.Enabled = true; }
    }

    private static void AddTab(TabControl tabs, string name, RouteEditor send, RouteEditor receive, string note)
    {
        var page = new TabPage(name) { Padding = new Padding(8), AutoScroll = true };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        layout.Controls.Add(send, 0, 0);
        layout.Controls.Add(receive, 0, 1);
        layout.Controls.Add(new Label { Text = note, Dock = DockStyle.Fill, Padding = new Padding(3, 6, 3, 0) }, 0, 2);
        page.Controls.Add(layout);
        tabs.TabPages.Add(page);
    }

    private sealed class RouteEditor : GroupBox
    {
        internal CheckBox EnabledChoice { get; } = new() { Text = "启用此项共享", AutoSize = true };
        private readonly ComboBox peer = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
        private readonly ComboBox device = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, DropDownWidth = 650 };
        private readonly NumericUpDown volume = new() { Minimum = 0, Maximum = 100, Width = 80 };
        private readonly DataFlow flow;
        private readonly bool explicitDevice;

        internal RouteEditor(string title, AudioRoute value, DataFlow flow, bool explicitDevice = false)
        {
            Text = title; Dock = DockStyle.Fill; Padding = new Padding(8);
            this.flow = flow; this.explicitDevice = explicitDevice;
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 3 };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            for (int i = 0; i < 3; i++) grid.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33f));
            grid.Controls.Add(EnabledChoice, 0, 0); grid.SetColumnSpan(EnabledChoice, 4);
            grid.Controls.Add(new Label { Text = "电脑", AutoSize = true }, 0, 1);
            grid.Controls.Add(peer, 1, 1);
            grid.Controls.Add(new Label { Text = "音量 %", AutoSize = true }, 2, 1);
            grid.Controls.Add(volume, 3, 1);
            grid.Controls.Add(new Label { Text = "设备", AutoSize = true }, 0, 2);
            grid.Controls.Add(device, 1, 2); grid.SetColumnSpan(device, 3);
            Controls.Add(grid);
            EnabledChoice.Checked = value.Enabled;
            peer.Text = value.Peer;
            volume.Value = Math.Clamp(value.Volume, 0, 100);
            device.Items.Add(new AudioDeviceChoice(value.DeviceId, value.DeviceId.Length == 0 ? (explicitDevice ? "请选择接收麦克风的输出设备" : "跟随 Windows 默认设备") : "已保存设备（打开窗口后刷新）"));
            device.SelectedIndex = 0;
        }

        internal AudioRoute Read() => new()
        {
            Enabled = EnabledChoice.Checked, Peer = peer.Text.Trim(),
            DeviceId = (device.SelectedItem as AudioDeviceChoice)?.Id ?? "", Volume = (int)volume.Value,
        };

        internal void LoadRoute(AudioRoute value)
        {
            EnabledChoice.Checked = value.Enabled;
            peer.Text = value.Peer;
            volume.Value = value.Volume;
            device.Items.Clear();
            device.Items.Add(new AudioDeviceChoice(value.DeviceId, "已自动选择"));
            device.SelectedIndex = 0;
        }

        internal void RefreshDevices()
        {
            string currentPeer = peer.Text;
            peer.Items.Clear();
            peer.Items.AddRange(MachineStuff.MachineMatrix.Where(n => !string.IsNullOrWhiteSpace(n) && !n.Equals(Common.MachineName, StringComparison.OrdinalIgnoreCase)).Cast<object>().ToArray());
            peer.Text = currentPeer;
            string selected = Read().DeviceId;
            var choices = AudioDevices.List(flow, !explicitDevice);
            if (explicitDevice) choices.Insert(0, new("", "请选择虚拟音频线播放端或监听耳机"));
            if (!choices.Any(d => d.Id == selected)) choices.Add(new(selected, "已保存设备当前不可用（不会自动改用其他设备）"));
            device.Items.Clear();
            device.Items.AddRange(choices.Cast<object>().ToArray());
            device.SelectedItem = choices.First(d => d.Id == selected);
        }
    }
}
