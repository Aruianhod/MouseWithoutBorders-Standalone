// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Windows.Forms;

using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders
{
    public partial class FrmMouseCursor : System.Windows.Forms.Form
    {
        public FrmMouseCursor()
        {
            InitializeComponent();
            Width = Height = 32;
        }

        // A cursor is an overlay, never an input/focus target. Activating it can
        // make the game release its mouse lock before capture detection runs.
        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= 0x08000000 | 0x00000020 | 0x00000080; // NOACTIVATE, TRANSPARENT, TOOLWINDOW
                return parameters;
            }
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0021) // WM_MOUSEACTIVATE (including hover activation)
            {
                message.Result = (IntPtr)3; // MA_NOACTIVATE
                return;
            }
            base.WndProc(ref message);
        }

        private void FrmMouseCursor_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
            }
            else
            {
                Common.MouseCursorForm = null;
            }
        }

        private void FrmMouseCursor_LocationChanged(object sender, EventArgs e)
        {
            if (Setting.Values.DrawMouseEx)
            {
                const int DX = 0, DY = 2;
                IntPtr hDesktop = NativeMethods.GetDesktopWindow();
                IntPtr hdc = NativeMethods.GetWindowDC(hDesktop);

                if (hdc != IntPtr.Zero)
                {
                    for (int i = 0; i < 10; i++)
                    {
                        _ = NativeMethods.SetPixel(hdc, DX + Left + i, DY + Top + i, 0xFFFFFF);
                        _ = NativeMethods.SetPixel(hdc, DX + Left + 1, DY + Top + i, 0xFFFFFF);
                        _ = NativeMethods.SetPixel(hdc, DX + Left + i + 1, DY + Top + i, 0);
                        _ = NativeMethods.SetPixel(hdc, DX + Left, DY + Top + i, 0);
                    }

                    _ = NativeMethods.ReleaseDC(hDesktop, hdc);
                }
            }
        }
    }
}
