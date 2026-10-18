using System.Collections.Generic;
using System.Linq;

namespace AttractorLock;

/// <summary>A rectangle in physical screen pixels.</summary>
internal readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(ScreenRect other) =>
        other.X >= X && other.Y >= Y && other.Right <= Right && other.Bottom <= Bottom;
}

internal readonly record struct MonitorInfo(ScreenRect Bounds, bool IsPrimary);

/// <summary>
/// Turns the monitor list into curtain windows: one window per monitor, sized exactly to it.
/// Each window carries its own Access button. That way the button can never end up in a
/// gap between monitors of different sizes, or be mis-scaled on a mixed-DPI setup.
/// That was possible with the old single window stretched over the virtual-desktop bounding box.
/// </summary>
internal static class ScreenLayout
{
    public static IReadOnlyList<MonitorInfo> Compute(IEnumerable<MonitorInfo> monitors, ScreenRect fallback)
    {
        var result = monitors
            .Where(m => !m.Bounds.IsEmpty)
            .GroupBy(m => m.Bounds)                       // mirrored displays report the same rect
            .Select(g => new MonitorInfo(g.Key, g.Any(m => m.IsPrimary)))
            .OrderByDescending(m => m.IsPrimary)          // primary first: it gets activated
            .ThenBy(m => m.Bounds.X).ThenBy(m => m.Bounds.Y)
            .ToList();

        if (result.Count == 0 && !fallback.IsEmpty)
            result.Add(new MonitorInfo(fallback, true));
        return result;
    }

    public static bool SameLayout(IReadOnlyList<MonitorInfo> a, IReadOnlyList<MonitorInfo> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First == p.Second);
}
