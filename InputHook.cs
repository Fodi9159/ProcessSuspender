using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

/// <summary>
/// Low-level keyboard + mouse hooks (WH_KEYBOARD_LL / WH_MOUSE_LL).
///
/// SwitchStateChanged fires the moment a real Alt+Tab gesture starts (Tab pressed
/// while Alt is held - a stray Alt press is ignored) and again when it ends.
/// MouseSwitchEvent fires on every left button down/up with screen coordinates.
///
/// Purpose: a suspended process can never receive window activation, so Windows
/// refuses to focus the game when the user alt-tabs or clicks its taskbar button.
/// ProcessSuspender must unfreeze it BEFORE the activation happens - i.e. on the key/mouse
/// event itself, which these hooks see before the input is dispatched.
/// </summary>
public sealed class InputHook : IDisposable
{
    public event Action<bool> SwitchStateChanged;
    public event Action<bool, int, int> MouseSwitchEvent;

    private delegate IntPtr LLHookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const uint VK_TAB = 0x09;
    private const uint VK_MENU = 0x12;
    private const uint VK_LMENU = 0xA4;
    private const uint VK_RMENU = 0xA5;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LLHookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    private IntPtr _kbdHook = IntPtr.Zero;
    private IntPtr _mouseHook = IntPtr.Zero;
    private readonly LLHookProc _kbdProc;
    private readonly LLHookProc _mouseProc;
    private readonly bool[] _keyDown = new bool[256];
    private bool _altDown;
    private bool _tabSinceAlt;
    private bool _lastSwitch;

    public InputHook()
    {
        _kbdProc = KbdProc;
        _mouseProc = MouseProc;
        IntPtr mod = IntPtr.Zero;
        try
        {
            using (Process cur = Process.GetCurrentProcess())
            using (ProcessModule m = cur.MainModule)
                mod = GetModuleHandle(m.ModuleName);
        }
        catch { }
        _kbdHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbdProc, mod, 0);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, mod, 0);
        if (_kbdHook == IntPtr.Zero)
            ProcessSuspenderApp.Log("[WARN] Keyboard hook failed (err " + Marshal.GetLastWin32Error()
                + ") - Alt+Tab auto-resume unavailable.");
        if (_mouseHook == IntPtr.Zero)
            ProcessSuspenderApp.Log("[WARN] Mouse hook failed (err " + Marshal.GetLastWin32Error()
                + ") - taskbar click auto-resume unavailable.");
    }

    private IntPtr KbdProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            uint msg = (uint)wParam.ToInt64();
            var kb = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
            {
                if (kb.vkCode < 256) _keyDown[kb.vkCode] = true;
                if (IsAlt(kb.vkCode))
                {
                    if (!_altDown) _tabSinceAlt = false;
                    _altDown = true;
                }
                else if (kb.vkCode == VK_TAB && _altDown)
                {
                    _tabSinceAlt = true;
                }
            }
            else if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
            {
                if (kb.vkCode < 256) _keyDown[kb.vkCode] = false;
                if (IsAlt(kb.vkCode)) _altDown = false;
            }

            // Only treat the gesture as a window switch when Tab is used while Alt
            // is held (a real Alt+Tab, either direction). A stray Alt press (menus,
            // shortcuts) must not unfreeze the game.
            bool active = _altDown && (_keyDown[VK_TAB] || _tabSinceAlt);
            if (active != _lastSwitch)
            {
                _lastSwitch = active;
                if (SwitchStateChanged != null) SwitchStateChanged(active);
            }
        }
        return CallNextHookEx(_kbdHook, nCode, wParam, lParam);
    }

    private static bool IsAlt(uint vk)
    {
        return vk == VK_MENU || vk == VK_LMENU || vk == VK_RMENU;
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            uint msg = (uint)wParam.ToInt64();
            if (msg == WM_LBUTTONDOWN || msg == WM_LBUTTONUP)
            {
                var ms = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                if (MouseSwitchEvent != null)
                    MouseSwitchEvent(msg == WM_LBUTTONDOWN, ms.pt.x, ms.pt.y);
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_kbdHook != IntPtr.Zero) { UnhookWindowsHookEx(_kbdHook); _kbdHook = IntPtr.Zero; }
        if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
    }
}
