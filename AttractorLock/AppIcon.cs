using System;
using System.Drawing;

namespace AttractorLock;

/// <summary>
/// The AttractorLock logo (Lorenz attractor, drawn by tools\make-logo.ps1). The same .ico is
/// the exe's own icon (ApplicationIcon) and an embedded resource, loaded here for the tray and
/// the setup window. If it can't be loaded, the Windows shield is used, so the tray icon can
/// never fail to appear.
/// </summary>
internal static class AppIcon
{
    public const string ResourceName = "AttractorLock.icon.ico";

    /// <summary>The logo frame closest to <paramref name="size"/>, keeping the other frames too.</summary>
    public static Icon Load(Size size)
    {
        try
        {
            using var s = typeof(AppIcon).Assembly.GetManifestResourceStream(ResourceName);
            if (s is not null) return new Icon(s, size);
        }
        catch (Exception)
        {
            // A damaged resource must not stop the tray from starting.
        }
        return (Icon)SystemIcons.Shield.Clone();
    }
}
