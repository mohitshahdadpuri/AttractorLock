using System;
using System.Collections.Generic;

namespace AttractorLock;

/// <summary>
///   (no args)                          -> tray listener
///   --curtain                          -> the curtain (real locking)
///   --curtain --test [--lock-log PATH] -> the curtain in test mode: never locks, logs to PATH
/// </summary>
internal sealed record CommandLineOptions(bool Curtain, bool Test, string? LockLogPath)
{
    public static CommandLineOptions Parse(IReadOnlyList<string> args)
    {
        bool curtain = false, test = false;
        string? lockLog = null;
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (a.Equals("--curtain", StringComparison.OrdinalIgnoreCase)) curtain = true;
            else if (a.Equals("--test", StringComparison.OrdinalIgnoreCase)) test = true;
            else if (a.Equals("--lock-log", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
                lockLog = args[++i];
        }
        return new CommandLineOptions(curtain, test, lockLog);
    }
}
