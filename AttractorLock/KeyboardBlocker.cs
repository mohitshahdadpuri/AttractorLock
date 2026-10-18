using System;
using System.Runtime.InteropServices;
using System.Threading;
using static AttractorLock.Native;

namespace AttractorLock;

/// <summary>
/// Low-level keyboard hook that swallows shortcuts people could use to get
/// around the curtain (Start menu, Alt+Tab, Esc, context menu, Alt+Space...). While the
/// foreground isn't one of our windows, it swallows every key, so nothing typed reaches
/// the app underneath. The rules live in <see cref="KeyFilter"/>.
///
/// The hook runs on its own thread with its own message loop. If it ran on the UI thread,
/// any UI stall longer than LowLevelHooksTimeout would make Windows silently remove it for good.
///
/// What this CANNOT and does NOT block, by design:
///   - Ctrl+Alt+Del : reserved by Windows. It only reaches the secure screen, and
///                    switching to it makes the curtain lock.
///   - Win+L        : handled by Windows at a secure level, so it still locks.
/// Both of those end at the real Windows lock screen, which is exactly what we want.
/// </summary>
internal sealed class KeyboardBlocker : IDisposable
{
    private readonly KeyFilter _filter = new();
    private readonly LowLevelKeyboardProc _proc;   // kept alive so the GC can't collect it
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private IntPtr _hook = IntPtr.Zero;
    private uint _threadId;

    /// <summary>False if Windows refused the hook. The curtain still works; the focus guard still locks.</summary>
    public bool Installed { get; private set; }

    public KeyboardBlocker()
    {
        _proc = HookProc;
        _thread = new Thread(Run) { IsBackground = true, Name = "AttractorLock keyboard hook" };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        Installed = _hook != IntPtr.Zero;
        _ready.Set();
        if (!Installed) return;

        try
        {
            while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        finally
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                int msg = (int)wParam;
                bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool ctrlDown = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                if (_filter.ShouldSwallow((int)data.vkCode, data.flags, isDown, ForegroundIsCurrentProcess(), ctrlDown))
                    return (IntPtr)1;
            }
        }
        catch (Exception)
        {
            // Never let an exception escape into a native callback; just pass the key on.
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (Installed && _threadId != 0)
        {
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(TimeSpan.FromSeconds(2));
        }
        _ready.Dispose();
    }
}
