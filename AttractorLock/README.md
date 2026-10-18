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
Every monitor gets its own curtain window and its own **Access** button (bottom-right of
that monitor), so the button can never fall into a gap between screens.
- **Correct password** → the app closes, your desktop is exactly as you left it.
- **Wrong password** → Windows locks instantly (unlock with your normal Windows password).

### Why you can never get locked out of your own machine
Every escape route ends at the real Windows lock screen. **As soon as Windows reports the
session as locked, the curtain quits.** So when you unlock Windows, the curtain is already gone,
with no loop and no dependence on an "unlocked" notification.

| Situation | What happens |
|---|---|
| Correct password | App exits, desktop revealed |
| Wrong password | Windows locks → curtain quits → you unlock → desktop |
| Another window takes focus (for more than ~0.25 s) | Windows locks → curtain quits (a harmless false lock, never a loop) |
| Alt+F4 / close attempt | Windows locks instead of exiting (no free bypass) |
| You press Win+L or Ctrl+Alt+Del → Lock | Curtain quits; unlock as usual |
| Locking disabled by group policy | Curtain stays up; your password still works |
| Logoff / shutdown | Curtain quits quietly |
| Curtain crashes | It locks Windows first, then quits |
| Curtain freezes (UI stuck >10 s) | Watchdog locks Windows and ends the process |
| Curtain killed (Task Manager etc.) | The tray notices the abnormal exit and locks Windows |
| Password file missing / damaged / from another account | The curtain refuses to start, and the tray asks you to set a new password |
| Monitor plugged/unplugged | Curtains are rebuilt to cover the new layout |

### Shortcuts
- **Blocked:** Windows key (Start, Win+D, Win+Tab…), Alt+Tab, Alt+Esc, Ctrl+Esc,
  Ctrl+Shift+Esc, Esc, Alt+Space (window menu), context-menu key. While anything other than
  the curtain has the foreground, *all* keys are swallowed, so nothing typed reaches the app underneath.
- **Never blocked:** any key that types a character, including AltGr combinations. Your password can always be typed.
- **Still work (by design):** **Win+L** (Windows enforces it at a secure level) and
  **Ctrl+Alt+Del** (no app can block it; it only reaches the secure screen, and the curtain locks).

### Password rules
At least 4 characters. No control characters, and no emoji: the curtain blocks the Windows
key, so the emoji panel isn't available there. The password box warns when Caps Lock is on.
The password is stored as a salted PBKDF2-SHA256 hash (600 000 iterations) in
`%APPDATA%\AttractorLock\cfg.dat`, written atomically. Files from the first version still work.

### Grid code (a different code every time)
Instead of a fixed password you can pick **Grid code** in **Set password…**. Each time you click
**Access**, the dialog shows a new 5×5 grid of random letters and digits. Your secret is:

- an **anchor**: one letter or digit, which appears in the grid exactly once, somewhere new each time
- a **path**: 3–8 moves (U, D, L, R or arrows) from the anchor, wrapping around the edges
- **shift by one** (on by default): type the *next* character of each one you land on (A→B, Z→A, 9→0)

Example: anchor `K`, path `R D D L`, shift on.

```
  Q  7  M  4  X
  B [K] 2  Z  9        K → right: 2 → down: W → down: N → left: H
  R  E  W  5  T        shifted by one:  3 X O I
  3  H  N  8  C
  P  6  J  Y  A        type: 3XOI   (case and spaces don't matter)
```

Someone who watches you unlock sees a code that won't work next time. O and I never appear in
grids, so they can't be confused with 0 and 1, but they can be part of an answer (N→O, H→I).
The setup window shows a practice grid and the answer for it, so you can check you've got it
before saving. A wrong code locks Windows, exactly like a wrong password.

The grid secret has to be readable to work out each answer, so it can't be hashed. Instead it is
encrypted with Windows DPAPI for your account and stored in the same `cfg.dat`. If the file is
copied to another account or PC it can't be decrypted there, so the curtain refuses to start and
the tray asks you to set it again. Choosing a fixed password again replaces the grid secret.

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

## Set your password and run it
1. **Start it.** Double-click `AttractorLock.exe`. No window opens; a **shield icon** appears in
   the system tray next to the clock (it may be hidden behind the **^** arrow).
2. **Set your password.** On the very first start a **"Set access password"** window opens on
   its own: type the password twice and click **Save**. To change it later, right-click the
   shield and choose **Set password…**.
3. **Try it once in Test mode.** Right-click the shield, tick **Test mode**, and press **Ctrl+Break**.
   In test mode nothing is ever locked. When the curtain *would* lock, it shows the reason in an
   orange banner, writes it to `%LOCALAPPDATA%\AttractorLock\test-lock.log`, and closes, just as a
   real lock would. When you're happy, untick Test mode.
4. **Every day:** press **Ctrl+Break** (or double-click the shield) before you leave your desk.
   When you're back, click **Access** (bottom-right of any screen) and type your password.

Your **Windows account must have a password or PIN**: every way around the curtain ends at the
Windows lock screen, which is what really keeps people out.

### Sleep and screen-off
| Your power settings | What happens |
|---|---|
| Screen turns off | Nothing changes; the curtain is still there when the screen wakes |
| PC sleeps, "Require sign-in on wake" **on** (Windows default) | Windows locks as it sleeps, and the curtain closes. On wake you get the Windows lock screen, so sign in with your **Windows** password/PIN |
| PC sleeps, "Require sign-in on wake" **never** | The curtain survives sleep and takes the screen back on wake; use your curtain password |

### Diagnostics
The curtain logs what it does (attractor loaded or failed, sleep/wake, lock reasons, exit code)
to `%LOCALAPPDATA%\AttractorLock\curtain.log`.

## Tests
```powershell
dotnet test AttractorLock.sln                       # unit + integration + process tests (safe, no UI)
$env:ATTRACTORLOCK_E2E = "1"; dotnet test AttractorLock.sln --filter Category=E2E
```
The E2E tests drive the real curtain through UI Automation. They cover your screen for a few
seconds per test and press real keys, but they always run with `--test`, so Windows is never
actually locked. Don't touch the mouse or keyboard while they run.

## Install (starts with Windows)
After publishing, double-click **`installer\install.cmd`**. No admin rights are needed. It:
- copies the exe to `%LOCALAPPDATA%\Programs\AttractorLock`
- adds Startup and Start Menu shortcuts, so it runs every time you sign in
- registers AttractorLock under **Settings › Apps** (with Uninstall)
- starts it

The installer looks for the exe next to itself or in `..\publish\` (pass `-Source <path>` to use
another one). Running it again upgrades in place. Your password is never touched, and it refuses
to run while a curtain is up.

To remove it, use **Settings › Apps › AttractorLock › Uninstall**, or double-click
`installer\uninstall.cmd`. Your password is kept unless you run `uninstall.ps1 -RemoveData`.

## Honest caveats
- **Do your first real (non-test) run while you know your Windows password.** Every failure
  path ends at the Windows lock screen, so that password is your guaranteed way back in.
- Check Win+L on your own machine during the test run. Secure-hotkey handling varies
  slightly between Windows builds.
- This is a deterrent against casual access by someone at your desk. It is not protection
  against someone with admin rights, physical disk access, or a reboot. The Windows lock
  screen is your real security boundary, and this app leans on it deliberately.
