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
    private const uint PROCESS_SET_INFORMATION = 0x0200;
    private const uint PROCESS_SET_QUOTA = 0x0100;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_TERMINATE = 0x0001;
    private const uint PROCESS_ECO_ACCESS = PROCESS_SET_INFORMATION | PROCESS_SET_QUOTA | PROCESS_QUERY_INFORMATION | PROCESS_TERMINATE;
    private const uint PROCESS_ALL = PROCESS_SUSPEND_RESUME | PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_ECO_ACCESS;
    private const uint PROCESS_MINIMAL = PROCESS_SUSPEND_RESUME | PROCESS_QUERY_LIMITED_INFORMATION;
    private const int LogCapacity = 500;
    private const int SwitchGestureTimeoutMs = 5000;
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
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr h, uint flags, StringBuilder path, ref uint size);
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
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenJobObject(uint access, bool inherit, string name);
    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr h);

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
    private static readonly Dictionary<int, DateTime> HandleStartTimes = new Dictionary<int, DateTime>();
    private static readonly HashSet<int> Suspended = new HashSet<int>();
    private static readonly HashSet<int> Throttled = new HashSet<int>();
    private static readonly Dictionary<int, uint> OrigPriority = new Dictionary<int, uint>();
    private static readonly Dictionary<int, IntPtr> EcoJobs = new Dictionary<int, IntPtr>();
    private static volatile bool ecoQoSUnsupported;
    private static readonly HashSet<int> Minimized = new HashSet<int>();
    private static readonly HashSet<int> WarnedPids = new HashSet<int>();
    private static readonly HashSet<int> WarnedResumePids = new HashSet<int>();
    private static readonly HashSet<int> FocusedNow = new HashSet<int>();
    private static uint lastFgPid;
    private static DateTime lastNameRefresh = DateTime.MinValue;
    private static DateTime lastWindowRefresh = DateTime.MinValue;
    private static DateTime lastChildScan = DateTime.MinValue;
    private static DateTime lastHookCache = DateTime.MinValue;
    private static DateTime lastAudioRemute = DateTime.MinValue;
    private static volatile bool pendingForegroundSuspend;
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
        worker = new Thread(WorkerLoop);
        worker.IsBackground = true;
        worker.Name = "ProcessSuspenderWorker";
        worker.Start();
        RestoreLockedProcesses();
        Log("[INFO] ProcessSuspender started. Target: " + CurrentTargetDescription + ", poll: " + pollMs + "ms, mode: " + FreezeModeLabel);
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
    /// detected by the keyboard hook). Resume on gesture END must stay synchronous
    /// (the hook sees key-up before the shell activates, so resume lands first).
    /// Suspend on gesture START is deferred to the worker: NtSuspend inside the
    /// low-level hook would stall the switcher UI.
    /// </summary>
    public static void OnSwitchStateChanged(bool active)
    {
        kbdSwitching = active;
        lastSwitchActivity = DateTime.UtcNow;
        if (active)
        {
            pendingForegroundSuspend = true;
        }
        else
        {
            pendingForegroundSuspend = false;
            ResumeSuspended("switch gesture released");
        }
    }

    /// <summary>Suspends (or Eco-throttles) any locked target that still owns the foreground window.
    /// Runs on the worker thread (deferred from the keyboard hook).</summary>
    private static void SuspendForegroundTarget(string reason)
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return;
        uint fgPid;
        GetWindowThreadProcessId(fg, out fgPid);
        if (fgPid == 0) return;
        if (IsSelfPid(fgPid)) return;
        bool eco = IsEcoMode();
        IntPtr h = IntPtr.Zero;
        lock (StateLock)
        {
            if (!Handles.ContainsKey((int)fgPid) || IsFrozenLocked((int)fgPid)) return;
            h = Handles[(int)fgPid];
        }
        if (h == IntPtr.Zero) return;
        if (IsNeverSuspendPid(fgPid)) return;
        if (eco)
        {
            lock (StateLock)
            {
                if (!Handles.TryGetValue((int)fgPid, out h)) return;
                if (IsFrozenLocked((int)fgPid)) return;
                ApplyEcoLocked((int)fgPid, h);
            }
            return;
        }
        if (NtSuspendProcess(h) == 0)
        {
            lock (StateLock) Suspended.Add((int)fgPid);
            Log("[SUSPEND] " + DescribeProcess((int)fgPid) + " (pid " + fgPid + ") - " + reason);
        }
    }

    /// <summary>
    /// Left mouse button down/up. Fast path only: the low-level mouse hook must
    /// return in microseconds (Windows drops slow hooks). No Process enumeration,
    /// no UIA, no title matching here - just cached rects/handles. Only the
    /// target's own taskbar button (or its window) resumes it; clicks elsewhere
    /// are ignored so a suspended game stays frozen.
    /// </summary>
    public static void OnMouseSwitchEvent(bool down, int x, int y)
    {
        if (down)
        {
            bool anyFrozen;
            lock (StateLock) anyFrozen = Suspended.Count + Throttled.Count > 0;
            if (!anyFrozen)
            {
                mouseSwitching = true;
                mouseDownWasSwitch = false;
                lastSwitchActivity = DateTime.UtcNow;
                return;
            }
            bool isSwitch = CheapSwitchHit(x, y);
            mouseDownWasSwitch = isSwitch;
            if (!isSwitch) return;
            mouseSwitching = true;
            lastSwitchActivity = DateTime.UtcNow;
            ResumeSuspended("mouse click on target/taskbar");
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
    private static bool CheapSwitchHit(int x, int y)
    {
        bool inTaskbar = false;
        try { inTaskbar = IsInTaskbar(x, y); }
        catch { inTaskbar = false; }
        if (inTaskbar) return CheapTaskbarHit(x, y);
        try
        {
            if (PointInsideForegroundWindow(x, y)) return false;
        }
        catch { }
        IntPtr hit = IntPtr.Zero;
        try { hit = WindowFromPoint(x, y); }
        catch { return false; }
        if (hit == IntPtr.Zero) return false;
        lock (HookCacheLock)
        {
            foreach (IntPtr hw in hookTargetHwnds)
            {
                if (hit == hw) return true;
                try { if (IsChild(hw, hit)) return true; }
                catch { }
            }
            foreach (IntPtr mw in hookTargetMains)
            {
                if (hit == mw) return true;
                try { if (IsChild(mw, hit)) return true; }
                catch { }
            }
        }
        return false;
    }

    /// <summary>
    /// Hook-safe taskbar hit test. Only a click on the target's own taskbar button
    /// resumes it. Clicks on other apps' buttons, the tray, clock, Start, search or
    /// empty gaps return false so a suspended game stays frozen. Uses only data the
    /// worker cached (button rects + target exe/title); never touches Process or UIA.
    /// When the environment cannot be probed (Legacy/Unknown mode), the button
    /// cache is incomplete, or the target identity is unverifiable, returns true to
    /// preserve the old resume-first behaviour and avoid a stuck suspended game.
    /// Every verdict while suspended is logged (with its reason) so a wrong resume
    /// can be traced in focuslock.log.
    /// </summary>
    private static bool CheapTaskbarHit(int x, int y)
    {
        if (trayMode == TrayMode.Legacy || trayMode == TrayMode.Unknown)
        {
            Log("[MOUSE] taskbar click at (" + x + "," + y + ") resume (fallback: tray mode " + trayMode + ")");
            return true;
        }
        TrayButtonInfo hit = null;
        int buttonCount = 0;
        bool stale;
        lock (taskbarButtons)
        {
            buttonCount = taskbarButtons.Count;
            foreach (TrayButtonInfo b in taskbarButtons)
            {
                if (b.Contains(x, y)) { hit = b; break; }
            }
        }
        stale = taskbarButtonsStale;
        if (hit == null)
        {
            if (buttonCount == 0 || stale)
            {
                Log("[MOUSE] taskbar click at (" + x + "," + y + ") resume (fallback: buttons=" + buttonCount + ", stale=" + stale + ")");
                return true; // enumeration failed or incomplete: cannot tell, resume to be safe
            }
            return false; // gap, tray, clock, Start, Search: never activates the target
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
            Log("[MOUSE] taskbar click at (" + x + "," + y + ") resume (fallback: target identity unknown)");
            return true; // target identity unknown: resume to be safe
        }
        string btnName = hit.Name ?? "";
        string btnApp = hit.AppPath ?? "";
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
                if (matched)
                {
                    Log("[MOUSE] taskbar click at (" + x + "," + y + ") resume: target button (appid match \"" + Shorten(btnApp) + "\")");
                    return true;
                }
                if (pathLike)
                {
                    Log("[MOUSE] taskbar click at (" + x + "," + y + ") ignored: other app button \"" + Shorten(btnName) + "\"");
                    return false; // path AppId of a different app: definitely not ours
                }
                // opaque AppId with no exe-name match: undecided, try title below
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
                    {
                        Log("[MOUSE] taskbar click at (" + x + "," + y + ") resume: target button (title match \"" + Shorten(btnName) + "\")");
                        return true;
                    }
                    if (n.Equals(t, StringComparison.Ordinal))
                    {
                        Log("[MOUSE] taskbar click at (" + x + "," + y + ") resume: target button (title match \"" + Shorten(btnName) + "\")");
                        return true;
                    }
                }
                Log("[MOUSE] taskbar click at (" + x + "," + y + ") ignored: other app button \"" + Shorten(btnName) + "\"");
                return false;
            }
        }
        // Button could not be mapped (no AppPath, no usable name): treat as
        // potential target so a click never leaves the game stuck suspended.
        Log("[MOUSE] taskbar click at (" + x + "," + y + ") resume (fallback: unmappable button name=\"" + Shorten(btnName) + "\" appid=\"" + Shorten(btnApp) + "\")");
        return true;
    }

    private static string Shorten(string s)
    {
        if (s == null) return "";
        s = s.Trim();
        return s.Length > 60 ? s.Substring(0, 60) + "..." : s;
    }

    /// <summary>Refreshed by the worker (not the hook): cheap cache for CheapSwitchHit.</summary>
    private static void RefreshHookCache()
    {
        if ((DateTime.UtcNow - lastHookCache).TotalMilliseconds < 1000) return;
        lastHookCache = DateTime.UtcNow;
        try { RefreshTaskbarButtons(); } catch { }
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
                    Log("[RESUME]  " + DescribeProcess(pid) + " (pid " + pid + ") - " + reason);
                }
            }
            foreach (int pid in Throttled.ToList())
            {
                IntPtr h;
                if (Handles.TryGetValue(pid, out h))
                {
                    RestoreEcoLocked(pid, h);
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
                    // ponytail: don't treat a suspended locked target visible behind
                    // another window as backdrop — user clicking it wants it back.
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
            Process[] list = Process.GetProcessesByName(name);
            foreach (Process p in list)
            {
                try
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
                finally { try { p.Dispose(); } catch { } }
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
            string nameCopy = null;
            lock (StateLock)
            {
                if (WindowTargets.Count > 0)
                    pid = WindowTargets[0].Pid;
                else if (targetName != null)
                    nameCopy = targetName;
            }
            if (nameCopy != null && pid == 0)
            {
                Process[] list = Process.GetProcessesByName(nameCopy);
                foreach (Process p in list)
                {
                    try { AddExePath(p); }
                    finally { try { p.Dispose(); } catch { } }
                    if (targetExePaths.Count > 0) return true;
                }
                return targetExePaths.Count > 0;
            }
            if (pid != 0)
            {
                using (Process p = Process.GetProcessById((int)pid))
                    AddExePath(p);
            }
            else if (nameCopy != null)
            {
                Process[] list2 = Process.GetProcessesByName(nameCopy);
                foreach (Process p in list2)
                {
                    try { AddExePath(p); }
                    finally { try { p.Dispose(); } catch { } }
                }
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
        string nameCopy = null;
        lock (StateLock)
        {
            foreach (WindowTarget t in WindowTargets)
                if (t.Pid == pid) return true;
            if (targetName != null) nameCopy = targetName;
        }
        if (nameCopy != null)
        {
            try
            {
                Process[] list = Process.GetProcessesByName(nameCopy);
                foreach (Process p in list)
                {
                    try { if ((uint)p.Id == pid) return true; }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            catch { }
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

    private static DateTime taskbarDetectAt = DateTime.MinValue;

    private static void RefreshTaskbarButtons()
    {
        if ((DateTime.UtcNow - taskbarButtonsCached).TotalMilliseconds < 400) return;
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
        lock (taskbarButtons) taskbarButtons.Clear();
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
            foreach (IntPtr bar in bars)
            {
                bool ok;
                if (trayMode == TrayMode.Uia) ok = FillFromUia(bar);
                else ok = FillFromWin32(bar);
                if (!ok) failed = true;
            }
        }
        else
        {
            failed = true; // Legacy: no buttons, hook keeps old resume-first behaviour
        }
        taskbarButtonsStale = failed;
        LogTrayCache();
    }

    /// <summary>Logs tray mode / button / identity stats only when they change (worker thread).</summary>
    private static void LogTrayCache()
    {
        try
        {
            int buttons, exes, titles;
            lock (taskbarButtons) buttons = taskbarButtons.Count;
            lock (HookCacheLock) { exes = hookTargetExePaths.Count; titles = hookTargetTitles.Count; }
            string s = "trayMode=" + trayMode + " buttons=" + buttons + " stale=" + taskbarButtonsStale
                + " targetExe=" + exes + " targetTitles=" + titles;
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
        try
        {
            AutomationElement root = AutomationElement.FromHandle(tray);
            if (root == null) return false;
            var walker = TreeWalker.ControlViewWalker;
            lock (taskbarButtons)
            {
                FillUiaWalk(root, walker);
            }
            return true;
        }
        catch { lock (taskbarButtons) taskbarButtons.Clear(); return false; }
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

    private static bool FillFromWin32(IntPtr tray)
    {
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
                lock (taskbarButtons)
                    taskbarButtons.Add(new TrayButtonInfo
                    {
                        Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom,
                        Name = tb.ToString()
                    });
            }
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
                Process[] list = Process.GetProcessesByName(name);
                foreach (Process p in list)
                {
                    try
                    {
                        if (p.MainWindowHandle != IntPtr.Zero)
                        {
                            string t = GetWindowTitle(p.MainWindowHandle);
                            if (t.Length > 0) return t;
                        }
                    }
                    finally { try { p.Dispose(); } catch { } }
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
        lock (TaskbarLock)
        {
            if ((DateTime.UtcNow - taskbarWindowsAt).TotalMilliseconds < 500)
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
        string line = DateTime.Now.ToString("HH:mm:ss") + " " + msg;
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
            Thread.Sleep(pollMs);
        }
        try { FlushLogToFile(); } catch { }
    }

    private static void RunWorkerTick(int tick)
    {
        if (!enabled)
        {
            ResumeAll();
            return;
        }

        if (pendingForegroundSuspend)
        {
            pendingForegroundSuspend = false;
            try { SuspendForegroundTarget("switch gesture started"); } catch { }
        }
        RefreshHookCache();

        uint fgPid = 0;
        IntPtr fg = GetForegroundWindow();
        if (fg != IntPtr.Zero) GetWindowThreadProcessId(fg, out fgPid);
        if (fgPid != lastFgPid)
        {
            lastFgPid = fgPid;
            Log("[FG] foreground: pid " + fgPid + " (" + DescribeProcess((int)fgPid)
                + "), title=\"" + GetWindowTitle(fg) + "\"");
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

    private static void TickWindowMode()
    {
        bool holding = ComputeHolding();
        uint fgPid = ResolveForegroundPid();

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
                if (ignoreMinimize && !focused)
                {
                    try { if (IsIconic(t.Hwnd)) focused = true; } catch { }
                }
                bool wasSuspended = Suspended.Contains(pid) || Throttled.Contains(pid);
                ApplyLocked(pid, focused, fgPid, holding);
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
            ApplyLocked(cid, false, 0, false);
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
                if (ignoreMinimize && !focused) focused = Minimized.Contains(pid);
                bool wasSuspended = Suspended.Contains(pid) || Throttled.Contains(pid);
                ApplyLocked(pid, focused, fgPid, holding);
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

    private static void ApplyLocked(int pid, bool focused, uint fgPid, bool hold)
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
        }
        else
        {
            FocusedNow.Remove(pid);
        }

        if (!focused && !isFrozen && !hold)
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
                    Log("[SUSPEND] " + DescribeProcess(pid) + " (pid " + pid + ")");
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
        else if (focused && isFrozen)
        {
            if (isSuspended)
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
            if (isThrottled)
            {
                RestoreEcoLocked(pid, h);
                Log("[ECO-RESTORE] " + DescribeProcess(pid) + " (pid " + pid + ")");
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

    // ---- Eco (low-power) implementation ----
    // Caller must hold StateLock. Best-effort: IDLE priority + EcoQoS +
    // Job CPU hard cap (1% floor). A pid is added to Throttled even when
    // only part of the chain succeeded, so RestoreEcoLocked still runs
    // on focus.

    private static void ApplyEcoLocked(int pid, IntPtr h)
    {
        if (Throttled.Contains(pid)) return;
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
            TrySetJobCap(job, CPU_RATE_UNLIMITED);
        OrigPriority.Remove(pid);
        Throttled.Remove(pid);
        AudioMuter.SetPidMuted(pid, false);
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
        var copy = new List<KeyValuePair<int, IntPtr>>();
        var limited = new HashSet<int>();
        double cap = 2.0;
        lock (StateLock)
        {
            if (Config != null) cap = Config.EcoCpuCapPercentClamped;
            foreach (KeyValuePair<int, IntPtr> kv in EcoJobs)
                copy.Add(kv);
            foreach (int pid in Throttled)
                limited.Add(pid);
        }
        uint limitedRate = CapToRate(cap);
        foreach (KeyValuePair<int, IntPtr> kv in copy)
        {
            // Restored jobs are retained for reuse: keep them explicitly uncapped.
            TrySetJobCap(kv.Value, limited.Contains(kv.Key) ? limitedRate : CPU_RATE_UNLIMITED);
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
        if (h == IntPtr.Zero)
        {
            // Expanded rights can fail on hardened processes where the old minimal
            // mask still works (suspend-only). Fall back before giving up.
            h = OpenProcess(PROCESS_MINIMAL, false, (uint)pid);
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
            DropDescendants(parentPid, snap, new HashSet<int>());
        }
    }

    private static void DropDescendants(int pid, List<ProcInfo> snap, HashSet<int> seen)
    {
        seen.Add(pid);
        foreach (int cid in ChildPids(snap, pid))
        {
            if (!seen.Add(cid)) continue;
            if (Handles.ContainsKey(cid)) DropHandleLocked(cid);
            DropDescendants(cid, snap, seen);
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
    }

    private static string DescribeProcess(int pid)
    {
        try
        {
            using (Process p = Process.GetProcessById(pid))
                return p.ProcessName;
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
