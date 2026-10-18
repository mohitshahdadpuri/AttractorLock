using System;
using System.Runtime.InteropServices;

namespace AttractorLock;

/// <summary>Thin wrappers over the Win32 calls this app needs.</summary>
internal static class Native
{
    // ---- Locking the workstation (same as Win+L) ----
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool LockWorkStation();

    // ---- Global hotkey (Ctrl+Break) ----
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_NOREPEAT = 0x4000;
    public const uint VK_CANCEL_U = 0x03;  // what the Pause/Break key reports while Ctrl is held
    public const uint VK_PAUSE = 0x13;     // what it reports on some keyboards / without Ctrl
    public const int WM_HOTKEY = 0x0312;

    // ---- Low-level keyboard hook ----
    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;
    public const uint LLKHF_ALTDOWN = 0x20;

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    // ---- Message loop for the hook thread ----
    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    public const uint WM_QUIT = 0x0012;

    [DllImport("user32.dll")]
    public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    // ---- Foreground tracking ----
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool AllowSetForegroundWindow(uint dwProcessId);

    public const uint ASFW_ANY = unchecked((uint)-1);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    /// <summary>
    /// Takes the foreground even when Windows' foreground-lock rules would refuse
    /// (e.g. the curtain was started by a process without foreground rights). We briefly
    /// share the input queue of the current foreground thread, which makes
    /// SetForegroundWindow allowed.
    /// </summary>
    public static void ForceForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        if (SetForegroundWindow(hwnd) && GetForegroundWindow() == hwnd) return;

        uint self = GetCurrentThreadId();
        uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        bool attached = fgThread != 0 && fgThread != self && AttachThreadInput(self, fgThread, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(self, fgThread, false);
        }
    }

    /// <summary>True when the foreground window belongs to this process (curtain, password box, ...).</summary>
    public static bool ForegroundIsCurrentProcess()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;   // e.g. secure desktop / mid-switch: not ours
        GetWindowThreadProcessId(fg, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    // ---- Window placement ----
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    public static readonly IntPtr HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint flags;
    }

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_HIDEWINDOW = 0x0080;

    public const int WM_WINDOWPOSCHANGING = 0x0046;
    public const int WM_SYSCOMMAND = 0x0112;
    public const int SC_SIZE = 0xF000;
    public const int SC_MOVE = 0xF010;
    public const int SC_MINIMIZE = 0xF020;
    public const int SC_MAXIMIZE = 0xF030;
    public const int SC_RESTORE = 0xF120;

    // Virtual key codes we care about
    public const int VK_TAB = 0x09;
    public const int VK_ESCAPE = 0x1B;
    public const int VK_SPACE = 0x20;
    public const int VK_CONTROL = 0x11;
    public const int VK_MENU = 0x12;     // Alt
    public const int VK_LWIN = 0x5B;
    public const int VK_RWIN = 0x5C;
    public const int VK_APPS = 0x5D;     // context-menu key
    public const int VK_PACKET = 0xE7;   // Unicode characters injected via SendInput
}
