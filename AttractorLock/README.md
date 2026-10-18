# AttractorLock

A privacy curtain for an unlocked Windows PC. Press **Ctrl+Break** on your way out
and the whole screen fills with cycling strange attractors. Anyone who tries to get
in is sent to the real Windows lock screen; only your Windows password gets past that.

## How it works

Two parts, one `.exe`:

1. **Tray listener** — runs in the system tray, starts with Windows, waits for your
   hotkey. Near-zero resources.
2. **Curtain** — launched by the hotkey. Full-screen on every monitor, always on top,
   taskbar hidden, new attractor every 5 seconds, one **Access** button.

### The Access button
- **Correct password** → the app closes, your desktop is exactly as you left it.
- **Wrong password** → Windows locks instantly (unlock with your normal Windows password).

### Why you can never get locked out of your own machine
Every escape route ends at the real Windows lock screen, and once you unlock,
the curtain shuts itself down automatically:

| Situation | What happens |
|---|---|
| Correct password | App exits, desktop revealed |
| Wrong password | Windows locks → you unlock → app exits |
| A popup steals focus | Windows locks → you unlock → app exits (a harmless false lock, never a loop) |
| Alt+F4 / close attempt | Locks instead of exiting (no free bypass) |
| App freezes or crashes | Hook and window vanish; Ctrl+Alt+Del always works |

### Shortcuts
- **Blocked:** Windows key / Start, Alt+Tab, Alt+Esc, Ctrl+Esc, Esc, context-menu key.
- **Still work (by design):** **Win+L** (Windows enforces it at a secure level) and
  **Ctrl+Alt+Del** (no app can block it — it only reaches the secure screen, not your desktop).

## Build (on your Windows machine)

You need the **.NET 8 SDK** (`winget install Microsoft.DotNet.SDK.8`). Then:

```powershell
cd AttractorLock
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

The single `AttractorLock.exe` lands in
`bin\Release\net8.0-windows\win-x64\publish\`.

> WebView2 runtime is already on Windows 10/11. If a machine is missing it,
> install from https://developer.microsoft.com/microsoft-edge/webview2/.

## First run
1. Double-click `AttractorLock.exe`. A shield icon appears in the tray and asks you to
   set an access password.
2. **Turn on Test mode first** (tray menu → Test mode), press **Ctrl+Break**, and check
   that your usual popups don't trigger a lock. In test mode it shows a message instead
   of locking.
3. When you're happy, untick Test mode. Press **Ctrl+Break** whenever you leave.

## Start with Windows (optional)
Put a shortcut to `AttractorLock.exe` in the Startup folder:
press **Win+R**, type `shell:startup`, and drop a shortcut there.

## Honest caveats
- I wrote and reviewed the code but could not compile or run it here (this was built in
  a Linux environment with no Windows toolchain). **Do your first test while you know
  your Windows password**, so a surprise lock is only a minor inconvenience.
- Verify Win+L behaviour on your own machine during the test run — secure-hotkey handling
  varies slightly by Windows build.
- This is a deterrent against casual access by someone at your desk. It is not protection
  against someone with admin rights, physical disk access, or a reboot — the Windows lock
  screen is your real security boundary, and this app leans on it deliberately.
