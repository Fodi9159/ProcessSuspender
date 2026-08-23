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
    public bool ResumeOnExit { get; set; }
    public bool AutoStartWithWindows { get; set; }
    public bool LogToFile { get; set; }
    public string LockHotkey { get; set; }
    public string FreezeHotkey { get; set; }
    public bool AutoLockFullscreen { get; set; }
    /// <summary>
    /// Process names that must never be locked or suspended (case-insensitive,
    /// no ".exe" extension needed). Also covers services by their host process
    /// name (e.g. "svchost"). Explorer is whitelisted by default.
    /// </summary>
    public List<string> NeverSuspend { get; set; }
    // legacy keys from older config files - only used for migration in Load()
    public string ToggleHotkey { get; set; }

    /// <summary>
    /// Windows locked (Pick / Lock hotkey) that are restored on the next launch.
    /// Stored by process name + pid so the live window can be re-found after a restart.
    /// </summary>
    public List<LockedWindowEntry> LockedWindows { get; set; }

    public Settings()
    {
        ProcessName = "";
        PollIntervalMs = 250;
        SuspendWhenMinimized = true;
        ResumeOnExit = true;
        AutoStartWithWindows = false;
        LogToFile = true;
        LockHotkey = "Ctrl+Alt+F9";
        FreezeHotkey = "Ctrl+Alt+F10";
        AutoLockFullscreen = true;
        NeverSuspend = new List<string>();
        NeverSuspend.Add("explorer");
        LockedWindows = new List<LockedWindowEntry>();
    }

    public int PollIntervalMsClamped
    {
        get { return Math.Max(125, Math.Min(2000, PollIntervalMs)); }
    }
}

/// <summary>Serializable record of a locked window for persistence across restarts.</summary>
public class LockedWindowEntry
{
    public string ProcessName { get; set; }
    public int Pid { get; set; }
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
                    if (s.LockedWindows == null) s.LockedWindows = new List<LockedWindowEntry>();
                    return s;
                }
            }
        }
        catch { }

        Settings def = new Settings();
        Save(def);
        return def;
    }

    public static void Save(Settings s)
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(ConfigPath, new JavaScriptSerializer().Serialize(s));
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
