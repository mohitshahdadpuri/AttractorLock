using System.Drawing;
using System.Runtime.InteropServices;
using Xunit;

namespace AttractorLock.Tests;

/// <summary>
/// The logo has to reach the three places it shows up: the exe's own icon (Settings › Apps,
/// shortcuts), and the embedded copy the tray and setup window load.
/// </summary>
public class AppIconTests
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);

    private static byte[] EmbeddedIco()
    {
        using var s = typeof(AppIcon).Assembly.GetManifestResourceStream(AppIcon.ResourceName);
        Assert.NotNull(s);
        using var ms = new MemoryStream();
        s!.CopyTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public void EmbeddedIco_HasTheSizesWindowsAsksFor()
    {
        byte[] ico = EmbeddedIco();
        Assert.Equal(1, BitConverter.ToInt16(ico, 2));   // type 1 = icon
        int count = BitConverter.ToInt16(ico, 4);
        var sizes = Enumerable.Range(0, count).Select(i => ico[6 + 16 * i] == 0 ? 256 : ico[6 + 16 * i]).ToList();
        // Tray at 100/125/150 % scaling, title bars, Explorer, Settings.
        Assert.Subset(new HashSet<int>(sizes), new HashSet<int> { 16, 20, 24, 32, 48, 256 });
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void Load_ReturnsTheLogoAtTheRequestedSize(int size)
    {
        using var icon = AppIcon.Load(new Size(size, size));
        // The Windows shield fallback would come back at its own default size, not 16/20/24.
        Assert.Equal(size, icon.Width);
        Assert.Equal(size, icon.Height);
    }

    [Fact]
    public void BuiltExe_CarriesTheIcon()
    {
        // Settings › Apps and the shortcuts point at "AttractorLock.exe,0". Without an icon
        // resource in the exe they show a blank tile.
        string exe = Path.Combine(AppContext.BaseDirectory, "AttractorLock.exe");
        Assert.True(File.Exists(exe), exe);
        Assert.True(ExtractIconEx(exe, -1, null, null, 0) >= 1);
    }
}
