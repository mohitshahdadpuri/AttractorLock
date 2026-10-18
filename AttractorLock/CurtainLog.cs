using System;
using System.IO;

namespace AttractorLock;

/// <summary>
/// Small diagnostics log for the curtain (what loaded, what failed, sleep/resume, exits).
/// Never throws and never holds the file open. It is cut back once it grows past ~256 KB.
/// Default: %LOCALAPPDATA%\AttractorLock\curtain.log. Tests point it next to their lock log.
/// </summary>
internal static class CurtainLog
{
    private const long MaxBytes = 256 * 1024;
    private static readonly object Gate = new();

    public static string PathName { get; private set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AttractorLock", "curtain.log");

    public static void UseDirectory(string directory) =>
        PathName = System.IO.Path.Combine(directory, "curtain.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathName)!);
                var info = new FileInfo(PathName);
                if (info.Exists && info.Length > MaxBytes)
                {
                    // Keep the newest half.
                    string all = File.ReadAllText(PathName);
                    File.WriteAllText(PathName, all[(all.Length / 2)..]);
                }
                File.AppendAllText(PathName,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {message}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // Diagnostics must never affect the curtain.
        }
    }
}
