// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Forms;


// <summary>
//     Matrix/Settings form.
// </summary>
// <history>
//     2008 created by Truong Do (ductdo).
//     2009-... modified by Truong Do (TruongDo).
//     2023- Included in PowerToys.
// </history>
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

using Clipboard = MouseWithoutBorders.Core.Clipboard;
using Timer = System.Windows.Forms.Timer;

[module: SuppressMessage("Microsoft.Globalization", "CA1300:SpecifyMessageBoxOptions", Scope = "member", Target = "MouseWithoutBorders.frmMatrix.#buttonOK_Click(System.Object,System.EventArgs)", Justification = "Dotnet port with style preservation")]
[module: SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Scope = "member", Target = "MouseWithoutBorders.frmMatrix.#buttonSendHello_Click(System.Object,System.EventArgs)", Justification = "Dotnet port with style preservation")]
[module: SuppressMessage("Microsoft.Globalization", "CA1304:SpecifyCultureInfo", Scope = "member", Target = "MouseWithoutBorders.frmMatrix.#frmMatrix_Load(System.Object,System.EventArgs)", MessageId = "System.String.ToLower", Justification = "Dotnet port with style preservation")]

// [module: SuppressMessage("Microsoft.Mobility", "CA1601:DoNotUseTimersThatPreventPowerStateChanges", Scope = "member", Target = "MouseWithoutBorders.frmMatrix.#ChangeUI2OneRow(System.Boolean)")]
[module: SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Scope = "member", Target = "MouseWithoutBorders.frmMatrix.#logoTimer_Tick(System.Object,System.EventArgs)", Justification = "Dotnet port with style preservation")]
[module: SuppressMessage("Microsoft.Usage", "CA2213:DisposableFieldsShouldBeDisposed", Scope = "member", Target = "MouseWithoutBorders.frmMatrix.#Dispose(System.Boolean)", MessageId = "logoBitmap", Justification = "Dotnet port with style preservation")]
[module: SuppressMessage("Microsoft.Mobility", "CA1601:DoNotUseTimersThatPreventPowerStateChanges", Scope = "member", Target = "MouseWithoutBorders.frmMatrix.#frmMatrix_Shown(System.Object,System.EventArgs)", Justification = "Dotnet port with style preservation")]
[module: SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Scope = "member", Target = "MouseWithoutBorders.frmMatrix.#PaintMyLogo()", Justification = "Dotnet port with style preservation")]
[module: SuppressMessage("Style", "IDE1006:Naming Styles", Scope = "member", Target = "~M:MouseWithoutBorders.FrmMatrix.M_EnabledChanged(System.Object,System.EventArgs)", Justification = "Dotnet port with style preservation")]

namespace MouseWithoutBorders
{
    internal partial class FrmMatrix : System.Windows.Forms.Form, IDisposable
    {
#pragma warning disable CA2213 // Disposing is done by ComponentResourceManager
        private Timer helperTimer;
#pragma warning restore CA2213
        private bool formShown;
        private int formOrgHeight;
        private bool matrixOneRow;

        internal FrmMatrix()
        {
            InitializeComponent();
            var gamePage = new TabPage("游戏鼠标");
            var automaticGame = new CheckBox { Text = "自动识别游戏锁定鼠标（两端需 .19 或更新）", AutoSize = true, Location = new Point(24, 24), Checked = Setting.Values.AutomaticGameMouse };
            automaticGame.CheckedChanged += (_, _) => { Setting.Values.AutomaticGameMouse = automaticGame.Checked; Setting.Values.SaveSettings(); };
            gamePage.Controls.Add(automaticGame);
            gamePage.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(600, 0), Location = new Point(24, 64), Text =
                "检测到前台程序隐藏并限制光标时，临时使用相对移动、隐藏共享光标并暂停鼠标跨屏。\r\n退出锁定或切出游戏后恢复原来的桌面设置，不改动已保存的相对移动选项。仅游戏相对移动时，请关闭“其他选项”里的相对移动。\r\n\r\n全屏隐藏光标采用启发式识别；若播放器等程序被误判，可关闭此选项。\r\n部分仅接受原始设备输入的游戏仍可能不兼容。\r\n\r\n紧急切回：Ctrl+Alt+F1～F4（默认快捷键，对应电脑排列）。" });
            var gameStatus = new Label { AutoSize = true, Location = new Point(24, 290) };
            gamePage.Controls.Add(gameStatus);
            var gameTimer = new Timer(components) { Interval = 500 };
            gameTimer.Tick += (_, _) =>
            {
                gameStatus.Text = GameMouseMode.Status + "\r\n" + GameMouseMode.RecentRemoteStatus;
                if (automaticGame.Checked != Setting.Values.AutomaticGameMouse) automaticGame.Checked = Setting.Values.AutomaticGameMouse;
            };
            gameTimer.Start();
            tabControlSetting.TabPages.Add(gamePage);
            var audioPage = new TabPage("声音与麦克风");
            var audioButton = new Button { Text = "配置声音与麦克风共享…", AutoSize = true, Location = new System.Drawing.Point(24, 24) };
            audioButton.Click += (_, _) => { using var audioSettings = new Audio.AudioSettingsForm(); DialogWindows.Show(audioSettings, this); };
            audioPage.Controls.Add(audioButton);
            audioPage.Controls.Add(new Label { Text = "独立配置系统声音、麦克风的发送与接收。\r\n复用现有配对关系；首次使用默认关闭。", AutoSize = true, Location = new System.Drawing.Point(24, 72) });
            tabControlSetting.TabPages.Add(audioPage);
            var updatePage = new TabPage("软件更新");
            var updateButton = new Button { Text = "检查更新…", AutoSize = true, Location = new System.Drawing.Point(24, 24) };
            updateButton.Click += (_, _) => { using var updates = new Updates.UpdateForm(); DialogWindows.Show(updates, this); };
            updatePage.Controls.Add(updateButton);
            updatePage.Controls.Add(new Label { Text = "从共享文件夹获取新版，确认后自动下载、替换并重启。\r\n保留配对、电脑记忆和音频设置。", AutoSize = true, Location = new System.Drawing.Point(24, 72) });
            tabControlSetting.TabPages.Add(updatePage);
            var filePage = new TabPage("文件传输");
            var fileButton = new Button { Text = "拖拽传文件…", AutoSize = true, Location = new System.Drawing.Point(24, 24) };
            fileButton.Click += (_, _) => { using var files = new Files.FileTransferForm(); DialogWindows.Show(files, this); };
            filePage.Controls.Add(fileButton);
            filePage.Controls.Add(new Label { Text = "选择已连接电脑，将文件拖入窗口即可发送。\r\n支持多文件、取消及完整性校验，同名自动改名。", AutoSize = true, Location = new System.Drawing.Point(24, 72) });
            tabControlSetting.TabPages.Add(filePage);
            var syncPage = new TabPage("设置同步");
            var syncButton = new Button { Text = "配置配对设置同步…", AutoSize = true, Location = new Point(24, 24) };
            syncButton.Click += (_, _) => { using var sync = new Sync.SyncSettingsForm(); DialogWindows.Show(sync, this); };
            syncPage.Controls.Add(syncButton);
            syncPage.Controls.Add(new Label { Text = "两边都能修改；连接后自动同步通用设置和音频收发关系。\r\n保留各自设备和音量，不传输登录密码或配对码。", AutoSize = true, Location = new Point(24, 72) });
            tabControlSetting.TabPages.Add(syncPage);

            textBoxEnc.Font = new System.Drawing.Font(Control.DefaultFont.Name, 12F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, 0);

            var elevateButton = new Button { Text = ElevationRestart.IsElevated ? "已以管理员身份运行" : "以管理员身份重新启动…", Enabled = !ElevationRestart.IsElevated, AutoSize = true, Location = new Point(24, 148) };
            elevateButton.Click += async (_, _) => { elevateButton.Enabled = false; try { await ElevationRestart.RestartAsync(this); } finally { if (!elevateButton.IsDisposed) elevateButton.Enabled = !ElevationRestart.IsElevated; } };
            updatePage.Controls.Add(elevateButton);
            updatePage.Controls.Add(new Label { Text = "默认以管理员身份启动；由 Windows 请求授权，配对与设置保留。\r\n首次启动取消授权时不运行程序；需在被控制的电脑上更新。\r\n不包含服务模式，不能控制 UAC 安全桌面或登录界面。", AutoSize = true, Location = new Point(24, 196) });

            var autoStart = new CheckBox { Text = "登录 Windows 后自动启动（管理员权限）", AutoSize = true, Location = new Point(24, 268) };
            var startupStatus = new Label { Text = "仅在当前用户登录后启动；取消勾选即可关闭。\r\n启用后请保持程序文件夹位置不变。", AutoSize = true, Location = new Point(24, 304) };
            updatePage.Controls.Add(autoStart); updatePage.Controls.Add(startupStatus);
            bool loadingStartup = true;
            Shown += (_, _) =>
            {
                try
                {
                    var startup = StartupRegistration.Read();
                    autoStart.Checked = startup.Enabled && string.Equals(startup.Executable, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase);
                    if (startup.Enabled && !autoStart.Checked) startupStatus.Text = "旧位置已启用自动启动，勾选可改为当前程序位置。";
                }
                catch (Exception ex) { startupStatus.Text = "读取自动启动状态失败：" + ex.Message; }
                finally { loadingStartup = false; }
            };
            autoStart.CheckedChanged += (_, _) =>
            {
                if (loadingStartup) return;
                bool desired = autoStart.Checked;
                autoStart.Enabled = false;
                try
                {
                    StartupRegistration.SetEnabled(desired);
                    startupStatus.Text = desired ? "已启用：当前用户登录约 5 秒后以管理员权限启动。\r\n启用后请保持程序文件夹位置不变。" : "已关闭登录后自动启动。";
                }
                catch (Exception ex)
                {
                    loadingStartup = true; autoStart.Checked = !desired; loadingStartup = false;
                    MessageBox.Show(this, ex.Message, "自动启动设置未完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                finally { autoStart.Enabled = true; }
            };

            Text = Application.ProductName + " " + Application.ProductVersion + " - 设置";
            toolTip.ToolTipTitle = Application.ProductName;
            toolTipManual.ToolTipTitle = Application.ProductName;
            labelExitMM.Text = "Exit the application, Ctrl+Alt+Shift+:";
            textBoxMachineName2IP.Text = Setting.Values.Name2IP;
        }

        private void ButtonCancel_Click(object sender, EventArgs e)
        {
            buttonCancel.Enabled = false;
            Close();
            Common.MatrixForm = null;
        }

        private void ButtonOK_Click(object sender, EventArgs e)
        {
            buttonOK.Enabled = false;

            if (!UpdateKey(Regex.Replace(textBoxEnc.Text, @"\s+", string.Empty)))
            {
                buttonOK.Enabled = true;
                return;
            }

            string[] st = new string[MachineStuff.MAX_MACHINE];
            for (int i = 0; i < MachineStuff.MAX_MACHINE; i++)
            {
                if (machines[i].MachineEnabled)
                {
                    for (int j = 0; j < i; j++)
                    {
                        if (st[j].Equals(machines[i].MachineName, StringComparison.OrdinalIgnoreCase))
                        {
                            machines[i].MachineName = string.Empty;
                            machines[i].MachineEnabled = false;
                        }
                    }

                    st[i] = machines[i].MachineName;
                }
                else
                {
                    st[i] = string.Empty;
                }
            }

            MachineStuff.MachineMatrix = st;
            Setting.Values.MatrixOneRow = matrixOneRow = !checkBoxTwoRow.Checked;

            if (Process.GetCurrentProcess().SessionId != NativeMethods.WTSGetActiveConsoleSessionId())
            {
                Program.StartService();
                Common.ShowToolTip("新设置已应用到本机桌面。", 3000, ToolTipIcon.Warning, false);
            }
            else
            {
                SocketStuff.InvalidKeyFound = false;
                showInvalidKeyMessage = false;
                InitAndCleanup.ReopenSocketDueToReadError = true;
                Common.ReopenSockets(true);

                for (int i = 0; i < 10; i++)
                {
                    if (Common.AtLeastOneSocketConnected())
                    {
                        Common.MMSleep(0.5);
                        break;
                    }

                    Common.MMSleep(0.2);
                }

                MachineStuff.SendMachineMatrix();
            }

            buttonOK.Enabled = true;
        }

        internal void UpdateKeyTextBox()
        {
            _ = Helper.GetUserName();
            textBoxEnc.Text = Setting.Values.PairingKeyDisplayText;
        }

        private void InitAll()
        {
            formOrgHeight = Height;
            matrixOneRow = Setting.Values.MatrixOneRow;
            CreateMachines();
            LoadSettingsToUI();
            UpdateKeyTextBox();
        }

        private void LoadMachines()
        {
            bool meAdded = false;
            string machineName;

            if (MachineStuff.MachineMatrix != null && MachineStuff.MachineMatrix.Length == MachineStuff.MAX_MACHINE)
            {
                Logger.LogDebug("LoadMachines: Machine Matrix: " + Setting.Values.MachineMatrixString);

                for (int i = 0; i < MachineStuff.MAX_MACHINE; i++)
                {
                    machineName = MachineStuff.MachineMatrix[i].Trim();
                    machines[i].MachineName = machineName;

                    if (string.IsNullOrEmpty(machineName))
                    {
                        machines[i].CheckAble = true;
                    }
                    else
                    {
                        machines[i].MachineEnabled = true;
                    }

                    bool found = MachineStuff.MachinePool.TryFindMachineByName(machineName, out MachineInf machineInfo);
                    if (found)
                    {
                        if (machineInfo.Id == Common.MachineID)
                        {
                            machines[i].LocalHost = true;
                            meAdded = true;
                        }
                    }
                }
            }

            if (!meAdded)
            {
                foreach (Machine m in machines)
                {
                    if (string.IsNullOrEmpty(m.MachineName))
                    {
                        m.MachineName = Common.MachineName.Trim();
                        m.LocalHost = true;
                        meAdded = true;
                        break;
                    }
                }
            }
        }

        private void CheckBoxShowKey_CheckedChanged(object sender, EventArgs e)
        {
            textBoxEnc.PasswordChar = checkBoxShowKey.Checked ? (char)0 : '*';
        }

        private void FrmMatrix_Shown(object sender, EventArgs e)
        {
            if (Setting.Values.FirstRun)
            {
                Setting.Values.FirstRun = false;
                Common.ReopenSockets(false);

                /*
                string fireWallLog = Path.GetDirectoryName(Application.ExecutablePath) + "\\FirewallError.log";

                if (File.Exists(fireWallLog))
                {
                    //@"http://bing.com/search?q=Allow+a+program+through+Windows+Firewall"

                    MessageBox.Show(Application.ProductName + " was unable to add itself to the Firewall exception list.\r\n" +
                        "The following application needs to be added to the Firewall exception list:\r\n\r\n" +
                        Application.ExecutablePath +
                        "\r\n\r\nYou can go to bing.com and do a search on" + "\r\n'Allow a program through Windows Firewall' to know how.",
                        Application.ProductName,
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }

                linkLabelHelp_LinkClicked(null, null);
                 * */
            }

            InitAll();

            if (Setting.Values.IsMyKeyRandom)
            {
                Setting.Values.IsMyKeyRandom = false;
                checkBoxShowKey.Checked = true;
            }

            if (helperTimer == null)
            {
                helperTimer = new Timer();
                helperTimer.Interval = 200;
                helperTimer.Tick += new EventHandler(HelperTimer_Tick);
                helperTimer.Start();
            }

            formShown = true;
        }

        private void FrmMatrix_FormClosed(object sender, FormClosedEventArgs e)
        {
            /*
            if (logoTimer != null)
            {
                logoTimer.Stop();
                logoTimer.Dispose();
                logoTimer = null;
            }
             * */

            if (helperTimer != null)
            {
                helperTimer.Stop();
                helperTimer.Dispose();
                helperTimer = null;
            }

            // if (logoBitmap != null) logoBitmap.Dispose();
            Common.MatrixForm = null;
        }

        private int pivot;
        private Bitmap logoBitmap;

        private void PaintMyLogo()
        {
            if (!Visible || !(tabControlSetting.SelectedTab == tabPageAdvancedSettings))
            {
                return;
            }

            uint rv = 0;
            try
            {
                Color c;
                uint cl;
                double dC;
                logoBitmap ??= new Bitmap(pictureBoxMouseWithoutBorders0.BackgroundImage);
                int bWidth = logoBitmap.Width;
                int bHeight = logoBitmap.Height;
                double dx = (double)pictureBoxMouseWithoutBorders.Width / bWidth;
                double dy = (double)pictureBoxMouseWithoutBorders.Height / bHeight;

                IntPtr hdc = NativeMethods.GetWindowDC(pictureBoxMouseWithoutBorders.Handle);
                for (int i = 0; i < bWidth; i++)
                {
                    for (int j = 0; j < bHeight; j++)
                    {
                        c = logoBitmap.GetPixel(i, j);

                        // c.G > 245
                        if (c.R < 240 && c.B < 240)
                        {
                            dC = Math.Abs(pivot - i);
                            if (bWidth - pivot + i < dC)
                            {
                                dC = bWidth - pivot + i;
                            }

                            if (bWidth - i + pivot < dC)
                            {
                                dC = bWidth - i + pivot;
                            }

                            dC /= bWidth;

                            // c = Color.FromArgb(80, (int)(255 - 255 * dC), 80);
                            cl = (160 << 16) | ((uint)(255 - (255 * dC)) << 8) | 160;

                            // Using GDI SetPixel so we dont have to assign the image later on
                            // b.SetPixel(i, j, c);
                            rv = NativeMethods.SetPixel(hdc, (int)(i * dx), (int)(j * dy), cl);
                        }
                    }
                }

                // Image im = pictureBoxMouseWithoutBorders.BackgroundImage;
                // pictureBoxMouseWithoutBorders.BackgroundImage = b;
                // if (im != null) im.Dispose();
                rv = (uint)NativeMethods.ReleaseDC(pictureBoxMouseWithoutBorders.Handle, hdc);
                pivot = (pivot + 5) % bWidth;
            }
            catch (Exception ee)
            {
                Logger.Log(ee);
                Logger.Log(rv.ToString(CultureInfo.CurrentCulture));
            }
        }

        private void AddNewMachine()
        {
            string newMachine;
            Machine unUsedMachine;

            foreach (MachineInf inf in MachineStuff.MachinePool.ListAllMachines())
            {
                bool found = false;
                unUsedMachine = null;
                newMachine = inf.Name.Trim();
                foreach (Machine m in machines)
                {
                    if (m.MachineName.Equals(
                        newMachine,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                    }
                    else if (unUsedMachine == null && string.IsNullOrEmpty(m.MachineName.Trim()))
                    {
                        unUsedMachine = m;
                    }
                }

                if (!found && unUsedMachine != null)
                {
                    unUsedMachine.MachineName = newMachine;
                }
            }
        }

        private int helperTimerCounter;
        private bool showInvalidKeyMessage;

        private void HelperTimer_Tick(object sender, EventArgs e)
        {
            string keyNotMatchedMachines = string.Empty;

            if (Setting.Values.Changed)
            {
                Setting.Values.Changed = false;
                matrixOneRow = Setting.Values.MatrixOneRow;
                LoadSettingsToUI();

                /*
                if (!Common.InMachineMatrix(Common.MachineName))
                {
                    foreach (Machine m in machines)
                    {
                        if (!m.LocalHost)
                        {
                            m.MachineEnabled = false;
                        }
                    }
                }
                 * */
            }

            helperTimerCounter++;

            // 1 sec
            if (helperTimerCounter % 5 == 0)
            {
                comboBoxEasyMouseOption.Text = UiLanguage.EasyMouseLabel(Setting.Values.EasyMouse);

                if (!textBoxMachineName2IP.Text.Equals(Setting.Values.Name2IP, StringComparison.OrdinalIgnoreCase))
                {
                    Setting.Values.Name2IP = textBoxMachineName2IP.Text;
                }

                // 2 times
                if (helperTimerCounter < 15)
                {
                    Common.SendHello();
                }

                AddNewMachine();
            }

            // NOTE(@yuyoyuppe): this option is deprecated
            // checkBoxVKMap.Checked = Setting.Values.UseVKMap;
            foreach (Machine m in machines)
            {
                if (m.StatusClient != SocketStatus.NA)
                {
                    m.StatusClient = SocketStatus.NA;
                }

                if (m.StatusServer != SocketStatus.NA)
                {
                    m.StatusServer = SocketStatus.NA;
                }
            }

            SocketStuff sk = Common.Sk;

            if (sk != null)
            {
                lock (sk.TcpSocketsLock)
                {
                    if (sk.TcpSockets != null)
                    {
                        foreach (TcpSk t in sk.TcpSockets)
                        {
                            if (t.Status == SocketStatus.InvalidKey)
                            {
                                keyNotMatchedMachines += string.Format(CultureInfo.CurrentCulture, "[{0}]", t.MachineName);
                            }

                            foreach (Machine m in machines)
                            {
                                if (m.MachineEnabled)
                                {
                                    if (m.MachineName.Equals(t.MachineName, StringComparison.OrdinalIgnoreCase))
                                    {
                                        if (t.IsClient)
                                        {
                                            if (t.Status > m.StatusClient)
                                            {
                                                m.StatusClient = t.Status;
                                            }
                                        }
                                        else
                                        {
                                            if (t.Status > m.StatusServer)
                                            {
                                                m.StatusServer = t.Status;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            if (SocketStuff.InvalidKeyFound)
            {
                if (!showInvalidKeyMessage)
                {
                    showInvalidKeyMessage = true;

                    Common.ShowToolTip(
                        "配对码不一致。\r\n请在所有电脑输入相同配对码，并使用相同版本的"
                        + Application.ProductName + "。\r\n" + keyNotMatchedMachines + "\r\n当前版本：" + FrmAbout.AssemblyVersion,
                        20000,
                        ToolTipIcon.Warning,
                        Setting.Values.ShowClipNetStatus);
                }
            }
            else
            {
                showInvalidKeyMessage = false;
            }

            PaintMyLogo();
        }

        private void ShowKeyErrorMsg(string msg)
        {
            Common.ShowToolTip(msg, 10000, ToolTipIcon.Error, false);
            _ = textBoxEnc.Focus();
            textBoxEnc.SelectAll();
        }

        private bool UpdateKey(string newKey)
        {
            if (!PairingKeyEditor.TrySave(Setting.Values, newKey, out string rv))
            {
                ShowKeyErrorMsg(rv);
                return false;
            }
            return true;
        }

        private readonly Machine[] machines = new Machine[MachineStuff.MAX_MACHINE];
        private Machine dragDropMachine;
        private Machine desMachine;
        private Machine desMachineX;
        private Machine desMachineY;
        private Machine oldDesMachine;
        private Point desMachinePos;
        private Point oldDesMachinePos;

        private void CreateMachines()
        {
            for (int i = 0; i < MachineStuff.MAX_MACHINE; i++)
            {
                Machine m = new();
                m.MouseDown += Machine_MouseDown;
                m.EnabledChanged += new EventHandler(M_EnabledChanged);
                m.Parent = groupBoxMachineMatrix;
                m.MachineEnabled = false;
                machines[i] = m;
            }

            FrmMatrix_Resize(this, EventArgs.Empty);
            ArrangeMachines();
        }

        private void ArrangeMachines()
        {
            Height = matrixOneRow ? formOrgHeight : formOrgHeight + 60;
            int dx = (groupBoxMachineMatrix.Width - 40) / 4;
            int yOffset = groupBoxMachineMatrix.Height / 3;

            for (int i = 0; i < MachineStuff.MAX_MACHINE; i++)
            {
                machines[i].Left = matrixOneRow ? 22 + (i * dx) : 22 + dx + ((i % 2) * dx);
                machines[i].Top = matrixOneRow ? yOffset : (yOffset / 2) + (i / 2 * (machines[i].Width + 2));
                machines[i].Visible = true;
            }
        }

        private void M_EnabledChanged(object sender, EventArgs e)
        {
            Machine m = sender as Machine;

            SocketStuff sk = Common.Sk;

            if (!m.MachineEnabled && sk != null)
            {
                lock (sk.TcpSocketsLock)
                {
                    if (sk.TcpSockets != null)
                    {
                        foreach (TcpSk t in sk.TcpSockets)
                        {
                            if (t.MachineName != null && t.MachineName.Equals(m.MachineName.Trim(), StringComparison.OrdinalIgnoreCase))
                            {
                                t.Status = SocketStatus.NA;
                            }
                        }
                    }
                }
            }
        }

        private void Machine_MouseDown(object sender, MouseEventArgs e)
        {
            oldDesMachine = desMachine = dragDropMachine = sender as Machine;
            desMachinePos.X = desMachine.Left;
            desMachinePos.Y = desMachine.Top;
            oldDesMachinePos.X = oldDesMachine.Left;
            oldDesMachinePos.Y = oldDesMachine.Top;

            dragDropMachineOrgX = dragDropMachine.Left;
            dragDropMachineOrgY = dragDropMachine.Top;
            dragDropMachine.BringToFront();
            _ = DoDragDrop(dragDropMachine, DragDropEffects.Move);
        }

        private int startX;
        private int startY;
        private int dragDropMachineOrgX;
        private int dragDropMachineOrgY;

        private void Form_DragEnter(object sender, DragEventArgs e)
        {
            startX = e.X;
            startY = e.Y;
            e.Effect = DragDropEffects.Move;
        }

        private bool IsOnSameRow(Machine m1, Machine m2)
        {
            return matrixOneRow || (m1 == dragDropMachine ? desMachinePos.Y : m1.Top) == m2.Top;
        }

        private bool IsOnSameCol(Machine m1, Machine m2)
        {
            return !matrixOneRow && (m1 == dragDropMachine ? desMachinePos.X : m1.Left) == m2.Left;
        }

        private long lastMove;

        private void Form_DragOver(object sender, DragEventArgs e)
        {
            if (dragDropMachine == null)
            {
                return;
            }

            e.Effect = DragDropEffects.Move;

            dragDropMachine.Left = dragDropMachineOrgX + (e.X - startX);
            dragDropMachine.Top = dragDropMachineOrgY + (e.Y - startY);

            /*
            dragDropMachine.Left = e.X - dragDropMachine.MouseDownPos.X - Left - 3
                - groupBoxMachineMatrix.Left - tabControlSetting.Left;
            dragDropMachine.Top = e.Y - dragDropMachine.MouseDownPos.Y - Top - 25
                - groupBoxMachineMatrix.Top - tabControlSetting.Top;
             * */

            if (!matrixOneRow && Common.GetTick() - lastMove < 500)
            {
                return;
            }

            int minX = Math.Abs(dragDropMachine.Left - desMachinePos.X);
            int minY = Math.Abs(dragDropMachine.Top - desMachinePos.Y);

            desMachineX = desMachineY = desMachine;

            for (int i = 0; i < MachineStuff.MAX_MACHINE; i++)
            {
                if (machines[i] == dragDropMachine)
                {
                    continue;
                }

                if (IsOnSameRow(oldDesMachine, machines[i]))
                {
                    if (minX > Math.Abs(dragDropMachine.Left - machines[i].Left))
                    {
                        minX = Math.Abs(dragDropMachine.Left - machines[i].Left);
                        desMachineX = machines[i];
                    }
                }

                if (IsOnSameCol(oldDesMachine, machines[i]))
                {
                    if (minY > Math.Abs(dragDropMachine.Top - machines[i].Top))
                    {
                        minY = Math.Abs(dragDropMachine.Top - machines[i].Top);
                        desMachineY = machines[i];
                    }
                }
            }

            oldDesMachine = desMachine;
            desMachine = desMachineY == oldDesMachine ? desMachineX : desMachineX == oldDesMachine ? desMachineY : minX < minY ? desMachineX : desMachineY;

            if (desMachine != oldDesMachine)
            {
                oldDesMachinePos.X = desMachinePos.X;
                desMachinePos.X = desMachine.Left;

                oldDesMachinePos.Y = desMachinePos.Y;
                desMachinePos.Y = desMachine.Top;

                desMachine.Left = oldDesMachinePos.X;
                desMachine.Top = oldDesMachinePos.Y;

                desMachine = dragDropMachine;

                lastMove = Common.GetTick();
            }
        }

        private void Form_DragDrop(object sender, DragEventArgs e)
        {
            if (desMachine != null)
            {
                dragDropMachine.Left = desMachinePos.X;
                dragDropMachine.Top = desMachinePos.Y;

                Machine tmp;
                for (int i = 0; i < MachineStuff.MAX_MACHINE - 1; i++)
                {
                    for (int j = 0; j < MachineStuff.MAX_MACHINE - 1 - i; j++)
                    {
                        if (machines[j + 1].Top < machines[j].Top || (machines[j + 1].Top == machines[j].Top && machines[j + 1].Left < machines[j].Left))
                        {
                            tmp = machines[j];
                            machines[j] = machines[j + 1];
                            machines[j + 1] = tmp;
                        }
                    }
                }
            }
        }

        private void FrmMatrix_DragLeave(object sender, EventArgs e)
        {
            Form_DragDrop(sender, null);
            InputSimulation.MouseUp();
        }

        private void LoadSettingsToUI()
        {
            checkBoxCircle.Checked = Setting.Values.MatrixCircle;
            checkBoxTwoRow.Checked = !matrixOneRow;
            checkBoxBlockMouseAtCorners.Checked = Setting.Values.BlockMouseAtCorners;
            checkBoxDrawMouse.Checked = Setting.Values.DrawMouse;
            checkBoxReverseLookup.Checked = Setting.Values.ReverseLookup;
            checkBoxSameSubNet.Checked = Setting.Values.SameSubNetOnly;

            // NOTE(@yuyoyuppe): this option is deprecated
            // checkBoxVKMap.Checked = Setting.Values.UseVKMap;
            foreach (Machine m in machines)
            {
                m.MachineName = string.Empty;
                m.MachineEnabled = false;
                m.LocalHost = false;
            }

            LoadMachines();
        }

        internal static readonly string[] Separator = new string[] { "\r\n" };

        internal void ShowTip(ToolTipIcon icon, string text, int duration)
        {
            int x = 0;
            text += "\r\n ";
            int y = (-text.Split(Separator, StringSplitOptions.None).Length * 15) - 30;

            toolTipManual.Hide(this);

            toolTipManual.ToolTipIcon = icon;
            toolTipManual.Show(text, this, x, y, duration);
        }

        private void LinkLabelHelp_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            linkLabelHelp.Enabled = false;
            linkLabelHelp.Enabled = true;
        }

        private void CheckBoxShareClipboard_CheckedChanged(object sender, EventArgs e)
        {
            Setting.Values.ShareClipboard = checkBoxShareClipboard.Checked;

            checkBoxTransferFile.Enabled = checkBoxTransferFile.Checked = Setting.Values.ShareClipboard;

            ShowUpdateMessage();
        }

        private void CheckBoxTransferFile_CheckedChanged(object sender, EventArgs e)
        {
            Setting.Values.TransferFile = checkBoxTransferFile.Checked;

            ShowUpdateMessage();

            Clipboard.HasSwitchedMachineSinceLastCopy = true;
        }

        private void CheckBoxDisableCAD_CheckedChanged(object sender, EventArgs e)
        {
            if (!Common.RunWithNoAdminRight)
            {
                Helper.ApplyCADSetting();
                ShowUpdateMessage();
            }
        }

        private void FrmMatrix_Load(object sender, EventArgs e)
        {
            if (Common.RunWithNoAdminRight)
            {
                checkBoxDisableCAD.Enabled = false;
                checkBoxHideLogo.Enabled = false;
            }

            // Note(@htcfreek): Disable checkboxes of settings that we don't support in the PowerToys implementation
            checkBoxDisableCAD.Enabled = false;
            checkBoxDisableCAD.Text = checkBoxDisableCAD.Text + "［不支持］";
            checkBoxHideLogo.Enabled = false;
            checkBoxHideLogo.Text = checkBoxHideLogo.Text + "［不支持］";
            checkBoxSendLog.Enabled = false;
            checkBoxSendLog.Text = checkBoxSendLog.Text + "［不支持］";

            checkBoxShareClipboard.Checked = Setting.Values.ShareClipboard;

            if (!Setting.Values.ShareClipboard)
            {
                checkBoxTransferFile.Enabled = checkBoxTransferFile.Checked = false;
            }
            else
            {
                checkBoxTransferFile.Checked = Setting.Values.TransferFile;
            }

            checkBoxDisableCAD.Checked = Setting.Values.DisableCAD;
            checkBoxHideLogo.Checked = Setting.Values.HideLogonLogo;
            checkBoxHideMouse.Checked = Setting.Values.HideMouse;
            checkBoxBlockScreenSaver.Checked = Setting.Values.BlockScreenSaver;
            checkBoxMouseMoveRelatively.Checked = Setting.Values.MoveMouseRelatively;
            checkBoxClipNetStatus.Checked = Setting.Values.ShowClipNetStatus;
            checkBoxSendLog.Checked = Setting.Values.SendErrorLogV2;

            if (Setting.Values.HotKeySwitchMachine == (int)VK.F1)
            {
                radioButtonF1.Checked = true;
            }
            else if (Setting.Values.HotKeySwitchMachine == '1')
            {
                radioButtonNum.Checked = true;
            }
            else
            {
                radioButtonDisable.Checked = true;
            }

            comboBoxShowSettings.Text = "Disable";

            comboBoxExitMM.Text = Setting.Values.HotKeyExitMM == 0 ? "Disable" : new string(new char[] { (char)Setting.Values.HotKeyExitMM });
#if OBSOLETE_SHORTCUTS
            comboBoxLockMachine.Text = Setting.Values.HotKeyLockMachine == 0 ? "Disable" : new string(new char[] { (char)Setting.Values.HotKeyLockMachine });

            comboBoxReconnect.Text = Setting.Values.HotKeyReconnect == 0 ? "Disable" : new string(new char[] { (char)Setting.Values.HotKeyReconnect });

            comboBoxSwitchToAllPC.Text = Setting.Values.HotKeySwitch2AllPC == 1
                ? "Ctrl*3"
                : Setting.Values.HotKeySwitch2AllPC == 0 ? "Disable" : new string(new char[] { (char)Setting.Values.HotKeySwitch2AllPC });

            comboBoxEasyMouseOption.Text = UiLanguage.EasyMouseLabel(Setting.Values.EasyMouse);

            comboBoxEasyMouse.Text = Setting.Values.HotKeyToggleEasyMouse == 0 ? "Disable" : new string(new char[] { (char)Setting.Values.HotKeyToggleEasyMouse });
#endif

            // Apply policy configuration on UI elements
            // Replace obsolete PowerToys shortcut editors with the actual active shortcuts.
            groupBoxShortcuts.Controls.Clear();
            var shortcutSummary = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 90,
                Text = "切换电脑： Ctrl+Alt+F1 / F2 / F3 / F4\r\n" +
                    "重新连接： " + Setting.Values.HotKeyReconnect + "\r\n" +
                    "切换鼠标跨屏： " + Setting.Values.HotKeyToggleEasyMouse + "\r\n" +
                    "锁定电脑： " + Setting.Values.HotKeyLockMachine,
            };
            comboBoxEasyMouseOption.Enabled = true;
            comboBoxEasyMouseOption.DropDownStyle = ComboBoxStyle.DropDownList;
            comboBoxEasyMouseOption.AccessibleName = "鼠标跨屏方式";
            comboBoxEasyMouseOption.Text = UiLanguage.EasyMouseLabel(Setting.Values.EasyMouse);
            comboBoxEasyMouseOption.Dock = DockStyle.Bottom;
            groupBoxShortcuts.Controls.Add(shortcutSummary);
            groupBoxShortcuts.Controls.Add(comboBoxEasyMouseOption);

            // Has to be the last action
            if (Setting.Values.ShareClipboardIsGpoConfigured)
            {
                checkBoxShareClipboard.Enabled = false;
                checkBoxShareClipboard.Text += "［组织管理］";

                // transfer file setting depends on clipboard sharing
                checkBoxTransferFile.Enabled = false;
            }

            if (Setting.Values.TransferFileIsGpoConfigured)
            {
                checkBoxTransferFile.Enabled = false;
                checkBoxTransferFile.Text += "［组织管理］";
            }

            if (Setting.Values.BlockScreenSaverIsGpoConfigured)
            {
                checkBoxBlockScreenSaver.Enabled = false;
                checkBoxBlockScreenSaver.Text += "［组织管理］";
            }

            if (Setting.Values.SameSubNetOnlyIsGpoConfigured)
            {
                checkBoxSameSubNet.Enabled = false;
                checkBoxSameSubNet.Text += "［组织管理］";
            }

            if (Setting.Values.ReverseLookupIsGpoConfigured)
            {
                checkBoxReverseLookup.Enabled = false;
                checkBoxReverseLookup.Text += "［组织管理］";
            }

            if (Setting.Values.Name2IpIsGpoConfigured)
            {
                textBoxMachineName2IP.Enabled = false;
                groupBoxDNS.ForeColor = Color.DimGray;
                groupBoxDNS.Text += "［组织管理］";
            }

            if (Setting.Values.Name2IpPolicyListIsGpoConfigured)
            {
                pictureBoxMouseWithoutBorders.Visible = false;
                groupBoxName2IPPolicyList.Visible = true;
                textBoxMachineName2IPPolicyList.Visible = true;
                textBoxMachineName2IPPolicyList.Text = Setting.Values.Name2IpPolicyList;
            }
        }

        private void RadioButton_CheckedChanged(object sender, EventArgs e)
        {
            RadioButton r = sender as RadioButton;

            if (r.Checked)
            {
                Setting.Values.HotKeySwitchMachine = sender.Equals(radioButtonF1) ? (int)VK.F1 : sender.Equals(radioButtonNum) ? '1' : 0;

                ShowUpdateMessage();
            }
        }

        private void ComboBoxShowSettings_TextChanged(object sender, EventArgs e)
        {
            ShowUpdateMessage();
        }

        private void ComboBoxExitMM_TextChanged(object sender, EventArgs e)
        {
            ShowUpdateMessage();
        }

        private void ComboBoxLockMachine_TextChanged(object sender, EventArgs e)
        {
#if OBSOLETE_SHORTCUTS
            if (comboBoxLockMachine.Text.Contains("Disable"))
            {
                Setting.Values.HotKeyLockMachine = 0;
            }
            else if (comboBoxLockMachine.Text.Length > 0)
            {
                Setting.Values.HotKeyLockMachine = comboBoxLockMachine.Text[0];
            }
#endif
        }

        private void ComboBoxSwitchToAllPC_TextChanged(object sender, EventArgs e)
        {
#if OBSOLETE_SHORTCUTS
            if (comboBoxSwitchToAllPC.Text.Contains("Disable"))
            {
                Setting.Values.HotKeySwitch2AllPC = 0;
            }
            else if (comboBoxSwitchToAllPC.Text.Contains("Ctrl*3"))
            {
                Setting.Values.HotKeySwitch2AllPC = 1;
            }
            else if (comboBoxSwitchToAllPC.Text.Length > 0)
            {
                Setting.Values.HotKeySwitch2AllPC = comboBoxSwitchToAllPC.Text[0];
            }
#endif
            ShowUpdateMessage();
        }

        private void CheckBoxHideLogo_CheckedChanged(object sender, EventArgs e)
        {
            ShowUpdateMessage();
        }

        private void ShowUpdateMessage()
        {
            if (!formShown)
            {
                return;
            }

            foreach (Control c in tabPageOther.Controls)
            {
                if (c != groupBoxShortcuts)
                {
                    c.Enabled = false;
                }
            }

            foreach (Control c in groupBoxShortcuts.Controls)
            {
                if (c != pictureBoxMouseWithoutBorders)
                {
                    c.Enabled = false;
                }
            }

            for (int i = 0; i < 3; i++)
            {
                Application.DoEvents();
                Thread.Sleep(20);
            }

            foreach (Control c in tabPageOther.Controls)
            {
                if (c != groupBoxShortcuts)
                {
                    c.Enabled = true;
                }
            }

            foreach (Control c in groupBoxShortcuts.Controls)
            {
                if (c != pictureBoxMouseWithoutBorders && c != comboBoxExitMM && c != comboBoxShowSettings && c != comboBoxScreenCapture)
                {
                    c.Enabled = true;
                }
            }
            Setting.Values.SaveSettings();
        }

        private void CheckBoxBlockScreenSaver_CheckedChanged(object sender, EventArgs e)
        {
            Setting.Values.BlockScreenSaver = checkBoxBlockScreenSaver.Checked;
            ShowUpdateMessage();
        }

        private void ComboBoxReconnect_TextChanged(object sender, EventArgs e)
        {
#if OBSOLETE_SHORTCUTS
            if (comboBoxReconnect.Text.Contains("Disable"))
            {
                Setting.Values.HotKeyReconnect = 0;
            }
            else if (comboBoxReconnect.Text.Length > 0)
            {
                Setting.Values.HotKeyReconnect = comboBoxReconnect.Text[0];
            }
#endif
            ShowUpdateMessage();
        }

        private void CheckBoxCircle_CheckedChanged(object sender, EventArgs e)
        {
            if (Setting.Values.MatrixCircle != checkBoxCircle.Checked)
            {
                Setting.Values.MatrixCircle = checkBoxCircle.Checked;
                ShowUpdateMessage();
                MachineStuff.SendMachineMatrix();
            }
        }

        private void CheckBoxBlockMouseAtCorners_CheckedChanged(object sender, EventArgs e)
        {
            Setting.Values.BlockMouseAtCorners = checkBoxBlockMouseAtCorners.Checked;
            ShowUpdateMessage();
        }

        private void CheckBoxHideMouse_CheckedChanged(object sender, EventArgs e)
        {
            Setting.Values.HideMouse = checkBoxHideMouse.Checked;
            ShowUpdateMessage();
        }

        private void ComboBoxEasyMouseOption_TextChanged(object sender, EventArgs e)
        {
            string selectedOption = comboBoxEasyMouseOption.Text;
            int oldEasyMouseOption = Setting.Values.EasyMouse;

            Setting.Values.EasyMouse = UiLanguage.ParseEasyMouseLabel(selectedOption);

            if (oldEasyMouseOption != Setting.Values.EasyMouse)
            {
                ShowUpdateMessage();
            }
        }

        private void ComboBoxEasyMouse_TextChanged(object sender, EventArgs e)
        {
#if OBSOLETE_SHORTCUTS
            if (comboBoxEasyMouse.Text.Contains("Disable"))
            {
                Setting.Values.HotKeyToggleEasyMouse = 0;
            }
            else if (comboBoxEasyMouse.Text.Length > 0)
            {
                Setting.Values.HotKeyToggleEasyMouse = comboBoxEasyMouse.Text[0];
            }
#endif
            ShowUpdateMessage();
        }

        private void CheckBoxMouseMoveRelatively_CheckedChanged(object sender, EventArgs e)
        {
            Setting.Values.MoveMouseRelatively = checkBoxMouseMoveRelatively.Checked;
            ShowUpdateMessage();
        }

        private void CheckBoxDrawMouse_CheckedChanged(object sender, EventArgs e)
        {
            if (!(Setting.Values.DrawMouse = checkBoxDrawMouse.Checked))
            {
                CustomCursor.ShowFakeMouseCursor(int.MinValue, int.MinValue);
            }

            ShowUpdateMessage();
        }

        private void CheckBoxTwoRow_CheckedChanged(object sender, EventArgs e)
        {
            matrixOneRow = !checkBoxTwoRow.Checked;
            ArrangeMachines();
        }

        private void ButtonNewKey_Click(object sender, EventArgs e)
        {
            string message = "确定生成新的配对码吗？\r\n" +
                "需要在其他电脑输入这个新配对码，才能重新连接。";

            if (MessageBox.Show(message, Application.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
            {
                var key = Encryption.CreateRandomKey();
                Setting.Values.MyKey = key;
                Encryption.MyKey = key;
                textBoxEnc.Text = Encryption.MyKey;
                checkBoxShowKey.Checked = true;
                Encryption.GeneratedKey = true;
                ButtonOK_Click(null, null);
                Common.ShowToolTip("已生成新配对码，请在其他电脑上更新为相同的配对码。", 10000, ToolTipIcon.Info, false);
            }
        }

        private void CheckBoxClipNetStatus_CheckedChanged(object sender, EventArgs e)
        {
            Setting.Values.ShowClipNetStatus = checkBoxClipNetStatus.Checked;
            ShowUpdateMessage();
        }

        private void CheckBoxSendLog_CheckedChanged(object sender, EventArgs e)
        {
            ShowUpdateMessage();
        }

        private void FrmMatrix_Resize(object sender, EventArgs e)
        {
            if (WindowState != FormWindowState.Minimized)
            {
                groupBoxMachineMatrix.Top = groupBoxKeySetup.Top + groupBoxKeySetup.Height + 10;
                groupBoxMachineMatrix.Height = ClientSize.Height - groupBoxKeySetup.Height - (int)(buttonOK.Height * 3.5);
                checkBoxTwoRow.Top = groupBoxMachineMatrix.Height - (int)(checkBoxTwoRow.Height * 1.4);
                buttonOK.Top = groupBoxMachineMatrix.Bottom + (int)(buttonOK.Height * 0.3);
                buttonCancel.Top = groupBoxMachineMatrix.Bottom + (int)(buttonCancel.Height * 0.3);
                groupBoxShortcuts.Height = ClientSize.Height - groupBoxOtherOptions.Bottom - 40;
                groupBoxDNS.Height = ClientSize.Height - pictureBoxMouseWithoutBorders.Height - textBoxDNS.Height - 70;
            }
        }

        private void CheckBoxReverseLookup_CheckedChanged(object sender, EventArgs e)
        {
            Setting.Values.ReverseLookup = checkBoxReverseLookup.Checked;
            ShowUpdateMessage();
        }

        private void CheckBoxVKMap_CheckedChanged(object sender, EventArgs e)
        {
            // NOTE(@yuyoyuppe): this option is deprecated
            // Setting.Values.UseVKMap = checkBoxVKMap.Checked;
            ShowUpdateMessage();
        }

        private void LinkLabelMiniLog_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            string miniLog = Helper.GetMiniLog(new[] { groupBoxOtherOptions.Controls, groupBoxShortcuts.Controls });

            Clipboard.SetText(miniLog);
            Common.ShowToolTip("日志已复制到剪贴板。", 30000, ToolTipIcon.Info, false);
        }

        private void ComboBoxScreenCapture_TextChanged(object sender, EventArgs e)
        {
            ShowUpdateMessage();
        }

        private void LinkLabelReConfigure_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            string message = "这将清空电脑排列，并重新开始配对向导。\r\n";
            message += "请在所有电脑上重新配对：第一台选择“否”，其他电脑选择“是”。\r\n";
            message += "然后按向导完成设置。\r\n\r\n";
            message += "确定继续吗？";

            if (MessageBox.Show(message, Application.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == System.Windows.Forms.DialogResult.Yes)
            {

                ButtonCancel_Click(this, new EventArgs());
                Setting.Values.FirstRun = true;
                Setting.Values.EasyMouse = (int)EasyMouseOption.Enable;
                MachineStuff.ClearComputerMatrix();
                MachineStuff.ShowSetupForm(true);
            }
        }

        private void CheckBoxSameSubNet_CheckedChanged(object sender, EventArgs e)
        {
            Setting.Values.SameSubNetOnly = checkBoxSameSubNet.Checked;
            ShowUpdateMessage();
        }

#if USE_TO_CREATE_LOGO_BITMAP
        private void PaintMyLogo()
        {
            Graphics g = Graphics.FromHwnd(this.Handle);
            Font font = new Font("Chiller", 40);
            g.DrawString(Common.BinaryName, font, Brushes.Lime, comboBoxIPs.Right + 50, comboBoxIPs.Bottom - 5);

            Bitmap b = new Bitmap(220, 100);
            Graphics g2 = Graphics.FromImage(b);
            g2.FillRectangle(Brushes.WhiteSmoke, 0, 0, 220, 100);
            g2.DrawString(Common.BinaryName, font, Brushes.Lime, 0, 0);
            b.Save("c:\\zzz.bmp");
            string p = "";
            Color c;
            int l = 0;
            for (int i = 0; i < b.Width; i++)
            {
                for (int j = 0; j < b.Height; j++)
                {
                    c = b.GetPixel(i, j);
                    if (c.G > 0)
                    {
                        p += "{" + i + "," + j + "},";
                        l++;
                    }
                }
                p += "\r\n";
            }
            //File.WriteAllText("c:\\zzz.txt", l + ":" + p, Encoding.Unicode);
            b.Dispose();
            g2.Dispose();
        }
#endif
    }
}
