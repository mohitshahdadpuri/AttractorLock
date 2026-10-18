using System;
using System.IO;
using System.Security.Cryptography;

namespace AttractorLock;

/// <summary>
/// Stores the access password as a salted PBKDF2 hash (never plain text)
/// in %APPDATA%\AttractorLock\cfg.dat.
/// </summary>
internal static class PasswordStore
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 100_000;

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AttractorLock");

    private static string File => Path.Combine(Dir, "cfg.dat");

    public static bool IsSet => System.IO.File.Exists(File);

    public static void Set(string password)
    {
        Directory.CreateDirectory(Dir);
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);

        byte[] blob = new byte[SaltSize + HashSize];
        Buffer.BlockCopy(salt, 0, blob, 0, SaltSize);
        Buffer.BlockCopy(hash, 0, blob, SaltSize, HashSize);
        System.IO.File.WriteAllBytes(File, blob);
    }

    public static bool Verify(string password)
    {
        if (!IsSet) return false;
        byte[] blob = System.IO.File.ReadAllBytes(File);
        if (blob.Length != SaltSize + HashSize) return false;

        byte[] salt = new byte[SaltSize];
        byte[] expected = new byte[HashSize];
        Buffer.BlockCopy(blob, 0, salt, 0, SaltSize);
        Buffer.BlockCopy(blob, SaltSize, expected, 0, HashSize);

        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
