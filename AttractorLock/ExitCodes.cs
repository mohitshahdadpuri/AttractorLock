namespace AttractorLock;

/// <summary>Exit codes of the curtain process. The tray uses them to decide whether to lock.</summary>
internal static class ExitCodes
{
    /// <summary>Correct password, Windows session locked/unlocked, or logoff/shutdown.</summary>
    public const int Clean = 0;
    /// <summary>Another curtain is already up and still guarding the screen.</summary>
    public const int AlreadyRunning = 2;
    /// <summary>No valid password stored: the curtain refused to start and never showed a window.</summary>
    public const int ConfigInvalid = 3;
    /// <summary>Unhandled exception: the curtain locked Windows and then quit.</summary>
    public const int Crashed = 4;
    /// <summary>UI thread stopped responding: the curtain locked Windows and then quit.</summary>
    public const int Hung = 5;
}

internal enum TrayAction
{
    None,
    NotifyConfigInvalid,
    LockWorkstation,
}

/// <summary>
/// What the tray does when the curtain process exits. Anything that is not a known
/// clean exit (killed from Task Manager, crashed, hung and terminated...) locks
/// Windows, so the desktop is never left exposed. That can't strand the user: the
/// curtain is gone, so after a normal Windows unlock they are straight back in.
/// </summary>
internal static class CurtainExitPolicy
{
    public static TrayAction Decide(int exitCode) => exitCode switch
    {
        ExitCodes.Clean => TrayAction.None,
        ExitCodes.AlreadyRunning => TrayAction.None,
        ExitCodes.ConfigInvalid => TrayAction.NotifyConfigInvalid,
        _ => TrayAction.LockWorkstation,
    };
}
