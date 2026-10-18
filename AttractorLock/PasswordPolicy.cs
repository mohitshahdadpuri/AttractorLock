namespace AttractorLock;

/// <summary>
/// Rules for a new password. The main goal is that whatever you set can always be typed
/// on the curtain. The curtain blocks the Windows key, so emoji from the Win+. panel can't
/// be typed there. Pasted control characters can't be typed at all.
/// </summary>
internal static class PasswordPolicy
{
    public const int MinLength = 4;

    /// <summary>Returns an error message, or null if the password is acceptable.</summary>
    public static string? Validate(string password, string confirm)
    {
        if (password.Length == 0) return "Password can't be empty.";
        if (password.Length < MinLength) return $"Use at least {MinLength} characters.";
        if (password != confirm) return "Passwords don't match.";
        foreach (char c in password)
        {
            if (char.IsControl(c))
                return "The password contains a control character (pasted text?). Type it by hand instead.";
            if (char.IsSurrogate(c))
                return "Emoji and similar symbols can't be typed on the curtain (the Windows key is blocked there). " +
                       "Use characters you can type directly on your keyboard.";
        }
        return null;
    }
}
