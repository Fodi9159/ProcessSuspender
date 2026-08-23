using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class Toast
{
    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_TOPMOST = 0x8;
    private const int WS_EX_NOACTIVATE = 0x8000000;

    public static void Show(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var t = new ToastForm(text);
        t.Show();
    }

    private class ToastForm : Form
    {
        private readonly Timer _timer;
        private double _opacity = 1.0;

        public ToastForm(string text)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;

            Font = new Font("Segoe UI", 12f, FontStyle.Bold);
            Size = TextRenderer.MeasureText(text, Font) + new Size(40, 20);

            Screen screen = Screen.PrimaryScreen;
            int x = (screen.WorkingArea.Width - Width) / 2;
            int y = screen.WorkingArea.Bottom - Height - 50;
            Location = new Point(x, y);

            BackColor = Color.FromArgb(40, 40, 40);
            ForeColor = Color.White;
            Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = RoundedRect(new Rectangle(0, 0, Width, Height), 10))
                {
                    Region = new Region(path);
                }
                TextRenderer.DrawText(e.Graphics, text, Font, new Rectangle(20, 10, Width - 40, Height - 20),
                    ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            };

            _timer = new Timer { Interval = 50 };
            _timer.Tick += (s, e) =>
            {
                _opacity -= 0.04;
                if (_opacity <= 0)
                {
                    _timer.Stop();
                    Close();
                    Dispose();
                }
                else
                {
                    Opacity = _opacity;
                }
            };

            Shown += (s, e) =>
            {
                Opacity = 1.0;
                _timer.Start();
            };
        }

        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(value);
            if (value)
            {
                int ex = GetWindowLong(Handle, GWL_EXSTYLE);
                SetWindowLong(Handle, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE);
            }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
