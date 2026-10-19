using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

public class TrayApp : ApplicationContext
{
    private NotifyIcon _icon;
    private ToolStripMenuItem _miStatus;
    private ToolStripMenuItem _miFreeze;
    private ToolStripMenuItem _miLocked;
    private ToolStripMenuItem _miPoll125;
    private ToolStripMenuItem _miPoll250;
    private ToolStripMenuItem _miPoll500;
    private ToolStripMenuItem _miPoll1000;
    private ToolStripMenuItem _miModeSuspend;
    private ToolStripMenuItem _miModeEco;
    private ToolStripMenuItem _miThemeSystem;
    private ToolStripMenuItem _miThemeLight;
    private ToolStripMenuItem _miThemeDark;
    private ContextMenuStrip _menu;
    private SettingsForm _form;
    private Hotkeys _hotkeys;
    private InputHook _inputHook;
    private Timer _hookSyncTimer;

    public TrayApp(bool showSettings)
    {
        _hotkeys = new Hotkeys();
        _hotkeys.LockPressed += delegate {
            string msg = ProcessSuspenderApp.LockToForeground();
            if (msg != null) Toast.Show(msg);
        };
        _hotkeys.FreezePressed += delegate { ToggleFreeze(); };

        _inputHook = new InputHook();
        _inputHook.SwitchStateChanged += delegate(bool active) { ProcessSuspenderApp.OnSwitchStateChanged(active); };
        _inputHook.MouseSwitchEvent += delegate(bool down, int x, int y) { ProcessSuspenderApp.OnMouseSwitchEvent(down, x, y); };
        // On-demand hooks: both hooks are uninstalled entirely when there is no
        // locked target, so an idle app does no input work. The mouse hook is
        // additionally gated on something being frozen (avoids cursor overhead).
        ProcessSuspenderApp.TargetsChanged += delegate
        {
            try { UpdateHookState(); }
            catch { }
        };
        _hookSyncTimer = new Timer();
        _hookSyncTimer.Interval = 500;
        _hookSyncTimer.Tick += delegate
        {
            try { UpdateHookState(); }
            catch { }
        };
        _hookSyncTimer.Start();
        try { UpdateHookState(); }
        catch { }
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        try { Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged; } catch { }

        _icon = new NotifyIcon();
        _icon.Icon = BuildIcon();
        _icon.Text = "Process Suspender";
        _icon.ContextMenuStrip = BuildMenu();
        _icon.Visible = true;
        _icon.DoubleClick += delegate { ShowSettings(false); };

        ProcessSuspenderApp.SettingsApplied += delegate
        {
            if (_hotkeys != null) _hotkeys.Apply(ProcessSuspenderApp.Config);
            RefreshChecks();
            ApplyMenuTheme();
            try { if (_form != null && !_form.IsDisposed) _form.ApplyTheme(); } catch { }
        };

        if (showSettings) ShowSettings(false);
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        _menu = menu;
        menu.Opening += delegate { RefreshChecks(); };

        _miStatus = new ToolStripMenuItem("Target: ...") { Enabled = false };
        menu.Items.Add(_miStatus);
        menu.Items.Add(new ToolStripSeparator());

        _miFreeze = new ToolStripMenuItem("Enable/disable the app");
        _miFreeze.Click += delegate { ToggleFreeze(); };
        menu.Items.Add(_miFreeze);

        var pickItem = new ToolStripMenuItem("Pick windows...");
        pickItem.Click += delegate
        {
            if (WindowPicker.Pick())
            {
                var picks = WindowPicker.GetSelectedTargets();
                if (picks == null || picks.Count == 0) return;
                ProcessSuspenderApp.Config.ProcessName = "";
                SettingsStore.Save(ProcessSuspenderApp.Config);
                foreach (var kv in picks)
                {
                    if (IsSelfPid(kv.Value))
                    {
                        Toast.Show("Cannot lock ProcessSuspender itself.");
                        continue;
                    }
                    if (ProcessSuspenderApp.IsNeverSuspendPid(kv.Value))
                    {
                        Toast.Show(DescribePid(kv.Value) + " is in the never-suspend list - not locked.");
                        continue;
                    }
                    ProcessSuspenderApp.AddTargetWindow(kv.Key, kv.Value);
                }
            }
        };
        menu.Items.Add(pickItem);

        var lockedItem = new ToolStripMenuItem("Locked windows");
        menu.Items.Add(lockedItem);
        _miLocked = lockedItem;

        var poll = new ToolStripMenuItem("Poll interval");
        _miPoll125 = MakePollItem(poll, 125);
        _miPoll250 = MakePollItem(poll, 250);
        _miPoll500 = MakePollItem(poll, 500);
        _miPoll1000 = MakePollItem(poll, 1000);
        menu.Items.Add(poll);

        var mode = new ToolStripMenuItem("Freeze mode");
        _miModeSuspend = new ToolStripMenuItem("Suspend (0% CPU)");
        _miModeSuspend.Click += delegate { SetFreezeMode("Suspend"); };
        mode.DropDownItems.Add(_miModeSuspend);
        _miModeEco = new ToolStripMenuItem("Eco low-power (game keeps running)");
        _miModeEco.Click += delegate { SetFreezeMode("Eco"); };
        mode.DropDownItems.Add(_miModeEco);
        menu.Items.Add(mode);

        var theme = new ToolStripMenuItem("Appearance");
        _miThemeSystem = new ToolStripMenuItem("Follow Windows");
        _miThemeSystem.Click += delegate { SetTheme("System"); };
        theme.DropDownItems.Add(_miThemeSystem);
        _miThemeLight = new ToolStripMenuItem("Light");
        _miThemeLight.Click += delegate { SetTheme("Light"); };
        theme.DropDownItems.Add(_miThemeLight);
        _miThemeDark = new ToolStripMenuItem("Dark");
        _miThemeDark.Click += delegate { SetTheme("Dark"); };
        theme.DropDownItems.Add(_miThemeDark);
        menu.Items.Add(theme);

        menu.Items.Add(new ToolStripSeparator());

        var settingsItem = new ToolStripMenuItem("Settings...");
        settingsItem.Click += delegate { ShowSettings(false); };
        menu.Items.Add(settingsItem);

        var logItem = new ToolStripMenuItem("Show log");
        logItem.Click += delegate { ShowSettings(true); };
        menu.Items.Add(logItem);

        var folderItem = new ToolStripMenuItem("Open config folder");
        folderItem.Click += delegate { SettingsStore.OpenFolder(); };
        menu.Items.Add(folderItem);

        menu.Items.Add(new ToolStripSeparator());

        var quitItem = new ToolStripMenuItem("Quit");
        quitItem.Click += delegate { Quit(); };
        menu.Items.Add(quitItem);

        ApplyMenuTheme();
        return menu;
    }

    private ToolStripMenuItem MakePollItem(ToolStripMenuItem parent, int ms)
    {
        var mi = new ToolStripMenuItem(ms + " ms");
        mi.Click += delegate { SetPoll(ms); };
        parent.DropDownItems.Add(mi);
        return mi;
    }

    private void UpdateHookState()
    {
        try
        {
            if (_inputHook == null) return;
            bool hasTargets = ProcessSuspenderApp.HasLockedTargets();
            _inputHook.SetEnabled(hasTargets);
            bool want = hasTargets && ProcessSuspenderApp.Enabled && ProcessSuspenderApp.AnyFrozen;
            _inputHook.SetMouseEnabled(want);
        }
        catch { }
    }

    private void RefreshChecks()
    {
        _miStatus.Text = "Target: " + ProcessSuspenderApp.CurrentTargetDescription
                        + "  |  " + ProcessSuspenderApp.TargetStateDescription
                        + "  |  " + ProcessSuspenderApp.FreezeModeLabel;
        _miFreeze.Checked = ProcessSuspenderApp.Enabled;
        RefreshLockedMenu();
        int cur = ProcessSuspenderApp.Config.PollIntervalMsClamped;
        _miPoll125.Checked = cur == 125;
        _miPoll250.Checked = cur == 250;
        _miPoll500.Checked = cur == 500;
        _miPoll1000.Checked = cur == 1000;
        bool eco = ProcessSuspenderApp.IsEcoMode();
        _miModeSuspend.Checked = !eco;
        _miModeEco.Checked = eco;
        string theme = "System";
        try { theme = AppTheme.NormalizeTheme(ProcessSuspenderApp.Config.Theme); } catch { }
        if (_miThemeSystem != null) _miThemeSystem.Checked = theme == "System";
        if (_miThemeLight != null) _miThemeLight.Checked = theme == "Light";
        if (_miThemeDark != null) _miThemeDark.Checked = theme == "Dark";
        ApplyMenuTheme();
    }

    private void RefreshLockedMenu()
    {
        try
        {
            foreach (ToolStripItem old in _miLocked.DropDownItems)
            {
                try { old.Dispose(); } catch { }
            }
        }
        catch { }
        _miLocked.DropDownItems.Clear();
        var entries = ProcessSuspenderApp.GetLockedWindows();
        if (entries.Count == 0)
        {
            _miLocked.DropDownItems.Add(new ToolStripMenuItem("(none)") { Enabled = false });
            return;
        }
        foreach (ProcessSuspenderApp.LockedEntry e in entries)
        {
            string icon = !e.Suspended ? "✅ " : (e.Eco ? "🐢 " : "⛔ ");
            var mi = new ToolStripMenuItem(icon + e.Label);
            int idx = e.Index;
            mi.Click += delegate { ProcessSuspenderApp.RemoveWindowTarget(idx); };
            _miLocked.DropDownItems.Add(mi);
        }
    }

    private static string DescribePid(uint pid)
    {
        try { using (System.Diagnostics.Process p = System.Diagnostics.Process.GetProcessById((int)pid)) return p.ProcessName; }
        catch { return pid.ToString(); }
    }

    private static bool IsSelfPid(uint pid)
    {
        try { return (uint)System.Diagnostics.Process.GetCurrentProcess().Id == pid; }
        catch { return false; }
    }

    private void OnSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock)
            ProcessSuspenderApp.OnSessionLocked();
    }

    private void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        try
        {
            // Windows theme change: re-apply only when following the system.
            string theme = "System";
            try { theme = AppTheme.NormalizeTheme(ProcessSuspenderApp.Config.Theme); } catch { }
            if (theme != "System") return;
            ApplyMenuTheme();
            try { if (_form != null && !_form.IsDisposed) _form.ApplyTheme(); } catch { }
        }
        catch { }
    }

    private void ApplyMenuTheme()
    {
        try
        {
            if (_menu == null) return;
            bool dark = AppTheme.IsDark(ProcessSuspenderApp.Config);
            AppTheme.ApplyMenu(_menu, dark);
        }
        catch { }
    }

    private void ToggleFreeze()
    {
        ProcessSuspenderApp.ToggleFreeze();
        RefreshChecks();
    }

    private void SetPoll(int ms)
    {
        ProcessSuspenderApp.Config.PollIntervalMs = ms;
        SettingsStore.Save(ProcessSuspenderApp.Config);
        ProcessSuspenderApp.ApplySettings();
    }

    private void SetFreezeMode(string mode)
    {
        ProcessSuspenderApp.SetFreezeMode(mode);
        RefreshChecks();
    }

    private void SetTheme(string theme)
    {
        try
        {
            ProcessSuspenderApp.Config.Theme = AppTheme.NormalizeTheme(theme);
            SettingsStore.Save(ProcessSuspenderApp.Config);
            ProcessSuspenderApp.ApplySettings();
            ApplyMenuTheme();
            try { if (_form != null && !_form.IsDisposed) _form.ApplyTheme(); } catch { }
            RefreshChecks();
        }
        catch { }
    }

    private void ShowSettings(bool logTab)
    {
        if (_form == null || _form.IsDisposed)
        {
            _form = new SettingsForm();
            _form.HotkeyCaptureChanged += delegate(bool capturing)
            {
                if (_hotkeys != null) _hotkeys.Suspended = capturing;
            };
            _form.Show();
        }
        else
        {
            _form.Show();
            _form.Activate();
        }
        try { _form.ApplyTheme(); } catch { }
        if (logTab) _form.ShowLogTab();
    }

    private void Quit()
    {
        _icon.Visible = false;
        ExitThread();
    }

    protected override void ExitThreadCore()
    {
        try { Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch; } catch { }
        try { Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged; } catch { }
        try { if (_hookSyncTimer != null) { _hookSyncTimer.Stop(); _hookSyncTimer.Dispose(); _hookSyncTimer = null; } } catch { }
        _icon.Visible = false;
        if (_icon != null) _icon.Dispose();
        if (_hotkeys != null) _hotkeys.Dispose();
        if (_inputHook != null) _inputHook.Dispose();
        base.ExitThreadCore();
    }

    private static Icon BuildIcon()
    {
        // Use the icon embedded in the exe (appicon.ico) so the tray, taskbar
        // and Explorer all show the same artwork. Fall back to a hand-drawn
        // placeholder if extraction ever fails.
        try
        {
            return Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch
        {
            using (Bitmap bmp = new Bitmap(16, 16))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(0, 120, 215)))
                        g.FillEllipse(b, 1, 1, 14, 14);
                    using (SolidBrush b = new SolidBrush(Color.White))
                    {
                        g.FillRectangle(b, 5, 4, 2, 8);
                        g.FillRectangle(b, 9, 4, 2, 8);
                    }
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }
    }
}
