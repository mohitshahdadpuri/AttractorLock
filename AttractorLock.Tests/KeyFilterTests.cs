using Xunit;
using static AttractorLock.Native;

namespace AttractorLock.Tests;

public class KeyFilterTests
{
    private const uint Alt = LLKHF_ALTDOWN;

    private static bool Down(KeyFilter f, int vk, uint flags = 0, bool ours = true, bool ctrl = false) =>
        f.ShouldSwallow(vk, flags, isKeyDown: true, foregroundIsOurs: ours, ctrlDown: ctrl);

    private static bool Up(KeyFilter f, int vk, uint flags = 0, bool ours = true, bool ctrl = false) =>
        f.ShouldSwallow(vk, flags, isKeyDown: false, foregroundIsOurs: ours, ctrlDown: ctrl);

    // ---------- The password must always be typeable ----------

    public static IEnumerable<object[]> TypingKeys()
    {
        for (int vk = 'A'; vk <= 'Z'; vk++) yield return new object[] { vk };
        for (int vk = '0'; vk <= '9'; vk++) yield return new object[] { vk };
        foreach (int vk in new[]
                 {
                     VK_SPACE, 0x0D /*Enter*/, 0x08 /*Backspace*/, 0x10 /*Shift*/, 0xA0, 0xA1, VK_CONTROL, VK_MENU,
                     0x14 /*CapsLock*/, 0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF, 0xC0, 0xDB, 0xDC, 0xDD, 0xDE, 0xE2 /*OEM*/,
                     0x60, 0x69 /*numpad*/, 0x6A, 0x6B, 0x6D, 0x6E, 0x6F, VK_PACKET, 0x25, 0x27 /*arrows*/, 0x2E /*Del*/,
                     VK_TAB /* moving between OK / Cancel */
                 })
            yield return new object[] { vk };
    }

    [Theory]
    [MemberData(nameof(TypingKeys))]
    public void TypingKeys_PassThrough_WhenOurWindowIsForeground(int vk)
    {
        var f = new KeyFilter();
        Assert.False(Down(f, vk));
        Assert.False(Up(f, vk));
    }

    [Theory]
    [InlineData('E')]   // AltGr+E = € on many layouts
    [InlineData('Q')]   // AltGr+Q = @ on German layouts
    [InlineData('2')]
    [InlineData(VK_SPACE)]
    public void AltGrCombos_PassThrough(int vk)  // AltGr = Ctrl + Alt flag
    {
        var f = new KeyFilter();
        Assert.False(Down(f, vk, Alt, ctrl: true));
        Assert.False(Up(f, vk, Alt, ctrl: true));
    }

    [Fact]
    public void ShiftedCharacters_PassThrough()
    {
        var f = new KeyFilter();
        Assert.False(Down(f, 0x10));
        Assert.False(Down(f, '1'));
        Assert.False(Up(f, '1'));
        Assert.False(Up(f, 0x10));
    }

    // ---------- Escape routes are blocked ----------

    [Theory]
    [InlineData(VK_LWIN, 0u)]
    [InlineData(VK_RWIN, 0u)]
    [InlineData(VK_APPS, 0u)]
    [InlineData(VK_ESCAPE, 0u)]       // Esc, Ctrl+Esc, Ctrl+Shift+Esc
    [InlineData(VK_ESCAPE, Alt)]      // Alt+Esc
    [InlineData(VK_TAB, Alt)]         // Alt+Tab
    [InlineData(VK_SPACE, Alt)]       // Alt+Space -> window menu -> Move
    public void Shortcuts_AreSwallowed(int vk, uint flags)
    {
        var f = new KeyFilter();
        Assert.True(Down(f, vk, flags));
        Assert.True(Up(f, vk, flags));
    }

    [Fact]
    public void CtrlShiftEsc_TaskManager_IsSwallowed()
    {
        var f = new KeyFilter();
        Assert.False(Down(f, VK_CONTROL));
        Assert.False(Down(f, 0x10));
        Assert.True(Down(f, VK_ESCAPE, ctrl: true));
    }

    [Fact]
    public void AutoRepeat_OfBlockedKey_StaysSwallowed()
    {
        var f = new KeyFilter();
        Assert.True(Down(f, VK_LWIN));
        Assert.True(Down(f, VK_LWIN));
        Assert.True(Down(f, VK_LWIN));
        Assert.True(Up(f, VK_LWIN));
    }

    // ---------- Nothing leaks to other apps ----------

    [Theory]
    [MemberData(nameof(TypingKeys))]
    public void AllKeys_AreSwallowed_WhenForegroundIsNotOurs(int vk)
    {
        var f = new KeyFilter();
        Assert.True(Down(f, vk, ours: false));
        Assert.True(Up(f, vk, ours: false));
    }

    // ---------- No stuck keys ----------

    [Fact]
    public void KeyUp_WithoutSwallowedKeyDown_PassesThrough()  // e.g. Win held while the curtain appeared
    {
        var f = new KeyFilter();
        Assert.False(Up(f, VK_LWIN));
        Assert.False(Up(f, VK_ESCAPE));
    }

    [Fact]
    public void KeyPressedWhileOurs_ReleasedWhileForeign_UpPassesThrough()
    {
        var f = new KeyFilter();
        Assert.False(Down(f, 'A', ours: true));
        Assert.False(Up(f, 'A', ours: false));
    }

    [Fact]
    public void KeySwallowedWhileForeign_ReleasedWhileOurs_UpIsSwallowedToo()
    {
        var f = new KeyFilter();
        Assert.True(Down(f, 'A', ours: false));
        Assert.True(Up(f, 'A', ours: true));
        // and the next press is normal again
        Assert.False(Down(f, 'A'));
        Assert.False(Up(f, 'A'));
    }

    [Fact]
    public void AltReleasedAfterBlockedAltTab_IsNotSwallowed()
    {
        var f = new KeyFilter();
        Assert.False(Down(f, VK_MENU, Alt));
        Assert.True(Down(f, VK_TAB, Alt));
        Assert.True(Up(f, VK_TAB, Alt));
        Assert.False(Up(f, VK_MENU));   // Alt must be released, or it would stick
    }
}
