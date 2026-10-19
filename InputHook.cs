using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

/// <summary>
/// Low-level keyboard + mouse hooks (WH_KEYBOARD_LL / WH_MOUSE_LL).
///
/// The hooks live on a dedicated message-pumping thread, not the UI thread:
/// Windows dispatches every hook callback to the thread that installed the
/// hook and waits for it to return, so keeping that thread free of UI work
/// (settings refresh, toasts, theming) is what keeps system input latency low.
/// SetEnabled/SetMouseEnabled only record the desired state and post it there.
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
    private const uint VK_LWIN = 0x5B;
    private const uint VK_RWIN = 0x5C;
    private const uint VK_ESCAPE = 0x1B;
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
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(uint vk);
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(int x, int y);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    private IntPtr _kbdHook = IntPtr.Zero;
    private IntPtr _mouseHook = IntPtr.Zero;
    private readonly LLHookProc _kbdProc;
    private readonly LLHookProc _mouseProc;
    private IntPtr _hookModule = IntPtr.Zero;
    // Desired hook state, written by any thread; the hook thread applies it.
    // Installing/uninstalling LL hooks must happen on the thread that owns
    // (dispatches) them, and that thread must keep pumping messages while they
    // are installed.
    private volatile bool _wantHooks = false;
    private volatile bool _wantMouse = false;
    private Thread _thread;
    private uint _threadId;
    private readonly ManualResetEvent _ready = new ManualResetEvent(false);
    private const uint WM_APP_HOOK_STATE = 0x8000 + 1; // re-apply _wantHooks/_wantMouse
    private const uint WM_APP_HOOK_QUIT = 0x8000 + 2;  // exit the hook thread
    private bool _altDown;
    private bool _winDown;
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
        _hookModule = mod;
        // Dedicated message-pumping thread: hook callbacks run there, never on
        // the UI thread, so settings/log/toast work can never delay every
        // keystroke in the system while a target is locked. The hooks
        // themselves are installed on demand (only while a target is locked) -
        // see SetEnabled.
        _thread = new Thread(HookThreadProc);
        _thread.IsBackground = true;
        _thread.Name = "ProcessSuspenderInputHook";
        _thread.Start();
        try { _ready.WaitOne(5000); } catch { }
    }

    private void InstallKeyboardHook()
    {
        if (_kbdHook != IntPtr.Zero) return;
        _kbdHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbdProc, _hookModule, 0);
        if (_kbdHook == IntPtr.Zero)
            ProcessSuspenderApp.Log("[WARN] Keyboard hook failed (err " + Marshal.GetLastWin32Error()
                + ") - Alt+Tab auto-resume unavailable.");
    }

    private void UninstallKeyboardHook()
    {
        if (_kbdHook != IntPtr.Zero) { UnhookWindowsHookEx(_kbdHook); _kbdHook = IntPtr.Zero; }
    }

    private void InstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero) return;
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, _hookModule, 0);
        if (_mouseHook == IntPtr.Zero)
            ProcessSuspenderApp.Log("[WARN] Mouse hook failed (err " + Marshal.GetLastWin32Error()
                + ") - taskbar click auto-resume unavailable.");
    }

    private void UninstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
    }

    /// <summary>
    /// Requests the global mouse hook state. Disabling removes the per-mousemove
    /// context switch into this process, which is the residual cursor overhead
    /// when nothing is frozen; the keyboard hook stays installed (key events are
    /// rare and carry no cursor cost). Thread-safe: the state is applied by the
    /// dedicated hook thread.
    /// </summary>
    public void SetMouseEnabled(bool enable)
    {
        if (enable == _wantMouse) return;
        _wantMouse = enable;
        SignalHookThread();
    }

    /// <summary>Requests both global hooks. With no locked target the hooks are
    /// removed so the app does no input work while idle. Thread-safe: the state
    /// is applied by the dedicated hook thread.</summary>
    public void SetEnabled(bool enable)
    {
        if (enable == _wantHooks) return;
        _wantHooks = enable;
        SignalHookThread();
    }

    private void SignalHookThread()
    {
        uint tid = _threadId;
        if (tid == 0) return;
        try { PostThreadMessage(tid, WM_APP_HOOK_STATE, IntPtr.Zero, IntPtr.Zero); }
        catch { }
    }

    /// <summary>
    /// The hook thread: installs/removes the hooks per _wantHooks/_wantMouse and
    /// pumps messages for the rest of the app's life, so callback dispatch can
    /// never be delayed by UI work. All install/uninstall runs on this thread,
    /// which is what SetWindowsHookEx requires for callback delivery.
    /// </summary>
    private void HookThreadProc()
    {
        MSG msg = new MSG();
        _threadId = GetCurrentThreadId();
        // Force the message queue into existence before _ready is set, so a
        // state post from another thread can never be lost.
        try { PeekMessage(out msg, IntPtr.Zero, 0, 0, 0); } catch { }
        ApplyHookState();
        try { _ready.Set(); } catch { }
        while (true)
        {
            int r;
            try { r = GetMessage(out msg, IntPtr.Zero, 0, 0); }
            catch { break; }
            if (r <= 0) break; // WM_QUIT or error
            if (msg.message == WM_APP_HOOK_STATE) ApplyHookState();
            else if (msg.message == WM_APP_HOOK_QUIT) break;
        }
        UninstallKeyboardHook();
        UninstallMouseHook();
    }

    /// <summary>Hook thread only: brings the installed hooks in line with the
    /// requested state. Install/uninstall logs every failure.</summary>
    private void ApplyHookState()
    {
        if (_wantHooks)
        {
            InstallKeyboardHook();
            if (_wantMouse) InstallMouseHook();
            else UninstallMouseHook();
        }
        else
        {
            UninstallKeyboardHook();
            UninstallMouseHook();
        }
    }

    private IntPtr KbdProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
        if (nCode >= 0)
        {
            uint msg = (uint)wParam.ToInt64();
            var kb = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
            // Heal FIRST, before applying this event: GetAsyncKeyState inside a
            // low-level hook still returns the pre-event state for the key that
            // is transitioning right now, so healing that same key here would
            // read stale data (Alt-down would look "up") and wipe legitimate
            // state. HealStuckKeys therefore skips the current vkCode.
            HealStuckKeys(kb.vkCode);
            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
            {
                if (IsAlt(kb.vkCode))
                {
                    // Fresh Alt hold: Tab must be pressed during THIS hold to count.
                    // Seed from the physical Tab state so Tab-held-first still counts.
                    if (!_altDown)
                    {
                        _tabSinceAlt = IsTabPhysicallyDown();
                        // Snapshot the about-to-be-left foreground for the worker's
                        // origin-freeze (hook-safe: two syscalls, no locks).
                        try { ProcessSuspenderApp.NoteModifierHeld(); }
                        catch { }
                    }
                    _altDown = true;
                }
                else if (IsWin(kb.vkCode))
                {
                    if (!_winDown)
                    {
                        _tabSinceAlt = IsTabPhysicallyDown();
                        try { ProcessSuspenderApp.NoteModifierHeld(); }
                        catch { }
                    }
                    _winDown = true;
                }
                else if (kb.vkCode == VK_TAB && (_altDown || _winDown))
                {
                    _tabSinceAlt = true;
                }
            }
            else if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
            {
                if (IsAlt(kb.vkCode)) _altDown = false;
                if (IsWin(kb.vkCode)) _winDown = false;
            }

            bool modifierHeld = _altDown || _winDown;
            // Gesture ends the moment the modifier is released: drop the latch so
            // a missed key-up can never leave a stale true behind.
            if (!modifierHeld) _tabSinceAlt = false;

            // Only treat the gesture as a window switch when Tab is used while Alt
            // or Win is held (a real Alt+Tab / Win+Tab). A stray Alt press (menus,
            // shortcuts) must not unfreeze the game: _tabSinceAlt is latched only
            // during the current modifier hold.
            bool active = modifierHeld && _tabSinceAlt;
            if (active != _lastSwitch)
            {
                _lastSwitch = active;
                if (SwitchStateChanged != null) SwitchStateChanged(active);
            }
            else if (active)
            {
                // Gesture already active: keep it alive while the user navigates
                // the switcher (repeated Tab, Shift+Tab, arrow keys, Esc/Enter).
                // Touch is timestamp-only (no resume spam, hook-safe).
                bool keepalive = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                if (keepalive)
                {
                    try { ProcessSuspenderApp.TouchSwitchActivity(); }
                    catch { }
                    // Esc mid-gesture = cancel: the shell returns to the origin
                    // window, so the release resume would be a pointless blip.
                    if (kb.vkCode == VK_ESCAPE)
                    {
                        try { ProcessSuspenderApp.NoteEscDuringGesture(); }
                        catch { }
                    }
                }
            }
        }
        }
        catch { }
        return CallNextHookEx(_kbdHook, nCode, wParam, lParam);
    }

    /// <summary>
    /// Self-heal keys stuck by a missed key-up (hook drop, nCode&lt;0 skip).
    /// GetAsyncKeyState is a fast syscall and safe on the hook thread.
    /// Must run BEFORE the current event is applied and must skip the current
    /// key: inside the hook the physical state of the transitioning key is
    /// still pre-event (Alt-down reads as "up"), so healing it here would wipe
    /// genuine state and break every Alt+Tab. Other keys are settled and safe.
    /// </summary>
    private void HealStuckKeys(uint currentVk)
    {
        try
        {
            if (!IsAlt(currentVk) && _altDown && !IsAltPhysicallyDown()) _altDown = false;
            if (!IsWin(currentVk) && _winDown && !IsWinPhysicallyDown()) _winDown = false;
        }
        catch { }
    }

    private static bool IsTabPhysicallyDown()
    {
        try { return (GetAsyncKeyState(VK_TAB) & 0x8000) != 0; }
        catch { return false; }
    }

    private static bool IsAltPhysicallyDown()
    {
        try
        {
            return ((GetAsyncKeyState(VK_MENU) & 0x8000) != 0)
                || ((GetAsyncKeyState(VK_LMENU) & 0x8000) != 0)
                || ((GetAsyncKeyState(VK_RMENU) & 0x8000) != 0);
        }
        catch { return true; } // on error keep cached state (don't force-clear)
    }

    private static bool IsWinPhysicallyDown()
    {
        try
        {
            return ((GetAsyncKeyState(VK_LWIN) & 0x8000) != 0)
                || ((GetAsyncKeyState(VK_RWIN) & 0x8000) != 0);
        }
        catch { return true; }
    }

    private static bool IsAlt(uint vk)
    {
        return vk == VK_MENU || vk == VK_LMENU || vk == VK_RMENU;
    }

    private static bool IsWin(uint vk)
    {
        return vk == VK_LWIN || vk == VK_RWIN;
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
        if (nCode >= 0)
        {
            uint msg = (uint)wParam.ToInt64();
            if (msg == WM_LBUTTONDOWN || msg == WM_LBUTTONUP)
            {
                var ms = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                // Click mid-gesture = committed selection in the switcher: record
                // the clicked window so the release resume can be skipped when
                // the foreground is already there. Hook-safe: two syscalls.
                if (msg == WM_LBUTTONDOWN)
                {
                    try
                    {
                        if (ProcessSuspenderApp.KbdGestureActive())
                        {
                            IntPtr hit = WindowFromPoint(ms.pt.x, ms.pt.y);
                            uint cpid = 0;
                            if (hit != IntPtr.Zero) GetWindowThreadProcessId(hit, out cpid);
                            if (cpid != 0) ProcessSuspenderApp.NoteClickDuringGesture(cpid);
                        }
                    }
                    catch { }
                }
                if (MouseSwitchEvent != null)
                    MouseSwitchEvent(msg == WM_LBUTTONDOWN, ms.pt.x, ms.pt.y);
            }
        }
        }
        catch { }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        // Ask the hook thread to quit (it also removes the hooks on the way
        // out), then wait for it. The fallback below unhooks if the thread did
        // not exit in time.
        try
        {
            uint tid = _threadId;
            if (tid != 0) PostThreadMessage(tid, WM_APP_HOOK_QUIT, IntPtr.Zero, IntPtr.Zero);
            if (_thread != null) _thread.Join(3000);
        }
        catch { }
        if (_kbdHook != IntPtr.Zero) { UnhookWindowsHookEx(_kbdHook); _kbdHook = IntPtr.Zero; }
        if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        try { _ready.Dispose(); } catch { }
    }
}
