using Xunit;

namespace AttractorLock.Tests;

/// <summary>
/// What happens around sleep. The session feeds the controller exactly these calls:
///   Suspend -> ExtendGrace(SuspendGrace); Resume -> ExtendGrace(ResumeGrace) and re-activate.
/// </summary>
public class SleepResumeTests
{
    private static void Suspend(ControllerHarness h) => h.C.ExtendGrace(h.Now, CurtainController.SuspendGrace);
    private static void Resume(ControllerHarness h) => h.C.ExtendGrace(h.Now, CurtainController.ResumeGrace);

    [Fact]
    public void PasswordOnWake_WindowsLocksDuringSleep_CurtainExitsCleanly_NoExtraLock()
    {
        var h = new ControllerHarness();
        h.SettleActive();

        Suspend(h);
        h.Foreign(TimeSpan.FromSeconds(2));   // lock screen / no foreground while going down
        h.C.SessionLocked();                  // Windows' own lock on sleep

        Assert.Equal(new[] { ExitCodes.Clean }, h.Exits);
        Assert.Empty(h.Lock.Reasons);         // we never had to lock it ourselves
    }

    [Fact]
    public void PasswordOnWake_LockNotificationArrivesOnlyAfterResume_StillExitsCleanly()
    {
        var h = new ControllerHarness();
        h.SettleActive();
        Suspend(h);
        h.Advance(TimeSpan.FromHours(8));     // asleep overnight; the clock jumps
        Resume(h);
        h.Foreign(TimeSpan.FromSeconds(1));   // lock screen has the foreground
        h.C.SessionLocked();

        Assert.Equal(new[] { ExitCodes.Clean }, h.Exits);
        Assert.Empty(h.Lock.Reasons);
    }

    [Fact]
    public void NoPasswordOnWake_FocusBouncesDuringResume_NoLock_AndPasswordStillWorks()
    {
        var h = new ControllerHarness();
        h.SettleActive();
        Suspend(h);
        h.Advance(TimeSpan.FromMinutes(30));
        Resume(h);

        h.Foreign(TimeSpan.FromSeconds(2));   // monitors reconnecting, focus elsewhere briefly
        h.C.ForegroundSample(true, h.Now);    // the curtain took the foreground back
        Assert.Empty(h.Lock.Reasons);
        Assert.Empty(h.Exits);

        h.C.PasswordSubmitted(true, h.Now);
        Assert.Equal(new[] { ExitCodes.Clean }, h.Exits);
    }

    [Fact]
    public void NoPasswordOnWake_SomeoneTakesFocusAfterResumeGrace_Locks()
    {
        var h = new ControllerHarness();
        h.SettleActive();
        Suspend(h);
        h.Advance(TimeSpan.FromMinutes(30));
        Resume(h);
        h.C.ForegroundSample(true, h.Now);

        h.Advance(CurtainController.ResumeGrace + TimeSpan.FromSeconds(1));
        h.Foreign(TimeSpan.FromSeconds(1));
        Assert.Equal(new[] { "focus lost" }, h.Lock.Reasons);
    }

    [Fact]
    public void WrongPasswordRightAfterWake_StillLocks()   // grace never weakens the password check
    {
        var h = new ControllerHarness();
        h.SettleActive();
        Suspend(h);
        Resume(h);
        h.C.PasswordSubmitted(false, h.Now);
        Assert.Equal(new[] { "wrong password" }, h.Lock.Reasons);
    }

    [Fact]
    public void CloseAttemptRightAfterWake_StillLocks()
    {
        var h = new ControllerHarness();
        Resume(h);
        Assert.True(h.C.CloseRequested(h.Now));
        Assert.Single(h.Lock.Reasons);
    }

    [Fact]
    public void ResumeGrace_NeverShortensALongerGrace()
    {
        var h = new ControllerHarness();
        h.SettleActive();
        Suspend(h);                           // 10 s
        Resume(h);                            // 3 s must not cut it short
        h.Foreign(TimeSpan.FromSeconds(8));
        Assert.Empty(h.Lock.Reasons);
    }
}

public class NavigationRuleTests
{
    [Theory]
    [InlineData("about:blank")]
    [InlineData("ABOUT:BLANK")]
    [InlineData("data:text/html;charset=utf-8;base64,PGh0bWw+")]
    [InlineData("data:text/html,<p>x</p>")]
    public void OwnPage_IsAllowed(string uri) => Assert.True(CurtainWindow.IsAllowedNavigation(uri));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.com")]
    [InlineData("http://localhost:8080")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:")]
    [InlineData("about:blankx")]
    [InlineData("edge://settings")]
    public void EverythingElse_IsBlocked(string? uri) => Assert.False(CurtainWindow.IsAllowedNavigation(uri));
}

public class CurtainLogTests
{
    [Fact]
    public void Writes_AndNeverThrows_EvenWhenPathIsBad()
    {
        string before = CurtainLog.PathName;
        string dir = Path.Combine(Path.GetTempPath(), "AttractorLockTests", Guid.NewGuid().ToString("N"));
        try
        {
            CurtainLog.UseDirectory(dir);
            CurtainLog.Write("hello log");
            Assert.Contains("hello log", File.ReadAllText(Path.Combine(dir, "curtain.log")));

            CurtainLog.UseDirectory(@"Z:\nope\" + Guid.NewGuid());
            CurtainLog.Write("must not throw");
        }
        finally
        {
            CurtainLog.UseDirectory(Path.GetDirectoryName(before)!);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void LargeLog_IsTrimmed()
    {
        string before = CurtainLog.PathName;
        string dir = Path.Combine(Path.GetTempPath(), "AttractorLockTests", Guid.NewGuid().ToString("N"));
        try
        {
            CurtainLog.UseDirectory(dir);
            string line = new('x', 1000);
            for (int i = 0; i < 400; i++) CurtainLog.Write(line);
            Assert.True(new FileInfo(Path.Combine(dir, "curtain.log")).Length < 300 * 1024);
        }
        finally
        {
            CurtainLog.UseDirectory(Path.GetDirectoryName(before)!);
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
