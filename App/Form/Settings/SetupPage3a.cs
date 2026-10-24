// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;
using MouseWithoutBorders.Properties;

namespace MouseWithoutBorders
{
    public partial class SetupPage3a : SettingsFormPage
    {
        private readonly Image[] _frames = { Images.copy_paste_example, Images.drag_example, Images.keyboard_example };
        private readonly string[] _messages =
        {
                                                  "在电脑间复制和粘贴",
                                                  "在电脑间拖动文件",
                                                  "多台电脑共用键盘",
        };

        private readonly int[] _timing = { 1000, 1000, 2000 };
#pragma warning disable CA2213 // Disposing is done by ComponentResourceManager
        private readonly System.Timers.Timer _animationTimer;
#pragma warning restore CA2213
        private bool connected;
        private bool done;
        private bool invalidKey;

        private int _animationFrame;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public bool ReturnToSettings { get; set; }

        public SetupPage3a()
        {
            InitializeComponent();
            _animationTimer = new System.Timers.Timer { Interval = 1000 };
        }

        private void ShowStatus(string status)
        {
            labelStatus.Text = status;
            Logger.Log(status);
        }

        public override void OnPageClosing()
        {
            _animationTimer.Stop();
            base.OnPageClosing();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            _animationTimer.Elapsed += AnimationTimerTick;

            Common.GetMachineName();
            invalidKey = false;

            UpdateAnimation();
            _animationTimer.Start();

            SocketStuff.InvalidKeyFound = false;
            SocketStuff.InvalidKeyFoundOnClientSocket = false;

            ShowStatus($"正在连接…");

            MachineStuff.SwitchToMultipleMode(false, false);
            Common.ReopenSockets(true);

            int timeOut = 0;
            TcpSk connectedClientSocket;

            // Client sockets run in different threads.
            while (timeOut < 10)
            {
                Common.MMSleep(1);

                if ((connectedClientSocket = Common.GetConnectedClientSocket()) != null)
                {
                    ShowStatus($"已连接，本机 IP：{connectedClientSocket.Address}");
                    InitAndCleanup.UpdateMachineTimeAndID();

                    Common.MMSleep(1);
                    connected = true;
                    return;
                }
                else if (SocketStuff.InvalidKeyFoundOnClientSocket)
                {
                    invalidKey = true;
                    ShowStatus("配对码不一致。");
                    Common.MMSleep(3);
                    break;
                }

                timeOut++;
            }

            done = true;
        }

        private void AnimationTimerTick(object sender, EventArgs e)
        {
            _ = Invoke(new MethodInvoker(UpdateAnimation));
        }

        private void UpdateAnimation()
        {
            if ((ModifierKeys & Keys.Control) != 0)
            {
                _animationTimer.Stop();
                SendNextPage(new SetupPage2ab());
                return;
            }

            ExamplePicture.Image = _frames[_animationFrame];
            MessageLabel.Text = _messages[_animationFrame];
            _animationTimer.Interval = _timing[_animationFrame];
            _animationFrame = (_animationFrame + 1) % _frames.Length;

            if (connected)
            {
                _animationTimer.Stop();
                SendNextPage(new SetupPage4());
            }
            else if (done)
            {
                _animationTimer.Stop();
                SendNextPage(new SetupPage2ab());

                if (invalidKey)
                {
                    Common.ShowToolTip(
                        "配对码不一致。\r\n请在所有电脑输入相同的配对码，并使用相同版本的"
                        + Application.ProductName + "。\r\n当前版本：" + FrmAbout.AssemblyVersion,
                        20000);
                }
                else
                {
                    string helpText = "连接失败！";
                    helpText += "\r\n请确认电脑连接到同一局域网。" + Application.ProductName + "使用有线连接通常更稳定。";
                    helpText += "\r\n请确认所有电脑的防火墙均允许" + Application.ProductName + "访问局域网。";
                    Common.ShowToolTip(helpText, 30000);
                }
            }
        }
    }
}
