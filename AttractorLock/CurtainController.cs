using System;

namespace AttractorLock;

internal enum CurtainState
{
    /// <summary>Curtain is up and guarding.</summary>
    Active,
    /// <summary>A lock was requested; waiting for Windows to report the session as locked.</summary>
    LockPending,
    /// <summary>Exit was requested; nothing else will happen.</summary>
    Exited,
}

/// <summary>
/// The curtain's rules, with no UI and no Win32 so every path can be unit-tested.
///
/// Safety contract:
///   - Correct password                 -> exit (clean), never locks.
///   - Wrong password / close attempt   -> lock Windows.
///   - Foreground lost (debounced)      -> lock Windows.
///   - Windows reports SessionLock      -> exit (clean). The Windows lock screen guards the
///                                         desktop now, so once you unlock Windows the
///                                         curtain is already gone. Nothing here depends
///                                         on the unlock notification arriving.
///   - SessionUnlock / logoff           -> exit (clean), as a backup.
///   - Lock refused (policy)            -> stay up; the password still works.
///   - Fatal error / hang               -> lock, then exit with an error code.
/// Thread-safe: SystemEvents and the watchdog call in from other threads.
/// The exit callback is always invoked outside the internal lock.
/// </summary>
internal sealed class CurtainController
{
    public static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan FocusLossConfirm = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan LockRetryInterval = TimeSpan.FromSeconds(5);
    /// <summary>Grace while the PC goes to sleep.</summary>
    public static readonly TimeSpan SuspendGrace = TimeSpan.FromSeconds(10);
    /// <summary>Grace after waking up (monitors reconnect, focus settles).</summary>
    public static readonly TimeSpan ResumeGrace = TimeSpan.FromSeconds(3);

    private readonly object _gate = new();
    private readonly ILockAction _lock;
    private readonly Action<int> _onExit;

    private DateTime _graceUntil;
    private DateTime? _firstForeignSample;
    private DateTime? _lastLockAttempt;

    public CurtainState State { get; private set; } = CurtainState.Active;
    public int? ExitCode { get; private set; }
    public string? LastLockReason { get; private set; }

    public CurtainController(ILockAction lockAction, Action<int> onExit, DateTime now)
    {
        _lock = lockAction;
        _onExit = onExit;
        _graceUntil = now + StartupGrace;
    }

    public bool InGrace(DateTime now)
    {
        lock (_gate) return now < _graceUntil;
    }

    /// <summary>Gives the windows time to settle (e.g. after a monitor was plugged in).</summary>
    public void ExtendGrace(DateTime now, TimeSpan span)
    {
        lock (_gate)
        {
            var until = now + span;
            if (until > _graceUntil) _graceUntil = until;
            _firstForeignSample = null;
        }
    }

    public void PasswordSubmitted(bool correct, DateTime now)
    {
        if (correct) { Exit(ExitCodes.Clean); return; }
        lock (_gate)
        {
            if (State == CurtainState.Exited) return;
            RequestLockLocked("wrong password", now, force: true);
        }
    }

    /// <summary>The password dialog was cancelled. Deliberately does nothing.</summary>
    public void Cancelled() { }

    /// <summary>Returns true if the close must be cancelled.</summary>
    public bool CloseRequested(DateTime now)
    {
        lock (_gate)
        {
            if (State == CurtainState.Exited) return false;
            RequestLockLocked("close attempted", now, force: false);
            return true;
        }
    }

    public void ForegroundSample(bool foregroundIsOurs, DateTime now)
    {
        lock (_gate)
        {
            if (State == CurtainState.Exited) return;
            ExpirePendingLocked(now);

            if (foregroundIsOurs) { _firstForeignSample = null; return; }

            _firstForeignSample ??= now;
            if (now < _graceUntil) return;                                   // still settling
            if (now - _firstForeignSample.Value < FocusLossConfirm) return;  // debounce blips
            RequestLockLocked("focus lost", now, force: false);
        }
    }

    public void SessionLocked() => Exit(ExitCodes.Clean);
    public void SessionUnlocked() => Exit(ExitCodes.Clean);
    public void SessionEnding() => Exit(ExitCodes.Clean);

    public void FatalError(string what) => LockThenExit("error: " + what, ExitCodes.Crashed);
    public void Hung() => LockThenExit("not responding", ExitCodes.Hung);

    private void LockThenExit(string reason, int code)
    {
        lock (_gate)
        {
            if (State == CurtainState.Exited) return;
            LastLockReason = reason;
            try { _lock.Lock(reason); } catch (Exception) { /* exiting anyway; the tray locks too */ }
        }
        Exit(code);
    }

    private void Exit(int code)
    {
        lock (_gate)
        {
            if (State == CurtainState.Exited) return;
            State = CurtainState.Exited;
            ExitCode = code;
        }
        _onExit(code);
    }

    private void ExpirePendingLocked(DateTime now)
    {
        // LockWorkStation said yes but Windows never reported the lock: re-arm.
        if (State == CurtainState.LockPending && _lastLockAttempt is { } t && now - t >= LockRetryInterval)
            State = CurtainState.Active;
    }

    private void RequestLockLocked(string reason, DateTime now, bool force)
    {
        ExpirePendingLocked(now);
        // Throttle repeats (e.g. focus samples every 100 ms while a lock is under way, or
        // locking disabled by policy). A wrong password always gets a fresh attempt.
        if (!force && _lastLockAttempt is { } last && now - last < LockRetryInterval) return;

        _lastLockAttempt = now;
        LastLockReason = reason;
        bool ok;
        try { ok = _lock.Lock(reason); }
        catch (Exception) { ok = false; }
        State = ok ? CurtainState.LockPending : CurtainState.Active;
    }
}
