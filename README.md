# AttractorLock

[![CI](https://github.com/mohitshahdadpuri/AttractorLock/actions/workflows/ci.yml/badge.svg)](https://github.com/mohitshahdadpuri/AttractorLock/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/mohitshahdadpuri/AttractorLock)](https://github.com/mohitshahdadpuri/AttractorLock/releases/latest)

A privacy curtain for a Windows PC you leave unlocked. Press **Ctrl+Break** as you step away
and every screen fills with slowly changing strange attractors. To get back in, click
**Access** and enter your password or grid code. Anything else, including a wrong code,
Alt+F4, Alt+Tab or killing the process, sends the person to the real Windows lock screen.

It's for the office desk you walk away from for a few minutes: quicker to come back to than
Win+L and MFA, and nobody can quietly use your session while you're gone.

Note - THIS IS SPECIFICALLY FOR CASE OF ENDPOINT MFA SO IT IS MOST PROBABLY USELESS TO YOU. 

## Install

1. Download **`AttractorLock-vX.Y.Z-win-x64.zip`** from the
   [latest release](https://github.com/mohitshahdadpuri/AttractorLock/releases/latest) and extract it.
2. Double-click **`install.cmd`**. No admin rights are needed.
3. Set your password when asked. A shield icon appears in the system tray.

The installer puts the app in `%LOCALAPPDATA%\Programs\AttractorLock`, starts it with Windows
and adds it to **Settings › Apps**, where you can uninstall it. Running a newer installer
upgrades in place and keeps your password.

**Requirements:** Windows 10 or 11 (x64) and the WebView2 runtime, which comes preinstalled on
current Windows. **Your Windows account must have a password or PIN**, because the Windows lock
screen is what actually keeps people out.

## Use

| To | Do |
|---|---|
| Cover the screen | **Ctrl+Break**, or double-click the tray shield |
| Come back | Click **Access** (bottom-right of any screen) and enter your password or code |
| Try it without real locks | Right-click the shield → tick **Test mode** |
| Change your password, or switch to a grid code | Right-click the shield → **Set password…** |

### Grid codes: a different code every time

Instead of a fixed password you can use a **grid code**. Each time you click Access you get a
new 5×5 grid. Find your **anchor** character, follow your **path** (for example R D D L,
wrapping around the edges), and type the characters you land on, each shifted by one
(A→B, 9→0) if you chose that. Someone who watches you type sees a code that won't work next time.

```
  Q  7  M  4  X
  B [K] 2  Z  9        anchor K, path R D D L:
  R  E  W  5  T        lands on 2 W N H
  3  H  N  8  C        shifted by one: 3 X O I
  P  6  J  Y  A        type 3XOI
```

## How it keeps you safe

Every way past the curtain other than your password ends at the Windows lock screen, and the
curtain closes as soon as Windows locks. So you can't be locked out: your Windows password or
PIN always gets you back to your desktop.

| Situation | What happens |
|---|---|
| Correct password or code | Curtain closes, desktop exactly as you left it |
| Wrong code, Alt+F4, another app taking focus | Windows locks, and the curtain closes |
| PC goes to sleep | Windows locks; on wake you get the Windows lock screen |
| Curtain crashes, freezes or is killed | Windows locks; the desktop is never exposed |
| Password file missing or damaged | The curtain refuses to start, and you're asked to set a new password |

The curtain blocks the Win key, Alt+Tab, Ctrl+Esc, Ctrl+Shift+Esc, Alt+Space and Esc. Keys that
type characters are never blocked. Passwords are stored as salted PBKDF2-SHA256 hashes, and grid
secrets are encrypted with Windows DPAPI for your account.

**What it isn't:** protection against someone with admin rights, physical access to the disk, or
the ability to reboot the PC. It deters casual access to an unlocked session. The Windows lock
screen remains your real security boundary.

The full behaviour, every edge case and the sleep settings are covered in the
[detailed guide](AttractorLock/README.md).

## Build from source

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) on Windows.

```powershell
dotnet publish AttractorLock/AttractorLock.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
installer\install.cmd        # installs publish\AttractorLock.exe
```

### Tests

```powershell
dotnet test AttractorLock.sln                                                     # unit, integration, process
$env:ATTRACTORLOCK_E2E = "1"; dotnet test AttractorLock.sln --filter Category=E2E   # drives the real UI
```

The E2E tests cover your screen and press real keys for a couple of minutes, but always in test
mode, so Windows is never really locked. Don't touch the mouse or keyboard while they run.
CI runs both suites on Windows for every push to `main` and every pull request.

[REAL_TESTING.md](REAL_TESTING.md) records the manual tests with real Windows locks done for v1.1.0.

### Layout

| Path | What's there |
|---|---|
| `AttractorLock/` | The app: tray listener, curtain, password store, grid codes, attractor page |
| `AttractorLock.Tests/` | xUnit tests, including UI Automation E2E tests |
| `installer/` | Per-user install and uninstall scripts |
| `.github/workflows/ci.yml` | Build and test on Windows |

## License

[MIT](LICENSE)
