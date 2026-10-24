using System;
using System.Drawing;
using System.Windows.Forms;
namespace MouseWithoutBorders.Sync;
internal sealed class SyncSettingsForm : System.Windows.Forms.Form
{
    private readonly CheckBox enabled = new() { Text = "配对后双向同步通用设置", AutoSize = true };
    private readonly CheckBox audio = new() { Text = "同时协调音频收发、端口和低延迟设置", AutoSize = true };
    private readonly TextBox status = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    internal SyncSettingsForm()
    {
        Text = "配对设置同步 · 无界鼠标"; Font = new Font("Microsoft YaHei UI", 9F);
        ClientSize = new Size(680, 420); MinimumSize = new Size(650, 420); StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 5 };
        foreach (int size in new[] { 32, 32, 104 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, size));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.Controls.Add(enabled); layout.Controls.Add(audio);
        layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "两边都能修改，连接后约几秒内同步；离线修改在重连后比较版本。\r\n同步剪贴板、文件传输、电脑排列及跨屏选项；同时修改时按统一版本规则处理。\r\n音频发送对应对面的接收，各自设备及音量保留。首次虚拟声卡安装和默认设备仍在本机一键配置。\r\n登录密码、配对码、开机启动和更新地址不在同步范围内。" });
        layout.Controls.Add(status);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var save = new Button { Text = "保存", AutoSize = true };
        var push = new Button { Text = "以本机设置立即同步", AutoSize = true };
        buttons.Controls.Add(save); buttons.Controls.Add(push); layout.Controls.Add(buttons); Controls.Add(layout);
        async System.Threading.Tasks.Task Apply(bool force)
        {
            save.Enabled = push.Enabled = false;
            try { await SyncService.Configure(enabled.Checked, audio.Checked, force); status.Text = SyncService.Status; }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Text); }
            finally { save.Enabled = push.Enabled = true; }
        }
        save.Click += async (_, _) => await Apply(false); push.Click += async (_, _) => await Apply(true);
        Shown += async (_, _) => { var options = await SyncService.Preferences(); enabled.Checked = options.Enabled; audio.Checked = options.Audio; status.Text = SyncService.Status; timer.Start(); };
        timer.Tick += (_, _) => status.Text = SyncService.Status;
        FormClosed += (_, _) => timer.Dispose();
    }
}
