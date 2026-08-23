using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

public class SettingsForm : Form
{
    private TabControl _tabs;
    private HotkeyBox _txtLockHotkey;
    private HotkeyBox _txtFreezeHotkey;
    private NumericUpDown _numPoll;
    private CheckBox _chkAutoLock;
    private CheckBox _chkAuto;
    private CheckBox _chkLog;
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

    public SettingsForm()
    {
        try { this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        Text = "Process Suspender settings";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(430, 500);
        BuildUi();
        WireHotkeyCaptureEvents();
        Shown += delegate { LoadSettings(); };
        FormClosing += delegate { _logTimer.Dispose(); };
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
        _tabs.SelectedIndex = 1;
        RefreshLog();
    }

    private void BuildUi()
    {
        _tabs = new TabControl { Bounds = new Rectangle(8, 8, 414, 440) };

        // ---- Settings tab ----
        var tabSettings = new TabPage("Settings");

        var grpBehavior = new GroupBox { Text = "Behavior", Bounds = new Rectangle(4, 4, 398, 84) };
        var lblPoll = new Label { Text = "Poll interval (ms):", Bounds = new Rectangle(10, 26, 110, 20) };
        _numPoll = new NumericUpDown { Bounds = new Rectangle(124, 22, 80, 24), Minimum = 125, Maximum = 2000, Increment = 125 };
        _chkAutoLock = new CheckBox { Text = "Auto-lock fullscreen windows (e.g. games)", Bounds = new Rectangle(10, 54, 380, 22) };
        grpBehavior.Controls.AddRange(new Control[] { lblPoll, _numPoll, _chkAutoLock });

        var grpSystem = new GroupBox { Text = "System", Bounds = new Rectangle(4, 92, 398, 82) };
        _chkAuto = new CheckBox { Text = "Auto-start with Windows", Bounds = new Rectangle(10, 24, 380, 22) };
        _chkLog = new CheckBox { Text = "Write log to file", Bounds = new Rectangle(10, 50, 380, 22) };
        grpSystem.Controls.AddRange(new Control[] { _chkAuto, _chkLog });

        var grpHotkeys = new GroupBox { Text = "Hotkeys", Bounds = new Rectangle(4, 224, 398, 126) };
        var lblLock = new Label { Text = "Lock to focused window:", Bounds = new Rectangle(10, 26, 190, 18) };
        _txtLockHotkey = new HotkeyBox { Bounds = new Rectangle(210, 22, 160, 24) };
        var btnClearLock = new Button { Text = "\u00D7", Bounds = new Rectangle(372, 22, 22, 24), FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 10f) };
        btnClearLock.FlatAppearance.BorderSize = 0;
        btnClearLock.Click += delegate { _txtLockHotkey.Combo = ""; };
        var lblFreeze = new Label { Text = "Freeze on defocus:", Bounds = new Rectangle(10, 64, 190, 18) };
        _txtFreezeHotkey = new HotkeyBox { Bounds = new Rectangle(210, 60, 160, 24) };
        var btnClearFreeze = new Button { Text = "\u00D7", Bounds = new Rectangle(372, 60, 22, 24), FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 10f) };
        btnClearFreeze.FlatAppearance.BorderSize = 0;
        btnClearFreeze.Click += delegate { _txtFreezeHotkey.Combo = ""; };
        var lblHotkeyHint = new Label
        {
            Text = "Click a field to capture, or press \u00D7 to clear.",
            Bounds = new Rectangle(10, 100, 380, 16),
            ForeColor = Color.Gray
        };
        grpHotkeys.Controls.AddRange(new Control[]
        {
            lblLock, _txtLockHotkey, btnClearLock, lblFreeze, _txtFreezeHotkey, btnClearFreeze, lblHotkeyHint
        });

        _lblPath = new Label { Bounds = new Rectangle(8, 354, 400, 18), ForeColor = Color.Gray, Text = "Config: " + SettingsStore.ConfigPath };
        tabSettings.Controls.AddRange(new Control[] { grpBehavior, grpSystem, grpHotkeys, _lblPath });

        // ---- Log tab ----
        var tabLog = new TabPage("Log");
        _btnOpenLog = new Button { Text = "Open log file", Bounds = new Rectangle(252, 6, 150, 28) };
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
        _txtLog = new TextBox
        {
            Bounds = new Rectangle(6, 40, 396, 300),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            Font = new Font("Consolas", 9f),
            BackColor = Color.White
        };
        tabLog.Controls.AddRange(new Control[] { _btnOpenLog, _txtLog });

        // ---- Never suspend tab ----
        var tabNever = new TabPage("Never suspend");
        var lblNeverHint = new Label
        {
            Text = "These processes (and services) are never locked or suspended.",
            Bounds = new Rectangle(4, 4, 398, 18),
            ForeColor = Color.Gray
        };
        var lblNeverName = new Label { Text = "Process or service name:", Bounds = new Rectangle(10, 30, 160, 20) };
        _txtNeverSuspend = new TextBox { Bounds = new Rectangle(10, 52, 250, 24) };
        var btnNeverAdd = new Button { Text = "Add", Bounds = new Rectangle(268, 50, 70, 28) };
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
        _lstNeverSuspend = new ListBox { Bounds = new Rectangle(10, 88, 394, 206), IntegralHeight = false };
        var btnNeverRemove = new Button { Text = "Remove", Bounds = new Rectangle(10, 302, 90, 28) };
        btnNeverRemove.Click += delegate { RemoveNeverSuspend(); };
        var lblNeverNote = new Label
        {
            Text = "Matches the process name (\"explorer\", \"explorer.exe\"). Services are\n"
                 + "covered by their host process (e.g. \"svchost\"). Explorer is included\n"
                 + "by default. Saved automatically when you Add/Remove.",
            Bounds = new Rectangle(10, 340, 394, 50),
            ForeColor = Color.Gray
        };
        tabNever.Controls.AddRange(new Control[]
        {
            lblNeverHint, lblNeverName, _txtNeverSuspend, btnNeverAdd,
            _lstNeverSuspend, btnNeverRemove, lblNeverNote
        });

        // ---- Locked windows tab ----
        var tabLocked = new TabPage("Locked windows");
        var lblLockedHint = new Label
        {
            Text = "Windows you locked (Pick / Lock hotkey). Suspended when not focused.",
            Bounds = new Rectangle(4, 4, 398, 18),
            ForeColor = Color.Gray
        };
        _lstLocked = new ListBox { Bounds = new Rectangle(10, 28, 394, 318), IntegralHeight = false };
        _btnUnlock = new Button { Text = "Unlock", Bounds = new Rectangle(10, 352, 90, 28) };
        _btnUnlock.Click += delegate { UnlockSelected(); };
        var _btnRefresh = new Button { Text = "Refresh", Bounds = new Rectangle(106, 352, 90, 28) };
        _btnRefresh.Click += delegate { RefreshLocked(); };
        var lblLockedNote = new Label
        {
            Text = "Updates live. Select an entry and click Unlock to remove it from the set.",
            Bounds = new Rectangle(10, 388, 394, 30),
            ForeColor = Color.Gray
        };
        tabLocked.Controls.AddRange(new Control[] { lblLockedHint, _lstLocked, _btnUnlock, _btnRefresh, lblLockedNote });

        _tabs.TabPages.Add(tabSettings);
        _tabs.TabPages.Add(tabLog);
        _tabs.TabPages.Add(tabNever);
        _tabs.TabPages.Add(tabLocked);

        _btnApply = new Button { Text = "Apply", Bounds = new Rectangle(330, 460, 92, 30) };
        _btnApply.Click += delegate { OnApply(); };
        _btnClose = new Button { Text = "Close", Bounds = new Rectangle(230, 460, 92, 30) };
        _btnClose.Click += delegate { Close(); };

        Controls.Add(_tabs);
        Controls.Add(_btnApply);
        Controls.Add(_btnClose);

        _logTimer = new Timer { Interval = 500 };
        _logTimer.Tick += delegate { RefreshLog(); RefreshLocked(); };
        _logTimer.Start();
    }

    private void LoadSettings()
    {
        _numPoll.Value = ProcessSuspenderApp.Config.PollIntervalMsClamped;
        _chkAutoLock.Checked = ProcessSuspenderApp.Config.AutoLockFullscreen;
        _chkAuto.Checked = ProcessSuspenderApp.Config.AutoStartWithWindows;
        _chkLog.Checked = ProcessSuspenderApp.Config.LogToFile;
        _txtLockHotkey.Combo = ProcessSuspenderApp.Config.LockHotkey;
        _txtFreezeHotkey.Combo = ProcessSuspenderApp.Config.FreezeHotkey;
        _lstNeverSuspend.Items.Clear();
        if (ProcessSuspenderApp.Config.NeverSuspend != null)
        {
            foreach (string s in ProcessSuspenderApp.Config.NeverSuspend)
                _lstNeverSuspend.Items.Add(s);
        }
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

        ProcessSuspenderApp.Config.PollIntervalMs = (int)_numPoll.Value;
        ProcessSuspenderApp.Config.AutoLockFullscreen = _chkAutoLock.Checked;
        ProcessSuspenderApp.Config.AutoStartWithWindows = _chkAuto.Checked;
        ProcessSuspenderApp.Config.LogToFile = _chkLog.Checked;
        ProcessSuspenderApp.Config.LockHotkey = lockHotkey;
        ProcessSuspenderApp.Config.FreezeHotkey = freezeHotkey;
        ProcessSuspenderApp.Config.NeverSuspend = NormalizeNeverSuspend();
        SettingsStore.Save(ProcessSuspenderApp.Config);
        SettingsStore.SetAutoStart(_chkAuto.Checked);
        ProcessSuspenderApp.ApplySettings();
    }

    private void RefreshLog()
    {
        if (IsDisposed || !Visible) return;
        string[] lines = ProcessSuspenderApp.GetLogLines();
        if (lines.Length == _lastLogCount) return;
        _lastLogCount = lines.Length;
        _txtLog.Text = string.Join("\r\n", lines);
        _txtLog.SelectionStart = _txtLog.TextLength;
        _txtLog.ScrollToCaret();
    }

    // Reuses the same data the tray menu shows, refreshed live on the timer.
    private void RefreshLocked()
    {
        if (IsDisposed || !Visible || _lstLocked == null) return;
        var entries = ProcessSuspenderApp.GetLockedWindows();
        // Rebuild only when the displayed text changes, so the live selection
        // highlight isn't wiped by the 500ms timer.
        string[] next = new string[entries.Count];
        for (int i = 0; i < entries.Count; i++)
            next[i] = (entries[i].Suspended ? "⛔ SUSPENDED  " : "✅ active  ") + entries[i].Label;
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
        EnumWindows(delegate(IntPtr h, IntPtr lp)
        {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(512);
            if (GetWindowText(h, sb, sb.Capacity) == 0) return true;
            uint p;
            GetWindowThreadProcessId(h, out p);
            items.Add(new WindowItem { Hwnd = h, Pid = p, Title = sb.ToString() });
            return true;
        }, IntPtr.Zero);

        using (var dlg = new Form
        {
            Text = "Pick windows to suspend",
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(470, 370)
        })
        {
            var label = new Label
            {
                Text = "Select one or more windows (Ctrl/Shift+click or drag). They are suspended when not focused.",
                Bounds = new Rectangle(10, 6, 450, 28)
            };
            var list = new ListBox
            {
                Bounds = new Rectangle(10, 36, 450, 280),
                IntegralHeight = false,
                SelectionMode = SelectionMode.MultiExtended
            };
            foreach (WindowItem it in items)
                list.Items.Add(it.Pid + "  -  " + it.Title);

            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Bounds = new Rectangle(300, 322, 75, 28) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(385, 322, 75, 28) };
            dlg.Controls.Add(label);
            dlg.Controls.Add(list);
            dlg.Controls.Add(ok);
            dlg.Controls.Add(cancel);
            dlg.AcceptButton = ok;
            dlg.CancelButton = cancel;

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
            || k == Keys.ShiftKey || k == Keys.LShiftKey || k == Keys.RShiftKey
            || k == Keys.Control || k == Keys.Alt || k == Keys.Shift;
    }
}
