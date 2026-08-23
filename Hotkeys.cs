using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public class Hotkeys : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int ID_LOCK = 1;
    private const int ID_FREEZE = 2;

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    private NativeWindow _win;
    private bool _lockRegistered;
    private bool _freezeRegistered;
    private bool _suspended;

    public event Action LockPressed;
    public event Action FreezePressed;

    /// <summary>
    /// While true, no hotkeys are registered (and current ones are unregistered).
    /// Used while the user captures a new combination in Settings, so that pressing
    /// an already-registered combo reaches the field instead of triggering it.
    /// </summary>
    public bool Suspended
    {
        get { return _suspended; }
        set
        {
            if (_suspended == value) return;
            _suspended = value;
            if (ProcessSuspenderApp.Config != null) Apply(ProcessSuspenderApp.Config);
        }
    }

    public Hotkeys()
    {
        _win = new HotkeySink(this);
        _win.CreateHandle(new CreateParams());
        Apply(ProcessSuspenderApp.Config);
    }

    /// <summary>
    /// (Re)register all hotkeys from the given settings. Unregisters the previous
    /// combos first, so this can be called any number of times at runtime.
    /// </summary>
    public void Apply(Settings s)
    {
        if (_win == null) return;
        ApplyOne(ID_LOCK, ref _lockRegistered, s.LockHotkey, "Lock to focused window");
        ApplyOne(ID_FREEZE, ref _freezeRegistered, s.FreezeHotkey, "Freeze on defocus");
    }

    private void ApplyOne(int id, ref bool registered, string combo, string action)
    {
        if (registered)
        {
            UnregisterHotKey(_win.Handle, id);
            registered = false;
        }
        if (_suspended) return;
        uint mods, vk;
        if (!TryParseCombo(combo, out mods, out vk))
        {
            ProcessSuspenderApp.Log("[WARN] Hotkey '" + combo + "' is not a valid combination - '" + action + "' has no hotkey.");
            return;
        }
        if (RegisterHotKey(_win.Handle, id, mods | MOD_NOREPEAT, vk))
        {
            registered = true;
            ProcessSuspenderApp.Log("[INFO] Hotkey '" + combo + "' registered for '" + action + "'.");
        }
        else
        {
            ProcessSuspenderApp.Log("[WARN] Hotkey '" + combo + "' could not be registered (conflict?) - '" + action + "' has no hotkey.");
        }
    }

    /// <summary>
    /// Parses combos like "Ctrl+Alt+F9", "Shift+Win+Space", "Alt+1", "F11".
    /// </summary>
    public static bool TryParseCombo(string combo, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(combo)) return false;
        foreach (string raw in combo.Split('+'))
        {
            string p = raw.Trim();
            if (p.Length == 0) return false;
            string lower = p.ToLowerInvariant();
            if (lower == "ctrl" || lower == "control") mods |= MOD_CONTROL;
            else if (lower == "alt") mods |= MOD_ALT;
            else if (lower == "shift") mods |= MOD_SHIFT;
            else if (lower == "win" || lower == "windows" || lower == "meta") mods |= MOD_WIN;
            else
            {
                uint k;
                if (!TryParseKey(p, out k)) return false;
                if (vk != 0) return false; // two non-modifier keys
                vk = k;
            }
        }
        if (vk == 0) return false;
        if (vk == (uint)Keys.ControlKey || vk == (uint)Keys.Menu || vk == (uint)Keys.ShiftKey
            || vk == (uint)Keys.LWin || vk == (uint)Keys.RWin
            || vk == (uint)Keys.Control || vk == (uint)Keys.Alt || vk == (uint)Keys.Shift)
            return false;
        return true;
    }

    /// <summary>Display name for a Keys value (D1..D9 become 1..9).</summary>
    public static string KeyName(Keys k)
    {
        if (k >= Keys.D0 && k <= Keys.D9)
            return ((char)('0' + (k - Keys.D0))).ToString();
        return k.ToString();
    }

    /// <summary>
    /// Builds a display combo string (e.g. "Ctrl+Alt+F9") from a KeyDown event.
    /// The Win key is tracked separately (winDown) because Windows does not
    /// report it as a KeyData modifier flag.
    /// </summary>
    public static string ComboToString(Keys keyData, bool winDown)
    {
        Keys kc = keyData & Keys.KeyCode;
        if (kc == Keys.None) return null;
        string key = KeyName(kc);
        if (key.Length == 0) return null;
        var parts = new List<string>();
        if ((keyData & Keys.Control) != 0) parts.Add("Ctrl");
        if ((keyData & Keys.Alt) != 0) parts.Add("Alt");
        if ((keyData & Keys.Shift) != 0) parts.Add("Shift");
        if (winDown) parts.Add("Win");
        parts.Add(key);
        return string.Join("+", parts.ToArray());
    }

    private static bool TryParseKey(string p, out uint vk)
    {
        vk = 0;
        if (p.Length == 1 && char.IsLetterOrDigit(p[0]))
        {
            char c = char.ToUpperInvariant(p[0]);
            if (c >= 'A' && c <= 'Z') { vk = (uint)c; return true; }
            if (c >= '0' && c <= '9') { vk = (uint)(Keys.D0 + (c - '0')); return true; }
            return false;
        }
        try
        {
            vk = (uint)Convert.ToInt32(Enum.Parse(typeof(Keys), p, true));
            return true;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        if (_win != null)
        {
            if (_lockRegistered) UnregisterHotKey(_win.Handle, ID_LOCK);
            if (_freezeRegistered) UnregisterHotKey(_win.Handle, ID_FREEZE);
            _win.DestroyHandle();
            _win = null;
        }
    }

    private class HotkeySink : NativeWindow
    {
        private readonly Hotkeys _owner;

        public HotkeySink(Hotkeys owner)
        {
            _owner = owner;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == ID_LOCK && _owner.LockPressed != null) _owner.LockPressed();
                else if (id == ID_FREEZE && _owner.FreezePressed != null) _owner.FreezePressed();
            }
            base.WndProc(ref m);
        }
    }
}
