using Xunit;

namespace AttractorLock.Tests;

/// <summary>
/// Launches the real exe in situations where it must refuse to show the curtain at all.
/// Safe to run anywhere: the curtain exits before any window appears. Even if it didn't,
/// it is in test mode and gets killed after the timeout.
/// </summary>
[Trait("Category", "Process")]
public class ProcessTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void NoPasswordSet_CurtainRefusesToStart()
    {
        using var data = new TestDataDir(password: null);
        using var p = CurtainProcess.Start(data);

        Assert.True(p.WaitForExit(Timeout), "curtain should exit immediately");
        Assert.Equal(ExitCodes.ConfigInvalid, p.Process.ExitCode);
        Assert.Empty(p.LockLogReasons());
        Assert.Equal(TrayAction.NotifyConfigInvalid, CurtainExitPolicy.Decide(p.Process.ExitCode));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(47)]
    [InlineData(48)]   // zero-filled, legacy size
    [InlineData(56)]   // zero-filled, v1 size
    public void DamagedPasswordFile_CurtainRefusesToStart(int size)
    {
        using var data = new TestDataDir(password: null);
        File.WriteAllBytes(Path.Combine(data.Dir, "cfg.dat"), new byte[size]);
        using var p = CurtainProcess.Start(data);

        Assert.True(p.WaitForExit(Timeout));
        Assert.Equal(ExitCodes.ConfigInvalid, p.Process.ExitCode);
        Assert.Empty(p.LockLogReasons());
    }

    [Fact]
    public void UndecryptableGridFile_CurtainRefusesToStart()
    {
        // e.g. cfg.dat copied from another Windows account: DPAPI can't read it.
        using var data = new TestDataDir(password: null);
        File.WriteAllBytes(Path.Combine(data.Dir, "cfg.dat"),
            "ALG1"u8.ToArray().Concat(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64)).ToArray());
        using var p = CurtainProcess.Start(data);

        Assert.True(p.WaitForExit(Timeout));
        Assert.Equal(ExitCodes.ConfigInvalid, p.Process.ExitCode);
        Assert.Empty(p.LockLogReasons());
    }

    [Fact]
    public void PasswordFileLockedByAnotherProgram_CurtainRefusesToStart()
    {
        using var data = new TestDataDir(password: "locked-file-pass");
        using (new FileStream(Path.Combine(data.Dir, "cfg.dat"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var p = CurtainProcess.Start(data))
        {
            Assert.True(p.WaitForExit(Timeout));
            Assert.Equal(ExitCodes.ConfigInvalid, p.Process.ExitCode);
        }
    }
}
