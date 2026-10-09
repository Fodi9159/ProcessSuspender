using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

/// <summary>
/// Windows 11 Fluent theming: Mica backdrop, rounded corners, card layout,
/// Segoe UI Variable type, accent primary buttons and toggle switches.
/// Falls back gracefully on Windows 10 (DWM attributes are best-effort).
/// Effective mode is "System" (follow Windows), "Light" or "Dark".
/// </summary>
public static class AppTheme
{
    // ---- Win11 page / card palette ----
    private static readonly Color LightPage = Color.FromArgb(243, 243, 243);
    private static readonly Color LightCard = Color.FromArgb(255, 255, 255);
    private static readonly Color LightCardBorder = Color.FromArgb(225, 225, 225);
    private static readonly Color LightInputBack = Color.FromArgb(255, 255, 255);
    private static readonly Color LightInputBorder = Color.FromArgb(210, 210, 210);
    private static readonly Color LightFore = Color.FromArgb(27, 27, 27);
    private static readonly Color LightSecondary = Color.FromArgb(97, 97, 97);
    private static readonly Color LightHover = Color.FromArgb(245, 245, 245);
    private static readonly Color LightPressed = Color.FromArgb(234, 234, 234);

    private static readonly Color DarkPage = Color.FromArgb(32, 32, 32);
    private static readonly Color DarkCard = Color.FromArgb(45, 45, 45);
    private static readonly Color DarkCardBorder = Color.FromArgb(62, 62, 62);
    private static readonly Color DarkInputBack = Color.FromArgb(45, 45, 45);
    private static readonly Color DarkInputBorder = Color.FromArgb(80, 80, 80);
    private static readonly Color DarkFore = Color.FromArgb(255, 255, 255);
    private static readonly Color DarkSecondary = Color.FromArgb(199, 199, 199);
    private static readonly Color DarkHover = Color.FromArgb(62, 62, 62);
    private static readonly Color DarkPressed = Color.FromArgb(78, 78, 78);

    private static readonly Color LightMenuBack = Color.FromArgb(249, 249, 249);
    private static readonly Color DarkMenuBack = Color.FromArgb(45, 45, 45);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_BORDER_COLOR = 34;

    private const int CORNER_ROUND = 2;
    private const int BACKDROP_MICA = 2;

    /// <summary>Canonicalizes the theme string ("System", "Light" or "Dark").</summary>
    public static string NormalizeTheme(string theme)
    {
        if (string.IsNullOrWhiteSpace(theme)) return "System";
        string t = theme.Trim().ToLowerInvariant();
        if (t == "dark" || t == "dunkel") return "Dark";
        if (t == "light" || t == "white" || t == "hell" || t == "lite") return "Light";
        return "System";
    }

    /// <summary>True when Windows apps are in dark mode (HKCU Personalize key).</summary>
    public static bool IsWindowsDark()
    {
        // Cached for 2s: called per nav-button paint otherwise (Registry I/O per
        // paint is orders of magnitude more expensive than the brush it draws).
        if ((DateTime.UtcNow - _winDarkAt).TotalSeconds < 2) return _winDark;
        _winDarkAt = DateTime.UtcNow;
        try
        {
            object v = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1);
            if (v == null) { _winDark = false; return false; }
            try { _winDark = Convert.ToInt32(v) == 0; }
            catch { _winDark = false; }
            return _winDark;
        }
        catch { _winDark = false; return false; }
    }

    private static bool _winDark;
    private static DateTime _winDarkAt = DateTime.MinValue;

    public static bool IsDark(Settings s)
    {
        if (s == null) return IsWindowsDark();
        return IsDark(s.Theme);
    }

    public static bool IsDark(string theme)
    {
        string t = NormalizeTheme(theme);
        if (t == "Dark") return true;
        if (t == "Light") return false;
        return IsWindowsDark();
    }

    public static Color PageBack(bool dark) { return dark ? DarkPage : LightPage; }
    public static Color CardBack(bool dark) { return dark ? DarkCard : LightCard; }
    public static Color CardBorder(bool dark) { return dark ? DarkCardBorder : LightCardBorder; }
    public static Color Fore(bool dark) { return dark ? DarkFore : LightFore; }
    public static Color SecondaryFore(bool dark) { return dark ? DarkSecondary : LightSecondary; }
    public static Color InputBack(bool dark) { return dark ? DarkInputBack : LightInputBack; }
    public static Color InputBorder(bool dark) { return dark ? DarkInputBorder : LightInputBorder; }
    public static Color HoverBack(bool dark) { return dark ? DarkHover : LightHover; }

    /// <summary>System accent color, or Fluent blue fallback.</summary>
    public static Color Accent(bool dark)
    {
        Color sys = GetSystemAccent();
        if (sys.A != 0) return sys;
        return dark ? Color.FromArgb(76, 194, 255) : Color.FromArgb(0, 103, 192);
    }

    public static Color AccentFill(bool dark)
    {
        // Button fill keeps white text readable in both modes.
        Color sys = GetSystemAccent();
        if (sys.A != 0 && !dark) return sys;
        if (sys.A != 0 && dark)
        {
            // Darken a light system accent so white text stays legible.
            int r = (int)(sys.R * 0.75);
            int g = (int)(sys.G * 0.75);
            int b = (int)(sys.B * 0.75);
            if (r + g + b < 120) return Color.FromArgb(0, 120, 212);
            return Color.FromArgb(r, g, b);
        }
        return dark ? Color.FromArgb(0, 120, 212) : Color.FromArgb(0, 103, 192);
    }

    private static Color _accentCache = Color.Empty;
    private static bool _accentRead;

    private static Color GetSystemAccent()
    {
        if (_accentRead) return _accentCache;
        _accentRead = true;
        try
        {
            object v = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM",
                "AccentColor", null);
            if (v != null)
            {
                int dw = Convert.ToInt32(v);
                int r = dw & 0xFF;
                int g = (dw >> 8) & 0xFF;
                int b = (dw >> 16) & 0xFF;
                _accentCache = Color.FromArgb(r, g, b);
                return _accentCache;
            }
        }
        catch { }
        return Color.Empty;
    }

    /// <summary>Preferred UI font: Segoe UI Variable (Win11) with Segoe UI fallback.</summary>
    public static Font UiFont(float size, FontStyle style)
    {
        // Cached per size+style: ApplyControl re-themes every control on each
        // theme apply, and each new Font is a GDI object that was never disposed.
        string key = size.ToString("F1") + "|" + ((int)style).ToString();
        Font cached;
        lock (FontCache)
        {
            if (FontCache.TryGetValue(key, out cached)) return cached;
        }
        string[] prefs = new string[] { "Segoe UI Variable Text", "Segoe UI Variable", "Segoe UI" };
        foreach (string name in prefs)
        {
            try
            {
                Font f = new Font(name, size, style, GraphicsUnit.Point);
                lock (FontCache) FontCache[key] = f;
                return f;
            }
            catch { }
        }
        Font fb = new Font(SystemFonts.MessageBoxFont.FontFamily, size, style);
        lock (FontCache) FontCache[key] = fb;
        return fb;
    }

    private static readonly System.Collections.Generic.Dictionary<string, Font> FontCache =
        new System.Collections.Generic.Dictionary<string, Font>();

    public static Font UiFont(float size)
    {
        return UiFont(size, FontStyle.Regular);
    }

    public static void UseDarkTitleBar(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            int value = dark ? 1 : 0;
            int r = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
            if (r != 0)
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref value, sizeof(int));
        }
        catch { }
    }

    /// <summary>Rounded corners + Mica backdrop + matching caption. No-op on Win10.</summary>
    public static void ApplyWindowChrome(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            UseDarkTitleBar(hwnd, dark);
            int corner = CORNER_ROUND;
            try { DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int)); }
            catch { }
            int backdrop = BACKDROP_MICA;
            try { DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)); }
            catch { }
            try
            {
                Color page = PageBack(dark);
                int caption = page.R | (page.G << 8) | (page.B << 16);
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
            }
            catch { }
            try
            {
                Color bd = CardBorder(dark);
                int border = bd.R | (bd.G << 8) | (bd.B << 16);
                DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
            }
            catch { }
        }
        catch { }
    }

    public static void ApplyWindowChrome(Form form, bool dark)
    {
        if (form == null) return;
        try
        {
            if (form.IsHandleCreated)
                ApplyWindowChrome(form.Handle, dark);
            else
            {
                form.HandleCreated += delegate(object s, EventArgs e)
                {
                    try
                    {
                        Form f = s as Form;
                        if (f == null) return;
                        bool d = IsDark(ProcessSuspenderApp.Config);
                        ApplyWindowChrome(f.Handle, d);
                    }
                    catch { }
                };
            }
        }
        catch { }
    }

    public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        int r = Math.Max(1, radius);
        int d = r * 2;
        if (bounds.Width < d) d = bounds.Width;
        if (bounds.Height < d) d = bounds.Height;
        r = d / 2;
        d = r * 2;
        GraphicsPath path = new GraphicsPath();
        try
        {
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
        }
        catch { }
        return path;
    }

    public static void SetRoundedRegion(Control c, int radius)
    {
        if (c == null) return;
        try
        {
            if (c.Width <= 0 || c.Height <= 0) return;
            using (GraphicsPath p = RoundedRect(new Rectangle(0, 0, c.Width, c.Height), radius))
            {
                try
                {
                    if (c.Region != null) { try { c.Region.Dispose(); } catch { } }
                    c.Region = new Region(p);
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>Styles a button Fluent-style. Tag == "primary" selects the accent fill.</summary>
    public static void StyleButton(Button b, bool dark, bool primary)
    {
        if (b == null) return;
        try
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 1;
            b.UseVisualStyleBackColor = false;
            try { b.Font = UiFont(9f); } catch { }
            b.Cursor = Cursors.Hand;
            if (primary)
            {
                Color acc = AccentFill(dark);
                b.BackColor = acc;
                b.ForeColor = Color.White;
                b.FlatAppearance.BorderColor = acc;
                b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(acc, 0.12f);
                b.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(acc, 0.12f);
                b.FlatAppearance.CheckedBackColor = acc;
            }
            else
            {
                b.BackColor = dark ? DarkCard : Color.FromArgb(253, 253, 253);
                b.ForeColor = Fore(dark);
                b.FlatAppearance.BorderColor = CardBorder(dark);
                b.FlatAppearance.MouseOverBackColor = HoverBack(dark);
                b.FlatAppearance.MouseDownBackColor = dark ? DarkPressed : LightPressed;
                b.FlatAppearance.CheckedBackColor = HoverBack(dark);
            }
            SetRoundedRegion(b, 4);
            try
            {
                // ApplyControl re-runs on every theme switch: hook resize once.
                if (!RoundedHooked.Contains(b))
                {
                    RoundedHooked.Add(b);
                    b.Resize += delegate(object s, EventArgs e)
                    {
                        try { SetRoundedRegion((Control)s, 4); } catch { }
                    };
                    Button hookRefX = b; hookRefX.Disposed += delegate(object s2, EventArgs e2) { try { RoundedHooked.Remove(hookRefX); } catch { } };
                }
            }
            catch { }
        }
        catch { }
    }

    private static readonly System.Collections.Generic.HashSet<Control> RoundedHooked =
        new System.Collections.Generic.HashSet<Control>();
    private static readonly System.Collections.Generic.HashSet<Control> NavPaintHooked =
        new System.Collections.Generic.HashSet<Control>();

    /// <summary>Left-navigation item: transparent until hovered/selected, accent bar when selected.</summary>
    public static void StyleNavButton(Button b, bool dark, bool selected)
    {
        if (b == null) return;
        try
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.UseVisualStyleBackColor = false;
            b.TextAlign = ContentAlignment.MiddleLeft;
            try { b.Font = UiFont(9.5f); } catch { }
            b.Cursor = Cursors.Hand;
            b.BackColor = selected ? (dark ? DarkHover : Color.FromArgb(234, 234, 234)) : Color.Transparent;
            b.ForeColor = Fore(dark);
            b.FlatAppearance.MouseOverBackColor = HoverBack(dark);
            b.FlatAppearance.MouseDownBackColor = dark ? DarkPressed : LightPressed;
            b.FlatAppearance.CheckedBackColor = b.BackColor;
            b.Padding = new Padding(14, 0, 8, 0);
            SetRoundedRegion(b, 4);
            try
            {
                if (!RoundedHooked.Contains(b))
                {
                    RoundedHooked.Add(b);
                    b.Resize += delegate(object s, EventArgs e)
                    {
                        try { SetRoundedRegion((Control)s, 4); } catch { }
                    };
                    Button hookRefX = b; hookRefX.Disposed += delegate(object s2, EventArgs e2) { try { RoundedHooked.Remove(hookRefX); } catch { } };
                }
                if (!NavPaintHooked.Contains(b))
                {
                    NavPaintHooked.Add(b);
                    // Accent selection bar painted on top of the button.
                    // Theme is read live so re-theming never leaves stale colors.
                    b.Paint += delegate(object s, PaintEventArgs e)
                    {
                        try
                        {
                            Button bb = s as Button;
                            if (bb == null) return;
                            object tag = bb.Tag;
                            bool sel = false;
                            if (tag is string)
                            {
                                string ts = (string)tag;
                                sel = ts.StartsWith("nav-selected", StringComparison.Ordinal);
                            }
                            if (!sel) return;
                            bool d = false;
                            try { d = AppTheme.IsDark(ProcessSuspenderApp.Config); } catch { }
                            Color acc = Accent(d);
                            using (SolidBrush br = new SolidBrush(acc))
                            {
                                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                                e.Graphics.FillRectangle(br, 3, 8, 3, Math.Max(8, bb.Height - 16));
                            }
                        }
                        catch { }
                    };
                    Button hookRefN = b; hookRefN.Disposed += delegate(object s2, EventArgs e2) { try { NavPaintHooked.Remove(hookRefN); } catch { } };
                }
            }
            catch { }
        }
        catch { }
    }

    public static void ApplyForm(Form form, bool dark)
    {
        if (form == null) return;
        try
        {
            form.BackColor = PageBack(dark);
            form.ForeColor = Fore(dark);
            try { form.Font = UiFont(9f); } catch { }
            ApplyWindowChrome(form, dark);
            foreach (Control c in form.Controls)
                ApplyControl(c, dark);
        }
        catch { }
    }

    private static bool IsHintColor(Color c)
    {
        if (c == Color.Gray) return true;
        if (c == SystemColors.GrayText) return true;
        if (c == SystemColors.ControlDark) return true;
        return false;
    }

    public static void ApplyControl(Control c, bool dark)
    {
        if (c == null) return;
        try
        {
            Color page = PageBack(dark);
            Color card = CardBack(dark);
            Color cardBorder = CardBorder(dark);
            Color fore = Fore(dark);
            Color secondary = SecondaryFore(dark);
            Color inputBack = InputBack(dark);

            if (c is FluentCard)
            {
                FluentCard fc = (FluentCard)c;
                fc.ApplyTheme(dark);
                foreach (Control child in c.Controls)
                    ApplyControl(child, dark);
                return;
            }
            if (c is FluentToggle)
            {
                FluentToggle tg = (FluentToggle)c;
                tg.ApplyTheme(dark);
                return;
            }
            if (c is ToolStrip)
            {
                ApplyMenu((ToolStrip)c, dark);
                return;
            }
            if (c is Button)
            {
                Button b = (Button)c;
                bool primary = b.Tag != null && b.Tag.ToString() == "primary";
                string tag = b.Tag != null ? b.Tag.ToString() : "";
                if (tag.StartsWith("nav", StringComparison.Ordinal))
                {
                    bool sel = tag.StartsWith("nav-selected", StringComparison.Ordinal);
                    StyleNavButton(b, dark, sel);
                }
                else
                {
                    StyleButton(b, dark, primary);
                }
                return;
            }
            if (c is HotkeyBox)
            {
                HotkeyBox hb = (HotkeyBox)c;
                bool empty = string.IsNullOrEmpty(hb.Combo);
                c.BackColor = inputBack;
                c.ForeColor = empty ? secondary : fore;
                try
                {
                    hb.BorderStyle = BorderStyle.FixedSingle;
                    hb.Font = UiFont(9f);
                }
                catch { }
            }
            else if (c is TextBox)
            {
                TextBox tb = (TextBox)c;
                if (tb.ReadOnly && tb.Multiline)
                {
                    c.BackColor = card;
                    c.ForeColor = fore;
                }
                else
                {
                    c.BackColor = inputBack;
                    c.ForeColor = fore;
                }
                try
                {
                    tb.BorderStyle = BorderStyle.FixedSingle;
                    tb.Font = UiFont(9f);
                }
                catch { }
            }
            else if (c is ListBox)
            {
                c.BackColor = card;
                c.ForeColor = fore;
                try
                {
                    c.Font = UiFont(9f);
                    ((ListBox)c).BorderStyle = BorderStyle.FixedSingle;
                }
                catch { }
            }
            else if (c is ComboBox)
            {
                ComboBox cb = (ComboBox)c;
                cb.BackColor = inputBack;
                cb.ForeColor = fore;
                try { cb.Font = UiFont(9f); } catch { }
                try { cb.FlatStyle = FlatStyle.Flat; } catch { }
            }
            else if (c is NumericUpDown)
            {
                c.BackColor = inputBack;
                c.ForeColor = fore;
                try { c.Font = UiFont(9f); } catch { }
                try { ((NumericUpDown)c).BorderStyle = BorderStyle.FixedSingle; } catch { }
            }
            else if (c is CheckBox)
            {
                CheckBox ch = (CheckBox)c;
                ch.BackColor = Color.Transparent;
                ch.ForeColor = fore;
                try { ch.Font = UiFont(9f); } catch { }
                try { ch.UseVisualStyleBackColor = false; } catch { }
            }
            else if (c is RadioButton)
            {
                RadioButton rb = (RadioButton)c;
                rb.BackColor = Color.Transparent;
                rb.ForeColor = fore;
                try { rb.Font = UiFont(9f); } catch { }
                try { rb.UseVisualStyleBackColor = false; } catch { }
            }
            else if (c is Label)
            {
                bool isHint = IsHintColor(c.ForeColor);
                if (c.BackColor != Color.Transparent)
                    c.BackColor = Color.Transparent;
                object ltag = c.Tag;
                if (ltag != null && ltag.ToString() == "card-title")
                {
                    c.ForeColor = fore;
                    try { c.Font = UiFont(10f, FontStyle.Bold); } catch { }
                }
                else if (ltag != null && ltag.ToString() == "page-title")
                {
                    c.ForeColor = fore;
                    try { c.Font = UiFont(16f, FontStyle.Bold); } catch { }
                }
                else if (ltag != null && ltag.ToString() == "page-subtitle")
                {
                    c.ForeColor = secondary;
                    try { c.Font = UiFont(9f); } catch { }
                }
                else
                {
                    c.ForeColor = isHint ? secondary : fore;
                    try { c.Font = UiFont(9f); } catch { }
                }
            }
            else if (c is GroupBox)
            {
                // Legacy containers render as Fluent cards.
                c.BackColor = card;
                c.ForeColor = fore;
                try { c.Font = UiFont(10f, FontStyle.Bold); } catch { }
            }
            else if (c is TabControl)
            {
                c.BackColor = page;
                c.ForeColor = fore;
                try { c.Font = UiFont(9f); } catch { }
            }
            else if (c is TabPage)
            {
                TabPage tp = (TabPage)c;
                try { tp.UseVisualStyleBackColor = false; } catch { }
                tp.BackColor = page;
                tp.ForeColor = fore;
            }
            else if (c is Panel)
            {
                if (c.BackColor == SystemColors.Control || c.BackColor == Color.Empty)
                    c.BackColor = page;
                // Leave explicitly card/nav colored panels alone.
                if (c.ForeColor == SystemColors.ControlText)
                    c.ForeColor = fore;
            }
            else if (c.HasChildren)
            {
                c.BackColor = page;
                c.ForeColor = fore;
            }

            foreach (Control child in c.Controls)
                ApplyControl(child, dark);
        }
        catch { }
    }

    private static readonly DarkMenuRenderer CachedDarkRenderer = new DarkMenuRenderer();
    private static readonly LightMenuRenderer CachedLightRenderer = new LightMenuRenderer();

    public static void ApplyMenu(ToolStrip menu, bool dark)
    {
        if (menu == null) return;
        try
        {
            if (dark)
            {
                menu.Renderer = CachedDarkRenderer;
                menu.BackColor = DarkMenuBack;
                menu.ForeColor = DarkFore;
            }
            else
            {
                menu.Renderer = CachedLightRenderer;
                menu.BackColor = LightMenuBack;
                menu.ForeColor = LightFore;
            }
            try { menu.Font = UiFont(9f); } catch { }
            try { menu.Padding = new Padding(4); } catch { }
            try
            {
                ContextMenuStrip cms = menu as ContextMenuStrip;
                if (cms != null)
                {
                    cms.ShowImageMargin = false;
                    cms.ShowCheckMargin = true;
                }
            }
            catch { }
            foreach (ToolStripItem item in menu.Items)
                ApplyMenuItem(item, dark);
        }
        catch { }
    }

    private static void ApplyMenuItem(ToolStripItem item, bool dark)
    {
        if (item == null) return;
        try
        {
            item.BackColor = dark ? DarkMenuBack : LightMenuBack;
            item.ForeColor = dark ? DarkFore : LightFore;
            try { item.Font = UiFont(9f); } catch { }
            try { item.Padding = new Padding(8, 6, 8, 6); } catch { }
            ToolStripMenuItem mi = item as ToolStripMenuItem;
            if (mi != null)
            {
                if (mi.DropDown != null)
                {
                    try
                    {
                        mi.DropDown.BackColor = dark ? DarkMenuBack : LightMenuBack;
                        mi.DropDown.ForeColor = dark ? DarkFore : LightFore;
                    }
                    catch { }
                    foreach (ToolStripItem sub in mi.DropDownItems)
                        ApplyMenuItem(sub, dark);
                }
            }
        }
        catch { }
    }

    private sealed class FluentColorTable : ProfessionalColorTable
    {
        private readonly bool _dark;
        public FluentColorTable(bool dark) { _dark = dark; }

        private Color Sel { get { return _dark ? Color.FromArgb(62, 62, 62) : Color.FromArgb(234, 234, 234); } }
        private Color Bd { get { return _dark ? Color.FromArgb(80, 80, 80) : Color.FromArgb(210, 210, 210); } }
        private Color Bg { get { return _dark ? Color.FromArgb(45, 45, 45) : Color.FromArgb(249, 249, 249); } }

        public override Color ButtonSelectedHighlight { get { return Sel; } }
        public override Color ButtonSelectedHighlightBorder { get { return Bd; } }
        public override Color ButtonPressedHighlight { get { return Sel; } }
        public override Color ButtonPressedHighlightBorder { get { return Bd; } }
        public override Color ButtonCheckedHighlight { get { return Sel; } }
        public override Color ButtonCheckedHighlightBorder { get { return Bd; } }
        public override Color ButtonPressedBorder { get { return Bd; } }
        public override Color ButtonSelectedBorder { get { return Bd; } }
        public override Color MenuItemBorder { get { return Bd; } }
        public override Color MenuBorder { get { return Bd; } }
        public override Color MenuItemSelected { get { return Sel; } }
        public override Color MenuItemSelectedGradientBegin { get { return Sel; } }
        public override Color MenuItemSelectedGradientEnd { get { return Sel; } }
        public override Color MenuItemPressedGradientBegin { get { return Sel; } }
        public override Color MenuItemPressedGradientEnd { get { return Sel; } }
        public override Color MenuItemPressedGradientMiddle { get { return Sel; } }
        public override Color ToolStripDropDownBackground { get { return Bg; } }
        public override Color MenuStripGradientBegin { get { return Bg; } }
        public override Color MenuStripGradientEnd { get { return Bg; } }
        public override Color ToolStripGradientBegin { get { return Bg; } }
        public override Color ToolStripGradientMiddle { get { return Bg; } }
        public override Color ToolStripGradientEnd { get { return Bg; } }
        public override Color ImageMarginGradientBegin { get { return Bg; } }
        public override Color ImageMarginGradientMiddle { get { return Bg; } }
        public override Color ImageMarginGradientEnd { get { return Bg; } }
        public override Color SeparatorDark { get { return Bd; } }
        public override Color SeparatorLight { get { return Bg; } }
        public override Color GripDark { get { return Bd; } }
        public override Color GripLight { get { return Bg; } }
        public override Color StatusStripGradientBegin { get { return Bg; } }
        public override Color StatusStripGradientEnd { get { return Bg; } }
        public override Color OverflowButtonGradientBegin { get { return Bg; } }
        public override Color OverflowButtonGradientEnd { get { return Bg; } }
        public override Color OverflowButtonGradientMiddle { get { return Bg; } }
        public override Color RaftingContainerGradientBegin { get { return Bg; } }
        public override Color RaftingContainerGradientEnd { get { return Bg; } }
    }

    private sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new FluentColorTable(true)) { }
    }

    private sealed class LightMenuRenderer : ToolStripProfessionalRenderer
    {
        public LightMenuRenderer() : base(new FluentColorTable(false)) { }
    }
}

/// <summary>
/// Win11 Settings-style card: rounded fill with a 1px border and content padding.
/// </summary>
public class FluentCard : Panel
{
    private bool _dark;

    public FluentCard()
    {
        try
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Padding = new Padding(16, 12, 16, 12);
            Margin = new Padding(0, 0, 0, 12);
        }
        catch { }
        try { _dark = AppTheme.IsDark(ProcessSuspenderApp.Config); } catch { }
    }

    public void ApplyTheme(bool dark)
    {
        _dark = dark;
        try { Invalidate(); } catch { }
        // Children are themed by AppTheme.ApplyControl recursion.
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Suppressed: background is painted rounded in OnPaint.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        try
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : AppTheme.PageBack(_dark));
            Color fill = AppTheme.CardBack(_dark);
            Color border = AppTheme.CardBorder(_dark);
            Rectangle bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using (GraphicsPath path = AppTheme.RoundedRect(new Rectangle(0, 0, Math.Max(1, Width), Math.Max(1, Height)), 8))
            {
                using (SolidBrush br = new SolidBrush(fill))
                    g.FillPath(br, path);
                using (Pen pen = new Pen(border, 1f))
                    g.DrawPath(pen, path);
            }
            // Dark cards get a subtle top highlight like Win11.
            if (_dark)
            {
                try
                {
                    using (Pen hi = new Pen(Color.FromArgb(70, 70, 70), 1f))
                        g.DrawLine(hi, 10, 1, Math.Max(11, Width - 10), 1);
                }
                catch { }
            }
        }
        catch
        {
            try { base.OnPaintBackground(e); } catch { }
        }
    }
}

/// <summary>
/// Win11 toggle switch that stays API-compatible with CheckBox (Checked/Text).
/// Label is drawn to the right of the pill so existing layouts keep working.
/// </summary>
public class FluentToggle : CheckBox
{
    private bool _dark;
    private bool _hover;

    public FluentToggle()
    {
        try
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer, true);
            AutoSize = false;
            Height = 24;
            Width = 220;
            Cursor = Cursors.Hand;
        }
        catch { }
        try { _dark = AppTheme.IsDark(ProcessSuspenderApp.Config); } catch { }
        try
        {
            MouseEnter += delegate { _hover = true; try { Invalidate(); } catch { } };
            MouseLeave += delegate { _hover = false; try { Invalidate(); } catch { } };
        }
        catch { }
    }

    public void ApplyTheme(bool dark)
    {
        _dark = dark;
        try { Invalidate(); } catch { }
    }

    protected override void OnCheckedChanged(EventArgs e)
    {
        base.OnCheckedChanged(e);
        try { Invalidate(); } catch { }
    }

    protected override void OnClick(EventArgs e)
    {
        // Let the base toggle Checked; repaint is handled above.
        base.OnClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        try
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            Color page = Parent != null ? Parent.BackColor : AppTheme.PageBack(_dark);
            g.Clear(page);

            int pillW = 40;
            int pillH = 20;
            int pillY = (Height - pillH) / 2;
            Rectangle pill = new Rectangle(2, pillY, pillW, pillH);

            Color track;
            Color trackBorder;
            if (Checked)
            {
                Color acc = AppTheme.AccentFill(_dark);
                track = _hover ? ControlPaint.Light(acc, 0.1f) : acc;
                trackBorder = track;
            }
            else
            {
                track = _dark ? Color.FromArgb(110, 110, 110) : Color.FromArgb(255, 255, 255);
                if (!_dark && _hover) track = Color.FromArgb(245, 245, 245);
                if (_dark && _hover) track = Color.FromArgb(130, 130, 130);
                trackBorder = _dark ? Color.FromArgb(153, 153, 153) : Color.FromArgb(138, 138, 138);
            }

            using (GraphicsPath path = AppTheme.RoundedRect(pill, pillH / 2))
            {
                using (SolidBrush br = new SolidBrush(track))
                    g.FillPath(br, path);
                using (Pen pen = new Pen(trackBorder, 1f))
                    g.DrawPath(pen, path);
            }

            int knobD = 12;
            int knobY = pillY + (pillH - knobD) / 2;
            int knobX = Checked ? pill.Right - knobD - 4 : pill.X + 4;
            Color knob = Checked ? Color.White : (_dark ? Color.White : Color.FromArgb(90, 90, 90));
            if (!Checked && !_dark) knob = Color.FromArgb(90, 90, 90);
            if (!Checked && _dark) knob = Color.FromArgb(220, 220, 220);
            using (SolidBrush br = new SolidBrush(knob))
            {
                g.FillEllipse(br, knobX, knobY, knobD, knobD);
            }

            string text = Text != null ? Text : "";
            if (text.Length > 0)
            {
                Color fore = AppTheme.Fore(_dark);
                Font f = Font;
                try
                {
                    TextRenderer.DrawText(g, text, f,
                        new Rectangle(pill.Right + 10, 0, Math.Max(0, Width - pill.Right - 12), Height),
                        fore, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                }
                catch { }
            }

            if (Focused)
            {
                try
                {
                    Rectangle focus = new Rectangle(0, 0, Width - 1, Height - 1);
                    using (GraphicsPath fp = AppTheme.RoundedRect(focus, 6))
                    using (Pen pen = new Pen(AppTheme.Accent(_dark), 1f))
                    {
                        pen.DashStyle = DashStyle.Dot;
                        g.DrawPath(pen, fp);
                    }
                }
                catch { }
            }
        }
        catch { }
    }
}
