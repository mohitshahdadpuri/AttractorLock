using System.Diagnostics;
using Xunit;

namespace AttractorLock.Tests;

/// <summary>
/// The ONLY way tests start AttractorLock.exe. It always passes --test and --lock-log, so
/// the curtain under test can never call the real LockWorkStation. It points the curtain at
/// a throw-away password folder, and kills the process on Dispose so nothing is left on screen.
/// </summary>
internal sealed class CurtainProcess : IDisposable
{
    public Process Process { get; }
    public string LockLog { get; }

    private CurtainProcess(Process p, string lockLog)
    {
        Process = p;
        LockLog = lockLog;
    }

    public static string ExePath
    {
        get
        {
            string local = Path.Combine(AppContext.BaseDirectory, "AttractorLock.exe");
            if (File.Exists(local)) return local;
            throw new FileNotFoundException("AttractorLock.exe not found next to the tests. Build the solution first.", local);
        }
    }

    public static CurtainProcess Start(TestDataDir data, params string[] extraArgs)
    {
        var psi = new ProcessStartInfo(ExePath) { UseShellExecute = false };
        psi.ArgumentList.Add("--curtain");
        psi.ArgumentList.Add("--test");
        psi.ArgumentList.Add("--lock-log");
        psi.ArgumentList.Add(data.LockLog);
        foreach (var a in extraArgs) psi.ArgumentList.Add(a);
        psi.Environment[PasswordStore.DataDirEnvVar] = data.Dir;

        // Safety interlock: refuse to launch anything that could lock for real.
        var parsed = CommandLineOptions.Parse(psi.ArgumentList.ToList());
        Assert.True(parsed.Curtain && parsed.Test && parsed.LockLogPath == data.LockLog,
            "Refusing to start a curtain that is not in test mode.");

        Native.AllowSetForegroundWindow(Native.ASFW_ANY);
        var p = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        return new CurtainProcess(p, data.LockLog);
    }

    public bool WaitForExit(TimeSpan timeout) => Process.WaitForExit((int)timeout.TotalMilliseconds);

    public string[] LockLogReasons() =>
        File.Exists(LockLog)
            ? File.ReadAllLines(LockLog).Where(l => l.Length > 0).Select(l => l.Split('\t', 2)[1]).ToArray()
            : Array.Empty<string>();

    public void Dispose()
    {
        try
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
                Process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException) { }
        Process.Dispose();
    }
}

/// <summary>A temp folder holding a password file and the simulated-lock log.</summary>
internal sealed class TestDataDir : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "AttractorLockTests", Guid.NewGuid().ToString("N"));
    public string LockLog => Path.Combine(Dir, "lock.log");
    /// <summary>The curtain's diagnostics log (written next to the lock log in test mode).</summary>
    public string CurtainLog => Path.Combine(Dir, "curtain.log");
    public string CurtainLogText => File.Exists(CurtainLog) ? File.ReadAllText(CurtainLog) : "(no curtain.log)";
    public PasswordStore Store => new(Dir, 1_000);

    public TestDataDir(string? password = null)
    {
        Directory.CreateDirectory(Dir);
        if (password is not null) Store.Set(password);
    }

    public void Dispose()
    {
        for (int i = 0; i < 5; i++)
        {
            try { Directory.Delete(Dir, true); return; }
            catch (IOException) { Thread.Sleep(200); }
            catch (UnauthorizedAccessException) { Thread.Sleep(200); }
        }
    }
}
