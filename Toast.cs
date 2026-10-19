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
        try
        {
            // Toasts need a message loop (WinForms Timer). If called from a
            // background/hook thread, marshal creation to the UI thread so the
            // fade timer ticks and the window does not leak.
            if (Application.OpenForms.Count > 0)
            {
                Form owner = Application.OpenForms[0];
                if (owner.InvokeRequired)
                {
                    try { owner.BeginInvoke((Action)delegate { ShowOnUi(text); }); }
                    catch { }
                    return;
                }
            }
            ShowOnUi(text);
        }
        catch { }
    }

    private static void ShowOnUi(string text)
    {
        try
        {
            var t = new ToastForm(text);
            t.Show();
        }
        catch { }
    }

    private class ToastForm : Form
    {
        private readonly Timer _timer;
        private int _ticks;
        private readonly bool _dark;
        private readonly string _text;

        public ToastForm(string text)
        {
            _text = text;
            try { _dark = AppTheme.IsDark(ProcessSuspenderApp.Config); }
            catch { _dark = true; }
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;

            Font font;
            try { font = AppTheme.UiFont(9.5f); }
            catch { font = new Font("Segoe UI", 9.5f); }
            Font = font;

            // Fluent notification card: wrap long text, cap width like Win11.
            int maxW = 420;
            Size proposed = new Size(maxW - 48, 1000);
            Size measured;
            try
            {
                measured = TextRenderer.MeasureText(text, Font, proposed,
                    TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
            }
            catch { measured = TextRenderer.MeasureText(text, Font); }
            int w = Math.Min(maxW, Math.Max(220, measured.Width + 48));
            int h = Math.Max(56, measured.Height + 32);
            // Re-measure at the final width for a tight wrap.
            try
            {
                Size m2 = TextRenderer.MeasureText(text, Font, new Size(w - 48, 1000),
                    TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
                h = Math.Max(56, Math.Min(220, m2.Height + 32));
            }
            catch { }
            Size = new Size(w, h);

            Screen screen = Screen.PrimaryScreen;
            int x = (screen.WorkingArea.Width - Width) / 2;
            int y = screen.WorkingArea.Bottom - Height - 50;
            Location = new Point(x, y);

            BackColor = _dark ? Color.FromArgb(45, 45, 45) : Color.FromArgb(249, 249, 249);
            ForeColor = _dark ? Color.White : Color.FromArgb(27, 27, 27);
            try { AppTheme.ApplyWindowChrome(Handle, _dark); } catch { }
            Paint += OnPaint;

            // Visible ~1.6s, then fade out over ~0.6s.
            _timer = new Timer { Interval = 50 };
            _timer.Tick += OnTick;

            Shown += delegate
            {
                Opacity = 1.0;
                _ticks = 0;
                try
                {
                    using (GraphicsPath path = AppTheme.RoundedRect(new Rectangle(0, 0, Math.Max(1, Width), Math.Max(1, Height)), 8))
                    {
                        if (Region != null) { try { Region.Dispose(); } catch { } }
                        Region = new Region(path);
                    }
                }
                catch { }
                _timer.Start();
            };
        }

        private void OnPaint(object s, PaintEventArgs e)
        {
            try
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                Color fill = BackColor;
                Color border = _dark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(225, 225, 225);
                using (GraphicsPath path = AppTheme.RoundedRect(new Rectangle(0, 0, Width, Height), 8))
                {
                    using (SolidBrush br = new SolidBrush(fill))
                        g.FillPath(br, path);
                    using (Pen pen = new Pen(border, 1f))
                        g.DrawPath(pen, path);
                }
                // Accent bar on the left, like a Win11 InfoBar.
                Color acc = AppTheme.Accent(_dark);
                using (SolidBrush br = new SolidBrush(acc))
                    g.FillRectangle(br, 12, 14, 3, Math.Max(10, Height - 28));
                TextRenderer.DrawText(g, _text, Font,
                    new Rectangle(24, 12, Width - 36, Height - 24),
                    ForeColor, TextFormatFlags.WordBreak | TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            }
            catch { }
        }

        private void OnTick(object s, EventArgs e)
        {
            _ticks++;
            if (_ticks < 32) return; // hold fully visible first
            double op = Opacity - 0.08;
            if (op <= 0)
            {
                _timer.Stop();
                Close();
                Dispose();
            }
            else
            {
                Opacity = op;
            }
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
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE;
                return cp;
            }
        }
    }
}
