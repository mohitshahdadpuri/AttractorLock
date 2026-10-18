using System;
using System.IO;

namespace AttractorLock;

/// <summary>Whatever "lock the machine" means in this run.</summary>
internal interface ILockAction
{
    /// <summary>Requests a lock. Returns false if the request was refused (e.g. locking disabled by policy).</summary>
    bool Lock(string reason);
}

/// <summary>The real thing: Windows lock screen (same as Win+L).</summary>
internal sealed class WindowsLockAction : ILockAction
{
    public bool Lock(string reason) => Native.LockWorkStation();
}

/// <summary>
/// Test mode: never locks. It appends the reason to a log file and raises
/// <see cref="Locked"/>, so the curtain can mimic what a real lock would do.
/// </summary>
internal sealed class SimulatedLockAction : ILockAction
{
    public string LogPath { get; }
    public event Action<string>? Locked;

    public SimulatedLockAction(string? logPath)
    {
        LogPath = string.IsNullOrWhiteSpace(logPath) ? DefaultLogPath : logPath;
    }

    public static string DefaultLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AttractorLock", "test-lock.log");

    public bool Lock(string reason)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(LogPath))!);
            File.AppendAllText(LogPath, $"{DateTime.Now:O}\t{reason}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Logging is best effort; the simulated lock still "happens".
        }
        Locked?.Invoke(reason);
        return true;
    }
}
