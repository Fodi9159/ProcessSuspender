using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

public class SettingsForm : Form
{
    private Button[] _navButtons;
    private Panel[] _pages;
    private int _selectedPage;
    private HotkeyBox _txtLockHotkey;
    private HotkeyBox _txtFreezeHotkey;
    private NumericUpDown _numPoll;
    private RadioButton _rbSuspend;
    private RadioButton _rbEco;
    private NumericUpDown _numEcoCap;
    private FluentToggle _chkAuto;
    private FluentToggle _chkLog;
    private ComboBox _cmbTheme;
    private Label _lblPath;
    private Button _btnApply;
    private Button _btnClose;
    private TextBox _txtLog;
    private Button _btnOpenLog;
    private Timer _logTimer;
    private int _lastLogCount = -1;
    private TextBox _txtNeverSuspend;
    private ListBox _lstNeverSuspend;
    private ListBox _lstLocked;
    private Button _btnUnlock;

    private static readonly string[] NavTitles = new string[]
    {
        "Settings", "Log", "Never suspend", "Locked windows"
    };

    public SettingsForm()
    {
        try { this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        Text = "Process Suspender settings";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(760, 560);
        MinimumSize = new Size(700, 520);
        AutoScaleMode = AutoScaleMode.Dpi;
        BuildUi();
        WireHotkeyCaptureEvents();
        ApplyTheme();
        try { SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged; } catch { }
        Shown += delegate { LoadSettings(); ApplyTheme(); };
        FormClosing += delegate
        {
            try { if (_logTimer != null) _logTimer.Dispose(); } catch { }
            try { SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged; } catch { }
        };
    }

    /// <summary>Raised with true while any hotkey field is capturing (global hotkeys are suspended then).</summary>
    public event Action<bool> HotkeyCaptureChanged;

    private void WireHotkeyCaptureEvents()
    {
        HotkeyBox[] boxes = { _txtLockHotkey, _txtFreezeHotkey };
        foreach (HotkeyBox b in boxes)
            b.CaptureStateChanged += delegate
            {
                bool any = _txtLockHotkey.Capturing || _txtFreezeHotkey.Capturing;
                if (HotkeyCaptureChanged != null) HotkeyCaptureChanged(any);
            };
        FormClosing += delegate
        {
            foreach (HotkeyBox b in boxes) b.EndCapture();
        };
    }

    public void ShowLogTab()
    {
        ShowPage(1);
        RefreshLog();
    }

    private void ShowPage(int index)
    {
        if (_pages == null || index < 0 || index >= _pages.Length) return;
        _selectedPage = index;
        for (int i = 0; i < _pages.Length; i++)
        {
            try { _pages[i].Visible = (i == index); } catch { }
        }
        try { _pages[index].BringToFront(); } catch { }
        UpdateNavSelection();
    }

    private void UpdateNavSelection()
    {
        if (_navButtons == null) return;
        bool dark = false;
        try { dark = AppTheme.IsDark(ProcessSuspenderApp.Config); } catch { }
        for (int i = 0; i < _navButtons.Length; i++)
        {
            try
            {
                _navButtons[i].Tag = (i == _selectedPage) ? "nav-selected" : "nav";
                AppTheme.StyleNavButton(_navButtons[i], dark, i == _selectedPage);
            }
            catch { }
        }
    }

    private FluentCard CreateCard(string title, string subtitle, int height)
    {
        FluentCard card = new FluentCard();
        try
        {
            card.Height = height;
            card.Dock = DockStyle.Top;
            Label lblTitle = new Label();
            lblTitle.Text = title;
            lblTitle.Tag = "card-title";
            lblTitle.Bounds = new Rectangle(16, 10, 600, 22);
            lblTitle.AutoSize = false;
            card.Controls.Add(lblTitle);
            if (!string.IsNullOrEmpty(subtitle))
            {
                Label lblSub = new Label();
                lblSub.Text = subtitle;
                lblSub.Bounds = new Rectangle(16, 32, 600, 18);
                lblSub.AutoSize = false;
                card.Controls.Add(lblSub);
            }
        }
        catch { }
        return card;
    }

    private void BuildUi()
    {
        // ---- Header (Win11 Settings title) ----
        Panel header = new Panel();
        header.Dock = DockStyle.Top;
        header.Height = 76;
        header.Padding = new Padding(20, 14, 20, 6);
        Label lblTitle = new Label();
        lblTitle.Text = "Process Suspender";
        lblTitle.Tag = "page-title";
        lblTitle.Bounds = new Rectangle(20, 10, 500, 30);
        lblTitle.AutoSize = false;
        header.Controls.Add(lblTitle);
        Label lblSub = new Label();
        lblSub.Text = "Freeze background apps when they lose focus";
        lblSub.Tag = "page-subtitle";
        lblSub.Bounds = new Rectangle(20, 42, 500, 20);
        lblSub.AutoSize = false;
        header.Controls.Add(lblSub);
        // NOTE: not added here; dock z-order is fixed at the end of BuildUi
        // (Fill must be added before edge-docked controls).

        // ---- Footer (config path + actions) ----
        Panel footer = new Panel();
        footer.Dock = DockStyle.Bottom;
        footer.Height = 60;
        footer.Padding = new Padding(20, 12, 20, 12);
        _lblPath = new Label();
        _lblPath.Bounds = new Rectangle(20, 20, 430, 18);
        _lblPath.AutoSize = false;
        _lblPath.Anchor = AnchorStyles.Top | AnchorStyles.Left; // width set by the footer Resize handler below
        _lblPath.Text = "Config: " + SettingsStore.ConfigPath;
        footer.Controls.Add(_lblPath);
        _btnClose = new Button();
        _btnClose.Text = "Close";
        _btnClose.Size = new Size(96, 32);
        _btnClose.Location = new Point(542, 14);
        _btnClose.Click += delegate { Close(); };
        footer.Controls.Add(_btnClose);
        _btnApply = new Button();
        _btnApply.Text = "Apply";
        _btnApply.Tag = "primary";
        _btnApply.Size = new Size(96, 32);
        _btnApply.Location = new Point(646, 14);
        _btnApply.Click += delegate { OnApply(); };
        footer.Controls.Add(_btnApply);
        // Right-edge positions cannot rely on AnchorStyles.Right here: the
        // footer is still at its default width (undocked, no parent yet), so
        // the anchor distance would be captured against ~200px and the
        // buttons would end up off-screen once the footer docks. Position
        // them explicitly on every resize instead (same pattern as the
        // card Resize handlers below).
        footer.Resize += delegate
        {
            try
            {
                _btnApply.Left = Math.Max(0, footer.ClientSize.Width - 20 - _btnApply.Width);
                _btnClose.Left = Math.Max(0, _btnApply.Left - 8 - _btnClose.Width);
                _lblPath.Width = Math.Max(100, _btnClose.Left - 8 - 20);
            }
            catch { }
        };
        // NOTE: added at the end of BuildUi in Fill-first dock order.

        // ---- Main split: nav left, content right ----
        Panel main = new Panel();
        main.Dock = DockStyle.Fill;
        main.Padding = new Padding(12, 4, 12, 4);
        // NOTE: added at the end of BuildUi in Fill-first dock order.

        Panel nav = new Panel();
        nav.Width = 190;
        nav.Dock = DockStyle.Left;
        nav.Padding = new Padding(4, 4, 12, 4);
        // NOTE: added at the end of BuildUi in Fill-first dock order.

        Panel contentPanel = new Panel();
        contentPanel.Dock = DockStyle.Fill;
        contentPanel.Padding = new Padding(4, 4, 4, 4);
        // NOTE: added at the end of BuildUi in Fill-first dock order.

        _navButtons = new Button[NavTitles.Length];
        for (int i = 0; i < NavTitles.Length; i++)
        {
            Button nb = new Button();
            nb.Text = NavTitles[i];
            nb.Tag = (i == 0) ? "nav-selected" : "nav";
            nb.Height = 40;
            nb.Dock = DockStyle.Top;
            int idx = i;
            nb.Click += delegate { ShowPage(idx); };
            // Dock Top stacks in reverse; add in reverse at the end.
            _navButtons[i] = nb;
        }
        for (int i = _navButtons.Length - 1; i >= 0; i--)
            nav.Controls.Add(_navButtons[i]);

        _pages = new Panel[NavTitles.Length];
        for (int i = 0; i < _pages.Length; i++)
        {
            Panel p = new Panel();
            p.Dock = DockStyle.Fill;
            p.AutoScroll = true;
            p.Padding = new Padding(4, 4, 10, 10);
            p.Visible = (i == 0);
            contentPanel.Controls.Add(p);
            // Fill-docked pages stack; keep only selected on top.
            try { p.BringToFront(); } catch { }
            _pages[i] = p;
        }
        _selectedPage = 0;
        try { _pages[0].BringToFront(); } catch { }

        BuildSettingsPage(_pages[0]);
        BuildLogPage(_pages[1]);
        BuildNeverPage(_pages[2]);
        BuildLockedPage(_pages[3]);

        // ---- Dock z-order (the actual layout fix) ----
        // WinForms docks in z-order: a Fill panel added AFTER an edge-docked
        // panel covers the whole area and slides underneath it. That is what
        // the screenshot showed (card labels hidden behind the nav, first
        // card hidden behind the header). Fill must be added FIRST so edges
        // take their strip and Fill gets the remainder.
        main.Controls.Add(contentPanel); // Fill first -> remainder
        main.Controls.Add(nav);           // Left second -> left strip
        Controls.Add(main);               // Fill first -> middle remainder
        Controls.Add(footer);             // Bottom second -> bottom strip
        Controls.Add(header);             // Top last -> top strip

        _logTimer = new Timer { Interval = 500 };
        _logTimer.Tick += delegate { RefreshLog(); RefreshLocked(); };
        _logTimer.Start();
    }

    private void BuildSettingsPage(Panel page)
    {
        // ---- Card: Freezing ----
        FluentCard grpBehavior = CreateCard("Freezing",
            "How the target is quieted when it loses focus.", 168);
        Label lblPoll = new Label();
        lblPoll.Text = "Poll interval (ms)";
        lblPoll.Bounds = new Rectangle(16, 58, 150, 20);
        _numPoll = new NumericUpDown();
        _numPoll.Bounds = new Rectangle(180, 56, 90, 24);
        _numPoll.Minimum = 125;
        _numPoll.Maximum = 2000;
        _numPoll.Increment = 125;
        Label lblMode = new Label();
        lblMode.Text = "Freeze mode";
        lblMode.Bounds = new Rectangle(16, 88, 150, 20);
        _rbSuspend = new RadioButton();
        _rbSuspend.Text = "Suspend  (0% CPU)";
        _rbSuspend.Bounds = new Rectangle(180, 86, 220, 22);
        _rbEco = new RadioButton();
        _rbEco.Text = "Eco low-power  (keeps running)";
        _rbEco.Bounds = new Rectangle(180, 108, 260, 22);
        Label lblCap = new Label();
        lblCap.Text = "Eco CPU cap (%)";
        lblCap.Bounds = new Rectangle(16, 134, 150, 20);
        _numEcoCap = new NumericUpDown();
        _numEcoCap.Bounds = new Rectangle(180, 132, 90, 24);
        _numEcoCap.Minimum = 0.1m;
        _numEcoCap.Maximum = 100;
        _numEcoCap.DecimalPlaces = 1;
        _numEcoCap.Increment = 0.5m;
        Label lblCapHint = new Label();
        lblCapHint.Text = "0.1\u2013100, default 2 (<5% can wedge games)";
        lblCapHint.Bounds = new Rectangle(278, 134, 360, 20);
        lblCapHint.AutoSize = true;
        grpBehavior.Controls.AddRange(new Control[] { lblPoll, _numPoll, lblMode, _rbSuspend, _rbEco, lblCap, _numEcoCap, lblCapHint });

        // ---- Card: System ----
        FluentCard grpSystem = CreateCard("System",
            "Startup, logging and appearance.", 150);
        _chkAuto = new FluentToggle();
        _chkAuto.Text = "Auto-start with Windows";
        _chkAuto.Bounds = new Rectangle(16, 58, 300, 24);
        _chkLog = new FluentToggle();
        _chkLog.Text = "Write log to file";
        _chkLog.Bounds = new Rectangle(16, 86, 300, 24);
        Label lblTheme = new Label();
        lblTheme.Text = "Appearance";
        lblTheme.Bounds = new Rectangle(16, 116, 150, 20);
        _cmbTheme = new ComboBox();
        _cmbTheme.Bounds = new Rectangle(180, 112, 180, 24);
        _cmbTheme.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbTheme.Items.AddRange(new object[] { "Follow Windows", "Light", "Dark" });
        _cmbTheme.SelectedIndexChanged += delegate { PreviewThemeSelection(); };
        grpSystem.Controls.AddRange(new Control[] { _chkAuto, _chkLog, lblTheme, _cmbTheme });

        // ---- Card: Hotkeys ----
        FluentCard grpHotkeys = CreateCard("Hotkeys",
            "Click a field to capture a new combination, or clear it.", 148);
        Label lblLock = new Label();
        lblLock.Text = "Lock to focused window";
        lblLock.Bounds = new Rectangle(16, 60, 180, 20);
        _txtLockHotkey = new HotkeyBox();
        _txtLockHotkey.Bounds = new Rectangle(210, 58, 170, 24);
        Button btnClearLock = new Button();
        btnClearLock.Text = "\u00D7";
        btnClearLock.Bounds = new Rectangle(386, 58, 30, 24);
        try { btnClearLock.Font = new Font("Segoe UI", 10f); } catch { }
        btnClearLock.Click += delegate { _txtLockHotkey.Combo = ""; };
        Label lblFreeze = new Label();
        lblFreeze.Text = "Enable / disable the app";
        lblFreeze.Bounds = new Rectangle(16, 92, 180, 20);
        _txtFreezeHotkey = new HotkeyBox();
        _txtFreezeHotkey.Bounds = new Rectangle(210, 90, 170, 24);
        Button btnClearFreeze = new Button();
        btnClearFreeze.Text = "\u00D7";
        btnClearFreeze.Bounds = new Rectangle(386, 90, 30, 24);
        try { btnClearFreeze.Font = new Font("Segoe UI", 10f); } catch { }
        btnClearFreeze.Click += delegate { _txtFreezeHotkey.Combo = ""; };
        Label lblHotkeyHint = new Label();
        lblHotkeyHint.Text = "Example: Ctrl+Alt+F9. Escape cancels, Tab moves on.";
        lblHotkeyHint.Bounds = new Rectangle(16, 120, 420, 18);
        grpHotkeys.Controls.AddRange(new Control[]
        {
            lblLock, _txtLockHotkey, btnClearLock, lblFreeze, _txtFreezeHotkey, btnClearFreeze, lblHotkeyHint
        });
        // DockStyle.Top stacks last-added on top, so add bottom-up.
        page.Controls.Add(grpHotkeys);
        page.Controls.Add(grpSystem);
        page.Controls.Add(grpBehavior);
    }

    private void BuildLogPage(Panel page)
    {
        FluentCard card = CreateCard("Log",
            "Live view of what the app is doing. Full history is in the log file.", 400);
        card.Dock = DockStyle.Fill;
        _btnOpenLog = new Button();
        _btnOpenLog.Text = "Open log file";
        _btnOpenLog.Bounds = new Rectangle(16, 58, 140, 32);
        _btnOpenLog.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _btnOpenLog.Click += delegate
        {
            try
            {
                if (System.IO.File.Exists(ProcessSuspenderApp.LogFile))
                    Process.Start("notepad.exe", "\"" + ProcessSuspenderApp.LogFile + "\"");
                else
                    MessageBox.Show(this, "No log file yet.", "Process Suspender");
            }
            catch { }
        };
        _txtLog = new TextBox();
        _txtLog.Multiline = true;
        _txtLog.ReadOnly = true;
        _txtLog.ScrollBars = ScrollBars.Both;
        try { _txtLog.Font = new Font("Cascadia Mono", 9f); } catch { try { _txtLog.Font = new Font("Consolas", 9f); } catch { } }
        _txtLog.Bounds = new Rectangle(16, 98, 480, 270);
        _txtLog.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        card.Controls.AddRange(new Control[] { _btnOpenLog, _txtLog });
        page.Controls.Add(card);
        // Keep the open button right-aligned as the card resizes.
        card.Resize += delegate
        {
            try { _btnOpenLog.Left = Math.Max(16, card.ClientSize.Width - 156); } catch { }
            try { _txtLog.Width = Math.Max(100, card.ClientSize.Width - 32); } catch { }
            try { _txtLog.Height = Math.Max(80, card.ClientSize.Height - 114); } catch { }
        };
    }

    private void BuildNeverPage(Panel page)
    {
        FluentCard card = CreateCard("Never suspend",
            "These processes are never locked or suspended.", 430);
        card.Dock = DockStyle.Fill;
        Label lblNeverName = new Label();
        lblNeverName.Text = "Process or service name";
        lblNeverName.Bounds = new Rectangle(16, 58, 200, 20);
        _txtNeverSuspend = new TextBox();
        _txtNeverSuspend.Bounds = new Rectangle(16, 80, 260, 24);
        _txtNeverSuspend.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Button btnNeverAdd = new Button();
        btnNeverAdd.Text = "Add";
        btnNeverAdd.Tag = "primary";
        btnNeverAdd.Bounds = new Rectangle(286, 78, 80, 30);
        btnNeverAdd.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnNeverAdd.Click += delegate { AddNeverSuspend(); };
        _txtNeverSuspend.KeyDown += delegate(object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                AddNeverSuspend();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        };
        _lstNeverSuspend = new ListBox();
        _lstNeverSuspend.Bounds = new Rectangle(16, 114, 350, 200);
        _lstNeverSuspend.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _lstNeverSuspend.IntegralHeight = false;
        Button btnNeverRemove = new Button();
        btnNeverRemove.Text = "Remove";
        btnNeverRemove.Bounds = new Rectangle(16, 322, 96, 32);
        btnNeverRemove.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        btnNeverRemove.Click += delegate { RemoveNeverSuspend(); };
        Label lblNeverNote = new Label();
        lblNeverNote.Text = "Matches the process name (\u201Cexplorer\u201D, \u201Cexplorer.exe\u201D). Services are covered by their host process (e.g. \u201Csvchost\u201D). Windows core, GPU driver and anti-cheat processes are included by default.";
        lblNeverNote.Bounds = new Rectangle(16, 362, 350, 50);
        lblNeverNote.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        card.Controls.AddRange(new Control[]
        {
            lblNeverName, _txtNeverSuspend, btnNeverAdd,
            _lstNeverSuspend, btnNeverRemove, lblNeverNote
        });
        page.Controls.Add(card);
        card.Resize += delegate
        {
            try
            {
                _txtNeverSuspend.Width = Math.Max(80, card.ClientSize.Width - 130);
                btnNeverAdd.Left = card.ClientSize.Width - 96;
                _lstNeverSuspend.Width = Math.Max(100, card.ClientSize.Width - 32);
                _lstNeverSuspend.Height = Math.Max(60, card.ClientSize.Height - 228);
                btnNeverRemove.Top = card.ClientSize.Height - 108;
                lblNeverNote.Top = card.ClientSize.Height - 68;
                lblNeverNote.Width = Math.Max(100, card.ClientSize.Width - 32);
            }
            catch { }
        };
    }

    private void BuildLockedPage(Panel page)
    {
        FluentCard card = CreateCard("Locked windows",
            "Windows you locked (Pick / Lock hotkey). Suspended when not focused.", 440);
        card.Dock = DockStyle.Fill;
        _lstLocked = new ListBox();
        _lstLocked.Bounds = new Rectangle(16, 58, 350, 280);
        _lstLocked.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _lstLocked.IntegralHeight = false;
        _btnUnlock = new Button();
        _btnUnlock.Text = "Unlock";
        _btnUnlock.Bounds = new Rectangle(16, 346, 96, 32);
        _btnUnlock.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        _btnUnlock.Click += delegate { UnlockSelected(); };
        Button btnRefresh = new Button();
        btnRefresh.Text = "Refresh";
        btnRefresh.Bounds = new Rectangle(120, 346, 96, 32);
        btnRefresh.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        btnRefresh.Click += delegate { RefreshLocked(); };
        Label lblLockedNote = new Label();
        lblLockedNote.Text = "Updates live. Select an entry and click Unlock to remove it from the set.";
        lblLockedNote.Bounds = new Rectangle(16, 386, 350, 30);
        lblLockedNote.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        card.Controls.AddRange(new Control[] { _lstLocked, _btnUnlock, btnRefresh, lblLockedNote });
        page.Controls.Add(card);
        card.Resize += delegate
        {
            try
            {
                _lstLocked.Width = Math.Max(100, card.ClientSize.Width - 32);
                _lstLocked.Height = Math.Max(60, card.ClientSize.Height - 152);
                _btnUnlock.Top = card.ClientSize.Height - 94;
                btnRefresh.Top = card.ClientSize.Height - 94;
                lblLockedNote.Top = card.ClientSize.Height - 54;
                lblLockedNote.Width = Math.Max(100, card.ClientSize.Width - 32);
            }
            catch { }
        };
    }

    private void LoadSettings()
    {
        _numPoll.Value = ProcessSuspenderApp.Config.PollIntervalMsClamped;
        _chkAuto.Checked = ProcessSuspenderApp.Config.AutoStartWithWindows;
        _chkLog.Checked = ProcessSuspenderApp.Config.LogToFile;
        bool eco = ProcessSuspenderApp.IsEcoMode();
        _rbSuspend.Checked = !eco;
        _rbEco.Checked = eco;
        try { _numEcoCap.Value = (decimal)ProcessSuspenderApp.Config.EcoCpuCapPercentClamped; }
        catch { _numEcoCap.Value = 2; }
        SetThemeCombo(AppTheme.NormalizeTheme(ProcessSuspenderApp.Config.Theme));
        _txtLockHotkey.Combo = ProcessSuspenderApp.Config.LockHotkey;
        _txtFreezeHotkey.Combo = ProcessSuspenderApp.Config.FreezeHotkey;
        _lstNeverSuspend.Items.Clear();
        if (ProcessSuspenderApp.Config.NeverSuspend != null)
        {
            foreach (string s in ProcessSuspenderApp.Config.NeverSuspend)
                _lstNeverSuspend.Items.Add(s);
        }
        ApplyTheme();
    }

    /// <summary>Applies the current effective theme to this window.</summary>
    public void ApplyTheme()
    {
        try
        {
            bool dark = AppTheme.IsDark(ProcessSuspenderApp.Config);
            AppTheme.ApplyForm(this, dark);
            UpdateNavSelection();
        }
        catch { }
    }

    private void SetThemeCombo(string theme)
    {
        try
        {
            if (_cmbTheme == null) return;
            if (theme == "Dark") _cmbTheme.SelectedIndex = 2;
            else if (theme == "Light") _cmbTheme.SelectedIndex = 1;
            else _cmbTheme.SelectedIndex = 0;
        }
        catch { }
    }

    private string GetThemeCombo()
    {
        try
        {
            if (_cmbTheme == null) return "System";
            if (_cmbTheme.SelectedIndex == 2) return "Dark";
            if (_cmbTheme.SelectedIndex == 1) return "Light";
        }
        catch { }
        return "System";
    }

    /// <summary>Live preview when the Appearance dropdown changes (persisted on Apply).</summary>
    private void PreviewThemeSelection()
    {
        try
        {
            string t = GetThemeCombo();
            bool dark = AppTheme.IsDark(t);
            AppTheme.ApplyForm(this, dark);
            UpdateNavSelection();
        }
        catch { }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        try
        {
            if (IsDisposed || !Visible) return;
            // Only auto-follow when the saved mode is System; a preview selection
            // of System also follows so the dropdown feels live.
            string saved = "System";
            try { saved = AppTheme.NormalizeTheme(ProcessSuspenderApp.Config.Theme); } catch { }
            string shown = GetThemeCombo();
            if (saved != "System" && shown != "System") return;
            bool dark = AppTheme.IsWindowsDark();
            if (shown == "Light") dark = false;
            else if (shown == "Dark") dark = true;
            if (InvokeRequired)
            {
                bool d = dark;
                try { BeginInvoke((Action)delegate { try { AppTheme.ApplyForm(this, d); UpdateNavSelection(); } catch { } }); } catch { }
                return;
            }
            AppTheme.ApplyForm(this, dark);
            UpdateNavSelection();
        }
        catch { }
    }

    private void AddNeverSuspend()
    {
        string name = _txtNeverSuspend.Text.Trim();
        if (name.Length == 0) return;
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name.Substring(0, name.Length - 4);
        foreach (object existing in _lstNeverSuspend.Items)
        {
            if (((string)existing).Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                _txtNeverSuspend.Clear();
                return;
            }
        }
        _lstNeverSuspend.Items.Add(name);
        _txtNeverSuspend.Clear();
        SaveNeverSuspend();
    }

    private void RemoveNeverSuspend()
    {
        if (_lstNeverSuspend.SelectedIndex >= 0)
        {
            _lstNeverSuspend.Items.RemoveAt(_lstNeverSuspend.SelectedIndex);
            SaveNeverSuspend();
        }
    }

    // Persist the whitelist immediately so it survives an app close without Apply.
    private void SaveNeverSuspend()
    {
        ProcessSuspenderApp.Config.NeverSuspend = NormalizeNeverSuspend();
        SettingsStore.Save(ProcessSuspenderApp.Config);
    }

    private void OnApply()
    {
        string lockHotkey = _txtLockHotkey.Combo;
        string freezeHotkey = _txtFreezeHotkey.Combo;
        uint m, v;
        if ((!string.IsNullOrEmpty(lockHotkey) && !Hotkeys.TryParseCombo(lockHotkey, out m, out v))
            || (!string.IsNullOrEmpty(freezeHotkey) && !Hotkeys.TryParseCombo(freezeHotkey, out m, out v)))
        {
            MessageBox.Show(this,
                "Invalid hotkey format.\r\nUse modifiers (Ctrl / Alt / Shift / Win) plus one key, joined with '+', e.g. Ctrl+Alt+F9.",
                "Process Suspender", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!string.IsNullOrEmpty(lockHotkey) && lockHotkey.Equals(freezeHotkey, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this,
                "Lock and Freeze hotkeys must be different.",
                "Process Suspender", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        ProcessSuspenderApp.Config.PollIntervalMs = (int)_numPoll.Value;
        ProcessSuspenderApp.Config.AutoStartWithWindows = _chkAuto.Checked;
        ProcessSuspenderApp.Config.LogToFile = _chkLog.Checked;
        ProcessSuspenderApp.Config.Theme = GetThemeCombo();
        ProcessSuspenderApp.Config.FreezeMode = (_rbEco != null && _rbEco.Checked) ? "Eco" : "Suspend";
        try { ProcessSuspenderApp.Config.EcoCpuCapPercent = (double)_numEcoCap.Value; }
        catch { ProcessSuspenderApp.Config.EcoCpuCapPercent = 2; }
        ProcessSuspenderApp.Config.LockHotkey = lockHotkey;
        ProcessSuspenderApp.Config.FreezeHotkey = freezeHotkey;
        ProcessSuspenderApp.Config.NeverSuspend = NormalizeNeverSuspend();
        SettingsStore.Save(ProcessSuspenderApp.Config);
        SettingsStore.SetAutoStart(_chkAuto.Checked);
        ProcessSuspenderApp.ApplySettings();
        ApplyTheme();
    }

    private void RefreshLog()
    {
        if (IsDisposed || !Visible) return;
        if (_txtLog == null) return;
        if (_selectedPage != 1) return;
        string[] lines = ProcessSuspenderApp.GetLogLines();
        if (lines.Length == _lastLogCount) return;
        if (_lastLogCount < 0 || lines.Length < _lastLogCount || _txtLog.TextLength == 0)
        {
            // Full rebuild: first view, buffer rollover, or cleared text.
            _txtLog.Text = string.Join("\r\n", lines);
        }
        else
        {
            // Append only the delta instead of rebuilding the full buffer.
            var sb = new System.Text.StringBuilder();
            for (int i = _lastLogCount; i < lines.Length; i++)
                sb.Append("\r\n").Append(lines[i]);
            _txtLog.AppendText(sb.ToString());
        }
        _lastLogCount = lines.Length;
        _txtLog.SelectionStart = _txtLog.TextLength;
        _txtLog.ScrollToCaret();
    }

    // Reuses the same data the tray menu shows, refreshed live on the timer.
    private void RefreshLocked()
    {
        if (IsDisposed || !Visible || _lstLocked == null) return;
        if (_selectedPage != 3) return;
        var entries = ProcessSuspenderApp.GetLockedWindows();
        // Rebuild only when the displayed text changes, so the live selection
        // highlight isn't wiped by the 500ms timer.
        string[] next = new string[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            string prefix = "Active  \u25CF  ";
            if (entries[i].Suspended) prefix = entries[i].Eco ? "Throttled  \u25CF  " : "Suspended  \u25CF  ";
            next[i] = prefix + entries[i].Label;
        }
        bool changed = _lstLocked.Items.Count != next.Length;
        if (!changed)
        {
            for (int i = 0; i < next.Length; i++)
                if ((string)_lstLocked.Items[i] != next[i]) { changed = true; break; }
        }
        if (!changed) { _btnUnlock.Enabled = next.Length > 0; return; }
        string sel = _lstLocked.SelectedItem != null ? (string)_lstLocked.SelectedItem : null;
        _lstLocked.Items.Clear();
        _lstLocked.Items.AddRange(next);
        if (sel != null) _lstLocked.SelectedItem = sel; // restore highlight by value
        _btnUnlock.Enabled = next.Length > 0;
    }

    private void UnlockSelected()
    {
        if (_lstLocked == null || _lstLocked.SelectedIndex < 0) return;
        // Index in the list matches the entry Index returned by GetLockedWindows.
        var entries = ProcessSuspenderApp.GetLockedWindows();
        if (_lstLocked.SelectedIndex < entries.Count)
            ProcessSuspenderApp.RemoveWindowTarget(entries[_lstLocked.SelectedIndex].Index);
        RefreshLocked();
    }

    /// <summary>Trims, strips ".exe" and dedupes the never-suspend list (case-insensitive).</summary>
    private List<string> NormalizeNeverSuspend()
    {
        var result = new List<string>();
        foreach (object item in _lstNeverSuspend.Items)
        {
            string name = ((string)item).Trim();
            if (name.Length == 0) continue;
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4);
            bool dup = false;
            foreach (string existing in result)
            {
                if (existing.Equals(name, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
            }
            if (!dup) result.Add(name);
        }
        return result;
    }
}

public static class WindowPicker
{
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    private delegate bool EnumProc(IntPtr h, IntPtr lp);

    public static bool Pick()
    {
        var items = new List<WindowItem>();
        int selfId = -1;
        try { selfId = Process.GetCurrentProcess().Id; } catch { }
        EnumWindows(delegate(IntPtr h, IntPtr lp)
        {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(512);
            if (GetWindowText(h, sb, sb.Capacity) == 0) return true;
            uint p;
            GetWindowThreadProcessId(h, out p);
            if ((int)p == selfId) return true; // never list our own windows - locking them would freeze us
            items.Add(new WindowItem { Hwnd = h, Pid = p, Title = sb.ToString() });
            return true;
        }, IntPtr.Zero);

        using (var dlg = new Form
        {
            Text = "Pick windows to freeze",
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(520, 430)
        })
        {
            var label = new Label
            {
                Text = "Select one or more windows (Ctrl/Shift+click or drag). They are frozen when not focused.",
                Bounds = new Rectangle(20, 14, 480, 32)
            };
            var list = new ListBox
            {
                Bounds = new Rectangle(20, 50, 480, 310),
                IntegralHeight = false,
                SelectionMode = SelectionMode.MultiExtended
            };
            foreach (WindowItem it in items)
                list.Items.Add(it.Pid + "  \u2014  " + it.Title);

            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Bounds = new Rectangle(332, 372, 96, 32) };
            ok.Tag = "primary";
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(436, 372, 96, 32) };
            dlg.Controls.Add(label);
            dlg.Controls.Add(list);
            dlg.Controls.Add(ok);
            dlg.Controls.Add(cancel);
            dlg.AcceptButton = ok;
            dlg.CancelButton = cancel;
            try { AppTheme.ApplyForm(dlg, AppTheme.IsDark(ProcessSuspenderApp.Config)); } catch { }

            if (dlg.ShowDialog() != DialogResult.OK || list.SelectedIndices.Count == 0) return false;
            selected = new List<WindowItem>();
            foreach (int idx in list.SelectedIndices)
                selected.Add(items[idx]);
            return true;
        }
    }

    private static List<WindowItem> selected;

    /// <summary>Returns the picked windows as (hwnd, pid) pairs, or null if the pick was cancelled.</summary>
    public static List<KeyValuePair<IntPtr, uint>> GetSelectedTargets()
    {
        if (selected == null) return null;
        var result = new List<KeyValuePair<IntPtr, uint>>();
        foreach (WindowItem it in selected)
            result.Add(new KeyValuePair<IntPtr, uint>(it.Hwnd, it.Pid));
        return result;
    }

    private class WindowItem
    {
        public IntPtr Hwnd;
        public uint Pid;
        public string Title;
    }
}

/// <summary>
/// Read-only text field that records the next key combination the user presses.
/// Click it, press e.g. Ctrl+Alt+F9, and the combo appears as text. Escape
/// cancels back to the previous value; Tab navigates away normally.
/// </summary>
public class HotkeyBox : TextBox
{
    private const string Placeholder = "Click, then press keys...";
    private string _previous;
    private bool _winDown;
    private bool _capturing;

    /// <summary>Raised when capture mode starts (field focused) or stops.</summary>
    public event EventHandler CaptureStateChanged;

    public bool Capturing { get { return _capturing; } }

    /// <summary>Force-stops capture mode (e.g. when the form closes while a field is focused).</summary>
    public void EndCapture()
    {
        SetCapturing(false);
    }

    public HotkeyBox()
    {
        ReadOnly = true;
        SetPlaceholder();
    }

    /// <summary>Gets the captured combo ("" when none). Sets the displayed value.</summary>
    public string Combo
    {
        get { return (Text != null && Text != Placeholder) ? Text : ""; }
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                SetPlaceholder();
            else
            {
                Text = value;
                ForeColor = SystemColors.WindowText;
            }
        }
    }

    private void SetPlaceholder()
    {
        Text = Placeholder;
        ForeColor = Color.Gray;
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        SetCapturing(true);
        _previous = Combo.Length > 0 ? Combo : null;
        if (Text == Placeholder) Text = "";
        else SelectAll();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        SetCapturing(false);
        if (Combo.Length == 0) SetPlaceholder();
    }

    private void SetCapturing(bool value)
    {
        if (_capturing == value) return;
        _capturing = value;
        if (CaptureStateChanged != null) CaptureStateChanged(this, EventArgs.Empty);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        Keys kc = e.KeyCode;

        if (kc == Keys.LWin || kc == Keys.RWin)
        {
            _winDown = true;
            Suppress(e);
            return;
        }
        if (kc == Keys.Tab)
        {
            base.OnKeyDown(e); // normal tab navigation
            return;
        }
        if (kc == Keys.Escape)
        {
            if (_previous != null) { Text = _previous; ForeColor = SystemColors.WindowText; }
            else SetPlaceholder();
            _winDown = false;
            Suppress(e);
            return;
        }
        if (IsModifier(kc) || kc == Keys.CapsLock || kc == Keys.NumLock || kc == Keys.Scroll)
        {
            Suppress(e);
            return;
        }

        string combo = Hotkeys.ComboToString(e.KeyData, _winDown);
        if (combo != null && combo != Text)
        {
            Text = combo;
            ForeColor = SystemColors.WindowText;
        }
        _winDown = false;
        Suppress(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode == Keys.LWin || e.KeyCode == Keys.RWin) _winDown = false;
    }

    private static void Suppress(KeyEventArgs e)
    {
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private static bool IsModifier(Keys k)
    {
        return k == Keys.ControlKey || k == Keys.LControlKey || k == Keys.RControlKey
            || k == Keys.Menu || k == Keys.LMenu || k == Keys.RMenu
            || k == Keys.ShiftKey || k == Keys.LShiftKey || k == Keys.RShiftKey;
    }
}
