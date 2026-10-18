# AttractorLock — real testing of the grid-code version

Real exe, real Windows locks, **no Test mode**. Keep your Windows password or PIN handy.

The installed app is the new build (`%LOCALAPPDATA%\Programs\AttractorLock\AttractorLock.exe`).
Your old password was kept.

**Never tell Claude your anchor, path or codes.** Report only what happened ("ok", "locked",
"message said …"). Claude checks `%LOCALAPPDATA%\AttractorLock\curtain.log` and `cfg.dat` after each round.

How grid mode works: each time you click **Access**, a new 5×5 grid appears with your **anchor**
in it once. Start at the anchor, follow your **path** (U/D/L/R, wrapping round the edges), and
type the character you land on after each move. With **Shift by one** on, type the *next*
character instead (A→B, Z→A, 9→0). Case and spaces don't matter.

Tip: answers can contain the letters **O** and **I** (N→O, H→I), even though grids never show them.
Don't type the digits 0 or 1 for those.

---

## Round 1 — upgrade keeps your password

| # | Do this | Expected | Result |
|---|---|---|---|
| 1 | Look at the system tray (check behind **^**). Hover over the shield. | Shield is there. No "Set password" window popped up. Tooltip says press Ctrl+Break. | ✅ ok |
| 2 | Press **Ctrl+Break**. Click **Access**. Type your **current (old) password**, OK. | Attractor shows. Dialog says "Enter password to unlock:" with **no grid**. Curtain closes, Windows does **not** lock. | ✅ ok (log: real mode, clean exit code 0, no lock) |

## Round 2 — set up a grid code

| # | Do this | Expected | Result |
|---|---|---|---|
| 3 | Right-click shield → **Set password…** | New window: **Fixed password** (selected) and **Grid code** options. | ✅ ok |
| 4 | Choose **Grid code**. Then try these in turn and read the message under the practice grid: anchor `O`; anchor `K` with path `RD`; path `RDX`; path `RRRRRRRRR` (9 moves). | Window changes to grid fields. Messages: O/I never appear; at least 3 moves; only U, D, L, R; at most 8 moves. Practice grid shows dots. | ✅ ok |
| 5 | Enter **your real** anchor and path (3–8 moves). Leave Shift ticked (or untick it, your choice). Work out the answer to the practice grid in your head, then compare with "You would type". Click **New sample** 3–4 times and repeat, including one where the anchor is on an edge so the path wraps. Tick/untick Shift and watch the answer change. | Your answer always matches "You would type". | ✅ ok |
| 6 | Click **Cancel**. | Window closes, nothing saved (Claude checks cfg.dat is unchanged). | ✅ ok (closed as expected; cfg.dat wasn't checked between steps 6 and 7, so "nothing saved" rests on the code: Cancel never calls the store) |
| 7 | **Set password…** again → Grid code → your anchor and path → **Save**. | Toast "Grid code saved." | ✅ ok (cfg.dat rewritten 21:10:13, 234 bytes, "ALG1" header + DPAPI blob, no leftover .tmp) |

## Round 3 — unlocking with the grid

| # | Do this | Expected | Result |
|---|---|---|---|
| 8 | **Ctrl+Break** → **Access**. | Dialog says "Enter the code for this grid:" and shows a 5×5 grid, readable, nothing cut off. | ✅ ok |
| 9 | Type the correct code for that grid, OK. | Curtain closes, Windows does **not** lock. | ✅ ok (log 21:11:43, clean exit) |
| 10 | **Ctrl+Break** → **Access** → **Cancel** → **Access** again. Compare the two grids. Then type the correct code for the **second** grid. | Second grid is different (anchor somewhere else, usually). Code works, no lock. | ✅ ok (log 21:12:46, clean exit; an earlier attempt at 21:12:28 locked on a user typing slip, not a bug) |
| 11 | **Ctrl+Break** → **Access**. Type the correct code in **lowercase with spaces** between characters. | Accepted, no lock. | ✅ ok (log 21:13:25, clean exit) |
| 12 | **Ctrl+Break** → **Access** → work out the code but **don't type it**, click **Cancel** → **Access** again → type the **old** code from the first grid. | **Windows locks** (wrong code). Unlock with your Windows PIN → the curtain is gone, desktop normal. | ✅ ok (log 21:14:30: "wrong password" → SessionLock → curtain exited) |

## Round 4 — it sticks, and you can switch back

| # | Do this | Expected | Result |
|---|---|---|---|
| 13 | Right-click shield → **Exit**. Start **AttractorLock** from the Start Menu. Then **Ctrl+Break** → **Access**. | No setup window on start. Grid appears (still grid mode). Correct code works. | ✅ grid code worked (log 21:16:16, clean exit). ⚠️ The tray was not actually restarted: it is still the process started by the installer at 21:03:36. Persistence is still covered, since every curtain is a new process that reads cfg.dat afresh; only "no setup window when the tray restarts" is untested, and the next sign-in will exercise it. |
| 14 | **Set password…** → **Fixed password** → type a password twice → **Save**. **Ctrl+Break** → **Access** → that password. | Toast "Password saved." Dialog has **no grid**. Password works, no lock. | ⏭️ skipped |
| 15 | Pick the mode you want for daily use and set it with **Set password…**. Tell Claude which mode (not the secret). | Saved. | ⏭️ skipped (grid mode stays active) |

## Optional — auto-sleep in grid mode

| # | Do this | Expected | Result |
|---|---|---|---|
| 16 | With grid mode set, **Ctrl+Break** and leave the PC until it sleeps. Wake it. | Windows lock screen. After you unlock with your PIN, no curtain. | ⏭️ skipped (passed for fixed-password mode in v1.0.0; the sleep path doesn't depend on the unlock mode) |

---

## Summary (2026-10-18)

Steps 1–13 passed with real locks. Steps 14–16 were skipped. Grid mode is active on this PC.
Automated: 394 unit/integration/process tests and 19 E2E tests pass.

Not covered by real testing: switching back to a fixed password (14; unit and store tests
cover it), the tray restarting without a setup prompt (13, see note), and auto-sleep in grid mode (16).

## Findings (all fixed in v1.1.0)

- **Uninstaller race (fixed):** uninstalling v1.0.0 removed the shortcuts and the Settings entry but failed
  to delete the exe ("access denied"), because it deletes it right after killing the tray, before
  Windows releases the file. The installer had the same race when copying over a running copy.
  Both now retry for up to 10 seconds. Checked for real: an upgrade over the running app, then
  3 uninstall/reinstall cycles with the app running, all clean, with nothing left behind and cfg.dat kept.
- **Version not bumped (fixed):** Settings › Apps showed 1.0.0 for the new build. It now shows 1.1.0.
- **Wrong example in a code comment (fixed):** `GridSecret.cs` said landing on `2 W N H` gives `3 X P J`;
  shifted by one it is `3 X O I` (the README had it right). Comment only, no effect on behaviour.
