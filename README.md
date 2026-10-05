# Process Suspender

Suspends a game (or any window) when it loses focus (alt-tab, minimize, click away) and
resumes it the moment you come back. Runs in the system tray. Windows-only, no dependencies.

> **Warning:** Only use with offline / single-player games. Anti-cheat systems
> (EAC, BattlEye, Vanguard) can detect process suspension and ban you.
> Online games will likely kick you as unresponsive anyway.

## Build

```
build.cmd
```

Compiles with the built-in .NET Framework `csc.exe` (no installs, no NuGet).

## Usage

Right-click the tray icon:

| Item | Action |
|---|---|
| *(status line)* | Shows current target window title + running/SUSPENDED state |
| Enable/disable the app | Freeze/unfreeze toggle: suspends the target on defocus; disabling resumes everything and brings the game to the foreground |
| Pick window... | Choose the target from a list of visible windows |
| Poll interval | 125 / 250 / 500 / 1000 ms |
| Freeze mode | Suspend (0% CPU) or Eco low-power |
| Appearance | Follow Windows / Light / Dark (also in Settings -> System) |
| Settings... | Settings window (poll, auto-start, hotkeys, never-suspend list, locked windows) |
| Show log | Settings window on the Log tab |
| Open config folder | Reveals `%APPDATA%\ProcessSuspender` in Explorer |
| Quit | Resumes all targets, exits |

**Global hotkeys** (work even in fullscreen games, no alt-tabbing needed). Defaults:

| Hotkey | Action |
|---|---|
| `Ctrl+Alt+F9` | Lock onto the currently focused window — press it *while playing* |
| `Ctrl+Alt+F10` | Enable/disable the app (disabling resumes targets and focuses the game) |

Both combos are **customizable**: open Settings -> Hotkeys, click a field,
then simply press the combination you want (e.g. hold `Ctrl`+`Alt` and press
`F9`) - no typing needed. `Esc` cancels back to the previous value. The global
hotkeys are suspended while a field is active, so pressing an already-registered
combination also captures it. Invalid, duplicate or conflicting combos are refused with a
warning in the log and the tray menu shows the currently active combos.

Locked windows are remembered and restored on restart (by process name).

> The game does **not** need to be focused to lock it. While it is defocused you can
> also use Settings -> "Pick window..." (lists all windows) or `-p <name>`.

Command line (optional):
- `ProcessSuspender -p <name>` - start locked onto any window of process `<name>`
- `ProcessSuspender -t` - show the window picker at startup
- `ProcessSuspender -settings` - open the settings window at startup

## Settings (`%APPDATA%\ProcessSuspender\settings.json`)

All settings apply live from the Settings window - no restart needed.

- `ProcessName` - process-name target (empty = window-lock mode)
- `PollIntervalMs` - focus check interval (125-2000 ms, default 250)
- `SuspendWhenMinimized` - freeze also when the game window is minimized
- `FreezeMode` - `"Suspend"` (NtSuspendProcess, ~0% CPU) or `"Eco"` (low-power:
  IDLE priority + EcoQoS + CPU cap, game keeps running). Switch in tray ->
  Freeze mode or Settings -> Behavior.
- `EcoCpuCapPercent` - CPU hard cap percent for Eco mode (0.1-100, default 2; 0.5 = half a percent)
- `ResumeOnExit` - resume/restore targets when Process Suspender exits (default true)
- `AutoStartWithWindows` - register in `HKCU\...\Run`
- `LogToFile` - append `focuslock.log` next to the config
- `Theme` - `"System"` (follow Windows), `"Light"` or `"Dark"`. Switch in tray ->
  Appearance or Settings -> System -> Appearance. Window, tray menu, picker and
  toasts follow the Windows app theme live when set to `System`.
- `NeverSuspend` - whitelist of process names that are never locked or suspended
  (e.g. `["explorer"]`; `explorer` is included by default). Manage it in
  Settings -> Never suspend. Matches the process name, so services are covered by
  their host process (add `svchost` to whitelist all services in it).

## Never suspend list

Some processes must never be frozen - suspending Windows Explorer freezes the whole
taskbar. Settings -> "Never suspend" holds a whitelist of process names that Process Suspender
refuses to lock or suspend, enforced at every entry point:

- the lock hotkey / "Pick window..." / `-p <name>` all refuse
  whitelisted processes (with a warning in the log and a toast),
- the worker never suspends a whitelisted PID even if it was targeted before the
  entry was added, and applying the list immediately resumes any whitelisted
  process that was suspended,
- `explorer` ships in the list by default; add or remove entries in Settings, no
  restart needed.

Services are whitelisted by their host process name (e.g. `svchost`). Whitelisting
`svchost` protects every service running inside it.

## How it works

Every poll interval it compares the foreground window's PID against the target. On focus
loss it freezes the target; on return it restores it. Two freeze modes (tray ->
Freeze mode, or Settings -> Behavior):

- `Suspend` - `NtSuspendProcess` / `NtResumeProcess` (~0% CPU, game fully frozen).
- `Eco` - low-power: `IDLE_PRIORITY_CLASS` + `EcoQoS` (`ProcessPowerThrottling`)
  + Job Object CPU hard cap (default 2%, down to 0.1%) + game audio auto-mute.
  The game keeps running (no disconnect, no suspend-detection) but gets almost
  no CPU time. On focus the cap is set back to 100% (the job is retained, so
  restore is exact and repeated alt-tabs never nest jobs) and audio unmutes.

Child processes (launchers, helpers) are frozen/throttled too. CPU cost is negligible (~4 checks/sec).

**Why the input hooks:** a suspended process can never receive window activation, so
Windows silently refuses to give it focus when you alt-tab back or click its taskbar
button - the game stays frozen even though you are "looking at it". Process Suspender therefore
watches the keyboard and mouse via low-level hooks:

- The moment you release **Alt+Tab** or **Win+Tab** while the game is suspended, it unfreezes
  the game so your switch lands on a running game. If you switch to a different window
  instead, the game is re-suspended on the next poll.
- The same happens when you click the game's **taskbar button** or directly over the
  game window. Clicks are detected with a fast cached check so the mouse never lags;
  only the target's own button wakes it - clicks on other apps, the tray, clock or
  empty taskbar gaps leave it frozen.

Use unfreeze (`Ctrl+Alt+F10`) as a manual fallback for anything the hooks can't
catch.

## Test hook

`FOCUSLOCK_TEST_FGPID=<pid>` overrides the foreground window detection with a fixed pid
(manual test hook; normally unset).
