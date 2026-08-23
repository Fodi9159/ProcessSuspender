using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Automation;

public static class ProcessSuspenderApp
{
    private const uint PROCESS_SUSPEND_RESUME = 0x0800;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint PROCESS_ALL = PROCESS_SUSPEND_RESUME | PROCESS_QUERY_LIMITED_INFORMATION;
    private const int LogCapacity = 500;
    private const int SwitchGestureTimeoutMs = 5000;
    private const int PostSwitchGraceMs = 2000;

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr h);
    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr h, uint flags, StringBuilder path, ref uint size);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(int x, int y);
    [DllImport("user32.dll")]
    private static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, ref RECT lParam);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, ref TBBUTTON lParam);
    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    public static void SetDpiAware()
    {
        try { SetProcessDPIAware(); } catch { }
    }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TBBUTTON
    {
        public int iBitmap;
        public int idCommand;
        public byte fsState;
        public byte fsStyle;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
        public byte[] bReserved;
        public IntPtr dwData;
        public IntPtr iString;
    }

    private const uint TB_BUTTONCOUNT = 0x0418;
    private const uint TB_GETBUTTON = 0x0417;
    private const uint TB_GETITEMRECT = 0x041D;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    private static readonly object StateLock = new object();
    private static readonly Dictionary<int, IntPtr> Handles = new Dictionary<int, IntPtr>();
    private static readonly HashSet<int> Suspended = new HashSet<int>();
    private static readonly HashSet<int> Minimized = new HashSet<int>();
    private static readonly HashSet<int> WarnedPids = new HashSet<int>();
    private static readonly HashSet<int> WarnedResumePids = new HashSet<int>();
    private static readonly HashSet<int> FocusedNow = new HashSet<int>();
    private static uint lastFgPid;

    // Explicitly-locked windows (Pick or Lock hotkey). Each is suspended when not foreground.
    private sealed class WindowTarget
    {
        public IntPtr Hwnd;
        public uint Pid;
        public bool ChildrenLive;
    }
    private static readonly List<WindowTarget> WindowTargets = new List<WindowTarget>();
    // Pids currently belonging to the process-name target (refreshed periodically).
    private static readonly HashSet<int> NamePids = new HashSet<int>();

    private static string targetName;
    private static volatile bool enabled = true;
    private static volatile int pollMs = 250;
    private static Thread worker;
    private static volatile bool running;

    // true while the user is actively switching windows - either an Alt+Tab gesture
    // (kbdSwitching) or a mouse press over the taskbar / the target window
    // (mouseSwitching). While true the target is never suspended - it must be
    // running so the user's switch can activate it. Kept separate so ending one
    // gesture (e.g. a mouse-up) can never cut an in-progress keyboard gesture short.
    private static volatile bool kbdSwitching;
    private static volatile bool mouseSwitching;
    // true when the most recent mouse press landed in a switch area; the matching
    // mouse-up then extends the keep-running grace so the shell can finish activating.
    private static volatile bool mouseDownWasSwitch;
    // Timestamp of the most recent switch gesture event (Alt+Tab pressed/released, or
    // a taskbar / target-window mouse press/release). The target keeps running for a
    // short grace period after the last event so the shell can finish activating it.
    private static DateTime lastSwitchActivity = DateTime.UtcNow;

    // ---- taskbar button detection ----
    // Windows 10 keeps one "TrayButton" child window per running app; Windows 11
    // renders the buttons in a XAML island that WindowFromPoint cannot hit-test.
    // On Win11 the per-app buttons are found via UI Automation
    // ("Taskbar.TaskListButtonAutomationPeer"). "Legacy" keeps the old behaviour
    // of treating the whole taskbar as a switch area on environments we cannot probe.
    private enum TrayMode { Unknown, Uia, Win32Button, Legacy }
    private static TrayMode trayMode = TrayMode.Unknown;
    private static readonly List<TrayButtonInfo> taskbarButtons = new List<TrayButtonInfo>();
    private static DateTime taskbarButtonsCached = DateTime.MinValue;

    private sealed class TrayButtonInfo
    {
        public int Left, Top, Right, Bottom;
        public string Name;
        public string AppPath;
        public bool Contains(int x, int y)
        { return x >= Left && x <= Right && y >= Top && y <= Bottom; }
    }

    private static readonly List<string> LogBuffer = new List<string>();
    private static readonly object LogLock = new object();
    private static string logFilePath = "";
    private static bool logToFile = true;

    public static Settings Config { get; private set; }

    /// <summary>Raised after ApplySettings() so listeners (hotkeys, tray menu) can re-read the config.</summary>
    public static event Action SettingsApplied;

    public static bool Enabled
    {
        get { return enabled; }
        set { enabled = value; }
    }

    public static int PollMs { get { return pollMs; } }

    public static string CurrentTargetDescription
    {
        get
        {
            lock (StateLock)
            {
                if (targetName != null)
                {
                    string title = FirstMatchingWindowTitle(targetName);
                    string extra = title.Length > 0 ? " (\"" + title + "\")" : "";
                    return "process \"" + targetName + "\"" + extra;
                }
                if (WindowTargets.Count > 0)
                {
                    if (WindowTargets.Count == 1)
                    {
                        IntPtr hw = WindowTargets[0].Hwnd;
                        string title = GetWindowTitle(hw);
                        string extra = title.Length > 0 ? " (\"" + title + "\")" : "";
                        return DescribeProcess((int)WindowTargets[0].Pid) + extra + " (pid " + WindowTargets[0].Pid + ")";
                    }
                    return WindowTargets.Count + " locked windows";
                }
                return "none";
            }
        }
    }

    /// <summary>One line per locked window, each with its current suspended/active state. Empty string when none.</summary>
    public static string LockedTargetsDescription
    {
        get
        {
            lock (StateLock)
            {
                var parts = new List<string>();
                if (targetName != null)
                {
                    string state = "active";
                    foreach (int pid in NamePids)
                    {
                        if (Suspended.Contains(pid)) { state = "SUSPENDED"; break; }
                    }
                    string title = FirstMatchingWindowTitle(targetName);
                    string extra = title.Length > 0 ? " (\"" + title + "\")" : "";
                    parts.Add("process \"" + targetName + "\"" + extra + " - " + state);
                }
                foreach (WindowTarget t in WindowTargets)
                {
                    bool suspended = Suspended.Contains((int)t.Pid);
                    string title = GetWindowTitle(t.Hwnd);
                    string extra = title.Length > 0 ? " (\"" + title + "\")" : "";
                    parts.Add(DescribeProcess((int)t.Pid) + extra + " (pid " + t.Pid + ") - "
                        + (suspended ? "SUSPENDED" : "active"));
                }
                return string.Join("\r\n", parts.ToArray());
            }
        }
    }

    /// <summary>Locked window entries (Pick / Lock hotkey) with their suspended state, for the tray menu.</summary>
    public sealed class LockedEntry
    {
        public int Index;
        public string Label;
        public bool Suspended;
    }

    public static List<LockedEntry> GetLockedWindows()
    {
        var result = new List<LockedEntry>();
        lock (StateLock)
        {
            for (int i = 0; i < WindowTargets.Count; i++)
            {
                WindowTarget t = WindowTargets[i];
                string title = GetWindowTitle(t.Hwnd);
                string extra = title.Length > 0 ? " (\"" + title + "\")" : "";
                result.Add(new LockedEntry
                {
                    Index = i,
                    Label = DescribeProcess((int)t.Pid) + extra + " (pid " + t.Pid + ")",
                    Suspended = Suspended.Contains((int)t.Pid)
                });
            }
        }
        return result;
    }

    public static string TargetStateDescription
    {
        get
        {
            lock (StateLock)
            {
                if (Suspended.Count == 0) return "running";
                if (Suspended.Count == 1)
                {
                    foreach (int pid in Suspended)
                        return "SUSPENDED (" + DescribeProcess(pid) + ")";
                }
                return "SUSPENDED (" + Suspended.Count + " processes)";
            }
        }
    }

    private static string FirstMatchingWindowTitle(string name)
    {
        try
        {
            foreach (Process p in Process.GetProcessesByName(name))
            {
                IntPtr mw = p.MainWindowHandle;
                if (mw == IntPtr.Zero) continue;
                string t = GetWindowTitle(mw);
                if (t.Length > 0) return t;
            }
        }
        catch { }
        return "";
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        try
        {
            var sb = new StringBuilder(512);
            if (GetWindowText(hwnd, sb, sb.Capacity) > 0) return sb.ToString();
        }
        catch { }
        return "";
    }

    private static bool IsFullscreen(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        RECT r;
        if (!GetWindowRect(hwnd, out r)) return false;
        Screen sc = Screen.FromHandle(hwnd);
        if (sc == null) return false;
        var b = sc.Bounds;
        const int tolerance = 8;
        return r.Left <= b.Left + tolerance && r.Top <= b.Top + tolerance
            && r.Right >= b.Right - tolerance && r.Bottom >= b.Bottom - tolerance
            && r.Left >= b.Left - tolerance && r.Top >= b.Top - tolerance
            && r.Right <= b.Right + tolerance && r.Bottom <= b.Bottom + tolerance;
    }

    // Core Windows processes used to classify a pid as "system" when its image
    // path cannot be read (protected/elevated process). Never auto-lock these.
    private static readonly HashSet<string> CoreSystemNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "system", "idle", "registry", "smss", "csrss", "wininit", "winlogon", "services",
      "lsass", "svchost", "dwm", "fontdrvhost", "sihost", "explorer" };

    private static bool IsSystemProcess(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h != IntPtr.Zero)
        {
            try
            {
                var sb = new StringBuilder(1024);
                uint size = (uint)sb.Capacity;
                // Limited query survives elevation where MainModule (needs more access) throws.
                if (QueryFullProcessImageName(h, 0, sb, ref size))
                    return sb.ToString(0, (int)size).StartsWith(Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows", StringComparison.OrdinalIgnoreCase);
            }
            catch { }
            finally { CloseHandle(h); }
        }
        try
        {
            return CoreSystemNames.Contains(Process.GetProcessById((int)pid).ProcessName);
        }
        catch { return true; }
    }

    private static IntPtr FindFullscreenWindow()
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr hwnd, IntPtr lp)
        {
            if (!IsWindowVisible(hwnd)) return true;
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (pid == 0 || pid == (uint)Process.GetCurrentProcess().Id) return true;
            if (IsSystemProcess(pid)) return true;
            if (!IsFullscreen(hwnd)) return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    public static string LogFile { get { return logFilePath; } }

    public static void Initialize()
    {
        Config = SettingsStore.Load();
        logToFile = Config.LogToFile;
        pollMs = Config.PollIntervalMsClamped;
        logFilePath = Path.Combine(SettingsStore.ConfigDir, "focuslock.log");
        try
        {
            if (logToFile && File.Exists(logFilePath) && new FileInfo(logFilePath).Length > 2 * 1024 * 1024)
                File.WriteAllText(logFilePath, "--- log truncated ---\r\n");
        }
        catch { }
    }

    public static void Start()
    {
        running = true;
        worker = new Thread(WorkerLoop);
        worker.IsBackground = true;
        worker.Name = "ProcessSuspenderWorker";
        worker.Start();
        Log("[INFO] ProcessSuspender started. Target: " + CurrentTargetDescription + ", poll: " + pollMs + "ms");
    }

    public static void Stop()
    {
        running = false;
        if (worker != null) worker.Join(3000);
        if (Config != null && Config.ResumeOnExit)
        {
            ResumeAll();
            Log("[INFO] ProcessSuspender stopped. All targets resumed.");
        }
        else
        {
            Log("[INFO] ProcessSuspender stopped (resume-on-exit disabled).");
        }
    }

    public static void ApplySettings()
    {
        if (Config == null) return;
        pollMs = Config.PollIntervalMsClamped;
        logToFile = Config.LogToFile;
        Log("[INFO] Settings applied: poll=" + pollMs + "ms, suspendWhenMinimized=" + Config.SuspendWhenMinimized
            + ", resumeOnExit=" + Config.ResumeOnExit + ", logToFile=" + logToFile);
        if (!string.IsNullOrEmpty(Config.ProcessName))
        {
            string cur;
            lock (StateLock) cur = targetName;
            if (cur == null || !cur.Equals(Config.ProcessName, StringComparison.OrdinalIgnoreCase))
                SetTargetByName(Config.ProcessName);
        }
        ApplyNeverSuspendList();
        if (SettingsApplied != null) SettingsApplied();
    }

    /// <summary>
    /// The never-suspend list may have changed. Resume any whitelisted processes that
    /// were suspended before they were whitelisted, and note when the current target
    /// itself is whitelisted (it will never be suspended).
    /// </summary>
    private static void ApplyNeverSuspendList()
    {
        lock (StateLock)
        {
            foreach (int pid in Suspended.ToList())
            {
                if (IsNeverSuspendPid((uint)pid))
                {
                    IntPtr h;
                    if (Handles.TryGetValue(pid, out h) && ResumeFully(h))
                    {
                        Suspended.Remove(pid);
                        Log("[RESUME]  " + DescribeProcess(pid) + " (pid " + pid
                            + ") - whitelisted in never-suspend list");
                    }
                }
            }
            if (targetName != null && IsNeverSuspendName(targetName))
                Log("[INFO] Target \"" + targetName
                    + "\" is in the never-suspend list - it will never be suspended.");
        }
    }

    public static void SetTargetByName(string name)
    {
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name.Substring(0, name.Length - 4);
        if (IsNeverSuspendName(name))
        {
            Log("[WARN] \"" + name + "\" is in the never-suspend list - not locking it.");
            return;
        }
        lock (StateLock)
        {
            DropAllHandlesLocked();
            WarnedPids.Clear();
            targetName = name;
            NamePids.Clear();
            WindowTargets.Clear();
            Log("[INFO] Target set to process \"" + name + "\"");
        }
        SaveLockedWindows();
    }

    public static void SetTargetWindow(IntPtr hwnd, uint pid)
    {
        if (IsNeverSuspendPid(pid))
        {
            Log("[WARN] \"" + DescribeProcess((int)pid) + "\" is in the never-suspend list - not locking it.");
            return;
        }
        lock (StateLock)
        {
            targetName = null;
            NamePids.Clear();
            WindowTargets.Clear();
            WindowTarget t = FindWindowTarget(hwnd);
            if (t == null) { t = new WindowTarget(); WindowTargets.Add(t); }
            t.Hwnd = hwnd;
            t.Pid = pid;
            OpenHandleLocked((int)pid);
            Log("[INFO] Target locked onto " + DescribeProcess((int)pid) + " (pid " + pid + ")");
        }
        SaveLockedWindows();
    }

    /// <summary>Adds a window to the locked set without clearing the others (used by multi-select Pick).</summary>
    public static void AddTargetWindow(IntPtr hwnd, uint pid)
    {
        if (IsNeverSuspendPid(pid))
        {
            Log("[WARN] \"" + DescribeProcess((int)pid) + "\" is in the never-suspend list - not locking it.");
            return;
        }
        lock (StateLock)
        {
            if (FindWindowTarget(hwnd) != null) return;
            targetName = null;
            NamePids.Clear();
            WindowTarget t = new WindowTarget { Hwnd = hwnd, Pid = pid };
            WindowTargets.Add(t);
            OpenHandleLocked((int)pid);
            Log("[INFO] Added locked window " + DescribeProcess((int)pid) + " (pid " + pid + "). Total: " + WindowTargets.Count);
        }
        SaveLockedWindows();
    }

    /// <summary>Adds (or removes, if focused) the given window to the locked set. Returns a short result message.</summary>
    public static string ToggleWindowTarget(IntPtr hwnd, uint pid)
    {
        if (IsNeverSuspendPid(pid))
        {
            string proc = DescribeProcess((int)pid);
            Log("[WARN] \"" + proc + "\" is in the never-suspend list - not locking it.");
            return proc + " is in the never-suspend list - not locked";
        }
        lock (StateLock)
        {
            WindowTarget existing = FindWindowTarget(hwnd);
            if (existing != null)
            {
                WindowTargets.Remove(existing);
                DropHandleLocked((int)existing.Pid);
                if (existing.ChildrenLive) DropChildrenOf((int)existing.Pid);
                string name = DescribeProcess((int)existing.Pid);
                Log("[INFO] Unlocked " + name + " (pid " + existing.Pid + ").");
                SaveLockedWindows();
                return "Unlocked " + name;
            }
            targetName = null;
            NamePids.Clear();
            WindowTarget t = new WindowTarget { Hwnd = hwnd, Pid = pid };
            WindowTargets.Add(t);
            OpenHandleLocked((int)pid);
            string name2 = DescribeProcess((int)pid);
            Log("[INFO] Locked " + name2 + " (pid " + pid + "). Total locked: " + WindowTargets.Count);
            SaveLockedWindows();
            return "Locked " + name2;
        }
    }

    /// <summary>Locks the current foreground window into the set, or unlocks it if it is already locked.</summary>
    public static string LockToForeground()
    {
        IntPtr fg = GetForegroundWindow();
        uint pid;
        if (fg == IntPtr.Zero || GetWindowThreadProcessId(fg, out pid) == 0 || pid == 0)
        {
            Log("[WARN] No focused window to lock onto.");
            return null;
        }
        if (pid == (uint)Process.GetCurrentProcess().Id)
        {
            Log("[WARN] Foreground window belongs to ProcessSuspender itself - bring the window to focus and try again.");
            return null;
        }
        return ToggleWindowTarget(fg, pid);
    }

    /// <summary>Removes a locked window by index (used by the tray menu).</summary>
    public static void RemoveWindowTarget(int index)
    {
        lock (StateLock)
        {
            if (index < 0 || index >= WindowTargets.Count) return;
            WindowTarget t = WindowTargets[index];
            WindowTargets.RemoveAt(index);
            DropHandleLocked((int)t.Pid);
            DropChildrenOf((int)t.Pid);
            Log("[INFO] Unlocked " + DescribeProcess((int)t.Pid) + " (pid " + t.Pid + ") from tray.");
        }
        SaveLockedWindows();
    }

    /// <summary>Re-locks windows saved in the config (called once at startup).</summary>
    public static void RestoreLockedWindows()
    {
        if (Config == null || Config.LockedWindows == null) return;
        foreach (LockedWindowEntry e in Config.LockedWindows)
        {
            IntPtr hwnd = FindMainWindowForPid((uint)e.Pid);
            if (hwnd != IntPtr.Zero)
            {
                AddTargetWindow(hwnd, (uint)e.Pid);
            }
            else if (!string.IsNullOrWhiteSpace(e.ProcessName))
            {
                // pid gone (process restarted) -> recover by process name.
                // ponytail: re-locks every instance with a visible window; child
                // processes (mw=0) are skipped, so this stays bounded.
                bool relocked = false;
                foreach (Process p in Process.GetProcessesByName(e.ProcessName))
                {
                    IntPtr mw = p.MainWindowHandle;
                    if (mw == IntPtr.Zero) continue;
                    AddTargetWindow(mw, (uint)p.Id);
                    relocked = true;
                }
                Log(relocked
                    ? "[INFO] Saved locked window " + e.ProcessName + " (pid " + e.Pid + ") restarted - re-locked by name."
                    : "[INFO] Saved locked window " + e.ProcessName + " (pid " + e.Pid + ") not running - skipped on restore.");
            }
            else
            {
                Log("[INFO] Saved locked window (pid " + e.Pid + ") not running - skipped on restore.");
            }
        }
    }

    /// <summary>Persists the current locked-window set (process name + pid) to the config.</summary>
    public static void SaveLockedWindows()
    {
        if (Config == null) return;
        lock (StateLock)
        {
            Config.LockedWindows = new List<LockedWindowEntry>();
            foreach (WindowTarget t in WindowTargets)
            {
                string name = "";
                try { name = Process.GetProcessById((int)t.Pid).ProcessName; } catch { }
                Config.LockedWindows.Add(new LockedWindowEntry { ProcessName = name, Pid = (int)t.Pid });
            }
        }
        SettingsStore.Save(Config);
    }

    private static IntPtr FindMainWindowForPid(uint pid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr lp)
        {
            uint p;
            GetWindowThreadProcessId(h, out p);
            if (p != pid) return true;
            if (!IsWindowVisible(h)) return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private static WindowTarget FindWindowTarget(IntPtr hwnd)
    {
        foreach (WindowTarget t in WindowTargets)
            if (t.Hwnd == hwnd) return t;
        return null;
    }

    public static void ResumeAll()
    {
        lock (StateLock)
        {
            foreach (int pid in Suspended.ToList())
            {
                IntPtr h;
                if (Handles.TryGetValue(pid, out h) && ResumeFully(h))
                {
                    Suspended.Remove(pid);
                    Log("[RESUME]  " + DescribeProcess(pid) + " (pid " + pid + ")");
                }
            }
        }
    }

    /// <summary>
    /// The user pressed or released Alt+Tab (a real switch gesture, as detected by
    /// the keyboard hook). The moment the gesture starts we resume the target - a
    /// suspended process can never receive window activation, so it must be running
    /// before the user's switch can land on it. The last event timestamp keeps it
    /// running for a short grace period after the gesture ends; if the user settles
    /// on another window, the next poll re-suspends it.
    /// </summary>
    public static void OnSwitchStateChanged(bool active)
    {
        kbdSwitching = active;
        lastSwitchActivity = DateTime.UtcNow;
        if (active) ResumeSuspended("switch gesture started");
    }

    /// <summary>
    /// Left mouse button down/up somewhere on screen. If the press is over the
    /// taskbar (taskbar buttons activate on mouse-up, and a frozen game cannot be
    /// activated) or directly over the target window, treat it as a window switch
    /// attempt: resume the target so the click can activate it, and keep it running
    /// for a short grace period after the matching mouse-up.
    /// </summary>
    public static void OnMouseSwitchEvent(bool down, int x, int y)
    {
        if (down)
        {
            if (!IsAnySuspended())
            {
                // Nothing to resume, but keep the hold alive through the physical
                // press so the worker cannot freeze the target mid-gesture. Skip
                // the expensive hit-testing (EnumWindows / UIA / process enum) -
                // this runs inside the low-level mouse hook.
                mouseSwitching = true;
                mouseDownWasSwitch = false;
                lastSwitchActivity = DateTime.UtcNow;
                return;
            }
            string reason = SwitchAreaReason(x, y);
            mouseDownWasSwitch = reason != null;
            if (!mouseDownWasSwitch) return;
            mouseSwitching = true;
            lastSwitchActivity = DateTime.UtcNow;
            ResumeSuspended(reason);
            return;
        }
        mouseSwitching = false;
        if (mouseDownWasSwitch)
        {
            mouseDownWasSwitch = false;
            lastSwitchActivity = DateTime.UtcNow;
        }
    }

    private static void ResumeSuspended(string reason)
    {
        lock (StateLock)
        {
            foreach (int pid in Suspended.ToList())
            {
                IntPtr h;
                if (Handles.TryGetValue(pid, out h) && ResumeFully(h))
                {
                    Suspended.Remove(pid);
                    Log("[RESUME]  " + DescribeProcess(pid) + " (pid " + pid + ") - " + reason);
                }
            }
        }
    }

    /// <summary>
    /// Returns a short reason string when the click at (x, y) is treated as a
    /// switch back to the target (so the log says what matched), else null.
    /// A click counts only over the target's taskbar button or directly over the
    /// target window - and never when it lands inside the currently focused
    /// window: the user is using that window, not returning to the game, even if
    /// the hit test fell through to the game window behind it (transparent or
    /// disabled regions, coordinate quirks). Nor when the click is on a
    /// fullscreen/maximized target that is just the backdrop behind the window
    /// the user is actually working in.
    /// </summary>
    private static string SwitchAreaReason(int x, int y)
    {
        if (IsInTaskbar(x, y))
        {
            string tb = TaskbarClickReason(x, y);
            if (tb != null) return tb;
        }
        IntPtr hit = WindowFromPoint(x, y);
        if (hit == IntPtr.Zero) return null;
        if (PointInsideForegroundWindow(x, y)) return null;

        // Snapshot under lock; the matching below enumerates processes and reads
        // window state, which must not run while holding StateLock on the hook thread.
        List<WindowTarget> targets;
        string name;
        lock (StateLock)
        {
            targets = WindowTargets.ToList();
            name = targetName;
        }

        if (targets.Count > 0)
        {
            foreach (WindowTarget t in targets)
            {
                if (hit == t.Hwnd || IsChild(t.Hwnd, hit))
                {
                    if (IsBackdropClick(x, y, t.Hwnd)) return null;
                    return "click on target window";
                }
            }
            return null;
        }
        if (name == null) return null;
        // Only the process's main window(s) - and their children - count as
        // a switch-back target. A second window of the game process (a
        // launcher, a companion window) must not unfreeze the game.
        try
        {
            foreach (Process p in Process.GetProcessesByName(name))
            {
                IntPtr mw = p.MainWindowHandle;
                if (mw == IntPtr.Zero) continue;
                if (hit == mw || IsChild(mw, hit))
                {
                    if (!IsWindowVisible(hit)) return null;
                    if (IsBackdropClick(x, y, mw)) return null;
                    return "click on target window";
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>True when (x, y) lies within the rectangle of the currently focused window.</summary>
    private static bool PointInsideForegroundWindow(int x, int y)
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        RECT r;
        if (!GetWindowRect(fg, out r)) return false;
        return x >= r.Left && x <= r.Right && y >= r.Top && y <= r.Bottom;
    }

    /// <summary>
    /// True when the click at (x, y) is on a fullscreen/maximized target that is
    /// merely the backdrop behind the window the user is actually using: the
    /// target fills the same monitor as the focused window, and the focused
    /// window belongs to another process. Clicking such a target's visible
    /// slivers is accidental - the user is working in the other window, not
    /// returning to the game - so it must not unfreeze it.
    /// </summary>
    private static bool IsBackdropClick(int x, int y, IntPtr target)
    {
        if (!CoversMonitor(target)) return false;
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == target) return false;
        uint fgPid, tPid;
        GetWindowThreadProcessId(fg, out fgPid);
        GetWindowThreadProcessId(target, out tPid);
        if (fgPid == tPid) return false;
        RECT r;
        if (!GetWindowRect(fg, out r)) return false;
        Screen fgScreen = Screen.FromPoint(new Point(r.Left + (r.Right - r.Left) / 2, r.Top + (r.Bottom - r.Top) / 2));
        Screen clickScreen = Screen.FromPoint(new Point(x, y));
        return fgScreen != null && clickScreen != null && fgScreen.Equals(clickScreen);
    }

    /// <summary>True when the window covers nearly a whole monitor (fullscreen or maximized).</summary>
    private static bool CoversMonitor(IntPtr hwnd)
    {
        RECT r;
        if (!GetWindowRect(hwnd, out r)) return false;
        Screen sc = Screen.FromHandle(hwnd);
        if (sc == null) return false;
        var b = sc.Bounds;
        return (r.Right - r.Left) >= b.Width - 64 && (r.Bottom - r.Top) >= b.Height - 64;
    }

    /// <summary>
    /// Decides whether a click inside a taskbar counts as "switch back to the target".
    /// Tries, in order: (1) title match against enumerated taskbar buttons (trimmed,
    /// suffix-tolerant - works when the button label is clipped or has Win10's
    /// " - N running windows" / " pinned" suffixes), (2) a title-independent match via
    /// the Win10 taskbar toolbar button's dwData -> PID (only exists on Win7/8-style
    /// toolbars; on XAML/DComposition Win10 taskbars there is no ToolbarWindow32 and
    /// no window per button, so this is a no-op there), (3) a last-resort region
    /// fallback: on modern Win10 taskbars a frozen window's button cannot be mapped
    /// to a pid by any API (UIA peers report explorer's pid, names are app names not
    /// window titles), so a click inside the taskbar's button region while at least
    /// one process is suspended counts as a switch-back attempt - the worker poll
    /// re-suspends within the grace window if the user actually clicked another app.
    /// </summary>
    private static string TaskbarClickReason(int x, int y)
    {
        RefreshTaskbarButtons();
        if (trayMode == TrayMode.Legacy) return "taskbar click (compat mode)"; // environment not detectable: old behaviour

        string title = GetTargetTitle();

        // (1) strict title match, tolerant of trailing spaces, clipped labels and
        //     Win10 button-name suffixes (" - 1 running window", " pinned").
        if (title.Length > 0)
        {
            string t = title.Trim();
            lock (taskbarButtons)
            {
                for (int i = 0; i < taskbarButtons.Count; i++)
                {
                    TrayButtonInfo b = taskbarButtons[i];
                    if (!b.Contains(x, y)) continue;
                    string n = StripButtonSuffixes(b.Name).Trim();
                    if (n.Length > 0 &&
                        (n.StartsWith(t, StringComparison.OrdinalIgnoreCase) ||
                         t.StartsWith(n, StringComparison.OrdinalIgnoreCase)))
                        return "taskbar click";
                }
            }
        }

        // (2) title-independent match via toolbar button dwData (Win7/8 style taskbars).
        IntPtr appHwnd = TaskbarButtonAppWindowAt(x, y);
        if (appHwnd != IntPtr.Zero)
        {
            uint pid;
            GetWindowThreadProcessId(appHwnd, out pid);
            if (pid != 0 && IsTargetPid(pid)) return "taskbar click (pid match)";
        }

        // Win7/8-style toolbar: the click was resolved to a different app's button
        // (appHwnd != Zero but pid didn't match) - correctly not a switch-back.
        // On modern Win10 taskbars appHwnd is always Zero and the appid path below
        // handles the same strictness via the button's executable path.
        if (appHwnd != IntPtr.Zero)
        {
            if (IsAnySuspended())
                Log("[WARN] Taskbar click at (" + x + "," + y + ") ignored: that button belongs to another app.");
            return null;
        }

        // (3) definitive match: on modern Win10 taskbars each button exposes the
        //     app's executable path in its UIA AutomationId ("Appid: <exe path>").
        //     The clicked button's path is compared against the target process's
        //     exe path - a click on any OTHER app's button is refused, exactly like
        //     the old strict title path but without relying on window titles.
        TrayButtonInfo hit = ButtonUnder(x, y);
        if (hit != null && hit.AppPath.Length > 0)
        {
            string p = StripAppidPrefix(hit.AppPath);
            bool verifiable = RefreshTargetExePaths();
            if (verifiable && MatchesTargetPath(p)) return "taskbar click (appid match)";
            if (verifiable)
            {
                if (IsAnySuspended())
                    Log("[WARN] Taskbar click at (" + x + "," + y + ") ignored: that button belongs to another app (\"" + hit.Name + "\").");
                return null;
            }
            // target exe path unreadable: fall through to the region fallback
        }

        // (4) last resort: the click is over a button that could not be mapped to
        //     any app (no AutomationId), a secondary-monitor taskbar (buttons not
        //     enumerated), or enumeration is broken entirely - in those cases a
        //     click while something is suspended counts as a switch-back attempt.
        //     Clicks on empty taskbar gaps/edges are never switch-backs.
        if (IsAnySuspended())
        {
            bool noButtons;
            lock (taskbarButtons) noButtons = taskbarButtons.Count == 0;
            if (hit != null || ClickInSecondaryTaskbar(x, y) || noButtons)
            {
                if (title.Length == 0)
                    Log("[WARN] Target has no readable window title - taskbar click treated as switch-back attempt.");
                return "taskbar click (button region)";
            }
            Log("[WARN] Taskbar click at (" + x + "," + y + ") ignored: not on any taskbar button (gap or edge).");
        }
        return null;
    }

    private static TrayButtonInfo ButtonUnder(int x, int y)
    {
        lock (taskbarButtons)
        {
            foreach (TrayButtonInfo b in taskbarButtons)
                if (b.Contains(x, y)) return b;
        }
        return null;
    }

    /// <summary>Strips Win10 taskbar button name suffixes: " - 3 running windows", " pinned".</summary>
    private static string StripButtonSuffixes(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        int idx = name.IndexOf(" - ", StringComparison.Ordinal);
        if (idx > 0) name = name.Substring(0, idx);
        return name.Replace(" pinned", "");
    }

    private static string StripAppidPrefix(string s)
    {
        if (s.StartsWith("Appid:", StringComparison.OrdinalIgnoreCase)) s = s.Substring(6);
        return s.Trim();
    }

    private static readonly List<string> targetExePaths = new List<string>();
    private static DateTime targetExePathsAt = DateTime.MinValue;

    /// <summary>
    /// Caches the target process's exe path(s) for 5s. Returns false when the target
    /// path cannot be resolved (target gone, access denied) - callers must then treat
    /// appid matching as unverifiable and fall back instead of refusing the click.
    /// </summary>
    private static bool RefreshTargetExePaths()
    {
        if ((DateTime.UtcNow - targetExePathsAt).TotalSeconds < 5) return targetExePaths.Count > 0;
        targetExePathsAt = DateTime.UtcNow;
        targetExePaths.Clear();
        try
        {
            uint pid = 0;
            lock (StateLock)
            {
                if (WindowTargets.Count > 0)
                    pid = WindowTargets[0].Pid;
                else if (targetName != null)
                {
                    foreach (Process p in Process.GetProcessesByName(targetName))
                    { AddExePath(p); return targetExePaths.Count > 0; }
                }
            }
            if (pid != 0)
            {
                using (Process p = Process.GetProcessById((int)pid))
                    AddExePath(p);
            }
            else if (targetName != null)
            {
                foreach (Process p in Process.GetProcessesByName(targetName))
                    AddExePath(p);
            }
        }
        catch { }
        return targetExePaths.Count > 0;
    }

    private static void AddExePath(Process p)
    {
        try
        {
            string f = p.MainModule.FileName;
            if (!string.IsNullOrEmpty(f))
                targetExePaths.Add(f.Replace('\\', '/').ToLowerInvariant());
        }
        catch { }
    }

    /// <summary>True when appPath (an exe path from a taskbar button's AutomationId) belongs to the target process.</summary>
    private static bool MatchesTargetPath(string appPath)
    {
        if (targetExePaths.Count == 0) return false;
        string p = appPath.Replace('\\', '/').ToLowerInvariant();
        foreach (string t in targetExePaths)
        {
            if (p == t) return true;
            int slash = t.LastIndexOf('/');
            if (slash >= 0 && p.EndsWith(t.Substring(slash), StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>True when (x, y) lies over any secondary-monitor taskbar window.</summary>
    private static bool ClickInSecondaryTaskbar(int x, int y)
    {
        foreach (IntPtr bar in EnumTaskbars())
        {
            var sb = new StringBuilder(128);
            GetClassName(bar, sb, sb.Capacity);
            string cls = sb.ToString();
            if (cls == "Shell_SecondaryTrayWnd")
            {
                RECT r;
                if (GetWindowRect(bar, out r) && x >= r.Left && x <= r.Right && y >= r.Top && y <= r.Bottom)
                    return true;
            }
        }
        return false;
    }

    private static bool IsAnySuspended()
    {
        lock (StateLock) return Suspended.Count > 0;
    }

    /// <summary>True when the pid belongs to the current target (window mode or process-name mode).</summary>
    private static bool IsTargetPid(uint pid)
    {
        lock (StateLock)
        {
            foreach (WindowTarget t in WindowTargets)
                if (t.Pid == pid) return true;
            if (targetName != null)
            {
                try
                {
                    foreach (Process p in Process.GetProcessesByName(targetName))
                        if ((uint)p.Id == pid) return true;
                }
                catch { }
            }
        }
        return false;
    }

    /// <summary>
    /// Win10 taskbar toolbar buttons (ToolbarWindow32 inside MSTaskSwWClass) store the
    /// window handle of the app they represent in TBBUTTON.dwData. Returns that handle
    /// for the button under (x, y), or IntPtr.Zero when the click is not on a button or
    /// the toolbar cannot be read (Win11 XAML taskbar, tweaks, etc.).
    /// </summary>
    private static IntPtr TaskbarButtonAppWindowAt(int x, int y)
    {
        foreach (IntPtr bar in EnumTaskbars())
        {
            IntPtr toolbar = FindTrayToolbar(bar);
            if (toolbar == IntPtr.Zero) continue;
            int count = (int)SendMessage(toolbar, TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero);
            for (int i = 0; i < count; i++)
            {
                RECT r = new RECT();
                if (SendMessage(toolbar, TB_GETITEMRECT, (IntPtr)i, ref r) == IntPtr.Zero) continue;
                POINT p1 = new POINT { X = r.Left, Y = r.Top };
                POINT p2 = new POINT { X = r.Right, Y = r.Bottom };
                ClientToScreen(toolbar, ref p1);
                ClientToScreen(toolbar, ref p2);
                r.Left = p1.X; r.Top = p1.Y; r.Right = p2.X; r.Bottom = p2.Y;
                if (x >= r.Left && x <= r.Right && y >= r.Top && y <= r.Bottom)
                {
                    TBBUTTON b = new TBBUTTON();
                    if (SendMessage(toolbar, TB_GETBUTTON, (IntPtr)i, ref b) != IntPtr.Zero)
                        return b.dwData;
                    return IntPtr.Zero;
                }
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>Win10 taskbar structure: Shell_TrayWnd -> ReBarWindow32 -> MSTaskSwWClass -> ToolbarWindow32.</summary>
    private static IntPtr FindTrayToolbar(IntPtr tray)
    {
        IntPtr rebar = FindWindowEx(tray, IntPtr.Zero, "ReBarWindow32", null);
        IntPtr sw = rebar == IntPtr.Zero ? FindWindowEx(tray, IntPtr.Zero, "MSTaskSwWClass", null)
                                         : FindWindowEx(rebar, IntPtr.Zero, "MSTaskSwWClass", null);
        if (sw == IntPtr.Zero) return IntPtr.Zero;
        return FindWindowEx(sw, IntPtr.Zero, "ToolbarWindow32", null);
    }

    private static void RefreshTaskbarButtons()
    {
        if ((DateTime.UtcNow - taskbarButtonsCached).TotalMilliseconds < 400) return;
        taskbarButtonsCached = DateTime.UtcNow;
        IntPtr tray = FindTaskbarWindow();
        lock (taskbarButtons) taskbarButtons.Clear();
        if (tray == IntPtr.Zero) { trayMode = TrayMode.Legacy; return; }
        if (trayMode == TrayMode.Unknown) trayMode = DetectTrayMode(tray);
        if (trayMode == TrayMode.Uia) FillFromUia(tray);
        else if (trayMode == TrayMode.Win32Button) FillFromWin32(tray);
    }

    private static TrayMode DetectTrayMode(IntPtr tray)
    {
        try
        {
            AutomationElement root = AutomationElement.FromHandle(tray);
            if (root != null)
            {
                var walker = TreeWalker.ControlViewWalker;
                if (HasTaskListButtons(root, walker)) return TrayMode.Uia;
            }
        }
        catch { }
        foreach (IntPtr c in DescendantWindows(tray))
        {
            var sb = new StringBuilder(128);
            GetClassName(c, sb, sb.Capacity);
            if (sb.ToString() == "TrayButton") return TrayMode.Win32Button;
        }
        return TrayMode.Legacy;
    }

    private static bool HasTaskListButtons(AutomationElement parent, TreeWalker walker)
    {
        var el = walker.GetFirstChild(parent);
        while (el != null)
        {
            try
            {
                string cls = el.Current.ClassName ?? "";
                if (cls.Contains("TaskListButton")) return true;
                if (HasTaskListButtons(el, walker)) return true;
            }
            catch { }
            el = walker.GetNextSibling(el);
        }
        return false;
    }

    private static void FillFromUia(IntPtr tray)
    {
        try
        {
            AutomationElement root = AutomationElement.FromHandle(tray);
            if (root == null) return;
            var walker = TreeWalker.ControlViewWalker;
            lock (taskbarButtons)
            {
                FillUiaWalk(root, walker);
            }
        }
        catch { lock (taskbarButtons) taskbarButtons.Clear(); }
    }

    private static void FillUiaWalk(AutomationElement parent, TreeWalker walker)
    {
        var el = walker.GetFirstChild(parent);
        while (el != null)
        {
            try
            {
                string cls = el.Current.ClassName ?? "";
                if (cls.Contains("TaskListButton"))
                {
                    System.Windows.Rect r = el.Current.BoundingRectangle;
                    if (r.Width > 0 && r.Height > 0)
                    {
                        taskbarButtons.Add(new TrayButtonInfo
                        {
                            Left = (int)r.Left, Top = (int)r.Top,
                            Right = (int)(r.Left + r.Width), Bottom = (int)(r.Top + r.Height),
                            Name = el.Current.Name,
                            AppPath = el.Current.AutomationId ?? ""
                        });
                    }
                }
                else
                {
                    FillUiaWalk(el, walker);
                }
            }
            catch { }
            el = walker.GetNextSibling(el);
        }
    }

    private static void FillFromWin32(IntPtr tray)
    {
        foreach (IntPtr c in DescendantWindows(tray))
        {
            var sb = new StringBuilder(128);
            GetClassName(c, sb, sb.Capacity);
            if (sb.ToString() != "TrayButton") continue;
            RECT r;
            if (!GetWindowRect(c, out r)) continue;
            var tb = new StringBuilder(256);
            GetWindowText(c, tb, tb.Capacity);
            lock (taskbarButtons)
                taskbarButtons.Add(new TrayButtonInfo
                {
                    Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom,
                    Name = tb.ToString()
                });
        }
    }

    /// <summary>All descendant windows of root (breadth-first, DFS via stack).</summary>
    private static IEnumerable<IntPtr> DescendantWindows(IntPtr root)
    {
        var stack = new Stack<IntPtr>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            IntPtr parent = stack.Pop();
            IntPtr child = IntPtr.Zero;
            while (true)
            {
                child = FindWindowEx(parent, child, null, null);
                if (child == IntPtr.Zero) break;
                yield return child;
                stack.Push(child);
            }
        }
    }

    private static IntPtr FindTaskbarWindow()
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            var sb = new StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            if (sb.ToString() == "Shell_TrayWnd") { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static string GetTargetTitle()
    {
        IntPtr hwnd = IntPtr.Zero;
        string name = null;
        lock (StateLock)
        {
            if (targetName != null) name = targetName;
            else if (WindowTargets.Count > 0) hwnd = WindowTargets[0].Hwnd;
        }
        try
        {
            if (name != null)
            {
                foreach (Process p in Process.GetProcessesByName(name))
                {
                    if (p.MainWindowHandle != IntPtr.Zero)
                    {
                        string t = GetWindowTitle(p.MainWindowHandle);
                        if (t.Length > 0) return t;
                    }
                }
                return "";
            }
            if (hwnd != IntPtr.Zero) return GetWindowTitle(hwnd);
        }
        catch { }
        return "";
    }

    private static readonly string[] TaskbarClassNames = { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

    /// <summary>All taskbars on all monitors (primary is Shell_TrayWnd, additional are Shell_SecondaryTrayWnd).</summary>
    private static readonly List<IntPtr> taskbarWindows = new List<IntPtr>();
    private static DateTime taskbarWindowsAt = DateTime.MinValue;

    /// <summary>
    /// All taskbars on all monitors, cached for 500ms. Runs inside the low-level
    /// mouse hook on every left click, so the full EnumWindows scan must not run
    /// per event. Callers must treat the returned list as read-only.
    /// </summary>
    private static List<IntPtr> EnumTaskbars()
    {
        if ((DateTime.UtcNow - taskbarWindowsAt).TotalMilliseconds >= 500)
        {
            taskbarWindowsAt = DateTime.UtcNow;
            taskbarWindows.Clear();
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                var sb = new StringBuilder(128);
                GetClassName(h, sb, sb.Capacity);
                string cls = sb.ToString();
                for (int i = 0; i < TaskbarClassNames.Length; i++)
                {
                    if (cls == TaskbarClassNames[i])
                    {
                        taskbarWindows.Add(h);
                        break;
                    }
                }
                return true;
            }, IntPtr.Zero);
        }
        return taskbarWindows;
    }

    private static bool IsInTaskbar(int x, int y)
    {
        foreach (IntPtr h in EnumTaskbars())
        {
            RECT r;
            if (GetWindowRect(h, out r)
                && x >= r.Left - 8 && x <= r.Right + 8
                && y >= r.Top - 8 && y <= r.Bottom + 8)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Combined freeze/unfreeze toggle. While frozen, the target is suspended on
    /// defocus; unfreezing resumes everything, disables suspending and brings the
    /// target window back to the foreground.
    /// </summary>
    public static void ToggleFreeze()
    {
        if (enabled)
        {
            enabled = false;
            kbdSwitching = false;
            mouseSwitching = false;
            mouseDownWasSwitch = false;
            ResumeAll();
            FocusTargetWindow();
            Log("[UNFREEZE] Suspend-on-defocus turned off - targets resumed.");
        }
        else
        {
            enabled = true;
            // clear any stale switch state left over from while the app was off, so a
            // missed gesture-end event can't delay the first suspension after enabling
            kbdSwitching = false;
            mouseSwitching = false;
            mouseDownWasSwitch = false;
            Log("[FREEZE] Suspend-on-defocus turned on.");
        }
    }

    private static void FocusTargetWindow()
    {
        IntPtr hwnd = IntPtr.Zero;
        lock (StateLock)
        {
            if (WindowTargets.Count > 0)
                hwnd = WindowTargets[WindowTargets.Count - 1].Hwnd;
            else if (targetName != null)
            {
                try
                {
                    foreach (Process p in Process.GetProcessesByName(targetName))
                    {
                        if (p.MainWindowHandle != IntPtr.Zero)
                        {
                            hwnd = p.MainWindowHandle;
                            break;
                        }
                    }
                }
                catch { }
            }
        }
        if (hwnd == IntPtr.Zero)
        {
            Log("[INFO] Unfreeze: no target window to focus.");
            return;
        }
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9); // SW_RESTORE
        SetForegroundWindow(hwnd);
        Log("[FOCUS] Brought target window to the foreground.");
    }

    public static void Log(string msg)
    {
        string line = DateTime.Now.ToString("HH:mm:ss") + " " + msg;
        lock (LogLock)
        {
            LogBuffer.Add(line);
            while (LogBuffer.Count > LogCapacity) LogBuffer.RemoveAt(0);
            if (logToFile && logFilePath.Length > 0)
            {
                try { File.AppendAllText(logFilePath, line + "\r\n"); }
                catch { }
            }
        }
    }

    public static string[] GetLogLines()
    {
        lock (LogLock) return LogBuffer.ToArray();
    }

    private static void WorkerLoop()
    {
        int tick = 0;
        while (running)
        {
            try
            {
                RunWorkerTick(tick);
            }
            catch (Exception ex)
            {
                // A background-thread exception would tear down the whole process;
                // log and keep polling instead.
                try { Log("[WARN] Worker error: " + ex.Message); } catch { }
            }
            tick++;
            Thread.Sleep(pollMs);
        }
    }

    private static void RunWorkerTick(int tick)
    {
        if (!enabled)
        {
            ResumeAll();
            return;
        }

        uint fgPid = 0;
        IntPtr fg = GetForegroundWindow();
        if (fg != IntPtr.Zero) GetWindowThreadProcessId(fg, out fgPid);
        if (fgPid != lastFgPid)
        {
            lastFgPid = fgPid;
            Log("[FG] foreground: pid " + fgPid + " (" + DescribeProcess((int)fgPid)
                + "), title=\"" + GetWindowTitle(fg) + "\"");
        }

        lock (StateLock)
        {
            if (targetName == null && WindowTargets.Count == 0
                && Config != null && Config.AutoLockFullscreen
                && fgPid != 0 && fg != IntPtr.Zero)
            {
                IntPtr lockTarget = IntPtr.Zero;
                uint lockPid = 0;
                if (!IsSystemProcess(fgPid) && IsFullscreen(fg))
                {
                    lockTarget = fg;
                    lockPid = fgPid;
                }
                else if (IsSystemProcess(fgPid))
                {
                    lockTarget = FindFullscreenWindow();
                    if (lockTarget != IntPtr.Zero)
                        GetWindowThreadProcessId(lockTarget, out lockPid);
                }
                if (lockTarget != IntPtr.Zero && lockPid != 0
                    && lockPid != (uint)Process.GetCurrentProcess().Id
                    && !IsNeverSuspendPid(lockPid))
                    SetTargetWindow(lockTarget, lockPid);
            }
        }

        if (targetName != null)
        {
            if (tick % 4 == 0) RefreshNameTargets();
            TickNameMode();
        }
        else
        {
            if (tick % 4 == 0) RefreshWindowTargets();
            TickWindowMode(tick);
        }
    }

    private static void RefreshNameTargets()
    {
        List<Process> procs = new List<Process>();
        try
        {
            procs = Process.GetProcessesByName(targetName)
                .Where(p => p.ProcessName.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch { }

        var alive = new HashSet<int>();
        var minimizedNow = new HashSet<int>();
        foreach (Process p in procs)
        {
            alive.Add(p.Id);
            try
            {
                IntPtr mw = p.MainWindowHandle;
                if (mw != IntPtr.Zero && IsIconic(mw)) minimizedNow.Add(p.Id);
            }
            catch { }
        }

        lock (StateLock)
        {
            foreach (int pid in Handles.Keys.Where(pid => !alive.Contains(pid)).ToList())
                DropHandleLocked(pid);
            foreach (Process p in procs)
            {
                if (!Handles.ContainsKey(p.Id))
                    OpenHandleLocked(p.Id);
            }
            Minimized.Clear();
            foreach (int pid in minimizedNow) Minimized.Add(pid);
            NamePids.Clear();
            foreach (int pid in Handles.Keys) NamePids.Add(pid);
        }
    }

    private static void RefreshWindowTargets()
    {
        lock (StateLock)
        {
            for (int i = WindowTargets.Count - 1; i >= 0; i--)
            {
                if (!IsWindow(WindowTargets[i].Hwnd))
                {
                    WindowTarget dead = WindowTargets[i];
                    Log("[WARN] Locked window closed: " + DescribeProcess((int)dead.Pid) + " (pid " + dead.Pid + "). Removed from locked set.");
                    if (dead.ChildrenLive) DropChildrenOf((int)dead.Pid);
                    WindowTargets.RemoveAt(i);
                    DropHandleLocked((int)dead.Pid);
                }
            }
        }
    }

    /// <summary>FOCUSLOCK_TEST_FGPID is set by the test harness before launch; read once.</summary>
    private static readonly string TestFgPidOverride =
        Environment.GetEnvironmentVariable("FOCUSLOCK_TEST_FGPID") ?? "";

    /// <summary>
    /// Shared switch-gesture/grace computation for both tick modes: while a switch
    /// gesture is active the target must keep running (up to SwitchGestureTimeoutMs),
    /// and after the last gesture event it gets PostSwitchGraceMs of grace.
    /// </summary>
    private static bool ComputeHolding()
    {
        bool switchingNow = kbdSwitching || mouseSwitching;
        bool holding = switchingNow;
        if (switchingNow && (DateTime.UtcNow - lastSwitchActivity).TotalMilliseconds > SwitchGestureTimeoutMs)
        {
            kbdSwitching = false;
            mouseSwitching = false;
            holding = false;
        }
        if (!holding && (DateTime.UtcNow - lastSwitchActivity).TotalMilliseconds < PostSwitchGraceMs)
            holding = true;
        return holding;
    }

    /// <summary>Foreground pid, overridden by FOCUSLOCK_TEST_FGPID when set.</summary>
    private static uint ResolveForegroundPid()
    {
        uint fgPid = 0;
        if (!string.IsNullOrEmpty(TestFgPidOverride))
            uint.TryParse(TestFgPidOverride, out fgPid);
        if (fgPid == 0)
        {
            IntPtr fg = GetForegroundWindow();
            if (fg != IntPtr.Zero) GetWindowThreadProcessId(fg, out fgPid);
        }
        return fgPid;
    }

    private static void TickWindowMode(int tick)
    {
        bool holding = ComputeHolding();
        uint fgPid = ResolveForegroundPid();

        bool ignoreMinimize = Config != null && !Config.SuspendWhenMinimized;

        List<int> childScans = new List<int>();
        lock (StateLock)
        {
            foreach (WindowTarget t in WindowTargets)
            {
                int pid = (int)t.Pid;
                // Reopen handles lost to a stale-suspend failure so window-mode
                // targets recover instead of going permanently inert.
                if (!Handles.ContainsKey(pid)) OpenHandleLocked(pid);
                bool focused = (int)fgPid == pid;
                if (ignoreMinimize && !focused) focused = IsIconic(t.Hwnd);
                bool wasSuspended = Suspended.Contains(pid);
                ApplyLocked(pid, focused, fgPid, holding);
                if (!focused && !holding)
                {
                    // ponytail: WMI Win32_Process query is the heavy call; re-scan only
                    // on the suspend transition or every 2s, never on every 250ms tick.
                    // The query itself runs outside StateLock (see below).
                    if (!wasSuspended || tick % 8 == 0)
                    {
                        childScans.Add(pid);
                        t.ChildrenLive = true;
                    }
                }
                else if (focused && t.ChildrenLive)
                {
                    DropChildrenOf(pid);
                    t.ChildrenLive = false;
                }
            }
        }
        foreach (int parentPid in childScans) SuspendChildrenOf(parentPid);
    }

    // When the main window is suspended, also suspend its child worker processes
    // (renderers, helpers) so they don't burn CPU while the parent is frozen.
    // ponytail: depth-1 only (direct children); grandchildren are rare and the
    // whitelist still protects named processes. Children that are never-suspend
    // names are skipped by ApplyLocked's own guard.
    // WMI is used for child enumeration (GetChildProcesses needs .NET 4.7.1,
    // this targets .NET 4.0). System.Management ships with the framework.
    private static void SuspendChildrenOf(int parentPid)
    {
        var children = new List<uint>();
        try
        {
            using (var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT ProcessId FROM Win32_Process WHERE ParentProcessId = " + parentPid))
            {
                foreach (System.Management.ManagementObject mo in searcher.Get())
                {
                    uint cidU;
                    if (!uint.TryParse(mo["ProcessId"].ToString(), out cidU)) continue;
                    if (cidU == (uint)parentPid) continue;
                    children.Add(cidU);
                }
            }
        }
        catch { return; }

        // WMI ran outside StateLock so input-hook resumptions never wait on a
        // slow query; only the apply pass takes the lock.
        int selfPid = Process.GetCurrentProcess().Id;
        lock (StateLock)
        {
            foreach (uint c in children)
            {
                int cid = (int)c;
                if (cid == selfPid) continue;
                if (IsNeverSuspendPid((uint)cid)) continue;
                OpenHandleLocked(cid);
                ApplyLocked(cid, false, 0, false);
            }
        }
    }

    private static void TickNameMode()
    {
        bool holding = ComputeHolding();
        uint fgPid = ResolveForegroundPid();

        bool ignoreMinimize = Config != null && !Config.SuspendWhenMinimized;

        lock (StateLock)
        {
            NamePids.Clear();
            foreach (int pid in Handles.Keys) NamePids.Add(pid);
            foreach (int pid in Handles.Keys.ToList())
            {
                bool focused = fgPid != 0 && (int)fgPid == pid;
                if (ignoreMinimize && !focused) focused = Minimized.Contains(pid);
                ApplyLocked(pid, focused, fgPid, holding);
            }
        }
    }

    private static void ApplyLocked(int pid, bool focused, uint fgPid, bool hold)
    {
        // Stop() may have resumed everything and released StateLock while this tick
        // was already in flight; never suspend after shutdown began.
        if (!running) return;
        IntPtr h;
        if (!Handles.TryGetValue(pid, out h)) return;
        bool isSuspended = Suspended.Contains(pid);

        if (focused && !isSuspended)
        {
            if (FocusedNow.Add(pid))
                Log("[FOCUS] " + DescribeProcess(pid) + " (pid " + pid + ") focused (fg pid " + fgPid + ")");
        }
        else
        {
            FocusedNow.Remove(pid);
        }

        if (!focused && !isSuspended && !hold)
        {
            if (IsNeverSuspendPid((uint)pid))
            {
                // The whitelist is the last line of defence: never suspend these,
                // even if the target was locked before the entry was added.
                if (WarnedPids.Add(pid))
                    Log("[WARN] " + DescribeProcess(pid) + " (pid " + pid
                        + ") is in the never-suspend list - skipping suspend.");
                return;
            }
            if (NtSuspendProcess(h) == 0)
            {
                Suspended.Add(pid);
                Log("[SUSPEND] " + DescribeProcess(pid) + " (pid " + pid + ")");
            }
            else
            {
                // Handle went stale (target restarting, access revoked). Close it so
                // the next tick reopens fresh instead of dropping the lock target.
                CloseHandle(h);
                Handles.Remove(pid);
                Suspended.Remove(pid);
                WarnedPids.Remove(pid);
            }
        }
        else if (focused && isSuspended)
        {
            if (ResumeFully(h))
            {
                Suspended.Remove(pid);
                WarnedResumePids.Remove(pid);
                Log("[RESUME]  " + DescribeProcess(pid) + " (pid " + pid + ")");
            }
            else
            {
                // keep the handle and retry on the next poll
                if (WarnedResumePids.Add(pid))
                    Log("[WARN] Could not resume " + DescribeProcess(pid) + " (pid " + pid + ") - retrying.");
            }
        }
    }

    /// <summary>
    /// Resumes the process until it is actually running again. NtResumeProcess on a
    /// non-suspended process is a safe no-op, so the loop also clears any suspend count
    /// accumulated before ProcessSuspender started (e.g. a previous instance was killed, or the
    /// process was suspended manually).
    /// </summary>
    private static bool ResumeFully(IntPtr h)
    {
        for (int i = 0; i < 8; i++)
        {
            if (NtResumeProcess(h) != 0) return false;
        }
        return true;
    }

    private static void OpenHandleLocked(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_ALL, false, (uint)pid);
        if (h == IntPtr.Zero)
        {
            if (WarnedPids.Add(pid))
                Log("[WARN] Cannot open process " + pid + " (access denied?).");
            return;
        }
        Handles[pid] = h;
        // Blind cleanup: undo any stale suspension (e.g. a previous ProcessSuspender was killed
        // while the target was frozen, or it was suspended manually). ResumeFully also
        // clears accumulated suspend counts; resuming a running process is a safe no-op.
        ResumeFully(h);
    }

    private static void DropHandleLocked(int pid)
    {
        IntPtr h;
        if (Handles.TryGetValue(pid, out h))
        {
            // Resume before closing the handle: closing a handle does NOT unsuspend
            // the process, so an unlocked window would stay frozen otherwise.
            if (Suspended.Contains(pid)) ResumeFully(h);
            CloseHandle(h);
        }
        Handles.Remove(pid);
        Suspended.Remove(pid);
    }

    // Resume + drop every child worker we suspended (see SuspendChildrenOf).
    private static void DropChildrenOf(int parentPid)
    {
        try
        {
            using (var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT ProcessId FROM Win32_Process WHERE ParentProcessId = " + parentPid))
            {
                foreach (System.Management.ManagementObject mo in searcher.Get())
                {
                    uint cidU;
                    if (!uint.TryParse(mo["ProcessId"].ToString(), out cidU)) continue;
                    int cid = (int)cidU;
                    if (cid == parentPid) continue;
                    if (Handles.ContainsKey(cid)) DropHandleLocked(cid);
                }
            }
        }
        catch { }
    }

    private static void DropAllHandlesLocked()
    {
        foreach (int pid in Handles.Keys.ToList())
        {
            IntPtr h;
            if (Handles.TryGetValue(pid, out h))
            {
                if (Suspended.Contains(pid)) ResumeFully(h);
                CloseHandle(h);
            }
            Handles.Remove(pid);
        }
        Suspended.Clear();
        WarnedResumePids.Clear();
    }

    private static string DescribeProcess(int pid)
    {
        try
        {
            return Process.GetProcessById(pid).ProcessName;
        }
        catch
        {
            return "(unknown)";
        }
    }

    /// <summary>True when the process is on the never-suspend whitelist (by name, e.g. "explorer").</summary>
    public static bool IsNeverSuspendPid(uint pid)
    {
        try
        {
            return IsNeverSuspendName(Process.GetProcessById((int)pid).ProcessName);
        }
        catch { return false; }
    }

    /// <summary>True when the process name is on the never-suspend whitelist (case-insensitive, "explorer" or "explorer.exe").</summary>
    private static bool IsNeverSuspendName(string processName)
    {
        if (Config == null || Config.NeverSuspend == null) return false;
        foreach (string entry in Config.NeverSuspend)
        {
            string e = entry == null ? "" : entry.Trim();
            if (e.Length == 0) continue;
            if (e.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                e = e.Substring(0, e.Length - 4);
            if (e.Equals(processName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Low-level mouse hook coordinates and GetWindowRect are in physical pixels
        // for DPI-aware processes; without this they disagree on scaled displays.
        ProcessSuspenderApp.SetDpiAware();

        bool createdNew;
        using (Mutex m = new Mutex(true, "Local\\ProcessSuspender.SingleInstance", out createdNew))
        {
            if (!createdNew)
            {
                return 1;
            }

            ProcessSuspenderApp.Initialize();

            string name = null;
            bool pick = false;
            bool showSettings = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-p" && i + 1 < args.Length) name = args[i + 1];
                else if (args[i] == "-t") pick = true;
                else if (args[i] == "-settings") showSettings = true;
            }
            if (!string.IsNullOrEmpty(name)) ProcessSuspenderApp.SetTargetByName(name);

            ProcessSuspenderApp.Start();

            ProcessSuspenderApp.RestoreLockedWindows();

            if (pick)
            {
                if (WindowPicker.Pick())
                {
                    var picks = WindowPicker.GetSelectedTargets();
                    ProcessSuspenderApp.Config.ProcessName = "";
                    SettingsStore.Save(ProcessSuspenderApp.Config);
                    if (picks != null)
                    {
                        foreach (var kv in picks)
                            ProcessSuspenderApp.AddTargetWindow(kv.Key, kv.Value);
                    }
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApp(showSettings));

            ProcessSuspenderApp.Stop();
            return 0;
        }
    }
}
