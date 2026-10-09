using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Web.Script.Serialization;

public class Settings
{
    public string ProcessName { get; set; }
    public int PollIntervalMs { get; set; }
    public bool SuspendWhenMinimized { get; set; }
    /// <summary>
    /// How to quiet the target on defocus: "Suspend" (NtSuspendProcess, ~0% CPU)
    /// or "Eco" (low-power: IDLE priority + EcoQoS + CPU cap, game keeps running).
    /// </summary>
    public string FreezeMode { get; set; }
    /// <summary>CPU hard cap percent (0.1-100) applied in Eco mode via a Job Object.</summary>
    public double EcoCpuCapPercent { get; set; }
    public bool ResumeOnExit { get; set; }
    public bool AutoStartWithWindows { get; set; }
    public bool LogToFile { get; set; }
    public string LockHotkey { get; set; }
    public string FreezeHotkey { get; set; }
    /// <summary>
    /// UI theme: "System" (follow Windows), "Light" or "Dark".
    /// </summary>
    public string Theme { get; set; }
    /// <summary>
    /// Process names that must never be locked or suspended (case-insensitive,
    /// no ".exe" extension needed). Also covers services by their host process
    /// name (e.g. "svchost"). Critical Windows, GPU driver and anti-cheat
    /// processes are whitelisted by default.
    /// </summary>
    public List<string> NeverSuspend { get; set; }
    /// <summary>
    /// Process names of locked windows, saved so they survive a restart.
    /// HWNDs/PIDs cannot be saved (they change every run), so on startup
    /// we re-lock the main window of each name that is still running.
    /// </summary>
    public List<string> LockedProcesses { get; set; }
    // legacy keys from older config files - only used for migration in Load()
    public string ToggleHotkey { get; set; }

    /// <summary>
    /// Processes that must never be suspended: critical Windows components,
    /// GPU driver helpers and anti-cheat services. Suspending any of these
    /// freezes the desktop, breaks the display driver or triggers anti-cheat bans.
    /// </summary>
    public static readonly string[] DefaultNeverSuspend = new string[]
    {
        "explorer",
        // Windows core
        "dwm", "csrss", "wininit", "services", "svchost", "lsass", "smss", "winlogon", "fontdrvhost",
        // GPU driver helpers
        "amdow", "amdfendrsr", "atiesrxx", "nvcontainer", "nvdisplay.container",
        // Anti-cheat
        "easyanticheat", "battleye", "vgc", "vgcmda",
    };

    /// <summary>Adds any missing defaults to the list (case-insensitive, ".exe"-insensitive).</summary>
    public static void EnsureDefaults(List<string> list)
    {
        if (list == null) return;
        foreach (string def in DefaultNeverSuspend)
        {
            bool found = false;
            foreach (string entry in list)
            {
                string e = entry == null ? "" : entry.Trim();
                if (e.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    e = e.Substring(0, e.Length - 4);
                if (e.Equals(def, StringComparison.OrdinalIgnoreCase)) { found = true; break; }
            }
            if (!found) list.Add(def);
        }
    }

    public Settings()
    {
        ProcessName = "";
        PollIntervalMs = 250;
        SuspendWhenMinimized = true;
        FreezeMode = "Suspend";
        EcoCpuCapPercent = 2;
        ResumeOnExit = true;
        AutoStartWithWindows = false;
        LogToFile = true;
        LockHotkey = "Ctrl+Alt+F9";
        FreezeHotkey = "Ctrl+Alt+F10";
        Theme = "System";
        NeverSuspend = new List<string>(DefaultNeverSuspend);
        LockedProcesses = new List<string>();
    }

    public int PollIntervalMsClamped
    {
        get { return Math.Max(125, Math.Min(2000, PollIntervalMs)); }
    }

    public double EcoCpuCapPercentClamped
    {
        get { return Math.Max(0.1, Math.Min(100.0, EcoCpuCapPercent)); }
    }

    /// <summary>Canonicalizes the freeze mode string from config ("Eco" or "Suspend").</summary>
    public static string NormalizeFreezeMode(string mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return "Suspend";
        string m = mode.Trim().ToLowerInvariant();
        if (m == "eco" || m == "lowpower" || m == "low-power" || m == "low_power" || m == "ecoqos")
            return "Eco";
        // "Power" mode was removed: migrate old configs to Eco (keeps running).
        if (m == "power" || m == "gpu" || m == "gpucap" || m == "gpu-cap" || m == "nvidia" || m == "pl")
            return "Eco";
        return "Suspend";
    }
}

public static class SettingsStore
{
    public static string ConfigDir
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ProcessSuspender"); }
    }

    public static string ConfigPath
    {
        get { return Path.Combine(ConfigDir, "settings.json"); }
    }

    public static Settings Load()
    {
        // One-time migration: move the old %APPDATA%\FocusLock folder to ProcessSuspender
        // so saved windows/hotkeys survive the rename.
        try
        {
            string oldDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FocusLock");
            if (!Directory.Exists(ConfigDir) && Directory.Exists(oldDir))
                Directory.Move(oldDir, ConfigDir);
        }
        catch { }

        try
        {
            if (File.Exists(ConfigPath))
            {
                string raw = File.ReadAllText(ConfigPath);
                Settings s = new JavaScriptSerializer().Deserialize<Settings>(raw);
                if (s != null)
                {
                    // old config files (or explicit nulls in JSON) must not disable the hotkeys.
                    // Note: JavaScriptSerializer fills missing keys from the ctor defaults, so a
                    // legacy file without "FreezeHotkey" has to be detected in the raw JSON.
                    if (string.IsNullOrWhiteSpace(s.LockHotkey)) s.LockHotkey = "Ctrl+Alt+F9";
                    if (!raw.Contains("\"FreezeHotkey\"") || string.IsNullOrWhiteSpace(s.FreezeHotkey))
                        s.FreezeHotkey = string.IsNullOrWhiteSpace(s.ToggleHotkey) ? "Ctrl+Alt+F10" : s.ToggleHotkey;
                    if (s.NeverSuspend == null) s.NeverSuspend = new List<string>();
                    else Settings.EnsureDefaults(s.NeverSuspend);
                    if (s.LockedProcesses == null) s.LockedProcesses = new List<string>();
                    if (string.IsNullOrEmpty(s.ProcessName)) s.ProcessName = "";
                    s.FreezeMode = Settings.NormalizeFreezeMode(s.FreezeMode);
                    s.Theme = AppTheme.NormalizeTheme(s.Theme);
                    if (s.EcoCpuCapPercent < 0.1 || s.EcoCpuCapPercent > 100) s.EcoCpuCapPercent = 2;
                    return s;
                }
            }
        }
        catch
        {
            // Unreadable/corrupt config: preserve it before defaults overwrite it.
            try
            {
                if (File.Exists(ConfigPath))
                {
                    File.Copy(ConfigPath, ConfigPath + ".bak", true);
                    ProcessSuspenderApp.Log("[WARN] settings.json was unreadable - backed up to settings.json.bak, writing defaults.");
                }
            }
            catch { }
        }

        Settings def = new Settings();
        Save(def);
        return def;
    }

    public static void Save(Settings s)
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            // Atomic write: crash mid-save must never leave a half-written settings.json.
            string tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(s));
            try { File.Replace(tmp, ConfigPath, null); }
            catch
            {
                try { File.Copy(tmp, ConfigPath, true); } catch { }
                try { File.Delete(tmp); } catch { }
            }
        }
        catch { }
    }

    public static void SetAutoStart(bool enable)
    {
        try
        {
            using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                if (enable)
                {
                    string exe = Process.GetCurrentProcess().MainModule.FileName;
                    key.SetValue("ProcessSuspender", "\"" + exe + "\"");
                }
                else
                {
                    key.DeleteValue("ProcessSuspender", false);
                }
            }
        }
        catch { }
    }

    public static void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            Process.Start("explorer.exe", "\"" + ConfigDir + "\"");
        }
        catch { }
    }
}
