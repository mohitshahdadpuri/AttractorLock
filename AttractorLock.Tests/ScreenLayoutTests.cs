using Xunit;

namespace AttractorLock.Tests;

public class ScreenLayoutTests
{
    private static readonly ScreenRect NoFallback = new(0, 0, 0, 0);

    private static MonitorInfo M(int x, int y, int w, int h, bool primary = false) => new(new ScreenRect(x, y, w, h), primary);

    public static IEnumerable<object[]> Layouts() => new[]
    {
        new object[] { "single 1080p", new[] { M(0, 0, 1920, 1080, true) } },
        new object[] { "side by side", new[] { M(0, 0, 1920, 1080, true), M(1920, 0, 1920, 1080) } },
        new object[] { "unequal heights (gap bottom-right)", new[] { M(0, 0, 2560, 1440, true), M(2560, 0, 1280, 1024) } },
        new object[] { "secondary left of primary (negative X)", new[] { M(0, 0, 1920, 1080, true), M(-1280, 200, 1280, 1024) } },
        new object[] { "secondary above (negative Y)", new[] { M(0, 0, 1920, 1080, true), M(320, -1080, 1920, 1080) } },
        new object[] { "offset with gap", new[] { M(0, 0, 1920, 1080, true), M(1920, 600, 1080, 1920) } },
        new object[] { "mixed DPI laptop + 4K", new[] { M(0, 0, 2880, 1800, true), M(2880, -360, 3840, 2160) } },
        new object[] { "three monitors L-shape", new[] { M(0, 0, 1920, 1080, true), M(1920, 0, 1920, 1080), M(0, 1080, 1920, 1080) } },
    };

    [Theory]
    [MemberData(nameof(Layouts))]
    public void EveryMonitor_GetsExactlyOneCurtain_MatchingItsBounds(string name, object layout)  // object: MonitorInfo is internal
    {
        var monitors = (MonitorInfo[])layout;
        var slots = ScreenLayout.Compute(monitors, NoFallback);

        Assert.True(slots.Count == monitors.Length, name);
        foreach (var m in monitors)
            Assert.Contains(slots, s => s.Bounds == m.Bounds);
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void EveryCurtain_LiesEntirelyOnOneRealMonitor(string name, object layout)  // object: MonitorInfo is internal
    {
        var monitors = (MonitorInfo[])layout;
        // Each curtain carries its own Access button, docked inside its own bounds.
        // If every curtain sits on a real monitor, every Access button is visible.
        foreach (var s in ScreenLayout.Compute(monitors, NoFallback))
            Assert.True(monitors.Any(m => m.Bounds.Contains(s.Bounds)), $"{name}: {s.Bounds} not on any monitor");
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void Primary_ComesFirst(string name, object layout)  // object: MonitorInfo is internal
    {
        var monitors = (MonitorInfo[])layout;
        var slots = ScreenLayout.Compute(monitors.Reverse(), NoFallback);
        Assert.True(slots[0].IsPrimary, name);
        Assert.Single(slots, s => s.IsPrimary);
    }

    [Fact]
    public void MirroredDisplays_AreDeduplicated()
    {
        var slots = ScreenLayout.Compute(new[] { M(0, 0, 1920, 1080, true), M(0, 0, 1920, 1080) }, NoFallback);
        Assert.Single(slots);
        Assert.True(slots[0].IsPrimary);
    }

    [Fact]
    public void EmptyAndZeroSizedMonitors_FallBackToVirtualScreen()
    {
        var fallback = new ScreenRect(0, 0, 1920, 1080);
        var slots = ScreenLayout.Compute(new[] { M(0, 0, 0, 0, true) }, fallback);
        Assert.Single(slots);
        Assert.Equal(fallback, slots[0].Bounds);

        Assert.Single(ScreenLayout.Compute(Array.Empty<MonitorInfo>(), fallback));
    }

    [Fact]
    public void SameLayout_DetectsChanges()
    {
        var a = ScreenLayout.Compute(new[] { M(0, 0, 1920, 1080, true) }, NoFallback);
        var b = ScreenLayout.Compute(new[] { M(0, 0, 1920, 1080, true) }, NoFallback);
        var c = ScreenLayout.Compute(new[] { M(0, 0, 1920, 1080, true), M(1920, 0, 1920, 1080) }, NoFallback);
        var d = ScreenLayout.Compute(new[] { M(0, 0, 2560, 1440, true) }, NoFallback);
        Assert.True(ScreenLayout.SameLayout(a, b));
        Assert.False(ScreenLayout.SameLayout(a, c));
        Assert.False(ScreenLayout.SameLayout(a, d));
    }
}
