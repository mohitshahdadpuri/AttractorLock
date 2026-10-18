using Xunit;

namespace AttractorLock.Tests;

/// <summary>
/// Every path out of the curtain. The headline guarantees:
///   1. The correct password always exits cleanly and never locks, whatever state we're in.
///   2. Once Windows has locked, the curtain is gone, so a Windows unlock always gets you back in.
///   3. Nothing ever leaves the desktop exposed without a lock (fail secure).
/// </summary>
public class CurtainControllerTests
{
    // ---------- Correct password ----------

    [Fact]
    public void CorrectPassword_ExitsCleanly_WithoutLocking()
    {
        var h = new ControllerHarness();
        h.SettleActive();

        h.C.PasswordSubmitted(true, h.Now);

        Assert.Equal(new[] { ExitCodes.Clean }, h.Exits);
        Assert.Empty(h.Lock.Reasons);
        Assert.Equal(CurtainState.Exited, h.C.State);
    }

    [Fact]
    public void CorrectPassword_DuringStartupGrace_StillExits()
    {
        var h = new ControllerHarness();
        h.C.PasswordSubmitted(true, h.Now);
        Assert.Equal(new[] { ExitCodes.Clean }, h.Exits);
        Assert.Empty(h.Lock.Reasons);
    }

    [Fact]
    public void CorrectPassword_AfterWrongPassword_WhenLockingIsDisabledByPolicy_StillExits()
    {
        var h = new ControllerHarness();
        h.Lock.Succeeds = false;   // e.g. DisableLockWorkstation group policy
        h.SettleActive();

        h.C.PasswordSubmitted(false, h.Now);
        Assert.Equal(CurtainState.Active, h.C.State);   // no lock happened, curtain stays
        Assert.Empty(h.Exits);

        h.Advance(500);
        h.C.PasswordSubmitted(true, h.Now);
        Assert.Equal(new[] { ExitCodes.Clean }, h.Exits);
    }

    [Fact]
    public void CorrectPassword_WhileLockPending_StillExits()
    {
        var h = new ControllerHarness();
        h.SettleActive();
        h.C.PasswordSubmitted(false, h.Now);
        Assert.Equal(CurtainState.LockPending, h.C.State);

        h.C.PasswordSubmitted(true, h.Now);
        Assert.Equal(new[] { ExitCodes.Clean }, h.Exits);
    }

    [Fact]
    public void CorrectPassword_AfterLockThrew_StillExits()
    {
        var h = new ControllerHarness();
        h.Lock.Throws = true;
        h.SettleActive();
        h.C.PasswordSubmitted(false, h.Now);
        Assert.Equal(CurtainState.Active, h.C.State);

        h.C.PasswordSubmitted(true, h.Now);
        Assert.Equal(new[] { ExitCodes.Clean }, h.Exits);
    }

    [Fact]
    public void CorrectPassword_AfterManyWrongAttemptsAndFocusLosses_StillExits()
    {
        var h = new ControllerHarness();
        h.Lock.Succeeds = false;
        h.SettleActive();
        for (int i = 0; i < 20; i++)
        {
            h.C.PasswordSubmitted(false, h.Now);
            h.Foreign(TimeSpan.FromSeconds(1));
            h.C.CloseRequested(h.Now);
            h.Advance(TimeSpan.FromSeconds(3));
        }
        h.C.PasswordSubmitted(true, h.Now);
        Assert.Equal(new[] { ExitCodes.Clean }, h.Exits);
    }

    // ---------- Wrong password ----------

    [Fact]
    public void WrongPassword_Locks_AndDoesNotExit()
    {
        var h = new ControllerHarness();
        h.SettleActive();

        h.C.PasswordSubmitted(false, h.Now);

        Assert.Equal(new[] { "wrong password" }, h.Lock.Reasons);
        Assert.Empty(h.Exits);
        Assert.Equal(CurtainState.LockPending, h.C.State);
    }

    [Fact]
    public void WrongPassword_TwiceInARow_LocksBothTimes()
    {
        var h = new ControllerHarness();
        h.SettleActive();
        h.C.PasswordSubmitted(false, h.Now);
        h.Advance(200);
        h.C.PasswordSubmitted(false, h.Now);
        Assert.Equal(2, h.Lock.Reasons.Count);
    }

    [Fact]
    public void Cancel_DoesNothing()
    {
        var h = new ControllerHarness();
        h.SettleActive();
        h.C.Cancelled();
        Assert.Empty(h.Lock.Reasons);
        Assert.Empty(h.Exits);
        Assert.Equal(CurtainState.Active, h.C.State);
    }

    // ---------- Windows session events ----------

    [Fact]
    public void SessionLock_ExitsCleanly_SoWindowsUnlockAlwaysGetsYouBackIn()
    {
        var h = new ControllerHarness();
        h.SettleActive();
        h.C.PasswordSubmitted(false, h.Now);   // -> LockWorkStation
        h.C.SessionLocked();                   // Windows confirms the lock

        Assert.Equal(new[] { ExitCodes.Clean }, h.Exits);
        Assert.Equal(CurtainState.Exited, h.C.State);
    }

    [Fact]
    public void SessionLock_WithoutUsRequestingIt_Exits()  // e.g. user pressed Win+L, or Ctrl+Alt+Del > Lock
    {
        var h = new ControllerHarness();
        h.SettleActive();
        h.C.SessionLocked();
        Assert.Equal(new[] { ExitCodes.Clean }, h.Exits);
        Assert.Empty(h.Lock.Reasons);
    }

    [Fact]
    public void SessionUnlock_WithoutSeeingTheLock_StillExits()
    {
        var h = new ControllerHarness();
        h.SettleActive();
        h.C.PasswordSubmitted(false, h.Now);
        h.C.SessionUnlocked();
        Assert.Equal(new[] { ExitCodes.Clean }, h.Exits);
    }

    [Fact]
    public void AfterExit_FocusLossAndCloseAttempts_NeverLockAgain()  // the old unlock -> re-lock loop
    {
        var h = new ControllerHarness();
        h.SettleActive();
        h.C.PasswordSubmitted(false, h.Now);
        h.C.SessionLocked();
        int locksBefore = h.Lock.Reasons.Count;

        h.Foreign(TimeSpan.FromSeconds(10));     // desktop switch / other apps after unlock
        Assert.False(h.C.CloseRequested(h.Now)); // close is allowed once exited
        h.C.PasswordSubmitted(false, h.Now);
        h.C.SessionUnlocked();

        Assert.Equal(locksBefore, h.Lock.Reasons.Count);
        Assert.Single(h.Exits);
    }

    [Fact]
    public void SessionEnding_ExitsWithoutLocking()
    {
        var h = new ControllerHarness();
        h.SettleActive();
        h.C.SessionEnding();
        Assert.Equal(new[] { ExitCodes.Clean }, h.Exits);
        Assert.Empty(h.Lock.Reasons);
    }

    [Fact]
    public void ExitCallback_IsInvokedExactlyOnce()
    {
        var h = new ControllerHarness();
        h.C.PasswordSubmitted(true, h.Now);
        h.C.SessionLocked();
        h.C.SessionUnlocked();
        h.C.SessionEnding();
        h.C.FatalError("x");
        h.C.Hung();
        h.C.PasswordSubmitted(true, h.Now);
        Assert.Single(h.Exits);
        Assert.Empty(h.Lock.Reasons);
    }

    // ---------- Close attempts (Alt+F4) ----------

    [Fact]
    public void CloseAttempt_IsCancelled_AndLocks()
    {
        var h = new ControllerHarness();
        h.SettleActive();
        Assert.True(h.C.CloseRequested(h.Now));
        Assert.Equal(new[] { "close attempted" }, h.Lock.Reasons);
        Assert.Empty(h.Exits);
    }

    [Fact]
    public void CloseAttempt_IsCancelled_EvenWhenLockFails()
    {
        var h = new ControllerHarness();
        h.Lock.Succeeds = false;
        h.SettleActive();
        Assert.True(h.C.CloseRequested(h.Now));
        Assert.Empty(h.Exits);
    }

    // ---------- Focus guard ----------

    [Fact]
    public void ForeignForeground_IsIgnoredDuringStartupGrace()
    {
        var h = new ControllerHarness();
        h.Foreign(CurtainController.StartupGrace - TimeSpan.FromMilliseconds(100));
        Assert.Empty(h.Lock.Reasons);
    }

    [Fact]
    public void NeverGettingForeground_LocksRightAfterStartupGrace()  // curtain shown but keystrokes go elsewhere
    {
        var h = new ControllerHarness();
        h.Foreign(CurtainController.StartupGrace + TimeSpan.FromMilliseconds(200));
        Assert.Equal(new[] { "focus lost" }, h.Lock.Reasons);
    }

    [Fact]
    public void ShortForegroundBlip_DoesNotLock()
    {
        var h = new ControllerHarness();
        h.SettleActive();
        h.Advance(100); h.C.ForegroundSample(false, h.Now);
        h.Advance(100); h.C.ForegroundSample(true, h.Now);
        h.Advance(100); h.C.ForegroundSample(false, h.Now);
        h.Advance(100); h.C.ForegroundSample(true, h.Now);
        Assert.Empty(h.Lock.Reasons);
    }

    [Fact]
    public void SustainedForeignForeground_Locks()
    {
        var h = new ControllerHarness();
        h.SettleActive();
        h.Foreign(CurtainController.FocusLossConfirm + TimeSpan.FromMilliseconds(100));
        Assert.Equal(new[] { "focus lost" }, h.Lock.Reasons);
        Assert.Equal(CurtainState.LockPending, h.C.State);
    }

    [Fact]
    public void OurOwnWindows_InForeground_NeverLock()  // password dialog, other monitors' curtains
    {
        var h = new ControllerHarness();
        h.SettleActive();
        for (int i = 0; i < 600; i++) { h.Advance(100); h.C.ForegroundSample(true, h.Now); }
        Assert.Empty(h.Lock.Reasons);
        Assert.Empty(h.Exits);
    }

    [Fact]
    public void SustainedFocusLoss_IsThrottled_NotOneLockPerSample()
    {
        var h = new ControllerHarness();
        h.SettleActive();
        h.Foreign(TimeSpan.FromSeconds(4));
        Assert.Single(h.Lock.Reasons);
    }

    [Fact]
    public void LockThatNeverHappens_IsRetriedAfterInterval()
    {
        var h = new ControllerHarness();
        h.SettleActive();
        h.Foreign(CurtainController.LockRetryInterval + TimeSpan.FromSeconds(1));
        Assert.Equal(2, h.Lock.Reasons.Count);
    }

    [Fact]
    public void LockPending_RevertsToActive_IfWindowsNeverLocks()
    {
        var h = new ControllerHarness();
        h.SettleActive();
        h.C.PasswordSubmitted(false, h.Now);
        Assert.Equal(CurtainState.LockPending, h.C.State);
        h.Advance(CurtainController.LockRetryInterval);
        h.C.ForegroundSample(true, h.Now);
        Assert.Equal(CurtainState.Active, h.C.State);
    }

    [Fact]
    public void ExtendGrace_SuppressesLocksWhileWindowsAreRebuilt()
    {
        var h = new ControllerHarness();
        h.SettleActive();
        h.C.ExtendGrace(h.Now, TimeSpan.FromSeconds(1));
        h.Foreign(TimeSpan.FromMilliseconds(800));
        Assert.Empty(h.Lock.Reasons);
        h.Foreign(TimeSpan.FromMilliseconds(600));
        Assert.Single(h.Lock.Reasons);
    }

    [Fact]
    public void LockActionThrowing_DoesNotCrashTheController()
    {
        var h = new ControllerHarness();
        h.Lock.Throws = true;
        h.SettleActive();
        h.Foreign(TimeSpan.FromSeconds(1));
        Assert.True(h.C.CloseRequested(h.Now));
        Assert.Empty(h.Exits);
        Assert.Equal(CurtainState.Active, h.C.State);
    }

    // ---------- Fail secure ----------

    [Fact]
    public void FatalError_LocksThenExitsWithCrashCode()
    {
        var h = new ControllerHarness();
        h.SettleActive();
        h.C.FatalError("NullReferenceException");
        Assert.Single(h.Lock.Reasons);
        Assert.StartsWith("error:", h.Lock.Reasons[0]);
        Assert.Equal(new[] { ExitCodes.Crashed }, h.Exits);
    }

    [Fact]
    public void FatalError_LocksEvenIfALockWasJustRequested()
    {
        var h = new ControllerHarness();
        h.SettleActive();
        h.C.PasswordSubmitted(false, h.Now);
        h.C.FatalError("x");
        Assert.Equal(2, h.Lock.Reasons.Count);
    }

    [Fact]
    public void FatalError_StillExits_WhenLockThrows()
    {
        var h = new ControllerHarness();
        h.Lock.Throws = true;
        h.C.FatalError("x");
        Assert.Equal(new[] { ExitCodes.Crashed }, h.Exits);
    }

    [Fact]
    public void Hung_LocksThenExitsWithHungCode()
    {
        var h = new ControllerHarness();
        h.C.Hung();
        Assert.Equal(new[] { "not responding" }, h.Lock.Reasons);
        Assert.Equal(new[] { ExitCodes.Hung }, h.Exits);
    }

    [Fact]
    public void ConcurrentCalls_ExitOnlyOnce()
    {
        for (int round = 0; round < 50; round++)
        {
            var h = new ControllerHarness();
            var start = new ManualResetEventSlim();
            var threads = new[]
            {
                new Thread(() => { start.Wait(); h.C.SessionLocked(); }),
                new Thread(() => { start.Wait(); h.C.PasswordSubmitted(true, h.Now); }),
                new Thread(() => { start.Wait(); h.C.Hung(); }),
                new Thread(() => { start.Wait(); h.C.SessionEnding(); }),
            };
            foreach (var t in threads) t.Start();
            start.Set();
            foreach (var t in threads) t.Join();
            lock (h.Exits) Assert.Single(h.Exits);
        }
    }
}
