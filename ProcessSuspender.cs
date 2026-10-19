using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    private const uint PROCESS_SET_INFORMATION = 0x0200;
    private const uint PROCESS_SET_QUOTA = 0x0100;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_TERMINATE = 0x0001;
    private const uint PROCESS_ECO_ACCESS = PROCESS_SET_INFORMATION | PROCESS_SET_QUOTA | PROCESS_QUERY_INFORMATION | PROCESS_TERMINATE;
    private const uint PROCESS_ALL = PROCESS_SUSPEND_RESUME | PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_ECO_ACCESS;
    private const uint PROCESS_MINIMAL = PROCESS_SUSPEND_RESUME | PROCESS_QUERY_LIMITED_INFORMATION;
    private const int LogCapacity = 500;
    // Max time a switch gesture keeps the target running. Must cover a user
    // holding the Alt+Tab switcher open with no further keys (silent Alt hold
    // produces no hook events, so no keepalive can refresh it). Fail-open is
    // safe here: a stuck gesture only keeps the game running longer, while too
    // short a timeout re-freezes the game before Alt is released and the
    // release-time activation fails.
    private const int SwitchGestureTimeoutMs = 30000;
    private const int PostSwitchGraceMs = 500;

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr h);
    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr h);
    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int cls, IntPtr info, int length, out int returned);

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }

    // Prefix of the real record; Marshal.PtrToStructure only reads these bytes,
    // so the trailing thread array we don't care about is never touched.
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_PROCESS_INFORMATION
    {
        public uint NextEntryOffset;
        public uint NumberOfThreads;
        public long WorkingSetPrivateSize;
        public uint HardFaultCount;
        public uint NumberOfThreadsHighWatermark;
        public long CycleTime;
        public long CreateTime;
        public long UserTime;
        public long KernelTime;
        public UNICODE_STRING ImageName;
        public int BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);
    // ---- Eco / low-power mode ----
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetPriorityClass(IntPtr h, uint cls);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetPriorityClass(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr h, int infoClass, ref POWER_THROTTLING_STATE info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attrs, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOB_CPU_RATE_CONTROL info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, ref JOB_CPU_RATE_CONTROL info, uint size, out uint returned);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenJobObject(uint access, bool inherit, string name);

    private const uint IDLE_PRIORITY_CLASS = 0x0040;
    private const uint NORMAL_PRIORITY_CLASS = 0x0020;
    private const int ProcessPowerThrottling = 4;
    private const uint POWER_THROTTLING_EXECUTION_SPEED = 0x1;
    private const int JobObjectCpuRateControlInformation = 15;
    private const uint CPU_RATE_ENABLE = 0x1;
    private const uint CPU_RATE_HARD_CAP = 0x4;
    private const uint JOB_OBJECT_SET_ATTRIBUTES = 0x0010;
    // 10000 = 100%: the "no limit" rate written back on restore.
    private const uint CPU_RATE_UNLIMITED = 10000;

    [StructLayout(LayoutKind.Sequential)]
    private struct POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOB_CPU_RATE_CONTROL
    {
        public uint ControlFlags;
        public uint CpuRate;
    }
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
    private const int VK_LBUTTON = 0x01;
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

    public static void SetDpiAware()
    {
        try { SetProcessDPIAware(); } catch { }
    }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    private static readonly object StateLock = new object();
    private static readonly Dictionary<int, IntPtr> Handles = new Dictionary<int, IntPtr>();
    private static readonly Dictionary<int, DateTime> HandleStartTimes = new Dictionary<int, DateTime>();
    private static readonly HashSet<int> Suspended = new HashSet<int>();
    private static readonly HashSet<int> Throttled = new HashSet<int>();
    private static readonly Dictionary<int, uint> OrigPriority = new Dictionary<int, uint>();
    private static readonly Dictionary<int, IntPtr> EcoJobs = new Dictionary<int, IntPtr>();
    private static volatile bool ecoQoSUnsupported;
    private static readonly HashSet<int> Minimized = new HashSet<int>();
    private static readonly HashSet<int> WarnedPids = new HashSet<int>();
    private static readonly HashSet<int> WarnedResumePids = new HashSet<int>();
    private static readonly HashSet<int> WarnedEcoRestorePids = new HashSet<int>();
    // Anti-flap: throttle only after a stable defocus, and never right after
    // a restore. Switch animations flicker foreground game->explorer->game
    // across ticks; throttling the first transient tick starves the game
    // again before it paints a frame (DWM "Not Responding" death spiral).
    // Restore stays immediate (one tick).
    private static readonly Dictionary<int, DateTime> unfocusedSince = new Dictionary<int, DateTime>();
    private static readonly Dictionary<int, DateTime> lastRestoreAt = new Dictionary<int, DateTime>();
    private const int ThrottleDelayMs = 750;
    private const int RestoreRecoveryMs = 2500;
    // Handle tier tracking: a PROCESS_MINIMAL fallback handle cannot set
    // priority, EcoQoS or job caps, so Eco on it would be a silent no-op.
    private static readonly HashSet<int> MinimalHandles = new HashSet<int>();
    private static readonly HashSet<int> WarnedMinimalEco = new HashSet<int>();
    private static readonly HashSet<int> WarnedLowCap = new HashSet<int>();
    private static readonly HashSet<int> FocusedNow = new HashSet<int>();
    private static uint lastFgPid;
    // Last foreground change: while the user is actively switching (recent
    // change), button caches refresh every 1s; when the foreground has been
    // stable for a while they back off to every 4s. Clicks only need fresh
    // buttons around activity.
    private static DateTime lastFgChangeAt = DateTime.MinValue;
    private static DateTime lastNameRefresh = DateTime.MinValue;
    private static DateTime lastWindowRefresh = DateTime.MinValue;
    private static DateTime lastChildScan = DateTime.MinValue;
    private static DateTime lastHookCache = DateTime.MinValue;
    private static DateTime lastAudioRemute = DateTime.MinValue;
    private static volatile bool pendingForegroundSuspend;
    // Async resume: the low-level hooks must never block on NtResume/Eco/COM.
    // Hooks only set this flag + timestamp and queue background work; the
    // ThreadPool worker (and the poll worker as fallback) does the real resume.
    private static volatile bool pendingResume;
    private static readonly object PendingResumeLock = new object();
    private static string pendingResumeReason = "";
    // Hook-safe taskbar rects: worker refreshes, hook only reads (no EnumWindows
    // on the hook thread - that scan stalls all mouse input).
    private static readonly List<RECT> hookTaskbarRects = new List<RECT>();
    // Hook-safe system-area rects (Start button, notification area with tray +
    // clock): worker refreshes, hook only reads. Clicks here can never activate
    // the target, so they must never resume it - even when the taskbar-button
    // cache is empty/stale and the fallback below would otherwise wake the game
    // on every Start click.
    private static readonly List<RECT> hookSystemRects = new List<RECT>();
    // Cheap hook cache refreshed by the worker: the mouse hook must never do
    // Process enumeration or UIA walks (Windows drops slow low-level hooks).
    private static readonly List<IntPtr> hookTargetHwnds = new List<IntPtr>();
    private static readonly List<IntPtr> hookTargetMains = new List<IntPtr>();
    // Normalized target identity for hook-safe taskbar matching (worker refreshes,
    // hook only reads): exe file names + full paths lowercased with '/' separators,
    // and lowercased trimmed window titles. Empty = unverifiable (fall back to resume).
    private static readonly List<string> hookTargetExePaths = new List<string>();
    private static readonly List<string> hookTargetTitles = new List<string>();
    private static readonly object HookCacheLock = new object();
    private static readonly object TaskbarLock = new object();

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
    private static volatile TrayMode trayMode = TrayMode.Unknown;
    private static readonly List<TrayButtonInfo> taskbarButtons = new List<TrayButtonInfo>();
    private static DateTime taskbarButtonsCached = DateTime.MinValue;
    // True when the last button enumeration hit an error or found no taskbar, so
    // the cache may be incomplete. The hook then falls back to resume-on-click
    // (safer than leaving the game stuck) and logs the fallback reason.
    private static volatile bool taskbarButtonsStale = true;
    /// <summary>
    /// True when the target's own taskbar button is currently in the enumerated
    /// button cache (worker refreshes, hook only reads). Lets the hook ignore
    /// clicks that hit no button (Start, tray, clock, gaps) even while the
    /// cache is otherwise stale, instead of resuming first.
    /// </summary>
    private static volatile bool hookTargetButtonPresent;
    /// <summary>
    /// True when a button enumeration succeeded within the last 5s (worker
    /// refreshes, hook only reads). A freshly verified cache is complete by
    /// definition, so clicks matching no button are definitive - the stale
    /// flag and button proximity don't matter. Updated on every hook-cache
    /// refresh from the last success timestamp.
    /// </summary>
    private static volatile bool hookButtonsRecent;
    private static DateTime lastButtonSuccessAt = DateTime.MinValue;
    private static string lastTrayCacheLog = "";

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
    private static readonly List<string> PendingFileLines = new List<string>();
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

    /// <summary>True when at least one process/window target is locked.</summary>
    public static bool HasLockedTargets()
    {
        lock (StateLock) return targetName != null || WindowTargets.Count > 0;
    }

    /// <summary>Raised after targets are added/removed so listeners (hooks, event watcher) can go idle.</summary>
    public static event Action TargetsChanged;

    /// <summary>True when the configured freeze mode is Eco (low-power) instead of Suspend.</summary>
    public static bool IsEcoMode()
    {
        try
        {
            if (Config == null || string.IsNullOrEmpty(Config.FreezeMode)) return false;
            return Settings.NormalizeFreezeMode(Config.FreezeMode) == "Eco";
        }
        catch { return false; }
    }

    public static string FreezeModeLabel
    {
        get { return IsEcoMode() ? "Eco (low-power)" : "Suspend"; }
    }

    /// <summary>Switches the freeze mode and persists it. Takes effect on the next tick.</summary>
    public static void SetFreezeMode(string mode)
    {
        if (Config == null) return;
        string norm = Settings.NormalizeFreezeMode(mode);
        if (Settings.NormalizeFreezeMode(Config.FreezeMode) == norm
            && norm != "Eco") return;
        // In Eco mode the CPU cap may have changed too, so always re-apply caps below.
        Config.FreezeMode = norm;
        try { SettingsStore.Save(Config); } catch { }
        ApplyEcoCaps();
        Log("[INFO] Freeze mode set to " + norm
            + (norm == "Eco" ? " (low-power: IDLE + EcoQoS + " + Config.EcoCpuCapPercentClamped + "% CPU cap)" : " (NtSuspendProcess)"));
        if (SettingsApplied != null) SettingsApplied();
    }

    private static bool IsFrozenLocked(int pid)
    {
        return Suspended.Contains(pid) || Throttled.Contains(pid);
    }

    /// <summary>
    /// True when any target is currently frozen. Never blocks: uses TryEnter so
    /// a UI-thread caller (hook sync timer) can never stall on the worker's
    /// long StateLock hold. On contention returns true (conservative: keep the
    /// mouse hook installed rather than risk missing a click-back).
    /// </summary>
    public static bool AnyFrozen
    {
        get
        {
            bool took = false;
            try
            {
                took = Monitor.TryEnter(StateLock, 0);
                if (took) return Suspended.Count + Throttled.Count > 0;
            }
            catch { }
            finally { if (took) Monitor.Exit(StateLock); }
            return true;
        }
    }

    public static string CurrentTargetDescription
    {
        get
        {
            string nameCopy;
            IntPtr hwCopy = IntPtr.Zero;
            uint pidCopy = 0;
            int count;
            lock (StateLock)
            {
                nameCopy = targetName;
                count = WindowTargets.Count;
                if (count == 1) { hwCopy = WindowTargets[0].Hwnd; pidCopy = WindowTargets[0].Pid; }
            }
            if (nameCopy != null)
            {
                string title = FirstMatchingWindowTitle(nameCopy);
                string extra = title.Length > 0 ? " (\"" + title + "\")" : "";
                return "process \"" + nameCopy + "\"" + extra;
            }
            if (count > 0)
            {
                if (count == 1)
                {
                    string title = GetWindowTitle(hwCopy);
                    string extra = title.Length > 0 ? " (\"" + title + "\")" : "";
                    return DescribeProcess((int)pidCopy) + extra + " (pid " + pidCopy + ")";
                }
                return count + " locked windows";
            }
            return "none";
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
                        if (Throttled.Contains(pid)) { state = "THROTTLED (Eco)"; break; }
                    }
                    string title = FirstMatchingWindowTitle(targetName);
                    string extra = title.Length > 0 ? " (\"" + title + "\")" : "";
                    parts.Add("process \"" + targetName + "\"" + extra + " - " + state);
                }
                foreach (WindowTarget t in WindowTargets)
                {
                    bool suspended = Suspended.Contains((int)t.Pid);
                    bool throttled = Throttled.Contains((int)t.Pid);
                    string title = GetWindowTitle(t.Hwnd);
                    string extra = title.Length > 0 ? " (\"" + title + "\")" : "";
                    string state = suspended ? "SUSPENDED" : (throttled ? "THROTTLED (Eco)" : "active");
                    parts.Add(DescribeProcess((int)t.Pid) + extra + " (pid " + t.Pid + ") - " + state);
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
        public bool Eco;
    }

    public static List<LockedEntry> GetLockedWindows()
    {
        var snapshot = new List<WindowTarget>();
        var suspendedCopy = new HashSet<int>();
        var throttledCopy = new HashSet<int>();
        lock (StateLock)
        {
            foreach (WindowTarget t in WindowTargets) snapshot.Add(t);
            foreach (int pid in Suspended) suspendedCopy.Add(pid);
            foreach (int pid in Throttled) throttledCopy.Add(pid);
        }
        var result = new List<LockedEntry>();
        for (int i = 0; i < snapshot.Count; i++)
        {
            WindowTarget t = snapshot[i];
            string title = GetWindowTitle(t.Hwnd);
            string extra = title.Length > 0 ? " (\"" + title + "\")" : "";
            bool isEco = throttledCopy.Contains((int)t.Pid);
            bool frozen = suspendedCopy.Contains((int)t.Pid) || isEco;
            result.Add(new LockedEntry
            {
                Index = i,
                Label = DescribeProcess((int)t.Pid) + extra + " (pid " + t.Pid + ")"
                    + (isEco ? " [Eco]" : ""),
                Suspended = frozen,
                Eco = isEco
            });
        }
        return result;
    }

    public static string TargetStateDescription
    {
        get
        {
            lock (StateLock)
            {
                int frozen = Suspended.Count + Throttled.Count;
                if (frozen == 0) return "running";
                if (Suspended.Count > 0 && Throttled.Count == 0)
                {
                    if (Suspended.Count == 1)
                    {
                        foreach (int pid in Suspended)
                            return "SUSPENDED (" + DescribeProcess(pid) + ")";
                    }
                    return "SUSPENDED (" + Suspended.Count + " processes)";
                }
                if (Throttled.Count > 0 && Suspended.Count == 0)
                {
                    if (Throttled.Count == 1)
                    {
                        foreach (int pid in Throttled)
                            return "THROTTLED (Eco) (" + DescribeProcess(pid) + ")";
                    }
                    return "THROTTLED (Eco) (" + Throttled.Count + " processes)";
                }
                return "FROZEN (" + frozen + " processes)";
            }
        }
    }

    private static string FirstMatchingWindowTitle(string name)
    {
        try
        {
            Process[] list = Process.GetProcessesByName(name);
            try
            {
                foreach (Process p in list)
                {
                    try
                    {
                        IntPtr mw = p.MainWindowHandle;
                        if (mw == IntPtr.Zero) continue;
                        string t = GetWindowTitle(mw);
                        if (t.Length > 0) return t;
                    }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            finally
            {
                // individual Processes already disposed; array needs no dispose
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

    public static string LogFile { get { return logFilePath; } }

    public static void Initialize()
    {
        Config = SettingsStore.Load();
        if (Config.NeverSuspend == null) Config.NeverSuspend = new List<string>();
        Settings.EnsureDefaults(Config.NeverSuspend);
        if (Config.LockedProcesses == null) Config.LockedProcesses = new List<string>();
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
        // Warm the hook caches synchronously so clicks in the first seconds
        // (tray mode probe + taskbar/system rects) don't hit empty fallbacks.
        try { RefreshHookTaskbarRects(); } catch { }
        try { RefreshTaskbarButtons(); } catch { }
        worker = new Thread(WorkerLoop);
        worker.IsBackground = true;
        worker.Name = "ProcessSuspenderWorker";
        worker.Start();
        RestoreLockedProcesses();
        try { NotifyTargetsChanged(); } catch { }
        Log("[INFO] ProcessSuspender started. Target: " + CurrentTargetDescription + ", poll: " + pollMs + "ms, mode: " + FreezeModeLabel);
    }

    public static void Stop()
    {
        running = false;
        try { wakeEvent.Set(); } catch { }
        if (worker != null) worker.Join(3000);
        try { StopEventWatcher(); } catch { }
        if (Config != null && Config.ResumeOnExit)
        {
            ResumeAll();
            Log("[INFO] ProcessSuspender stopped. All targets resumed.");
        }
        else
        {
            Log("[INFO] ProcessSuspender stopped (resume-on-exit disabled).");
        }
        FlushLogToFile();
    }

    public static void ApplySettings()
    {
        if (Config == null) return;
        Config.FreezeMode = Settings.NormalizeFreezeMode(Config.FreezeMode);
        Config.Theme = AppTheme.NormalizeTheme(Config.Theme);
        if (Config.EcoCpuCapPercent < 0.1 || Config.EcoCpuCapPercent > 100) Config.EcoCpuCapPercent = 2;
        pollMs = Config.PollIntervalMsClamped;
        logToFile = Config.LogToFile;
        Log("[INFO] Settings applied: poll=" + pollMs + "ms, mode=" + FreezeModeLabel
            + ", ecoCap=" + Config.EcoCpuCapPercentClamped + "%"
            + ", suspendWhenMinimized=" + Config.SuspendWhenMinimized
            + ", resumeOnExit=" + Config.ResumeOnExit + ", logToFile=" + logToFile
            + ", theme=" + Config.Theme);
        if (!string.IsNullOrEmpty(Config.ProcessName))
        {
            string cur;
            lock (StateLock) cur = targetName;
            if (cur == null || !cur.Equals(Config.ProcessName, StringComparison.OrdinalIgnoreCase))
                SetTargetByName(Config.ProcessName);
        }
        ApplyNeverSuspendList();
        ApplyEcoCaps();
        if (SettingsApplied != null) SettingsApplied();
        NotifyTargetsChanged();
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
            foreach (int pid in Throttled.ToList())
            {
                if (IsNeverSuspendPid((uint)pid))
                {
                    IntPtr h;
                    if (Handles.TryGetValue(pid, out h))
                    {
                        RestoreEcoLocked(pid, h);
                        Log("[ECO-RESTORE] " + DescribeProcess(pid) + " (pid " + pid
                            + ") - whitelisted in never-suspend list");
                    }
                }
            }
            if (targetName != null && IsNeverSuspendName(targetName))
                Log("[INFO] Target \"" + targetName
                    + "\" is in the never-suspend list - it will never be suspended.");
        }
    }

    private static int SelfPid
    {
        get
        {
            try { return Process.GetCurrentProcess().Id; }
            catch { return -1; }
        }
    }

    private static bool IsSelfPid(uint pid)
    {
        try { return (uint)Process.GetCurrentProcess().Id == pid; }
        catch { return false; }
    }

    private static string GetProcessName(int pid)
    {
        try
        {
            using (Process p = Process.GetProcessById(pid))
                return p.ProcessName;
        }
        catch { return ""; }
    }

    private static DateTime GetProcessStartTime(int pid)
    {
        try
        {
            using (Process p = Process.GetProcessById(pid))
                return p.StartTime;
        }
        catch { return DateTime.MinValue; }
    }

    /// <summary>True when the stored start-time no longer matches the live process (PID reused).</summary>
    private static bool HasPidBeenReused(int pid)
    {
        DateTime stored;
        lock (StateLock)
        {
            if (!HandleStartTimes.TryGetValue(pid, out stored)) return false;
            if (stored == DateTime.MinValue) return false;
        }
        DateTime live = GetProcessStartTime(pid);
        if (live == DateTime.MinValue) return false; // process gone or unreadable: let normal cleanup handle it
        return live != stored;
    }

    private static void PersistLockedProcesses()
    {
        try
        {
            if (Config == null) return;
            var names = new List<string>();
            lock (StateLock)
            {
                foreach (WindowTarget t in WindowTargets)
                {
                    string n = GetProcessName((int)t.Pid);
                    if (n.Length == 0) continue;
                    bool dup = false;
                    foreach (string e in names)
                        if (e.Equals(n, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                    if (!dup) names.Add(n);
                }
                if (targetName != null) names.Clear(); // name-mode uses ProcessName, not this list
            }
            Config.LockedProcesses = names;
            SettingsStore.Save(Config);
        }
        catch { }
    }

    private static void RestoreLockedProcesses()
    {
        List<string> saved = null;
        try
        {
            if (Config != null && Config.LockedProcesses != null)
                saved = new List<string>(Config.LockedProcesses);
        }
        catch { }
        if (saved == null || saved.Count == 0) return;
        lock (StateLock)
        {
            if (targetName != null || WindowTargets.Count > 0) return;
        }
        if (!string.IsNullOrEmpty(Config.ProcessName)) return;
        foreach (string name in saved)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (IsNeverSuspendName(name)) continue;
                if (name.Equals(Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase)) continue;
                Process[] list = Process.GetProcessesByName(name);
                try
                {
                    foreach (Process p in list)
                    {
                        try
                        {
                            if (p.MainWindowHandle != IntPtr.Zero)
                            {
                                AddTargetWindow(p.MainWindowHandle, (uint)p.Id);
                                break;
                            }
                        }
                        finally { try { p.Dispose(); } catch { } }
                    }
                }
                finally { }
            }
            catch { }
        }
        if (WindowTargets.Count > 0)
            Log("[INFO] Restored " + WindowTargets.Count + " locked window(s) from settings.");
        if (WindowTargets.Count > 0) NotifyTargetsChanged();
    }

    public static void SetTargetByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name.Substring(0, name.Length - 4);
        if (IsNeverSuspendName(name))
        {
            Log("[WARN] \"" + name + "\" is in the never-suspend list - not locking it.");
            return;
        }
        try
        {
            if (name.Equals(Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                Log("[WARN] Cannot target ProcessSuspender itself - pick another window.");
                return;
            }
        }
        catch { }
        lock (StateLock)
        {
            DropAllHandlesLocked();
            WarnedPids.Clear();
            targetName = name;
            NamePids.Clear();
            WindowTargets.Clear();
            Log("[INFO] Target set to process \"" + name + "\"");
        }
        NotifyTargetsChanged();
    }

    /// <summary>Adds a window to the locked set without clearing the others (used by multi-select Pick).</summary>
    public static void AddTargetWindow(IntPtr hwnd, uint pid)
    {
        if (hwnd == IntPtr.Zero || pid == 0) return;
        if (IsSelfPid(pid))
        {
            Log("[WARN] Cannot lock ProcessSuspender itself - pick another window.");
            return;
        }
        if (IsNeverSuspendPid(pid))
        {
            Log("[WARN] \"" + DescribeProcess((int)pid) + "\" is in the never-suspend list - not locking it.");
            return;
        }
        bool added = false;
        lock (StateLock)
        {
            if (FindWindowTarget(hwnd) != null) return;
            targetName = null;
            NamePids.Clear();
            WindowTarget t = new WindowTarget { Hwnd = hwnd, Pid = pid };
            WindowTargets.Add(t);
            OpenHandleLocked((int)pid);
            Log("[INFO] Added locked window " + DescribeProcess((int)pid) + " (pid " + pid + "). Total: " + WindowTargets.Count);
            added = true;
        }
        if (added) PersistLockedProcesses();
        if (added) NotifyTargetsChanged();
    }

    /// <summary>Adds (or removes, if focused) the given window to the locked set. Returns a short result message.</summary>
    public static string ToggleWindowTarget(IntPtr hwnd, uint pid)
    {
        if (IsSelfPid(pid))
        {
            Log("[WARN] Cannot lock ProcessSuspender itself - pick another window.");
            return "Cannot lock ProcessSuspender itself";
        }
        if (IsNeverSuspendPid(pid))
        {
            string proc = DescribeProcess((int)pid);
            Log("[WARN] \"" + proc + "\" is in the never-suspend list - not locking it.");
            return proc + " is in the never-suspend list - not locked";
        }
        string result;
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
                result = "Unlocked " + name;
            }
            else
            {
                targetName = null;
                NamePids.Clear();
                WindowTarget t = new WindowTarget { Hwnd = hwnd, Pid = pid };
                WindowTargets.Add(t);
                OpenHandleLocked((int)pid);
                string name2 = DescribeProcess((int)pid);
                Log("[INFO] Locked " + name2 + " (pid " + pid + "). Total locked: " + WindowTargets.Count);
                result = "Locked " + name2;
            }
        }
        PersistLockedProcesses();
        NotifyTargetsChanged();
        return result;
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
        bool changed = false;
        lock (StateLock)
        {
            if (index < 0 || index >= WindowTargets.Count) return;
            WindowTarget t = WindowTargets[index];
            WindowTargets.RemoveAt(index);
            DropHandleLocked((int)t.Pid);
            DropChildrenOf((int)t.Pid);
            Log("[INFO] Unlocked " + DescribeProcess((int)t.Pid) + " (pid " + t.Pid + ") from tray.");
            changed = true;
        }
        if (changed) PersistLockedProcesses();
        if (changed) NotifyTargetsChanged();
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
            foreach (int pid in Throttled.ToList())
            {
                IntPtr h;
                if (Handles.TryGetValue(pid, out h))
                {
                    RestoreEcoLocked(pid, h);
                    Log("[ECO-RESTORE] " + DescribeProcess(pid) + " (pid " + pid + ")");
                }
                else
                {
                    Throttled.Remove(pid);
                    OrigPriority.Remove(pid);
                }
            }
        }
    }

    /// <summary>
    /// The user pressed or released Alt+Tab / Win+Tab (a real switch gesture, as
    /// detected by the keyboard hook). Gesture START only arms grace + origin
    /// freeze bookkeeping. RELEASE never resumes blindly: the hook cannot know
    /// which window was selected, and any speculative resume would run the game
    /// while the user looks at another app. Instead the release only records
    /// the pre-switch foreground and wakes the worker, which observes where the
    /// shell actually landed: moved elsewhere = certain, the game stays frozen
    /// (true zero); unchanged = ambiguous, the worker resumes the frozen target
    /// without ever changing which window is in front. Esc-cancel and
    /// mid-gesture clicks commit explicitly and skip everything. The low-level
    /// hook only flips flags and reads the foreground - never blocks, never
    /// touches StateLock.
    /// </summary>
    public static void OnSwitchStateChanged(bool active)
    {
        kbdSwitching = active;
        lastSwitchActivity = DateTime.UtcNow;
        // Always logged (hook-safe queue): proves the keyboard hook saw the
        // gesture even when there is nothing to resume. Missing [INPUT] lines
        // after an Alt+Tab means the hook is dead or detection missed it.
        try { Log("[INPUT] switch gesture " + (active ? "started" : "ended")); }
        catch { }
        if (active)
        {
            pendingForegroundSuspend = true;
            gestureCommitPid = 0; // new episode: drop any previous commit
            // The Tab that STARTED this gesture was already counted by the hook
            // (CountGestureTab runs in the same key-down callback, before this
            // state change). Resetting to 0 threw that count away and made every
            // ordinary single-Tab Alt+Tab look like a pure Alt tap, so the whole
            // release decision was skipped. Seed at 1 instead (also covers the
            // Tab-held-first path, where the gesture starts from the physical
            // Tab state without a key-down on our hook).
            gestureTabCount = 1;
            releasePending = false;
        }
        else
        {
            pendingForegroundSuspend = false;
            // Committed release (Esc-cancel back home, or click-selected window
            // already foreground): the switch is decided without us. Skip the
            // resume entirely - zero blip. A frozen foreground window self-heals
            // via the focused path on the next tick, so this is always safe.
            bool committed = false;
            try
            {
                uint commit = gestureCommitPid;
                gestureCommitPid = 0;
                if (commit != 0)
                {
                    IntPtr fg = GetForegroundWindow();
                    uint fgPid = 0;
                    if (fg != IntPtr.Zero) GetWindowThreadProcessId(fg, out fgPid);
                    committed = fgPid == commit;
                }
            }
            catch { committed = false; }
            if (committed)
            {
                try { Log("[INPUT] switch gesture ended (committed, no resume)"); }
                catch { }
                return;
            }
            // Pure Alt-tap (no Tab navigation, no click): no switch could have
            // happened. Skip everything - zero blip.
            if (gestureTabCount == 0)
            {
                try { Log("[INPUT] switch gesture ended (no navigation, no resume)"); }
                catch { }
                return;
            }
            // Ambiguous: record the pre-switch foreground and let the worker
            // decide once the shell has landed (see EvaluateReleasePending).
            // Hook-safe: two syscalls + volatile writes + a wake signal.
            try
            {
                IntPtr fg = GetForegroundWindow();
                uint fgPid = 0;
                if (fg != IntPtr.Zero) GetWindowThreadProcessId(fg, out fgPid);
                releaseOriginPid = originSwitchPid;
                releaseFgBefore = fgPid;
                releaseAt = DateTime.UtcNow;
                releaseResumeAt = DateTime.MinValue;
                releasePending = true;
                try { wakeEvent.Set(); } catch { }
            }
            catch { }
        }
    }

    /// <summary>
    /// Worker-side release decision (runs on the worker thread, never the hook).
    /// The hook recorded the pre-switch foreground at Alt-up; here the worker
    /// observes where the shell actually landed:
    /// - foreground is a locked target: the switch landed home. Resume it if
    ///   frozen (safety) and claim it. No yank needed - the shell did it.
    /// - foreground moved to a real third-party window (not the origin, not a
    ///   locked target, not transient NULL/explorer teardown): certain - the
    ///   user picked another app. The game was never resumed, so there is
    ///   nothing to refreeze: just clear. True zero.
    /// - foreground unchanged (or transient): ambiguous. If the gesture started
    ///   on the only locked target (user was leaving it), an ambiguous landing
    ///   means they stayed away: keep it frozen. Otherwise (switching toward a
    ///   frozen target) resume it once the grace expires - ReleaseIdleMs when
    ///   the shell reports no foreground at all (activation stuck), the longer
    ///   ReleaseAmbiguousMs for every other transient. No foreground yank: the
    ///   shell owns the switch, and forcing the target forward used to replace
    ///   the window the user actually picked. Suspend mode only: Eco processes
    ///   keep running, so their activation always works and an unchanged
    ///   foreground means "not the game" - leave throttled.
    /// A new gesture abandons the pending decision (its release decides fresh).
    /// </summary>
    private static void EvaluateReleasePending(uint fgPid)
    {
        if (!releasePending) return;
        // The keyboard gesture is over by definition when this runs (the hook
        // clears kbdSwitching before setting releasePending). Deliberately do
        // NOT test mouseSwitching here: the mouse hook is uninstalled as soon
        // as a click resumes the last frozen target, so its mouse-up can be
        // lost and the flag can stay set. A stale mouse flag must never veto
        // the user's keyboard switch decision.
        if (kbdSwitching) { releasePending = false; return; }
        if (!running) { releasePending = false; return; }
        if (!enabled) { releasePending = false; return; }

        HashSet<int> locked;
        lock (StateLock)
        {
            locked = new HashSet<int>();
            foreach (WindowTarget t in WindowTargets) locked.Add((int)t.Pid);
            foreach (int pid in NamePids) locked.Add(pid);
        }
        if (locked.Count == 0) { releasePending = false; return; }

        // Landed home: make sure it runs.
        if (fgPid != 0 && locked.Contains((int)fgPid))
        {
            releasePending = false;
            try { ResumeSuspended("switch landed on target"); } catch { }
            return;
        }

        // Moved to a real other app: certain. Explorer/teardown and NULL are
        // never verdicts (see below); anything else definitive is.
        if (fgPid != 0 && fgPid != releaseFgBefore && !locked.Contains((int)fgPid) && !IsShellPid(fgPid))
        {
            // Nothing was resumed, so nothing to undo: the game was never
            // woken by this release. True zero.
            releasePending = false;
            try { Log("[INPUT] switch committed elsewhere (no resume)"); }
            catch { }
            return;
        }

        // Ambiguous (unchanged or transient): the shell has not revealed the
        // landing yet. Deliberately NO SetForegroundWindow here: the shell owns
        // the switch, and yanking the target forward used to replace the window
        // the user actually picked (GetForegroundWindow can still report
        // NULL/transient right after Alt+Tab, which made the "activation
        // failed" guess fire on ordinary switches).
        //
        // If the gesture STARTED on a locked target and it is the only target,
        // the user was leaving it. An ambiguous landing means they did not come
        // back (desktop/shell/cancelled animation): keep it frozen. The focused
        // path resumes it if it does end up foreground, so this always heals.
        uint origin = releaseOriginPid;
        if (origin != 0 && locked.Count == 1 && locked.Contains((int)origin))
        {
            releasePending = false;
            try { Log("[INPUT] switch away from target (ambiguous, stays frozen)"); } catch { }
            return;
        }

        // Switching toward a frozen target. No foreground at all is the shell
        // failing to activate the suspended window - resume after a short grace
        // so the switch lands promptly. Any other ambiguous state (unchanged
        // origin, explorer teardown) keeps the conservative grace: a clear
        // "elsewhere" verdict arrives within ~10-100ms and wins first.
        if (releaseResumeAt == DateTime.MinValue)
            releaseResumeAt = releaseAt.AddMilliseconds(fgPid == 0 ? ReleaseIdleMs : ReleaseAmbiguousMs);
        if (DateTime.UtcNow < releaseResumeAt) return;
        releasePending = false;
        if (IsEcoMode()) return; // running anyway: unchanged fg means not the game
        try { ResumeSuspended("ambiguous switch release"); } catch { }
    }

    /// <summary>True for shell/teardown pids that are never switch verdicts (worker thread).</summary>
    private static bool IsShellPid(uint pid)
    {
        if (pid == 0) return true;
        try
        {
            string name = GetProcessName((int)pid);
            return name.Equals("explorer", StringComparison.OrdinalIgnoreCase)
                || name.Equals("dwm", StringComparison.OrdinalIgnoreCase)
                || name.Equals("shellexperiencehost", StringComparison.OrdinalIgnoreCase)
                || name.Equals("applicationframehost", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Lightweight keepalive from the keyboard hook (microseconds, hook-safe):
    /// refreshes the switch-gesture timestamp while the user keeps interacting
    /// with the switcher (repeated Tab, arrow keys) so the gesture timeout
    /// in ComputeHolding cannot expire mid-gesture and re-freeze the game
    /// before Alt is released. No resume, no locks, just the timestamp.
    /// </summary>
    public static void TouchSwitchActivity()
    {
        lastSwitchActivity = DateTime.UtcNow;
    }

    // Foreground pid captured on a FRESH Alt/Win press (hook thread), i.e.
    // before the system can open the switcher and move the foreground away
    // from the window the user is leaving. Lets the worker freeze the origin
    // window at gesture start even though GetForegroundWindow already reports
    // the switcher by the time the worker drains the flag. Two fast syscalls,
    // no locks, no logging: hook-safe. Stale values are harmless: the worker
    // only reads it while draining a pending gesture-start suspend.
    private static volatile uint originSwitchPid;

    // Commit pid for the gesture release (hook thread writes, hook reads):    // Esc during a gesture = cancel (shell returns to the origin window);
    // a mouse click during a gesture = committed selection. At release, when
    // the foreground already equals this pid, the switch is over and decided
    // without us - resuming would only create a pointless blip, so the release
    // resume is skipped. Always safe: a frozen window that owns the foreground
    // self-heals via the focused path on the next worker tick.
    private static volatile uint gestureCommitPid;

    /// <summary>Esc pressed mid-gesture: commit to the origin window (cancel).</summary>
    public static void NoteEscDuringGesture()
    {
        try { if (originSwitchPid != 0) gestureCommitPid = originSwitchPid; }
        catch { }
    }

    /// <summary>Mouse click mid-gesture: commit to the clicked window (selection).</summary>
    public static void NoteClickDuringGesture(uint pid)
    {
        try { if (pid != 0) gestureCommitPid = pid; }
        catch { }
    }

    /// <summary>Hook-safe read of the keyboard-gesture flag (for the mouse hook).</summary>
    public static bool KbdGestureActive()
    {
        return kbdSwitching;
    }

    /// <summary>Hook-safe Tab counter for the release decision (see releasePending).</summary>
    public static void CountGestureTab()
    {
        try { gestureTabCount++; }
        catch { }
    }

    // Release-decision state (hook writes, worker reads): the hook records the
    // pre-switch foreground + Tab count at Alt-up and wakes the worker, which
    // decides in EvaluateReleasePending once the shell has landed.
    private static volatile bool releasePending;
    private static volatile uint releaseFgBefore;
    private static volatile int gestureTabCount;
    // Window that owned the foreground when the gesture started (the window
    // being left). Lets the release decision tell "switching away from the
    // target" (ambiguous landing = stayed away, keep it frozen) from
    // "switching toward the frozen target" (ambiguous landing = activation
    // stuck, resume it).
    private static volatile uint releaseOriginPid;
    private static DateTime releaseAt = DateTime.MinValue;
    // Worker-chosen deadline for the ambiguous resume. Wake exactly at it
    // instead of sleeping a whole poll interval past it.
    private static DateTime releaseResumeAt = DateTime.MinValue;
    // Ambiguous-release grace: with no landing yet, wait this long after Alt-up
    // before resuming (lets a clear "switched elsewhere" verdict arrive first).
    private const int ReleaseAmbiguousMs = 300;
    // No foreground at all right after Alt-up ("pid 0"): the shell's activation
    // is stuck on the frozen target (a running window takes the foreground
    // within ~10-50ms). Resume sooner so the switch lands promptly; a false
    // positive only leaves the target running for one recovery window.
    private const int ReleaseIdleMs = 120;

    /// <summary>Hook-safe snapshot of the about-to-be-left foreground (see above).</summary>
    public static void NoteModifierHeld()
    {
        try
        {
            IntPtr fg = GetForegroundWindow();
            uint pid = 0;
            if (fg != IntPtr.Zero) GetWindowThreadProcessId(fg, out pid);
            originSwitchPid = pid;
        }
        catch { }
    }

    /// <summary>
    /// The mouse hook calls this (microseconds): record the request and resume on a
    /// ThreadPool thread (the low-level hook must never block).
    /// </summary>
    private static void ResumeTargetsAsync(string reason)
    {
        lock (PendingResumeLock)
        {
            pendingResumeReason = reason;
            pendingResume = true;
        }
        try
        {
            ThreadPool.QueueUserWorkItem(delegate(object unused)
            {
                try { DrainPendingResume(); }
                catch { }
            });
        }
        catch { }
    }

    /// <summary>Runs the queued resume request (ThreadPool; worker tick drains as fallback).</summary>
    private static void DrainPendingResume()
    {
        string reason;
        lock (PendingResumeLock)
        {
            if (!pendingResume) return;
            pendingResume = false;
            reason = pendingResumeReason;
            pendingResumeReason = "";
        }
        try { ResumeSuspended(reason); }
        catch { }
    }

    /// <summary>Suspends the locked target that still owns the foreground window
    /// at gesture start (worker thread, deferred from the keyboard hook).</summary>
    private static void SuspendForegroundTarget(string reason)
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return;
        uint fgPid;
        GetWindowThreadProcessId(fg, out fgPid);
        if (fgPid == 0) return;
        SuspendTargetPid((int)fgPid, reason);
    }

    /// <summary>Suspends the gesture origin window (captured at Alt/Win press,
    /// before the switcher stole the foreground). Skipped when the origin is
    /// already foreground again (user cancelled back) - the normal focused
    /// logic owns that case. Runs on the worker thread.</summary>
    private static void SuspendOriginTarget(string reason)
    {
        uint origin = originSwitchPid;
        if (origin == 0) return;
        try
        {
            IntPtr fg = GetForegroundWindow();
            uint fgPid = 0;
            if (fg != IntPtr.Zero) GetWindowThreadProcessId(fg, out fgPid);
            if (fgPid == origin) return; // back home (or never left): don't touch
        }
        catch { }
        SuspendTargetPid((int)origin, reason);
    }

    /// <summary>Shared freeze-one-pid body for the deferred gesture-start suspends.
    /// Caller is the worker thread (may block briefly on StateLock).</summary>
    private static void SuspendTargetPid(int pid, string reason)
    {
        if (pid <= 0) return;
        if (IsSelfPid((uint)pid)) return;
        bool eco = IsEcoMode();
        IntPtr h = IntPtr.Zero;
        lock (StateLock)
        {
            if (!Handles.ContainsKey(pid) || IsFrozenLocked(pid)) return;
            h = Handles[pid];
        }
        if (h == IntPtr.Zero) return;
        if (IsNeverSuspendPid((uint)pid)) return;
        if (eco)
        {
            lock (StateLock)
            {
                if (!Handles.TryGetValue(pid, out h)) return;
                if (IsFrozenLocked(pid)) return;
                ApplyEcoLocked(pid, h);
            }
            return;
        }
        if (NtSuspendProcess(h) == 0)
        {
            lock (StateLock) Suspended.Add(pid);
            Log("[SUSPEND] " + DescribeProcess(pid) + " (pid " + pid + ") - " + reason);
        }
    }

    /// <summary>
    /// Left mouse button down/up. Fast path only: the low-level mouse hook must
    /// return in microseconds (Windows drops slow hooks). No Process enumeration,
    /// no UIA, no EnumWindows, no logging here - just cached rects/handles.
    /// Only the target's own taskbar button (or its window) resumes it; clicks
    /// elsewhere are ignored so a suspended game stays frozen. Resume itself
    /// runs on a background thread (see ResumeTargetsAsync).
    /// </summary>
    public static void OnMouseSwitchEvent(bool down, int x, int y)
    {
        if (down)
        {
            // Never block the hook on StateLock: the worker can hold it for
            // 10-100ms across NtSuspend/COM. If the lock is busy, assume
            // frozen (conservative) and do the cheap hit test.
            bool anyFrozen = true;
            bool tookLock = false;
            try
            {
                tookLock = Monitor.TryEnter(StateLock, 0);
                if (tookLock)
                    anyFrozen = Suspended.Count + Throttled.Count > 0;
            }
            catch { anyFrozen = true; }
            finally { if (tookLock) Monitor.Exit(StateLock); }
            if (!anyFrozen)
            {
                mouseSwitching = true;
                mouseDownWasSwitch = false;
                lastSwitchActivity = DateTime.UtcNow;
                return;
            }
            string match;
            bool isSwitch = CheapSwitchHit(x, y, out match);
            mouseDownWasSwitch = isSwitch;
            if (!isSwitch) return;
            mouseSwitching = true;
            lastSwitchActivity = DateTime.UtcNow;
            ResumeTargetsAsync("mouse click on target/taskbar (" + match + " at " + x + "," + y + ")");
            return;
        }
        mouseSwitching = false;
        if (mouseDownWasSwitch)
        {
            mouseDownWasSwitch = false;
            lastSwitchActivity = DateTime.UtcNow;
        }
    }

    /// <summary>Cheap hook-safe hit test using only cached data + WindowFromPoint.</summary>
    private static bool CheapSwitchHit(int x, int y, out string match)
    {
        match = "";
        bool inTaskbar = false;
        try { inTaskbar = IsInTaskbarCached(x, y); }
        catch { inTaskbar = false; }
        if (inTaskbar)
        {
            // Start button / tray / clock can never activate the target: ignore
            // before any fallback (Unknown/Legacy mode, stale or empty button
            // cache) gets a chance to treat them as a switch-back attempt.
            try { if (IsInSystemAreaCached(x, y)) { match = ""; return false; } }
            catch { }
            return CheapTaskbarHit(x, y, out match);
        }
        try
        {
            if (PointInsideForegroundWindow(x, y)) return false;
        }
        catch { }
        IntPtr hit = IntPtr.Zero;
        try { hit = WindowFromPoint(x, y); }
        catch { match = ""; return false; }
        if (hit == IntPtr.Zero) { match = ""; return false; }
        lock (HookCacheLock)
        {
            foreach (IntPtr hw in hookTargetHwnds)
            {
                if (hit == hw) { match = "target window"; return true; }
                try { if (IsChild(hw, hit)) { match = "target window"; return true; } }
                catch { }
            }
            foreach (IntPtr mw in hookTargetMains)
            {
                if (hit == mw) { match = "target window"; return true; }
                try { if (IsChild(mw, hit)) { match = "target window"; return true; } }
                catch { }
            }
        }
        match = "";
        return false;
    }

    /// <summary>
    /// Hook-safe taskbar hit test. Whenever any button data is cached, only a
    /// click on the target's own taskbar button resumes it - in every tray mode,
    /// stale or not. Clicks on other apps' buttons, the tray, clock, Start,
    /// Search or empty gaps return false so a suspended game stays frozen. Uses
    /// only data the worker cached (button rects + target exe/title); never
    /// touches Process, UIA, EnumWindows or Log (all would stall the low-level
    /// hook). Only when no button data exists at all (Legacy/Unknown mode with
    /// an empty cache, or the target identity is unverifiable) does it return
    /// true, preserving resume-first so a click never leaves the game stuck.
    /// The out match string describes the verdict; the background resume thread
    /// logs it (hook thread never logs).
    /// </summary>
    private static bool CheapTaskbarHit(int x, int y, out string match)
    {
        TrayButtonInfo hit = null;
        int buttonCount = 0;
        bool nearButton = false;
        bool stale;
        lock (taskbarButtons)
        {
            buttonCount = taskbarButtons.Count;
            foreach (TrayButtonInfo b in taskbarButtons)
            {
                if (b.Contains(x, y)) { hit = b; break; }
            }
            // Is the click at least close to a known button? Adjacent buttons
            // touch, so a click far from every cached button (Start, tray,
            // clock, screen edges) cannot be a switch-back - even when the
            // cache is stale. A missed (unenumerated) button sits next to known
            // ones, so it stays inside the margin and keeps resume-first.
            if (hit == null && buttonCount > 0)
            {
                foreach (TrayButtonInfo b in taskbarButtons)
                {
                    if (x >= b.Left - 16 && x <= b.Right + 16
                        && y >= b.Top - 16 && y <= b.Bottom + 16)
                    { nearButton = true; break; }
                }
            }
        }
        stale = taskbarButtonsStale;
        if (buttonCount > 0)
        {
            if (hit == null)
            {
                // Freshly verified cache (success within 5s): complete by
                // definition, so a click matching no button is definitively not
                // the target (Start, tray, clock, gaps...) - neither the stale
                // flag nor button proximity matters.
                if (hookButtonsRecent) { match = ""; return false; }
                // The target's own button is currently enumerated: a click matching
                // no button is definitively not the target (Start, tray, clock,
                // gaps...), even when the cache is otherwise stale (e.g. a failed
                // secondary-monitor bar). Stale alone must not wake the game.
                if (hookTargetButtonPresent) { match = ""; return false; }
                if (!nearButton) { match = ""; return false; }
                if (!stale) { match = ""; return false; }
                match = "fallback near-button while stale";
                return true;
            }
        List<string> exeList;
        List<string> titleList;
        lock (HookCacheLock)
        {
            exeList = new List<string>(hookTargetExePaths);
            titleList = new List<string>(hookTargetTitles);
        }
        bool haveExe = exeList.Count > 0;
        bool haveTitle = titleList.Count > 0;
        if (!haveExe && !haveTitle)
        {
            match = "fallback target identity unknown";
            return true; // target identity unknown: resume to be safe
        }
        // Copy button strings while holding the lock, then match outside it so
        // the hook never blocks on the worker's UIA walk.
        string btnName, btnApp;
        lock (taskbarButtons)
        {
            btnName = hit.Name ?? "";
            btnApp = hit.AppPath ?? "";
        }
        string verdict = MatchButtonToTarget(btnName, btnApp, exeList, titleList);
        if (verdict == "appid")
        {
            match = "target button appid match";
            return true;
        }
        if (verdict == "title")
        {
            match = "target button title match";
            return true;
        }
        if (verdict == null)
        {
            match = "";
            return false; // button of a different app: definitely not ours
        }
        // Button could not be mapped (no AppPath, no usable name): if the
        // target's own button is enumerated elsewhere, this entry isn't it -
        // ignore. Otherwise treat it as a potential target so a click never
        // leaves the game stuck suspended.
        if (hookTargetButtonPresent) { match = ""; return false; }
        match = "fallback unmappable button";
        return true;
        }
        // No button data at all (Legacy/Unknown mode with an empty cache, or a
        // totally failed enumeration): buttons cannot be told apart, so resume
        // first and let the worker re-suspend if the click was not a switch-back.
        match = "fallback buttons=" + buttonCount + " stale=" + stale + " trayMode=" + trayMode;
        return true;
    }

    /// <summary>
    /// Pure button-vs-target matcher shared by the hook and the worker's
    /// target-presence check. Returns "appid"/"title" when the button belongs
    /// to the target, null when it definitively belongs to another app, and
    /// "unknown" when it cannot be mapped. No locks, no syscalls.
    /// </summary>
    private static string MatchButtonToTarget(string btnName, string btnApp, List<string> exeList, List<string> titleList)
    {
        bool haveExe = exeList != null && exeList.Count > 0;
        bool haveTitle = titleList != null && titleList.Count > 0;
        // (1) exe/AppId match. Most Win11 AutomationIds are opaque AppUserModelIds
        // ("Chrome", "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App") rather than exe
        // paths, so only a path-like AppId that fails to match is definitive proof
        // of another app. An opaque AppId falls through to title matching; it is
        // additionally compared against the target exe file name without extension
        // ("Chrome" vs "chrome.exe").
        if (haveExe && btnApp.Length > 0)
        {
            string p = StripAppidPrefix(btnApp).Replace('\\', '/').ToLowerInvariant();
            if (p.Length > 0)
            {
                bool pathLike = p.Contains("/") || p.EndsWith(".exe", StringComparison.Ordinal);
                bool matched = false;
                foreach (string t in exeList)
                {
                    if (p == t) { matched = true; break; }
                    int slash = t.LastIndexOf('/');
                    string file = slash >= 0 ? t.Substring(slash + 1) : t;
                    if (file.Length == 0) continue;
                    if (p == file || p.EndsWith("/" + file, StringComparison.Ordinal)) { matched = true; break; }
                    // opaque AppId vs "name.exe": compare without extension
                    string noExt = file.EndsWith(".exe", StringComparison.Ordinal)
                        ? file.Substring(0, file.Length - 4) : file;
                    if (noExt.Length > 0 && (p == noExt || p.EndsWith("/" + noExt, StringComparison.Ordinal)))
                    { matched = true; break; }
                }
                if (matched) return "appid";
                if (pathLike) return null; // path AppId of a different app: definitely not ours
                // opaque AppId with no exe-name match: undecided, try title below
            }
        }
        // (1b) button label vs target exe file name ("Notepad" vs "notepad.exe",
        // exact match only). Packaged apps expose opaque AppIds ("...!App") and
        // app-name-only labels, so neither the AppId nor the title comparison
        // can map them - yet the label equals the exe base name.
        if (haveExe && btnName.Length > 0)
        {
            string nb = StripButtonSuffixes(btnName).Trim().ToLowerInvariant();
            if (nb.Length > 0)
            {
                foreach (string t in exeList)
                {
                    int slash = t.LastIndexOf('/');
                    string file = slash >= 0 ? t.Substring(slash + 1) : t;
                    if (file.Length == 0) continue;
                    string noExt = file.EndsWith(".exe", StringComparison.Ordinal)
                        ? file.Substring(0, file.Length - 4) : file;
                    if (nb.Equals(file, StringComparison.Ordinal)
                        || (noExt.Length > 0 && nb.Equals(noExt, StringComparison.Ordinal)))
                        return "appid";
                }
            }
        }
        // (2) title match, tolerant of clipped labels and Win10 suffixes
        // (" - 1 running window", " pinned"). Prefix matching needs at least 3
        // overlapping chars so short titles can't collide ("War" vs "Warframe").
        if (haveTitle && btnName.Length > 0)
        {
            string n = StripButtonSuffixes(btnName).Trim().ToLowerInvariant();
            if (n.Length > 0)
            {
                foreach (string t in titleList)
                {
                    if (t.Length == 0) continue;
                    int overlap = Math.Min(n.Length, t.Length);
                    if (overlap >= 3 && (n.StartsWith(t, StringComparison.Ordinal) || t.StartsWith(n, StringComparison.Ordinal)))
                        return "title";
                    if (n.Equals(t, StringComparison.Ordinal))
                        return "title";
                }
                return null;
            }
        }
        return "unknown";
    }

    /// <summary>Refreshed by the worker (not the hook): cheap cache for CheapSwitchHit.</summary>
    private static void RefreshHookCache()
    {
        if ((DateTime.UtcNow - lastHookCache).TotalMilliseconds < 1000) return;
        lastHookCache = DateTime.UtcNow;
        // Fully idle (no targets, nothing frozen): the mouse hook is uninstalled
        // so none of this data is read. Skip the UIA walk / EnumWindows /
        // process queries entirely instead of paying them every second.
        bool idle = false;
        try
        {
            bool took = false;
            try
            {
                took = Monitor.TryEnter(StateLock, 0);
                if (took)
                    idle = targetName == null && WindowTargets.Count == 0
                        && Suspended.Count == 0 && Throttled.Count == 0;
            }
            finally { if (took) Monitor.Exit(StateLock); }
        }
        catch { idle = false; }
        if (idle) return;
        try { RefreshTaskbarButtons(); } catch { }
        try { hookButtonsRecent = (DateTime.UtcNow - lastButtonSuccessAt).TotalSeconds < 5; } catch { }
        try { RefreshHookTaskbarRects(); } catch { }
        List<IntPtr> hwnds = new List<IntPtr>();
        List<IntPtr> mains = new List<IntPtr>();
        string nameCopy = null;
        List<WindowTarget> windowCopy = new List<WindowTarget>();
        lock (StateLock)
        {
            foreach (WindowTarget t in WindowTargets) { hwnds.Add(t.Hwnd); windowCopy.Add(t); }
            nameCopy = targetName;
        }
        if (nameCopy != null)
        {
            try
            {
                Process[] list = Process.GetProcessesByName(nameCopy);
                foreach (Process p in list)
                {
                    try
                    {
                        if (p.MainWindowHandle != IntPtr.Zero)
                            mains.Add(p.MainWindowHandle);
                    }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            catch { }
        }
        lock (HookCacheLock)
        {
            hookTargetHwnds.Clear();
            hookTargetHwnds.AddRange(hwnds);
            hookTargetMains.Clear();
            hookTargetMains.AddRange(mains);
        }
        try { RefreshHookIdentityCache(windowCopy, nameCopy); } catch { }
    }

    /// <summary>
    /// Worker-only: snapshots the target's exe paths + window titles for the hook's
    /// precise taskbar matching. Runs on the worker so the hook never touches
    /// Process.MainModule or GetWindowText.
    /// </summary>
    private static void RefreshHookIdentityCache(List<WindowTarget> windowCopy, string nameCopy)
    {
        var exes = new List<string>();
        var titles = new List<string>();
        try
        {
            if (windowCopy != null && windowCopy.Count > 0)
            {
                foreach (WindowTarget t in windowCopy)
                {
                    try
                    {
                        using (Process p = Process.GetProcessById((int)t.Pid))
                        {
                            try
                            {
                                string f = p.MainModule.FileName;
                                if (!string.IsNullOrEmpty(f))
                                {
                                    string norm = f.Replace('\\', '/').ToLowerInvariant();
                                    if (!exes.Contains(norm)) exes.Add(norm);
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                    try
                    {
                        string title = GetWindowTitle(t.Hwnd);
                        if (!string.IsNullOrEmpty(title))
                        {
                            string norm = title.Trim().ToLowerInvariant();
                            if (norm.Length > 0 && !titles.Contains(norm)) titles.Add(norm);
                        }
                    }
                    catch { }
                }
            }
            else if (nameCopy != null)
            {
                try
                {
                    Process[] list = Process.GetProcessesByName(nameCopy);
                    foreach (Process p in list)
                    {
                        try
                        {
                            try
                            {
                                string f = p.MainModule.FileName;
                                if (!string.IsNullOrEmpty(f))
                                {
                                    string norm = f.Replace('\\', '/').ToLowerInvariant();
                                    if (!exes.Contains(norm)) exes.Add(norm);
                                }
                            }
                            catch { }
                            try
                            {
                                if (p.MainWindowHandle != IntPtr.Zero)
                                {
                                    string title = GetWindowTitle(p.MainWindowHandle);
                                    if (!string.IsNullOrEmpty(title))
                                    {
                                        string norm = title.Trim().ToLowerInvariant();
                                        if (norm.Length > 0 && !titles.Contains(norm)) titles.Add(norm);
                                    }
                                }
                            }
                            catch { }
                        }
                        finally { try { p.Dispose(); } catch { } }
                    }
                }
                catch { }
            }
        }
        catch { }
        // Is the target's own button in the current cache? Snapshot the buttons
        // without nesting locks (taskbarButtons -> compare -> volatile write),
        // so the hook never blocks on the worker's UIA walk.
        bool present = false;
        try
        {
            if (exes.Count > 0 || titles.Count > 0)
            {
                List<TrayButtonInfo> btnCopy;
                lock (taskbarButtons) btnCopy = new List<TrayButtonInfo>(taskbarButtons);
                foreach (TrayButtonInfo b in btnCopy)
                {
                    if (MatchButtonToTarget(b.Name ?? "", b.AppPath ?? "", exes, titles) != null)
                    { present = true; break; }
                }
            }
        }
        catch { }
        hookTargetButtonPresent = present;
        lock (HookCacheLock)
        {
            hookTargetExePaths.Clear();
            hookTargetExePaths.AddRange(exes);
            hookTargetTitles.Clear();
            hookTargetTitles.AddRange(titles);
        }
        try { LogTrayCache(); } catch { }
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
                    lastRestoreAt[pid] = DateTime.UtcNow;
                    Log("[RESUME]  " + DescribeProcess(pid) + " (pid " + pid + ") - " + reason);
                }
            }
            foreach (int pid in Throttled.ToList())
            {
                IntPtr h;
                if (Handles.TryGetValue(pid, out h))
                {
                    RestoreEcoLocked(pid, h);
                    lastRestoreAt[pid] = DateTime.UtcNow;
                    Log("[ECO-RESTORE] " + DescribeProcess(pid) + " (pid " + pid + ") - " + reason);
                }
            }
        }
    }

    /// <summary>Workstation locked (Win+L): freeze every locked target immediately.
    /// The normal defocus poll would catch this on the next tick anyway, but the
    /// explicit event is immediate and doesn't depend on what GetForegroundWindow
    /// returns on the secure desktop. Unlock needs no handler - the worker resumes
    /// on the next tick if the target is foreground again.</summary>
    public static void OnSessionLocked()
    {
        bool eco = IsEcoMode();
        List<int> pids;
        lock (StateLock) pids = Handles.Keys.ToList();
        foreach (int pid in pids)
        {
            if (IsSelfPid((uint)pid)) continue;
            if (IsNeverSuspendPid((uint)pid)) continue;
            IntPtr h = IntPtr.Zero;
            lock (StateLock)
            {
                if (IsFrozenLocked(pid)) continue;
                if (!Handles.TryGetValue(pid, out h)) continue;
            }
            if (h == IntPtr.Zero) continue;
            if (eco)
            {
                lock (StateLock)
                {
                    IntPtr hh;
                    if (!Handles.TryGetValue(pid, out hh)) continue;
                    if (IsFrozenLocked(pid)) continue;
                    ApplyEcoLocked(pid, hh);
                }
                continue;
            }
            if (NtSuspendProcess(h) == 0)
            {
                lock (StateLock) Suspended.Add(pid);
                Log("[SUSPEND] " + DescribeProcess(pid) + " (pid " + pid + ") - workstation locked");
            }
        }
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

    private static DateTime taskbarDetectAt = DateTime.MinValue;

    private static void RefreshTaskbarButtons()
    {
        // Adaptive cadence: the UIA walk is the most expensive periodic work
        // here (tens of ms), and clicks only need fresh buttons around window
        // switching. Refresh every 1s for 15s after any foreground change,
        // every 4s once the foreground is stable.
        long intervalMs = (DateTime.UtcNow - lastFgChangeAt).TotalSeconds < 15 ? 1000 : 4000;
        if ((DateTime.UtcNow - taskbarButtonsCached).TotalMilliseconds < intervalMs) return;
        taskbarButtonsCached = DateTime.UtcNow;
        // Enumerate buttons on every taskbar (primary + secondary monitors): a
        // click on another app's button on a secondary taskbar must be ignored too.
        List<IntPtr> bars;
        try { bars = EnumTaskbars(); }
        catch { bars = null; }
        if (bars == null || bars.Count == 0)
        {
            IntPtr single = IntPtr.Zero;
            try { single = FindTaskbarWindow(); }
            catch { }
            bars = new List<IntPtr>();
            if (single != IntPtr.Zero) bars.Add(single);
        }
        if (bars.Count == 0)
        {
            // Taskbar momentarily gone (Explorer restarting, fullscreen shell
            // hiccup): keep the previously probed mode, mark stale so the hook
            // resumes-first until the taskbar is back. Never latch Legacy here.
            taskbarButtonsStale = true;
            LogTrayCache();
            return;
        }
        List<TrayButtonInfo> prevButtons;
        lock (taskbarButtons)
        {
            prevButtons = new List<TrayButtonInfo>(taskbarButtons);
            taskbarButtons.Clear();
        }
        bool failed = false;
        // Re-probe while undecided (Unknown) or downgraded (Legacy): a single
        // failed probe (taskbar busy, Explorer restarting) must not stick
        // forever. Throttled to every 5s - the UIA walk is too heavy to run on
        // every refresh.
        if ((trayMode == TrayMode.Unknown || trayMode == TrayMode.Legacy)
            && (DateTime.UtcNow - taskbarDetectAt).TotalSeconds >= 5)
        {
            taskbarDetectAt = DateTime.UtcNow;
            TrayMode detected = TrayMode.Legacy;
            foreach (IntPtr bar in bars)
            {
                try
                {
                    TrayMode m = DetectTrayMode(bar);
                    if (m == TrayMode.Uia) { detected = TrayMode.Uia; break; }
                    if (m == TrayMode.Win32Button) detected = TrayMode.Win32Button;
                }
                catch { }
            }
            if (detected != trayMode)
                Log("[TASKBAR] tray mode re-probed: " + trayMode + " -> " + detected);
            trayMode = detected;
        }
        if (trayMode == TrayMode.Uia || trayMode == TrayMode.Win32Button)
        {
            bool attempted = false;
            foreach (IntPtr bar in bars)
            {
                // The bar list is cached for 2s and can hold a dead handle
                // (Explorer hiccup, secondary monitor detached). Filling a dead
                // bar always fails and wrongly marks the whole cache stale -
                // which is exactly what woke the game on clicks near (but not
                // on) real buttons. Skip dead bars without failing.
                bool alive = false;
                try { alive = IsWindow(bar); }
                catch { alive = false; }
                if (!alive) continue;
                attempted = true;
                bool ok;
                if (trayMode == TrayMode.Uia) ok = FillFromUia(bar);
                else ok = FillFromWin32(bar);
                if (!ok) failed = true;
            }
            if (!attempted) failed = true; // no live bar to verify against
        }
        else
        {
            failed = true; // Legacy: no buttons, hook keeps old resume-first behaviour
        }
        if (failed)
        {
            // A failed enumeration must not wipe the last-good cache: with zero
            // buttons every taskbar click (Start included) would resume-first
            // until the next successful tick.
            lock (taskbarButtons)
            {
                if (taskbarButtons.Count == 0 && prevButtons.Count > 0)
                    taskbarButtons.AddRange(prevButtons);
            }
        }
        else
        {
            // A "successful" walk that found zero buttons while we previously
            // had some is bogus too (Start-menu animation tears down the XAML
            // tree for a tick). Keep last-good and report stale instead of
            // emptying the cache. A genuinely empty taskbar (prev also empty)
            // still clears normally.
            bool emptied = false;
            lock (taskbarButtons) emptied = taskbarButtons.Count == 0;
            if (emptied && prevButtons.Count > 0)
            {
                lock (taskbarButtons)
                {
                    if (taskbarButtons.Count == 0)
                        taskbarButtons.AddRange(prevButtons);
                }
                failed = true;
            }
        }
        taskbarButtonsStale = failed;
        if (!failed) lastButtonSuccessAt = DateTime.UtcNow;
        LogTrayCache();
    }

    /// <summary>Logs tray mode / button / identity stats only when they change (worker thread).</summary>
    private static void LogTrayCache()
    {
        try
        {
            int buttons, exes, titles, sysAreas;
            lock (taskbarButtons) buttons = taskbarButtons.Count;
            lock (HookCacheLock) { exes = hookTargetExePaths.Count; titles = hookTargetTitles.Count; }
            lock (TaskbarLock) sysAreas = hookSystemRects.Count;
            string s = "trayMode=" + trayMode + " buttons=" + buttons + " stale=" + taskbarButtonsStale
                + " targetExe=" + exes + " targetTitles=" + titles + " sysAreas=" + sysAreas
                + " targetBtn=" + hookTargetButtonPresent + " recent=" + hookButtonsRecent;
            if (s != lastTrayCacheLog)
            {
                lastTrayCacheLog = s;
                Log("[TASKBAR] " + s);
            }
        }
        catch { }
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
        AutomationElement el;
        try { el = walker.GetFirstChild(parent); }
        catch { return false; }
        while (el != null)
        {
            try
            {
                string cls = el.Current.ClassName ?? "";
                if (cls.Contains("TaskListButton")) return true;
                if (HasTaskListButtons(el, walker)) return true;
            }
            catch { }
            try { el = walker.GetNextSibling(el); }
            catch { return false; }
        }
        return false;
    }

    /// <summary>Fills taskbarButtons from one taskbar via UIA. Returns false when the walk failed.</summary>
    private static bool FillFromUia(IntPtr tray)
    {
        List<TrayButtonInfo> fresh = new List<TrayButtonInfo>();
        try
        {
            AutomationElement root = AutomationElement.FromHandle(tray);
            if (root == null) return false;
            var walker = TreeWalker.ControlViewWalker;
            // Walk WITHOUT holding taskbarButtons: the UIA walk can take
            // 50-200ms and the mouse hook needs that lock per click. Holding
            // it here stalls all mouse input (jumpy cursor).
            FillUiaWalk(root, walker, fresh);
            lock (taskbarButtons)
            {
                taskbarButtons.AddRange(fresh);
            }
            return true;
        }
        catch { return false; }
    }

    private static void FillUiaWalk(AutomationElement parent, TreeWalker walker, List<TrayButtonInfo> output)
    {
        // Every UIA call can throw COM exceptions, especially while the shell
        // is animating (Start menu opening/closing). An unguarded throw here
        // used to fail the whole enumeration, marking the cache stale exactly
        // when the user clicks Start - and the stale fallback then woke the game.
        AutomationElement el;
        try { el = walker.GetFirstChild(parent); }
        catch { return; }
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
                        output.Add(new TrayButtonInfo
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
                    FillUiaWalk(el, walker, output);
                }
            }
            catch { }
            try { el = walker.GetNextSibling(el); }
            catch { break; }
        }
    }

    private static bool FillFromWin32(IntPtr tray)
    {
        var fresh = new List<TrayButtonInfo>();
        try
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
                fresh.Add(new TrayButtonInfo
                {
                    Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom,
                    Name = tb.ToString()
                });
            }
            lock (taskbarButtons)
                taskbarButtons.AddRange(fresh);
            return true;
        }
        catch { return false; }
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
        lock (TaskbarLock)
        {
            if ((DateTime.UtcNow - taskbarWindowsAt).TotalMilliseconds < 2000)
                return new List<IntPtr>(taskbarWindows);
        }
        var fresh = new List<IntPtr>();
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            var sb = new StringBuilder(128);
            GetClassName(h, sb, sb.Capacity);
            string cls = sb.ToString();
            for (int i = 0; i < TaskbarClassNames.Length; i++)
            {
                if (cls == TaskbarClassNames[i])
                {
                    fresh.Add(h);
                    break;
                }
            }
            return true;
        }, IntPtr.Zero);
        lock (TaskbarLock)
        {
            taskbarWindowsAt = DateTime.UtcNow;
            taskbarWindows.Clear();
            taskbarWindows.AddRange(fresh);
            return new List<IntPtr>(taskbarWindows);
        }
    }

    /// <summary>
    /// Hook-thread hit test: reads only the worker-cached rects, never calls
    /// EnumWindows (that scan on every click is what made the cursor jumpy).
    /// Empty cache = conservative true so a click never leaves the game stuck.
    /// </summary>
    private static bool IsInTaskbarCached(int x, int y)
    {
        lock (TaskbarLock)
        {
            if (hookTaskbarRects.Count == 0) return true;
            foreach (RECT r in hookTaskbarRects)
            {
                if (x >= r.Left - 8 && x <= r.Right + 8
                    && y >= r.Top - 8 && y <= r.Bottom + 8)
                    return true;
            }
        }
        return false;
    }

    /// <summary>Worker-only: refreshes the hook's taskbar rect cache (no hook may call this).</summary>
    private static void RefreshHookTaskbarRects()
    {
        List<IntPtr> bars;
        try { bars = EnumTaskbars(); }
        catch { return; }
        var rects = new List<RECT>(bars.Count);
        var sysRects = new List<RECT>(bars.Count * 2);
        foreach (IntPtr h in bars)
        {
            RECT r;
            try
            {
                if (GetWindowRect(h, out r)) rects.Add(r);
            }
            catch { }
            // System areas that are never app buttons (Win32 children, present on
            // Win10 and Win11 alike). The Win11 Start button itself is XAML with
            // no HWND, so it is covered by the button-cache path instead.
            try { AddChildRect(h, "Start", sysRects); } catch { }
            try { AddChildRect(h, "TrayNotifyWnd", sysRects); } catch { }
        }
        lock (TaskbarLock)
        {
            // Never zero the caches on a transient miss (Explorer restarting,
            // Start-menu animation): empty rects make every click look like it
            // is inside the taskbar with no known buttons around.
            if (rects.Count > 0)
            {
                hookTaskbarRects.Clear();
                hookTaskbarRects.AddRange(rects);
            }
            if (sysRects.Count > 0)
            {
                hookSystemRects.Clear();
                hookSystemRects.AddRange(sysRects);
            }
        }
    }

    private static void AddChildRect(IntPtr parent, string cls, List<RECT> output)
    {
        IntPtr child = FindWindowEx(parent, IntPtr.Zero, cls, null);
        if (child == IntPtr.Zero) return;
        RECT r;
        if (GetWindowRect(child, out r)
            && r.Right > r.Left && r.Bottom > r.Top)
            output.Add(r);
    }

    /// <summary>
    /// Hook-thread hit test against the worker-cached system areas (Start button,
    /// tray/clock). Tight rects, no margin: these sit adjacent to real app buttons
    /// and must never swallow a click on the game's own button.
    /// </summary>
    private static bool IsInSystemAreaCached(int x, int y)
    {
        lock (TaskbarLock)
        {
            foreach (RECT r in hookSystemRects)
            {
                if (x >= r.Left && x <= r.Right && y >= r.Top && y <= r.Bottom)
                    return true;
            }
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
            Log("[UNFREEZE] Freeze-on-defocus turned off - targets resumed.");
        }
        else
        {
            enabled = true;
            // clear any stale switch state left over from while the app was off, so a
            // missed gesture-end event can't delay the first suspension after enabling
            kbdSwitching = false;
            mouseSwitching = false;
            mouseDownWasSwitch = false;
            Log("[FREEZE] Freeze-on-defocus turned on (" + FreezeModeLabel + ").");
        }
    }

    private static void FocusTargetWindow()
    {
        IntPtr hwnd = IntPtr.Zero;
        string nameCopy = null;
        lock (StateLock)
        {
            if (WindowTargets.Count > 0)
                hwnd = WindowTargets[WindowTargets.Count - 1].Hwnd;
            else if (targetName != null)
                nameCopy = targetName;
        }
        if (hwnd == IntPtr.Zero && nameCopy != null)
        {
            try
            {
                Process[] list = Process.GetProcessesByName(nameCopy);
                foreach (Process p in list)
                {
                    try
                    {
                        if (p.MainWindowHandle != IntPtr.Zero)
                        {
                            hwnd = p.MainWindowHandle;
                            break;
                        }
                    }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            catch { }
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
        string line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg;
        lock (LogLock)
        {
            LogBuffer.Add(line);
            while (LogBuffer.Count > LogCapacity) LogBuffer.RemoveAt(0);
            // Never do file I/O on the caller thread (hooks call Log): queue it,
            // the worker flushes once per second.
            if (logToFile && logFilePath.Length > 0)
                PendingFileLines.Add(line);
            if (PendingFileLines.Count > 2000)
                PendingFileLines.RemoveRange(0, PendingFileLines.Count - 2000);
        }
    }

    private static void FlushLogToFile()
    {
        string[] pending = null;
        lock (LogLock)
        {
            if (PendingFileLines.Count == 0) return;
            pending = PendingFileLines.ToArray();
            PendingFileLines.Clear();
        }
        try
        {
            if (logToFile && logFilePath.Length > 0)
                File.AppendAllLines(logFilePath, pending);
        }
        catch { }
    }

    public static string[] GetLogLines()
    {
        lock (LogLock) return LogBuffer.ToArray();
    }

    // ---- Event-driven wakeups (foreground / minimize WinEvents) ----
    // Polling alone can lag a state change by up to one poll interval. These
    // hooks wake the worker the moment the foreground moves or a window
    // minimizes, so evaluation starts in milliseconds. The poll interval
    // remains as a fallback (a missed event can never stick), and all safety
    // gates (switch grace, settle delay, post-restore recovery) are unchanged.
    private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObj, int idChild, uint tid, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint evMin, uint evMax, IntPtr hmod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    private const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    // Rooted so the GC can never collect the callback while hooked.
    private static readonly WinEventProc winEventProc = WinEventCallback;
    private static IntPtr fgEventHook = IntPtr.Zero;
    private static IntPtr minEventHook = IntPtr.Zero;
    private static readonly AutoResetEvent wakeEvent = new AutoResetEvent(false);
    // Last MINIMIZESTART window + timestamp: lets the worker treat that exact
    // window as minimized immediately instead of waiting for the ~1s
    // minimized-state refresh. Guarded by MinEventLock; the callback only
    // records and wakes, never blocks on worker locks.
    private static readonly object MinEventLock = new object();
    private static IntPtr lastMinHwnd = IntPtr.Zero;
    private static DateTime lastMinAt = DateTime.MinValue;

    private static void StartEventWatcher()
    {
        StopEventWatcher();
        try { fgEventHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS); }
        catch { fgEventHook = IntPtr.Zero; }
        try { minEventHook = SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS); }
        catch { minEventHook = IntPtr.Zero; }
        if (fgEventHook == IntPtr.Zero && minEventHook == IntPtr.Zero)
            Log("[WARN] WinEvent hooks failed - falling back to polling only.");
    }

    private static void StopEventWatcher()
    {
        if (fgEventHook != IntPtr.Zero)
        {
            try { UnhookWinEvent(fgEventHook); }
            catch { }
            fgEventHook = IntPtr.Zero;
        }
        if (minEventHook != IntPtr.Zero)
        {
            try { UnhookWinEvent(minEventHook); }
            catch { }
            minEventHook = IntPtr.Zero;
        }
    }

    /// <summary>Start/stop WinEvent watchers to match whether any target is locked.</summary>
    public static void EnsureEventWatcherState()
    {
        if (HasLockedTargets())
        {
            if (fgEventHook == IntPtr.Zero && minEventHook == IntPtr.Zero)
                try { StartEventWatcher(); } catch { }
        }
        else
        {
            try { StopEventWatcher(); } catch { }
        }
    }

    private static void NotifyTargetsChanged()
    {
        try { wakeEvent.Set(); } catch { }
        try { EnsureEventWatcherState(); } catch { }
        try { if (TargetsChanged != null) TargetsChanged(); } catch { }
    }

    /// <summary>
    /// Runs on a system thread: record the minimize window and wake the worker.
    /// Must never throw (an unhandled hook exception would tear down the
    /// process) and never touch worker locks, Process, UIA or Log.
    /// </summary>
    private static void WinEventCallback(IntPtr hook, uint ev, IntPtr hwnd, int idObj, int idChild, uint tid, uint time)
    {
        try
        {
            if (ev == EVENT_SYSTEM_MINIMIZESTART && hwnd != IntPtr.Zero)
            {
                try
                {
                    lock (MinEventLock)
                    {
                        lastMinHwnd = hwnd;
                        lastMinAt = DateTime.UtcNow;
                    }
                }
                catch { }
            }
            try { wakeEvent.Set(); }
            catch { }
        }
        catch { }
    }

    /// <summary>True when hwnd was minimize-reported within the last 2s (worker thread).</summary>
    private static bool IsFreshMinEvent(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        lock (MinEventLock)
        {
            return hwnd == lastMinHwnd && (DateTime.UtcNow - lastMinAt).TotalMilliseconds < 2000;
        }
    }

    /// <summary>True when pid owns the minimize-reported window (worker thread).</summary>
    private static bool IsFreshMinPid(int pid)
    {
        if (pid <= 0) return false;
        IntPtr hwnd = IntPtr.Zero;
        lock (MinEventLock)
        {
            if ((DateTime.UtcNow - lastMinAt).TotalMilliseconds >= 2000) return false;
            hwnd = lastMinHwnd;
        }
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            uint wpid;
            if (GetWindowThreadProcessId(hwnd, out wpid) != 0 && (int)wpid == pid)
                return true;
        }
        catch { }
        return false;
    }

    private static void WorkerLoop()
    {
        DateTime lastFlush = DateTime.UtcNow;
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
            if ((DateTime.UtcNow - lastFlush).TotalMilliseconds >= 1000)
            {
                lastFlush = DateTime.UtcNow;
                try { FlushLogToFile(); } catch { }
            }
            // Event-driven wake with poll fallback: WinEvents cut reaction to
            // milliseconds; the wait cap keeps every safety net (child scans,
            // audio remute, log flush, missed events) on schedule.
            // When there is no locked target we don't need the foreground poll; hold
            // off longer and wake instantly when one is added (NotifyTargetsChanged).
            int waitMs = HasLockedTargets() ? pollMs : 2000;
            if (releasePending)
            {
                // Wake exactly when the release grace expires. Sleeping the
                // full poll interval past the deadline added up to 250ms of
                // frozen-window lag on every switch back to the target.
                DateTime due = releaseResumeAt;
                int remaining = due == DateTime.MinValue
                    ? 25
                    : (int)Math.Ceiling((due - DateTime.UtcNow).TotalMilliseconds);
                if (remaining < waitMs) waitMs = Math.Max(1, remaining);
            }
            try { wakeEvent.WaitOne(waitMs); }
            catch { try { Thread.Sleep(waitMs); } catch { } }
        }
        try { FlushLogToFile(); } catch { }
    }

    private static void RunWorkerTick(int tick)
    {
        // No locked target: nothing to freeze/resume/watch. Stay idle and keep
        // the caches untouched until a target is locked.
        if (!HasLockedTargets())
        {
            releasePending = false;
            return;
        }
        if (!enabled)
        {
            releasePending = false;
            ResumeAll();
            return;
        }

        // Fallback for hook-requested resumes (ThreadPool already handles the
        // fast path; this covers the case where it was delayed/starved).
        if (pendingResume)
        {
            try { DrainPendingResume(); } catch { }
        }
        if (pendingForegroundSuspend)
        {
            pendingForegroundSuspend = false;
            try { SuspendOriginTarget("switch gesture started (origin)"); } catch { }
            try { SuspendForegroundTarget("switch gesture started"); } catch { }
        }
        RefreshHookCache();

        uint fgPid = 0;
        IntPtr fg = GetForegroundWindow();
        if (fg != IntPtr.Zero) GetWindowThreadProcessId(fg, out fgPid);
        if (fgPid != lastFgPid)
        {
            lastFgPid = fgPid;
            lastFgChangeAt = DateTime.UtcNow;
            Log("[FG] foreground: pid " + fgPid + " (" + DescribeProcess((int)fgPid)
                + "), title=\"" + GetWindowTitle(fg) + "\"");
        }

        // Post-release switch decision (see EvaluateReleasePending): runs on the
        // fresh foreground reading, before the mode ticks below.
        if (releasePending)
        {
            try { EvaluateReleasePending(fgPid); } catch { }
        }

        DateTime now = DateTime.UtcNow;
        if (targetName != null)
        {
            if ((now - lastNameRefresh).TotalMilliseconds >= 1000)
            {
                lastNameRefresh = now;
                RefreshNameTargets();
            }
            TickNameMode();
        }
        else
        {
            if ((now - lastWindowRefresh).TotalMilliseconds >= 1000)
            {
                lastWindowRefresh = now;
                RefreshWindowTargets();
                GcChildHandles();
            }
            TickWindowMode();
        }
        try { RemuteThrottledAudio(); } catch { }
    }

    /// <summary>Re-mutes throttled pids every ~5s: games can recreate their audio
    /// session mid-throttle, which would come back unmuted. Idempotent.</summary>
    private static void RemuteThrottledAudio()
    {
        if ((DateTime.UtcNow - lastAudioRemute).TotalMilliseconds < 5000) return;
        lastAudioRemute = DateTime.UtcNow;
        List<int> pids;
        lock (StateLock)
        {
            if (Throttled.Count == 0) return;
            pids = Throttled.ToList();
        }
        foreach (int pid in pids)
        {
            try { AudioMuter.SetPidMuted(pid, true); }
            catch { }
        }
    }

    private static void RefreshNameTargets()
    {
        if (targetName == null) return;
        string nameCopy;
        lock (StateLock) nameCopy = targetName;
        if (nameCopy == null) return;
        List<int> ids = new List<int>();
        var minimizedNow = new HashSet<int>();
        try
        {
            Process[] list = Process.GetProcessesByName(nameCopy);
            foreach (Process p in list)
            {
                try
                {
                    if (!p.ProcessName.Equals(nameCopy, StringComparison.OrdinalIgnoreCase)) continue;
                    ids.Add(p.Id);
                    try
                    {
                        IntPtr mw = p.MainWindowHandle;
                        if (mw != IntPtr.Zero && IsIconic(mw)) minimizedNow.Add(p.Id);
                    }
                    catch { }
                }
                finally { try { p.Dispose(); } catch { } }
            }
        }
        catch { }

        var alive = new HashSet<int>(ids);
        lock (StateLock)
        {
            if (targetName == null || !targetName.Equals(nameCopy, StringComparison.OrdinalIgnoreCase)) return;
            foreach (int pid in Handles.Keys.ToList())
            {
                if (!alive.Contains(pid))
                    DropHandleLocked(pid);
                else if (HasPidBeenReused(pid))
                {
                    Log("[WARN] pid " + pid + " was reused by a new process - reopening handle.");
                    DropHandleLocked(pid);
                }
            }
            foreach (int id in ids)
            {
                if (!Handles.ContainsKey(id))
                    OpenHandleLocked(id);
            }
            Minimized.Clear();
            foreach (int pid in minimizedNow) Minimized.Add(pid);
            NamePids.Clear();
            foreach (int pid in Handles.Keys) NamePids.Add(pid);
        }
    }

    private static void RefreshWindowTargets()
    {
        List<WindowTarget> dead = new List<WindowTarget>();
        lock (StateLock)
        {
            for (int i = WindowTargets.Count - 1; i >= 0; i--)
            {
                if (HasPidBeenReused((int)WindowTargets[i].Pid))
                {
                    WindowTarget reused = WindowTargets[i];
                    Log("[WARN] Locked pid " + reused.Pid + " was reused - removing stale lock.");
                    if (reused.ChildrenLive) DropChildrenOf((int)reused.Pid);
                    WindowTargets.RemoveAt(i);
                    DropHandleLocked((int)reused.Pid);
                    continue;
                }
                if (!IsWindow(WindowTargets[i].Hwnd))
                {
                    dead.Add(WindowTargets[i]);
                    WindowTarget d = WindowTargets[i];
                    if (d.ChildrenLive) DropChildrenOf((int)d.Pid);
                    WindowTargets.RemoveAt(i);
                    DropHandleLocked((int)d.Pid);
                }
            }
        }
        foreach (WindowTarget d in dead)
            Log("[WARN] Locked window closed: " + DescribeProcess((int)d.Pid) + " (pid " + d.Pid + "). Removed from locked set.");
        if (dead.Count > 0) PersistLockedProcesses();
    }

    /// <summary>Drops handles for exited child processes the tree walk no longer sees.</summary>
    private static void GcChildHandles()
    {
        // Cheap checks first: the full process snapshot below is the most
        // expensive call in the idle loop. Skip it when there is nothing to clean.
        lock (StateLock)
        {
            if (targetName != null) return; // name-mode handles are all top-level, refreshed separately
            if (WindowTargets.Count == 0 || Handles.Count == 0) return;
        }
        List<ProcInfo> snap = null;
        try { snap = SnapshotProcesses(); } catch { return; }
        if (snap == null) return;
        var alive = new HashSet<int>();
        foreach (ProcInfo p in snap) alive.Add(p.Pid);
        List<int> parents = new List<int>();
        lock (StateLock)
        {
            if (targetName != null) return; // name-mode handles are all top-level, refreshed separately
            foreach (WindowTarget t in WindowTargets) parents.Add((int)t.Pid);
        }
        // valid = locked pids + all current descendants; anything else is a dead child leak
        var keep = new HashSet<int>(parents);
        var stack = new Stack<int>(parents);
        var index = new Dictionary<int, List<int>>();
        foreach (ProcInfo p in snap)
        {
            List<int> kids;
            if (!index.TryGetValue(p.Parent, out kids)) { kids = new List<int>(); index[p.Parent] = kids; }
            kids.Add(p.Pid);
        }
        while (stack.Count > 0)
        {
            int cur = stack.Pop();
            List<int> kids;
            if (!index.TryGetValue(cur, out kids)) continue;
            foreach (int k in kids)
            {
                if (keep.Add(k)) stack.Push(k);
            }
        }
        List<int> toDrop = new List<int>();
        lock (StateLock)
        {
            foreach (int pid in Handles.Keys.ToList())
            {
                if (!alive.Contains(pid)) toDrop.Add(pid);
                else if (!keep.Contains(pid) && WindowTargets.Count > 0) toDrop.Add(pid);
            }
            foreach (int pid in toDrop) DropHandleLocked(pid);
        }
        if (toDrop.Count > 0)
            Log("[INFO] Cleaned up " + toDrop.Count + " exited helper process handle(s).");
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
        // Self-heal a stale mouse gesture. The mouse hook is uninstalled as
        // soon as a click resumes the last frozen target (AnyFrozen becomes
        // false), so the matching mouse-up can be lost. Left set, the flag
        // would keep the target running (holding) until the 30s gesture
        // timeout - and it used to veto keyboard release decisions entirely.
        if (mouseSwitching && !IsLeftButtonPhysicallyDown())
        {
            mouseSwitching = false;
            mouseDownWasSwitch = false;
        }
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

    /// <summary>Physical left-button state (worker thread): heals a mouse-up
    /// lost when the mouse hook was uninstalled mid-click.</summary>
    private static bool IsLeftButtonPhysicallyDown()
    {
        try { return (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0; }
        catch { return true; } // on error keep the cached flag (conservative)
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

    private static void TickWindowMode()
    {
        bool holding = ComputeHolding();
        uint fgPid = ResolveForegroundPid();
        if (fgPid == 0) holding = true; // transient no-foreground reading
        // (Alt+Tab animation, UAC prompt, lock screen): change nothing this
        // tick instead of throttling on a phantom defocus and restoring a
        // tick later.

        bool ignoreMinimize = Config != null && !Config.SuspendWhenMinimized;

        bool doChildScan = (DateTime.UtcNow - lastChildScan).TotalMilliseconds >= 2000;
        if (doChildScan) lastChildScan = DateTime.UtcNow;
        List<int> childScans = new List<int>();
        lock (StateLock)
        {
            foreach (WindowTarget t in WindowTargets)
            {
                int pid = (int)t.Pid;
                // Reopen handles lost to a stale-suspend failure so window-mode
                // targets recover instead of going permanently inert.
                if (!Handles.ContainsKey(pid)) OpenHandleLocked(pid);
                bool focused = (int)fgPid == pid && !kbdSwitching; // gesture: focus is about to move; never resume mid-gesture
                // Minimizing is an explicit, stable signal (unlike a focus change
                // that may flicker during switch animations), so a minimized
                // target skips the settle delay in ApplyLocked and suspends
                // almost immediately. A minimized window can also keep owning
                // the foreground (nothing else takes it), so it counts as
                // defocused too - unless ignoring minimizes, which keeps it running.
                bool minimized = false;
                try { minimized = IsIconic(t.Hwnd); } catch { }
                if (!minimized) minimized = IsFreshMinEvent(t.Hwnd);
                if (minimized) focused = ignoreMinimize;
                bool wasSuspended = Suspended.Contains(pid) || Throttled.Contains(pid);
                ApplyLocked(pid, focused, fgPid, holding, minimized && !ignoreMinimize);
                if (!focused && !holding)
                {
                    // Re-scan only on the suspend transition or every 2s, never on every tick.
                    // The snapshot itself runs outside StateLock (see below).
                    if (!wasSuspended || doChildScan)
                    {
                        childScans.Add(pid);
                        t.ChildrenLive = true;
                    }
                }
                else if (focused && t.ChildrenLive)
                {
                    DropChildrenOf(pid);
                    // Only stop watching once no frozen descendants remain: a
                    // failed/incomplete drop (missed snapshot, new child) must
                    // be retried on the next focused tick, otherwise a helper
                    // process stays capped and the game never feels restored.
                    if (!AnyFrozenDescendantLocked(pid))
                        t.ChildrenLive = false;
                }
            }
        }
        foreach (int parentPid in childScans) SuspendChildrenOf(parentPid);
    }

    /// <summary>
    /// One snapshot of every running process: pid, parent pid, exe name.
    /// Replaces per-node WMI queries (~20-50 ms each) with a single
    /// NtQuerySystemInformation call (&lt;1 ms). Returns null if it fails;
    /// callers then skip that scan tick and retry next time.
    /// </summary>
    private static List<ProcInfo> SnapshotProcesses()
    {
        int len = 1 << 20; // 1 MB start; grows if a busy system needs more
        IntPtr buf = Marshal.AllocHGlobal(len);
        try
        {
            int returned;
            while (NtQuerySystemInformation(5 /*SystemProcessInformation*/, buf, len, out returned) == unchecked((int)0xC0000004) /*STATUS_INFO_LENGTH_MISMATCH*/)
            {
                Marshal.FreeHGlobal(buf);
                len = Math.Max(returned, len * 2);
                buf = Marshal.AllocHGlobal(len);
                if (len > (64 << 20)) return null; // runaway: give up, retry next tick
            }
            var list = new List<ProcInfo>(300);
            IntPtr cur = buf;
            while (true)
            {
                SYSTEM_PROCESS_INFORMATION p = (SYSTEM_PROCESS_INFORMATION)Marshal.PtrToStructure(cur, typeof(SYSTEM_PROCESS_INFORMATION));
                string name = "";
                if (p.ImageName.Buffer != IntPtr.Zero && p.ImageName.Length > 0)
                    name = Marshal.PtrToStringUni(p.ImageName.Buffer, p.ImageName.Length / 2);
                list.Add(new ProcInfo { Pid = (int)p.UniqueProcessId, Parent = (int)p.InheritedFromUniqueProcessId, Name = name });
                if (p.NextEntryOffset == 0) break;
                cur = new IntPtr(cur.ToInt64() + p.NextEntryOffset);
            }
            return list;
        }
        catch { return null; }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private class ProcInfo { public int Pid, Parent; public string Name; }

    // Children of parentPid from the latest snapshot, or null when no snapshot.
    private static HashSet<int> ChildPids(List<ProcInfo> snap, int parentPid)
    {
        var kids = new HashSet<int>();
        foreach (ProcInfo p in snap)
        {
            if (p.Pid == parentPid || p.Parent != parentPid) continue;
            kids.Add(p.Pid);
        }
        return kids;
    }

    // When the main window is suspended, also suspend its descendant processes
    // (renderers, helpers) so they don't burn CPU while the parent is frozen.
    // In Eco mode descendants are throttled instead. Walks the whole process
    // tree (children, grandchildren, ...) using one
    // NtQuerySystemInformation snapshot (see SnapshotProcesses) instead of a
    // WMI query per node. Never-suspend names stop the walk at that node.
    private static void SuspendChildrenOf(int parentPid)
    {
        var snap = SnapshotProcesses();
        if (snap == null) return;
        lock (StateLock)
        {
            SuspendDescendants(parentPid, snap, new HashSet<int>());
        }
    }

    private static void SuspendDescendants(int pid, List<ProcInfo> snap, HashSet<int> seen)
    {
        seen.Add(pid);
        int selfPid = Process.GetCurrentProcess().Id;
        foreach (int cid in ChildPids(snap, pid))
        {
            if (cid == selfPid || !seen.Add(cid)) continue;
            // Snapshot names are free; the old path paid a Process lookup per child.
            if (IsNeverSuspendName(NameOf(snap, cid))) continue;
            OpenHandleLocked(cid);
            ApplyLocked(cid, false, 0, false, false);
            SuspendDescendants(cid, snap, seen);
        }
    }

    // Exe name from the snapshot (without ".exe"), "" when unknown.
    private static string NameOf(List<ProcInfo> snap, int pid)
    {
        foreach (ProcInfo p in snap)
        {
            if (p.Pid != pid) continue;
            string n = p.Name ?? "";
            return n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n.Substring(0, n.Length - 4) : n;
        }
        return "";
    }

    private static void TickNameMode()
    {
        bool holding = ComputeHolding();
        uint fgPid = ResolveForegroundPid();
        if (fgPid == 0) holding = true; // same transient guard as window mode

        bool ignoreMinimize = Config != null && !Config.SuspendWhenMinimized;

        bool doChildScan = (DateTime.UtcNow - lastChildScan).TotalMilliseconds >= 2000;
        if (doChildScan) lastChildScan = DateTime.UtcNow;
        List<int> toSuspendChildren = new List<int>();
        List<int> toDropChildren = new List<int>();
        lock (StateLock)
        {
            NamePids.Clear();
            foreach (int pid in Handles.Keys) NamePids.Add(pid);
            foreach (int pid in Handles.Keys.ToList())
            {
                bool focused = fgPid != 0 && (int)fgPid == pid && !kbdSwitching; // same gesture guard as window mode
                // Minimized set is refreshed every ~1s: same fast-path as window
                // mode, just with slightly coarser minimize detection. A
                // minimized pid counts as defocused even if it still owns the
                // foreground.
                bool minimized = Minimized.Contains(pid);
                if (!minimized) minimized = IsFreshMinPid(pid);
                if (minimized) focused = ignoreMinimize;
                bool wasSuspended = Suspended.Contains(pid) || Throttled.Contains(pid);
                ApplyLocked(pid, focused, fgPid, holding, minimized && !ignoreMinimize);
                if (!focused && !holding)
                {
                    if (!wasSuspended || doChildScan) toSuspendChildren.Add(pid);
                }
                else if (focused)
                {
                    toDropChildren.Add(pid);
                }
            }
        }
        foreach (int pid in toSuspendChildren) SuspendChildrenOf(pid);
        foreach (int pid in toDropChildren) DropChildrenOf(pid);
    }

    private static void ApplyLocked(int pid, bool focused, uint fgPid, bool hold, bool minimized)
    {
        // Stop() may have resumed everything and released StateLock while this tick
        // was already in flight; never suspend after shutdown began.
        if (!running) return;
        IntPtr h;
        if (!Handles.TryGetValue(pid, out h)) return;
        bool eco = IsEcoMode();
        bool isSuspended = Suspended.Contains(pid);
        bool isThrottled = Throttled.Contains(pid);
        bool isFrozen = isSuspended || isThrottled;

        // Mode switch while unfocused: convert the old state to the new one.
        // Focused always restores, regardless of mode.
        if (eco && isSuspended)
        {
            if (ResumeFully(h))
            {
                Suspended.Remove(pid);
                WarnedResumePids.Remove(pid);
                isSuspended = false;
                isFrozen = isThrottled;
                Log("[RESUME]  " + DescribeProcess(pid) + " (pid " + pid + ") - mode switched to Eco");
            }
        }
        else if (!eco && isThrottled)
        {
            RestoreEcoLocked(pid, h);
            isThrottled = false;
            isFrozen = isSuspended;
            Log("[ECO-RESTORE] " + DescribeProcess(pid) + " (pid " + pid + ") - mode switched to Suspend");
        }

        if (focused && !isFrozen)
        {
            if (FocusedNow.Add(pid))
                Log("[FOCUS] " + DescribeProcess(pid) + " (pid " + pid + ") focused (fg pid " + fgPid + ")");
            unfocusedSince.Remove(pid);
        }
        else
        {
            FocusedNow.Remove(pid);
            if (!unfocusedSince.ContainsKey(pid))
                unfocusedSince[pid] = DateTime.UtcNow;
        }

        // A minimized target suspends without the anti-flap settle delay:
        // minimizing is explicit and stable, unlike a focus change that may
        // flicker mid-switch. The switch grace still applies, so a click that
        // is restoring the window can't freeze it mid-animation; plain
        // defocus keeps both protections. The post-restore recovery window
        // guards against re-freezing a game that was just restored and is
        // still painting its first frames back.
        if (!focused && !isFrozen && !hold && PastRecovery(pid) && (minimized || DefocusSettled(pid)))
        {
            FreezeLocked(pid, h, eco, minimized ? " - minimized" : "");
        }
        else if (focused && isFrozen)
        {
            unfocusedSince.Remove(pid);
            if (isSuspended)
            {
                if (ResumeFully(h))
                {
                    Suspended.Remove(pid);
                    WarnedResumePids.Remove(pid);
                    lastRestoreAt[pid] = DateTime.UtcNow;
                    Log("[RESUME]  " + DescribeProcess(pid) + " (pid " + pid + ")");
                }
                else
                {
                    // keep the handle and retry on the next poll
                    if (WarnedResumePids.Add(pid))
                        Log("[WARN] Could not resume " + DescribeProcess(pid) + " (pid " + pid + ") - retrying.");
                }
            }
            if (isThrottled)
            {
                RestoreEcoLocked(pid, h);
                lastRestoreAt[pid] = DateTime.UtcNow;
                Log("[ECO-RESTORE] " + DescribeProcess(pid) + " (pid " + pid + ")");
            }
        }
    }

    /// <summary>
    /// Freezes one pid right now, bypassing switch grace / settle / recovery.
    /// Used by the normal defocus path (after its gates pass) and by the
    /// release-decision fast paths (which confirmed the switch landed elsewhere).
    /// Caller must hold StateLock.
    /// </summary>
    private static void FreezeLocked(int pid, IntPtr h, bool eco, string reasonSuffix)
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
        if (IsSelfPid((uint)pid)) return;
        if (eco)
        {
            ApplyEcoLocked(pid, h);
        }
        else
        {
            if (NtSuspendProcess(h) == 0)
            {
                Suspended.Add(pid);
                Log("[SUSPEND] " + DescribeProcess(pid) + " (pid " + pid + ")" + reasonSuffix);
            }
            else
            {
                // Handle went stale (target restarting, access revoked). Close it so
                // the next tick reopens fresh instead of dropping the lock target.
                CloseHandle(h);
                Handles.Remove(pid);
                HandleStartTimes.Remove(pid);
                Suspended.Remove(pid);
                WarnedPids.Remove(pid);
            }
        }
    }

    /// <summary>True once the defocus has lasted ThrottleDelayMs (stable, not a switch flicker).</summary>
    private static bool DefocusSettled(int pid)
    {
        DateTime since;
        if (!unfocusedSince.TryGetValue(pid, out since)) return true; // no record: old path behaves as settled
        return (DateTime.UtcNow - since).TotalMilliseconds >= ThrottleDelayMs;
    }

    /// <summary>True once RestoreRecoveryMs passed since the last restore (recovery window).</summary>
    private static bool PastRecovery(int pid)
    {
        DateTime at;
        if (!lastRestoreAt.TryGetValue(pid, out at)) return true;
        return (DateTime.UtcNow - at).TotalMilliseconds >= RestoreRecoveryMs;
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

    // ---- Eco (low-power) implementation ----
    // Caller must hold StateLock. Best-effort: IDLE priority + EcoQoS +
    // Job CPU hard cap (1% floor). A pid is added to Throttled even when
    // only part of the chain succeeded, so RestoreEcoLocked still runs
    // on focus.

    private static void ApplyEcoLocked(int pid, IntPtr h)
    {
        if (Throttled.Contains(pid)) return;
        if (MinimalHandles.Contains(pid))
        {
            // Honesty: a minimal handle cannot set priority, EcoQoS or job
            // caps, so Eco here would be a silent no-op.
            if (WarnedMinimalEco.Add(pid))
                Log("[WARN] " + DescribeProcess(pid) + " (pid " + pid
                    + ") Eco-throttle skipped: process is hardened (full access denied).");
            return;
        }
        if (!OrigPriority.ContainsKey(pid))
        {
            uint orig = NORMAL_PRIORITY_CLASS;
            try
            {
                uint cur = GetPriorityClass(h);
                if (cur != 0) orig = cur;
            }
            catch { }
            OrigPriority[pid] = orig;
        }
        try { SetPriorityClass(h, IDLE_PRIORITY_CLASS); }
        catch { }
        SetEcoQoS(h, true);
        EnsureEcoJobLocked(pid, h);
        Throttled.Add(pid);
        double cap = Config != null ? Config.EcoCpuCapPercentClamped : 2.0;
        Log("[THROTTLE] " + DescribeProcess(pid) + " (pid " + pid + ") - Eco low-power (IDLE + EcoQoS + " + cap + "% cap)");
        if (cap < 5.0 && WarnedLowCap.Add(pid))
            Log("[WARN] Eco CPU cap is " + cap + "% - extremely low caps can permanently wedge fragile games "
                + "(they never recover even after restore). Consider 5%+ or Suspend mode.");
        AudioMuter.SetPidMuted(pid, true);
    }

    private static void RestoreEcoLocked(int pid, IntPtr h)
    {
        if (!Throttled.Contains(pid) && !OrigPriority.ContainsKey(pid) && !EcoJobs.ContainsKey(pid))
            return;
        SetEcoQoS(h, false);
        uint orig = NORMAL_PRIORITY_CLASS;
        if (OrigPriority.TryGetValue(pid, out orig) && orig == 0)
            orig = NORMAL_PRIORITY_CLASS;
        try { SetPriorityClass(h, orig); }
        catch { }
        // Lift the CPU cap explicitly: closing the job handle does NOT free
        // the process (association lasts until the process exits), so the
        // handle is kept for the next throttle cycle instead of closed.
        IntPtr job;
        if (EcoJobs.TryGetValue(pid, out job))
        {
            TrySetJobCap(job, CPU_RATE_UNLIMITED);
            // Verify the cap actually lifted. The state below is cleared and
            // the restore is logged as done, so a silent failure here used to
            // leave the game throttled forever while looking restored
            // (1 frame per few seconds at a 0.1% cap).
            uint rate;
            bool lifted = TryGetJobCap(job, out rate);
            if (!lifted || rate < CPU_RATE_UNLIMITED)
            {
                TrySetJobCap(job, CPU_RATE_UNLIMITED); // one immediate retry
                lifted = TryGetJobCap(job, out rate) && rate >= CPU_RATE_UNLIMITED;
            }
            if (!lifted)
            {
                // Stay in Throttled so the focused tick retries the restore
                // instead of declaring victory; warn once per stuck episode.
                if (WarnedEcoRestorePids.Add(pid))
                    Log("[WARN] " + DescribeProcess(pid) + " (pid " + pid
                        + ") still capped after restore - retrying while focused.");
                try { AudioMuter.SetPidMuted(pid, false); } catch { }
                return;
            }
        }
        // Verify the priority came back too: a silent failure here leaves the
        // game at IDLE while the state says restored.
        try
        {
            uint curPri = GetPriorityClass(h);
            if (curPri != 0 && curPri != orig)
            {
                try { SetPriorityClass(h, orig); } catch { }
                try { curPri = GetPriorityClass(h); } catch { }
            }
            if (curPri != 0 && curPri != orig)
            {
                if (WarnedEcoRestorePids.Add(pid))
                    Log("[WARN] " + DescribeProcess(pid) + " (pid " + pid
                        + ") priority still " + curPri + " after restore - retrying while focused.");
                try { AudioMuter.SetPidMuted(pid, false); } catch { }
                return;
            }
        }
        catch { }
        WarnedEcoRestorePids.Remove(pid);
        OrigPriority.Remove(pid);
        Throttled.Remove(pid);
        AudioMuter.SetPidMuted(pid, false);
    }

    /// <summary>Reads back the effective CPU rate of an Eco job (10000 = uncapped).</summary>
    private static bool TryGetJobCap(IntPtr job, out uint rate)
    {
        rate = 0;
        try
        {
            JOB_CPU_RATE_CONTROL info = new JOB_CPU_RATE_CONTROL();
            uint ret;
            if (!QueryInformationJobObject(job, JobObjectCpuRateControlInformation,
                ref info, (uint)Marshal.SizeOf(typeof(JOB_CPU_RATE_CONTROL)), out ret))
                return false;
            if ((info.ControlFlags & CPU_RATE_ENABLE) == 0)
            {
                rate = CPU_RATE_UNLIMITED;
                return true;
            }
            rate = info.CpuRate;
            return true;
        }
        catch { return false; }
    }

    private static void SetEcoQoS(IntPtr h, bool enable)
    {
        if (ecoQoSUnsupported) return;
        try
        {
            POWER_THROTTLING_STATE st = new POWER_THROTTLING_STATE();
            st.Version = 1;
            st.ControlMask = POWER_THROTTLING_EXECUTION_SPEED;
            st.StateMask = enable ? POWER_THROTTLING_EXECUTION_SPEED : 0;
            SetProcessInformation(h, ProcessPowerThrottling, ref st, (uint)Marshal.SizeOf(typeof(POWER_THROTTLING_STATE)));
        }
        catch (EntryPointNotFoundException)
        {
            ecoQoSUnsupported = true;
            Log("[WARN] EcoQoS not supported on this Windows version - using priority + CPU cap only.");
        }
        catch (DllNotFoundException)
        {
            ecoQoSUnsupported = true;
        }
        catch { }
    }

    /// <summary>Converts a percent cap (0.1-100) to a Job hard-cap rate (1-10000, 10000 = 100%).</summary>
    private static uint CapToRate(double capPercent)
    {
        double rate = capPercent * 100.0;
        if (rate < 1.0) rate = 1.0;
        if (rate > 10000.0) rate = 10000.0;
        return (uint)rate;
    }

    /// <summary>Per-pid Job name, so a new instance can reopen a stale capped job.</summary>
    private static string EcoJobName(int pid)
    {
        return "ProcessSuspender_Eco_PID" + pid;
    }

    private static bool TrySetJobCap(IntPtr job, uint rate)
    {
        try
        {
            JOB_CPU_RATE_CONTROL info = new JOB_CPU_RATE_CONTROL();
            info.ControlFlags = CPU_RATE_ENABLE | CPU_RATE_HARD_CAP;
            info.CpuRate = rate;
            return SetInformationJobObject(job, JobObjectCpuRateControlInformation, ref info, (uint)Marshal.SizeOf(typeof(JOB_CPU_RATE_CONTROL)));
        }
        catch { return false; }
    }

    private static void EnsureEcoJobLocked(int pid, IntPtr h)
    {
        double cap = Config != null ? Config.EcoCpuCapPercentClamped : 2.0;
        IntPtr existing;
        if (EcoJobs.TryGetValue(pid, out existing))
        {
            // Reuse the retained job (never nested deeper): just re-apply the cap.
            TrySetJobCap(existing, CapToRate(cap));
            return;
        }
        IntPtr job = IntPtr.Zero;
        try
        {
            job = CreateJobObject(IntPtr.Zero, EcoJobName(pid));
            if (job == IntPtr.Zero || job == new IntPtr(-1)) return;
            if (!TrySetJobCap(job, CapToRate(cap)))
            {
                try { CloseHandle(job); }
                catch { }
                return;
            }
            if (!AssignProcessToJobObject(job, h))
            {
                // Already in another job (launcher) or access denied: keep EcoQoS +
                // priority only. This is not fatal.
                try { CloseHandle(job); }
                catch { }
                if (WarnedPids.Add(pid))
                    Log("[WARN] Could not attach pid " + pid + " to CPU-cap job (already in a job?) - EcoQoS + priority only.");
                return;
            }
            EcoJobs[pid] = job;
        }
        catch
        {
            if (job != IntPtr.Zero)
            {
                try { CloseHandle(job); }
                catch { }
            }
        }
    }

    /// <summary>Re-applies the configured CPU cap to live Eco jobs (after a settings change).</summary>
    private static void ApplyEcoCaps()
    {
        // Writes happen under the same lock as the throttle/restore paths: a
        // snapshot-then-write-outside-the-lock race could otherwise uncap a
        // job that was throttled in between, leaving it unlimited while the
        // state says throttled. The write is a fast syscall; no deadlock risk.
        lock (StateLock)
        {
            double cap = 2.0;
            if (Config != null) cap = Config.EcoCpuCapPercentClamped;
            uint limitedRate = CapToRate(cap);
            var limited = new HashSet<int>();
            foreach (int pid in Throttled)
                limited.Add(pid);
            foreach (KeyValuePair<int, IntPtr> kv in EcoJobs)
            {
                // Restored jobs are retained for reuse: keep them explicitly uncapped.
                TrySetJobCap(kv.Value, limited.Contains(kv.Key) ? limitedRate : CPU_RATE_UNLIMITED);
            }
        }
    }

    // ---- Game audio auto-mute while Eco-throttled ----
    // A 1-2% CPU cap starves the game loop, so its audio buffer refills in
    // bursts: a split-second blip every few seconds. Muting the game's audio
    // session on throttle (and unmuting on restore) removes it. Suspend mode
    // needs none of this (a frozen process is silent). All COM GUIDs and
    // vtable layouts below were verified at runtime against the local system
    // (NAudio's declarations cross-checked); every call is best-effort and
    // failures silently disable the mute for that tick, never the throttle.
    private static class AudioMuter
    {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorCom { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
        }

        [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl { }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionManager2
        {
            [PreserveSig] int GetAudioSessionControl(IntPtr sessionId, int flags, out IAudioSessionControl c);
            [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionId, int flags, out IntPtr v);
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator e);
            [PreserveSig] int RegisterSessionNotification(IntPtr n);
            [PreserveSig] int UnregisterSessionNotification(IntPtr n);
            [PreserveSig] int RegisterDuckNotification(IntPtr a, IntPtr b);
            [PreserveSig] int UnregisterDuckNotification(IntPtr a);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetSession(int index, out IAudioSessionControl c);
        }

        [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl2
        {
            [PreserveSig] int P1(); [PreserveSig] int P2(); [PreserveSig] int P3();
            [PreserveSig] int P4(); [PreserveSig] int P5(); [PreserveSig] int P6();
            [PreserveSig] int P7(); [PreserveSig] int P8(); [PreserveSig] int P9();
            [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string s);
            [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string s);
            [PreserveSig] int GetProcessId(out uint pid);
        }

        [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISimpleAudioVolume
        {
            [PreserveSig] int SetMasterVolume(float level, IntPtr ctx);
            [PreserveSig] int GetMasterVolume(out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, IntPtr ctx);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
        }

        private static readonly object MuteLock = new object();
        private static readonly HashSet<int> MutedPids = new HashSet<int>();

        private static void Release(object o)
        {
            try { if (o != null) Marshal.ReleaseComObject(o); }
            catch { }
        }

        /// <summary>Mutes (or unmutes) every audio session owned by pid. Never throws.</summary>
        public static void SetPidMuted(int pid, bool mute)
        {
            try
            {
                // Sessions can be recreated while throttled, so every call does
                // a full pass; the per-session GetMute check keeps it idempotent.
                lock (MuteLock) DoSetPidMuted(pid, mute);
            }
            catch { }
        }

        private static void DoSetPidMuted(int pid, bool mute)
        {
            bool changed = false;
            try
            {
                IMMDeviceEnumerator enumerator = null;
                try
                {
                    enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                }
                catch { return; }
                try
                {
                    int[] roles = new int[] { 0, 1 }; // eConsole, eMultimedia
                    foreach (int role in roles)
                    {
                        IMMDevice device = null;
                        try
                        {
                            if (enumerator.GetDefaultAudioEndpoint(0, role, out device) != 0 || device == null)
                                continue;
                            Guid managerId = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
                            object managerObj = null;
                            try
                            {
                                if (device.Activate(ref managerId, 23, IntPtr.Zero, out managerObj) != 0 || managerObj == null)
                                    continue;
                            }
                            catch { continue; }
                            IAudioSessionManager2 manager = null;
                            try { manager = (IAudioSessionManager2)managerObj; }
                            catch { Release(managerObj); continue; }
                            try
                            {
                                IAudioSessionEnumerator sessionEnum = null;
                                try
                                {
                                    if (manager.GetSessionEnumerator(out sessionEnum) != 0 || sessionEnum == null)
                                        continue;
                                }
                                catch { continue; }
                                try
                                {
                                    int count = 0;
                                    try
                                    {
                                        if (sessionEnum.GetCount(out count) != 0) continue;
                                    }
                                    catch { continue; }
                                    for (int i = 0; i < count; i++)
                                    {
                                        IAudioSessionControl session = null;
                                        try
                                        {
                                            if (sessionEnum.GetSession(i, out session) != 0 || session == null)
                                                continue;
                                        }
                                        catch { continue; }
                                        try
                                        {
                                            IAudioSessionControl2 control2 = null;
                                            try { control2 = (IAudioSessionControl2)session; }
                                            catch { continue; }
                                            try
                                            {
                                                uint sessionPid = 0;
                                                try
                                                {
                                                    if (control2.GetProcessId(out sessionPid) != 0) continue;
                                                }
                                                catch { continue; }
                                                if ((int)sessionPid != pid) continue;
                                                ISimpleAudioVolume volume = null;
                                                try { volume = (ISimpleAudioVolume)session; }
                                                catch { continue; }
                                                try
                                                {
                                                    bool isMuted = false;
                                                    try
                                                    {
                                                        if (volume.GetMute(out isMuted) != 0) continue;
                                                    }
                                                    catch { continue; }
                                                    if (isMuted == mute) continue;
                                                    try
                                                    {
                                                        if (volume.SetMute(mute, IntPtr.Zero) != 0) continue;
                                                    }
                                                    catch { continue; }
                                                    changed = true;
                                                }
                                                finally { Release(volume); }
                                            }
                                            finally { Release(control2); }
                                        }
                                        finally { Release(session); }
                                    }
                                }
                                finally { Release(sessionEnum); }
                            }
                            finally { Release(manager); }
                        }
                        finally { Release(device); }
                    }
                }
                finally { Release(enumerator); }
            }
            catch { return; }
            if (mute && changed)
            {
                if (MutedPids.Add(pid))
                    Log("[AUDIO] Muted " + DescribeProcess(pid) + " (pid " + pid + ") while Eco-throttled.");
            }
            else if (!mute && MutedPids.Contains(pid))
            {
                MutedPids.Remove(pid);
                Log("[AUDIO] Unmuted " + DescribeProcess(pid) + " (pid " + pid + ") on restore.");
            }
        }
    }

    private static void OpenHandleLocked(int pid)
    {
        if (pid <= 0) return;
        try
        {
            if (pid == Process.GetCurrentProcess().Id) return;
        }
        catch { }
        IntPtr h = OpenProcess(PROCESS_ALL, false, (uint)pid);
        if (h != IntPtr.Zero)
        {
            MinimalHandles.Remove(pid);
        }
        else
        {
            // Expanded rights can fail on hardened processes where the old minimal
            // mask still works (suspend-only). Fall back before giving up.
            h = OpenProcess(PROCESS_MINIMAL, false, (uint)pid);
            if (h != IntPtr.Zero) MinimalHandles.Add(pid);
        }
        if (h == IntPtr.Zero)
        {
            if (WarnedPids.Add(pid))
                Log("[WARN] Cannot open process " + pid + " (access denied?).");
            return;
        }
        Handles[pid] = h;
        try { HandleStartTimes[pid] = GetProcessStartTime(pid); }
        catch { HandleStartTimes[pid] = DateTime.MinValue; }
        // Blind cleanup: undo any stale suspension (e.g. a previous ProcessSuspender was killed
        // while the target was frozen, or it was suspended manually). ResumeFully also
        // clears accumulated suspend counts; resuming a running process is a safe no-op.
        ResumeFully(h);
        // Blind cleanup for stale Eco attributes from a killed instance. Unlike the
        // suspend count, a Job CPU cap survives our handle closing (the job lives
        // as long as the process does), so reopen the named job and lift the cap.
        // Games never run at IDLE themselves, so IDLE still means stale priority.
        try { SetEcoQoS(h, false); } catch { }
        try
        {
            IntPtr stale = OpenJobObject(JOB_OBJECT_SET_ATTRIBUTES, false, EcoJobName(pid));
            if (stale != IntPtr.Zero && stale != new IntPtr(-1))
            {
                try { TrySetJobCap(stale, CPU_RATE_UNLIMITED); } catch { }
                try { CloseHandle(stale); } catch { }
            }
        }
        catch { }
        try
        {
            uint cur = GetPriorityClass(h);
            if (cur == IDLE_PRIORITY_CLASS) SetPriorityClass(h, NORMAL_PRIORITY_CLASS);
        }
        catch { }
    }

    private static void DropHandleLocked(int pid)
    {
        IntPtr h;
        if (Handles.TryGetValue(pid, out h))
        {
            // Resume/restore before closing the handle: closing a handle does NOT
            // unsuspend the process, so an unlocked window would stay frozen otherwise.
            if (Suspended.Contains(pid)) ResumeFully(h);
            if (Throttled.Contains(pid)) RestoreEcoLocked(pid, h);
            CloseHandle(h);
        }
        Handles.Remove(pid);
        HandleStartTimes.Remove(pid);
        Suspended.Remove(pid);
        Throttled.Remove(pid);
        OrigPriority.Remove(pid);
        MinimalHandles.Remove(pid);
        WarnedMinimalEco.Remove(pid);
        WarnedLowCap.Remove(pid);
        unfocusedSince.Remove(pid);
        lastRestoreAt.Remove(pid);
        IntPtr job;
        if (EcoJobs.TryGetValue(pid, out job))
        {
            EcoJobs.Remove(pid);
            try { CloseHandle(job); }
            catch { }
        }
    }

    // Resume + drop every descendant we suspended (see SuspendChildrenOf).
    private static void DropChildrenOf(int parentPid)
    {
        var snap = SnapshotProcesses();
        if (snap == null) return;
        lock (StateLock)
        {
            int restored = 0;
            DropDescendants(parentPid, snap, new HashSet<int>(), ref restored);
            if (restored > 0)
                Log("[ECO-RESTORE] " + restored + " child process(es) of "
                    + DescribeProcess(parentPid) + " (pid " + parentPid + ") restored on focus.");
        }
    }

    /// <summary>True when any live descendant of parentPid is still suspended/throttled.
    /// Caller must hold StateLock. Unknown (no snapshot) counts as true so the
    /// caller retries instead of assuming the drop worked.</summary>
    private static bool AnyFrozenDescendantLocked(int parentPid)
    {
        var snap = SnapshotProcesses();
        if (snap == null) return true;
        var seen = new HashSet<int>();
        seen.Add(parentPid);
        var stack = new Stack<int>(ChildPids(snap, parentPid));
        while (stack.Count > 0)
        {
            int c = stack.Pop();
            if (!seen.Add(c)) continue;
            if (Throttled.Contains(c) || Suspended.Contains(c)) return true;
            foreach (int g in ChildPids(snap, c)) stack.Push(g);
        }
        return false;
    }

    private static void DropDescendants(int pid, List<ProcInfo> snap, HashSet<int> seen, ref int restored)
    {
        seen.Add(pid);
        foreach (int cid in ChildPids(snap, pid))
        {
            if (!seen.Add(cid)) continue;
            if (Handles.ContainsKey(cid))
            {
                bool wasFrozen = Suspended.Contains(cid) || Throttled.Contains(cid);
                DropHandleLocked(cid);
                if (wasFrozen) restored++;
            }
            DropDescendants(cid, snap, seen, ref restored);
        }
    }

    private static void DropAllHandlesLocked()
    {
        foreach (int pid in Handles.Keys.ToList())
        {
            IntPtr h;
            if (Handles.TryGetValue(pid, out h))
            {
                if (Suspended.Contains(pid)) ResumeFully(h);
                if (Throttled.Contains(pid)) RestoreEcoLocked(pid, h);
                CloseHandle(h);
            }
            Handles.Remove(pid);
            HandleStartTimes.Remove(pid);
            OrigPriority.Remove(pid);
            IntPtr job;
            if (EcoJobs.TryGetValue(pid, out job))
            {
                EcoJobs.Remove(pid);
                try { CloseHandle(job); }
                catch { }
            }
        }
        Suspended.Clear();
        Throttled.Clear();
        WarnedResumePids.Clear();
        WarnedEcoRestorePids.Clear();
        WarnedMinimalEco.Clear();
        WarnedLowCap.Clear();
        MinimalHandles.Clear();
        unfocusedSince.Clear();
        lastRestoreAt.Clear();
    }

    private static readonly Dictionary<int, string> describeCache = new Dictionary<int, string>();
    private static readonly Dictionary<int, DateTime> describeCacheAt = new Dictionary<int, DateTime>();
    private static readonly object DescribeLock = new object();

    private static string DescribeProcess(int pid)
    {
        // Cache process names for 30s: this is called on suspend/resume/log paths
        // (sometimes while holding StateLock), and each lookup costs a Process
        // object + OpenProcess round-trip. Names are stable per pid lifetime.
        lock (DescribeLock)
        {
            string cached;
            DateTime at;
            if (describeCache.TryGetValue(pid, out cached)
                && describeCacheAt.TryGetValue(pid, out at)
                && (DateTime.UtcNow - at).TotalSeconds < 30)
                return cached;
        }
        string name;
        try
        {
            using (Process p = Process.GetProcessById(pid))
                name = p.ProcessName;
        }
        catch
        {
            name = "(unknown)";
        }
        lock (DescribeLock)
        {
            if (describeCache.Count > 512)
            {
                describeCache.Clear();
                describeCacheAt.Clear();
            }
            describeCache[pid] = name;
            describeCacheAt[pid] = DateTime.UtcNow;
        }
        return name;
    }

    /// <summary>True when the process is on the never-suspend whitelist (by name, e.g. "explorer").</summary>
    public static bool IsNeverSuspendPid(uint pid)
    {
        try
        {
            string name;
            using (Process p = Process.GetProcessById((int)pid))
                name = p.ProcessName;
            return IsNeverSuspendName(name);
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
