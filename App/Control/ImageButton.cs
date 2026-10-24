// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace MouseWithoutBorders
{
    public partial class ImageButton : PictureBox
    {
        public ImageButton()
        {
            InitializeComponent();
            UpdateEnabledState();
        }

        [Category("Appearance")]
        [Description("Image to show when Mouse is pressed on button")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public Image DownImage { get; set; }

        private Image _normalImage;

        [Category("Appearance")]
        [Description("Image to show when button is in normal state")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public Image NormalImage
        {
            get => _normalImage;
            set
            {
                _normalImage = value;
                UpdateEnabledState();
            }
        }

        [Category("Appearance")]
        [Description("Image to show when Mouse hovers over button")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public Image HoverImage { get; set; }

        [Category("Appearance")]
        [Description("Image to show when button is disabled")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public Image DisabledImage { get; set; }

        private bool _hovering;
        private bool _buttonDown;

        protected override void OnPaint(PaintEventArgs e)
        {
            string caption = UiLanguage.ButtonCaption(Name);
            if (caption == null)
            {
                base.OnPaint(e);
                return;
            }

            AccessibleName = caption;
            AccessibleRole = AccessibleRole.PushButton;
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var bounds = new Rectangle(2, 2, Width - 5, Height - 5);
            using var fill = new SolidBrush(!Enabled ? Color.FromArgb(110, 130, 140) :
                _buttonDown ? Color.FromArgb(0, 90, 140) :
                _hovering ? Color.FromArgb(0, 150, 210) : Color.FromArgb(0, 120, 180));
            using var border = new Pen(Enabled ? Color.White : Color.LightGray, 2);
            e.Graphics.FillEllipse(fill, bounds);
            e.Graphics.DrawEllipse(border, bounds);
            using var font = new Font("Microsoft YaHei UI", 12, FontStyle.Regular);
            TextRenderer.DrawText(e.Graphics, caption, font, ClientRectangle,
                Enabled ? Color.White : Color.LightGray,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            UpdateEnabledState();
            base.OnVisibleChanged(e);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            UpdateEnabledState();
            base.OnEnabledChanged(e);
        }

        protected override void OnLoadCompleted(AsyncCompletedEventArgs e)
        {
            UpdateEnabledState();
            base.OnLoadCompleted(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            _hovering = true;
            if (Enabled)
            {
                if (_buttonDown)
                {
                    if (DownImage != null && Image != DownImage)
                    {
                        Image = DownImage;
                    }
                }
                else
                {
                    Image = HoverImage ?? NormalImage;
                }
            }

            base.OnMouseMove(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            _buttonDown = true;
            if (Enabled)
            {
                _ = Focus();
                if (DownImage != null)
                {
                    Image = DownImage;
                }
            }

            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _buttonDown = false;
            if (Enabled)
            {
                if (_hovering)
                {
                    if (HoverImage != null)
                    {
                        Image = HoverImage;
                    }
                }
                else
                {
                    Image = NormalImage;
                }
            }

            base.OnMouseUp(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hovering = false;
            UpdateEnabledState();
            base.OnMouseLeave(e);
        }

        private void UpdateEnabledState()
        {
            if (Enabled)
            {
                Image = _hovering && HoverImage != null ? HoverImage : NormalImage;
            }
            else
            {
                if (DisabledImage != null)
                {
                    Image = DisabledImage;
                }
            }
        }
    }
}
