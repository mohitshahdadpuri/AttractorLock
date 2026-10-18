using System;
using System.Runtime.InteropServices;
using static AttractorLock.Native;

namespace AttractorLock;

/// <summary>
/// Low-level keyboard hook that swallows shortcuts people could use to get
/// around the curtain (Start menu, Alt+Tab, Esc, context menu, etc.).
///
/// What this CANNOT and does NOT block, by design:
///   - Ctrl+Alt+Del : reserved by Windows, no app can intercept it. That's fine —
///                    it only reaches the secure screen, not your desktop.
///   - Win+L        : handled by Windows at a secure level, so it still locks.
/// Both of those end at the real Windows lock screen, which is exactly what we want.
/// </summary>
internal sealed class KeyboardBlocker : IDisposable
{
    private IntPtr _hook = IntPtr.Zero;
    private readonly LowLevelKeyboardProc _proc;   // kept alive so the GC can't collect it

    public KeyboardBlocker()
    {
        _proc = HookProc;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
    }

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int vk = (int)data.vkCode;
            int msg = (int)wParam;
            bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;

            // 1) Windows key — swallow it so the Start menu can't open.
            //    (Win+L is enforced by the OS and still works regardless.)
            if (vk is VK_LWIN or VK_RWIN)
                return (IntPtr)1;

            // 2) Context-menu / "apps" key
            if (vk == VK_APPS)
                return (IntPtr)1;

            // 3) Escape in any form (Esc, Ctrl+Esc, Alt+Esc)
            if (vk == VK_ESCAPE)
                return (IntPtr)1;

            // 4) Alt+Tab and Alt+Esc (Tab/Esc while Alt is held)
            if (vk == VK_TAB && Down(VK_MENU))
                return (IntPtr)1;

            // 5) Ctrl+Esc (opens Start) — covered by (3) already, left explicit for clarity
            if (vk == VK_ESCAPE && Down(VK_CONTROL))
                return (IntPtr)1;

            _ = isDown; // reserved if you later want keydown-only logic
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
