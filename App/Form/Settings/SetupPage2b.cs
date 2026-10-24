// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Drawing;
using System.Windows.Forms;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;
using MouseWithoutBorders.Properties;

namespace MouseWithoutBorders
{
    public partial class SetupPage2b : SettingsFormPage
    {
        private readonly TextBox pairingCodeInput;
        private readonly Label pairingCodeStatus;
        private readonly Button savePairingCodeButton;

        public SetupPage2b()
        {
            InitializeComponent();
            SecurityCodeLabel.Visible = false;
            label5.Text = "可自行修改；至少 16 个字符，区分大小写。";
            label6.Location = new Point(61, 199);
            label6.TextAlign = ContentAlignment.MiddleLeft;
            pairingCodeInput = new TextBox
            {
                Name = "PairingCodeInput", AccessibleName = "可修改的配对码",
                Text = GetSecureKey(), Location = new Point(61, 222),
                Size = new Size(222, 30), Font = new Font(Control.DefaultFont.Name, 12F),
                MaxLength = 256, TabIndex = 10,
            };
            savePairingCodeButton = new Button
            {
                Name = "SavePairingCodeButton", Text = "保存配对码",
                Location = new Point(291, 220), Size = new Size(108, 32),
                UseVisualStyleBackColor = true, TabIndex = 11,
            };
            savePairingCodeButton.Click += SavePairingCode;
            pairingCodeStatus = new Label
            {
                Name = "PairingCodeStatus", Text = "修改后点击保存，另一台也要输入相同配对码。",
                Location = new Point(61, 260), Size = new Size(338, 42),
                ForeColor = Color.White, BackColor = Color.Transparent,
            };
            label4.Location = new Point(71, 308);
            MachineNameLabel.Location = new Point(50, 329);
            panel1.Top = 377;
            pictureBox1.Top = 391;
            Controls.Add(pairingCodeInput);
            Controls.Add(savePairingCodeButton);
            Controls.Add(pairingCodeStatus);
            MachineNameLabel.Text = Common.MachineName.Trim();
            BackButtonVisible = true;
            Common.ReopenSockets(true);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // InitializeComponent has already scaled the inherited wizard controls.
            // Position additions using the same design-coordinate scale after load.
            float sx = ClientSize.Width / 453F;
            float sy = ClientSize.Height / 438F;
            void Place(Control control, int x, int y, int width, int height) =>
                control.SetBounds((int)(x * sx), (int)(y * sy), (int)(width * sx), (int)(height * sy));
            Place(label6, 61, 199, 310, 18);
            Place(pairingCodeInput, 61, 222, 222, 30);
            Place(savePairingCodeButton, 291, 220, 108, 32);
            Place(pairingCodeStatus, 61, 260, 338, 42);
            Place(label4, 71, 308, 310, 18);
            Place(MachineNameLabel, 50, 329, 340, 40);
            Place(panel1, 41, 377, 370, 1);
            Place(pictureBox1, 206, 391, 44, 35);
            pairingCodeInput.BringToFront();
            savePairingCodeButton.BringToFront();
        }

        private void SavePairingCode(object sender, EventArgs e)
        {
            try
            {
                if (!PairingKeyEditor.TrySave(Setting.Values, pairingCodeInput.Text, out string error))
                {
                    pairingCodeStatus.Text = error;
                    pairingCodeInput.Focus();
                    return;
                }

                pairingCodeInput.Text = Encryption.MyKey;
                SocketStuff.InvalidKeyFound = false;
                SocketStuff.InvalidKeyFoundOnClientSocket = false;
                Common.ReopenSockets(true);
                pairingCodeStatus.Text = "已保存。请在另一台电脑输入相同配对码。";
            }
            catch (Exception ex)
            {
                // Do not display or log the pairing secret.
                Logger.Log(ex.GetType().Name);
                pairingCodeStatus.Text = "保存或重新连接失败，请检查配置目录权限后重试。";
            }
        }

        protected override SettingsFormPage CreateBackPage()
        {
            return new SetupPage1();
        }
    }
}
