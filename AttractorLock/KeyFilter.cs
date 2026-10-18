using System.Collections.Generic;
using static AttractorLock.Native;

namespace AttractorLock;

/// <summary>
/// Decides which keystrokes the low-level hook swallows. Pure logic, so it can be unit-tested.
///
/// It never blocks a key that produces a character, as long as one of our windows has
/// the foreground. Otherwise the password couldn't be typed.
/// The only exception is AltGr+Space, which is let through.
/// </summary>
internal sealed class KeyFilter
{
    // Keys whose key-down we swallowed. We only swallow a key-up if we swallowed its key-down;
    // otherwise Windows would think the key is still held (a stuck Win key turns "L" into Win+L).
    private readonly HashSet<int> _swallowedDown = new();

    public bool ShouldSwallow(int vk, uint flags, bool isKeyDown, bool foregroundIsOurs, bool ctrlDown)
    {
        if (!isKeyDown)
            return _swallowedDown.Remove(vk);

        bool swallow = !foregroundIsOurs || IsBlockedShortcut(vk, flags, ctrlDown);
        if (swallow) _swallowedDown.Add(vk);
        return swallow;
    }

    private static bool IsBlockedShortcut(int vk, uint flags, bool ctrlDown)
    {
        bool altDown = (flags & LLKHF_ALTDOWN) != 0;
        return vk switch
        {
            VK_LWIN or VK_RWIN => true,          // Start menu, Win+D, Win+Tab, emoji panel...
            VK_APPS => true,                     // context-menu key
            VK_ESCAPE => true,                   // Esc, Ctrl+Esc (Start), Alt+Esc, Ctrl+Shift+Esc (Task Manager)
            VK_TAB => altDown,                   // Alt+Tab
            VK_SPACE => altDown && !ctrlDown,    // Alt+Space opens the window menu (Move!). AltGr = Ctrl+Alt passes.
            _ => false,
        };
    }
}
