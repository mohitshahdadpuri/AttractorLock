using Xunit;

namespace AttractorLock.Tests;

public class CurtainExitPolicyTests
{
    [Theory]
    [InlineData(ExitCodes.Clean, TrayAction.None)]
    [InlineData(ExitCodes.AlreadyRunning, TrayAction.None)]
    [InlineData(ExitCodes.ConfigInvalid, TrayAction.NotifyConfigInvalid)]
    [InlineData(ExitCodes.Crashed, TrayAction.LockWorkstation)]
    [InlineData(ExitCodes.Hung, TrayAction.LockWorkstation)]
    [InlineData(-1, TrayAction.LockWorkstation)]                         // Process.Kill / Task Manager "End task"
    [InlineData(1, TrayAction.LockWorkstation)]                          // taskkill /f
    [InlineData(unchecked((int)0xE0434352), TrayAction.LockWorkstation)] // unhandled .NET exception
    [InlineData(unchecked((int)0xC0000005), TrayAction.LockWorkstation)] // access violation
    [InlineData(unchecked((int)0xC000013A), TrayAction.LockWorkstation)] // Ctrl+C / console close
    public void ExitCode_MapsToTrayAction(int code, object expected) =>  // object: TrayAction is internal
        Assert.Equal((TrayAction)expected, CurtainExitPolicy.Decide(code));
}

public class HangPolicyTests
{
    private static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    [Fact] public void Responsive_IsOk() => Assert.Equal(HangVerdict.Ok, HangPolicy.Evaluate(S(0.2), S(1)));
    [Fact] public void SlowButUnderThreshold_IsOk() => Assert.Equal(HangVerdict.Ok, HangPolicy.Evaluate(S(9), S(1)));
    [Fact] public void StaleHeartbeat_IsHung() => Assert.Equal(HangVerdict.Hung, HangPolicy.Evaluate(S(11), S(1)));

    [Fact] // PC was asleep: both clocks jumped. That is not a hang.
    public void AfterSleep_Resets() => Assert.Equal(HangVerdict.ResetAfterSuspend, HangPolicy.Evaluate(S(3600), S(3600)));

    [Fact]
    public void Watchdog_FiresOnce_WhenNoHeartbeat()
    {
        int fired = 0;
        using var fire = new ManualResetEventSlim();
        using var w = new HangWatchdog(() => { Interlocked.Increment(ref fired); fire.Set(); });
        Assert.True(fire.Wait(HangPolicy.Threshold + TimeSpan.FromSeconds(5)));
        Thread.Sleep(1500);
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Watchdog_StaysQuiet_WhileBeating()
    {
        int fired = 0;
        using var w = new HangWatchdog(() => Interlocked.Increment(ref fired));
        var end = DateTime.UtcNow + HangPolicy.Threshold + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < end) { w.Beat(); Thread.Sleep(200); }
        Assert.Equal(0, fired);
    }
}

public class PasswordPolicyTests
{
    [Theory]
    [InlineData("hunter22")]
    [InlineData("pass word with spaces")]
    [InlineData(" leading and trailing ")]
    [InlineData("Ünïcödé-ß-€")]       // typeable via layout / AltGr / dead keys
    [InlineData("пароль123")]
    [InlineData("abcd")]               // exactly MinLength
    public void Acceptable(string pw) => Assert.Null(PasswordPolicy.Validate(pw, pw));

    [Theory]
    [InlineData("", "")]
    [InlineData("abc", "abc")]          // too short
    [InlineData("password1", "password2")]
    [InlineData("pass\tword", "pass\tword")]
    [InlineData("pass\nword", "pass\nword")]
    [InlineData("pass\u001bword", "pass\u001bword")]
    [InlineData("smile😀ok", "smile😀ok")]  // needs Win+. emoji panel; Win key is blocked on the curtain
    public void Rejected(string pw, string confirm) => Assert.NotNull(PasswordPolicy.Validate(pw, confirm));
}

public class CommandLineTests
{
    [Fact]
    public void NoArgs_IsTray()
    {
        var o = CommandLineOptions.Parse(Array.Empty<string>());
        Assert.False(o.Curtain);
        Assert.False(o.Test);
    }

    [Fact]
    public void Curtain_IsRealMode()
    {
        var o = CommandLineOptions.Parse(new[] { "--curtain" });
        Assert.True(o.Curtain);
        Assert.False(o.Test);
    }

    [Fact]
    public void CurtainTest_WithLockLog()
    {
        var o = CommandLineOptions.Parse(new[] { "--CURTAIN", "--lock-log", @"C:\x y\l.log", "--Test" });
        Assert.True(o.Curtain);
        Assert.True(o.Test);
        Assert.Equal(@"C:\x y\l.log", o.LockLogPath);
    }

    [Fact]
    public void LockLog_WithoutValue_IsIgnored()
    {
        var o = CommandLineOptions.Parse(new[] { "--curtain", "--test", "--lock-log" });
        Assert.Null(o.LockLogPath);
        Assert.True(o.Test);
    }

    [Fact]
    public void LockLog_AloneDoesNotEnableTestMode()
    {
        var o = CommandLineOptions.Parse(new[] { "--curtain", "--lock-log", "x.log" });
        Assert.False(o.Test);
    }
}

public class SimulatedLockActionTests
{
    [Fact]
    public void AppendsReason_AndRaisesEvent_NeverLocks()
    {
        string dir = Path.Combine(Path.GetTempPath(), "AttractorLockTests", Guid.NewGuid().ToString("N"));
        string log = Path.Combine(dir, "sub", "lock.log");
        try
        {
            var sim = new SimulatedLockAction(log);
            var seen = new List<string>();
            sim.Locked += seen.Add;

            Assert.True(sim.Lock("wrong password"));
            Assert.True(sim.Lock("focus lost"));

            var lines = File.ReadAllLines(log);
            Assert.Equal(2, lines.Length);
            Assert.EndsWith("\twrong password", lines[0]);
            Assert.EndsWith("\tfocus lost", lines[1]);
            Assert.Equal(new[] { "wrong password", "focus lost" }, seen);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void UnwritableLog_StillReportsSuccess()
    {
        var sim = new SimulatedLockAction(@"Z:\definitely\not\here\" + Guid.NewGuid() + @"\l.log");
        Assert.True(sim.Lock("x"));
    }
}
