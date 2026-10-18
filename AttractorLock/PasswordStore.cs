using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace AttractorLock;

internal enum PasswordStatus
{
    /// <summary>No password file yet.</summary>
    NotSet,
    /// <summary>File parsed fine; a password can be verified.</summary>
    Valid,
    /// <summary>File exists but is damaged (truncated, empty, garbage). It can never verify.</summary>
    Corrupt,
    /// <summary>File couldn't be read (locked by AV/sync, access denied). It may work later.</summary>
    Unreadable,
}

/// <summary>
/// What a loaded cfg.dat holds: a <see cref="StoredPassword"/> (fixed password) or a
/// <see cref="GridSecret"/> (grid mode). Checking an answer needs no file access.
/// </summary>
internal abstract class StoredSecret
{
}

/// <summary>A loaded password hash. Verification is pure CPU, with no file access.</summary>
internal sealed class StoredPassword : StoredSecret
{
    public int Iterations { get; }
    private readonly byte[] _salt;
    private readonly byte[] _hash;

    public StoredPassword(int iterations, byte[] salt, byte[] hash)
    {
        Iterations = iterations;
        _salt = salt;
        _hash = hash;
    }

    public bool Verify(string password)
    {
        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password ?? "", _salt, Iterations, HashAlgorithmName.SHA256, _hash.Length);
        return CryptographicOperations.FixedTimeEquals(actual, _hash);
    }
}

/// <summary>Encrypts the grid secret at rest. Windows DPAPI in the app; a fake in tests.</summary>
internal interface ISecretProtector
{
    byte[] Protect(byte[] data);
    /// <summary>Throws <see cref="CryptographicException"/> if the data can't be decrypted.</summary>
    byte[] Unprotect(byte[] data);
}

/// <summary>DPAPI, current user: only your Windows account can read the file.</summary>
internal sealed class DpapiProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "AttractorLock.Grid.v1"u8.ToArray();

    public byte[] Protect(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
}

/// <summary>
/// Stores the unlock secret in %APPDATA%\AttractorLock\cfg.dat. Set ATTRACTORLOCK_DATA_DIR to
/// use another folder (used by tests). A fixed password is kept as a salted PBKDF2-SHA256
/// hash, never plain text. A grid secret has to be readable to work out each grid's answer,
/// so it is encrypted with DPAPI (current user) instead.
///
/// Formats:
///   v1     : "ALK1" | int32 LE iterations | 16-byte salt | 32-byte hash   (56 bytes)
///   legacy : 16-byte salt | 32-byte hash, 100 000 iterations              (48 bytes, read-only)
///   grid   : "ALG1" | protected( 1 | flags | anchor | path length | path ), ASCII,
///            flags bit 0 = shift by one
///
/// Writes are atomic: write to a temp file, flush to disk, verify it round-trips, then rename
/// over the real file. A crash or power cut mid-write leaves the old password working
/// instead of a half-written file that rejects every password.
/// </summary>
internal sealed class PasswordStore
{
    public const string DataDirEnvVar = "ATTRACTORLOCK_DATA_DIR";
    public const int DefaultIterations = 600_000;   // OWASP 2023 guidance for PBKDF2-SHA256
    public const int LegacyIterations = 100_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int MaxIterations = 10_000_000;
    private static readonly byte[] Magic = "ALK1"u8.ToArray();
    private static readonly byte[] GridMagic = "ALG1"u8.ToArray();
    private const byte GridVersion = 1;
    private const byte ShiftFlag = 1;
    private const int V1Size = 4 + 4 + SaltSize + HashSize;
    private const int LegacySize = SaltSize + HashSize;

    private readonly int _iterations;
    private readonly ISecretProtector _protector;

    public string DirectoryPath { get; }
    public string FilePath => Path.Combine(DirectoryPath, "cfg.dat");
    private string TempPath => FilePath + ".tmp";

    public PasswordStore(string directory, int iterations = DefaultIterations, ISecretProtector? protector = null)
    {
        if (iterations < 1 || iterations > MaxIterations) throw new ArgumentOutOfRangeException(nameof(iterations));
        DirectoryPath = directory;
        _iterations = iterations;
        _protector = protector ?? new DpapiProtector();
    }

    public static PasswordStore CreateDefault()
    {
        string? overrideDir = Environment.GetEnvironmentVariable(DataDirEnvVar);
        string dir = !string.IsNullOrWhiteSpace(overrideDir)
            ? overrideDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AttractorLock");
        return new PasswordStore(dir);
    }

    public PasswordStatus Status => TryLoad(out _);

    /// <summary>Never throws. Retries briefly so a transient AV/sync lock doesn't count as failure.</summary>
    public PasswordStatus TryLoad(out StoredSecret? stored)
    {
        stored = null;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(FilePath)) return PasswordStatus.NotSet;
                stored = Parse(File.ReadAllBytes(FilePath), _protector);
                return stored is null ? PasswordStatus.Corrupt : PasswordStatus.Valid;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 2) return PasswordStatus.Unreadable;
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>Convenience for callers that just want a yes/no on a fixed password. Never throws.</summary>
    public bool Verify(string password) =>
        TryLoad(out var stored) == PasswordStatus.Valid && stored is StoredPassword p && p.Verify(password);

    /// <summary>
    /// Saves a new fixed password atomically, replacing any grid secret. Throws if it can't be
    /// saved. In that case the previous file is left exactly as it was.
    /// </summary>
    public void Set(string password)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("Password can't be empty.", nameof(password));

        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, _iterations, HashAlgorithmName.SHA256, HashSize);

        byte[] blob = new byte[V1Size];
        Magic.CopyTo(blob, 0);
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(4), _iterations);
        salt.CopyTo(blob, 8);
        hash.CopyTo(blob, 8 + SaltSize);

        WriteAtomically(blob, check => check is StoredPassword p && p.Verify(password));
    }

    /// <summary>
    /// Saves a grid secret atomically, replacing any fixed password. Throws if it can't be
    /// saved. In that case the previous file is left exactly as it was.
    /// </summary>
    public void SetGrid(GridSecret secret)
    {
        byte[] payload = new byte[4 + secret.Path.Length];
        payload[0] = GridVersion;
        payload[1] = secret.Shift ? ShiftFlag : (byte)0;
        payload[2] = (byte)secret.Anchor;
        payload[3] = (byte)secret.Path.Length;
        for (int i = 0; i < secret.Path.Length; i++) payload[4 + i] = (byte)secret.Path[i];

        byte[] blob = [.. GridMagic, .. _protector.Protect(payload)];
        WriteAtomically(blob, check => secret.Equals(check));
    }

    private void WriteAtomically(byte[] blob, Func<StoredSecret?, bool> roundTrips)
    {
        Directory.CreateDirectory(DirectoryPath);
        try
        {
            using (var fs = new FileStream(TempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(blob);
                fs.Flush(flushToDisk: true);
            }

            // Prove the file on disk really verifies before it replaces the old one.
            if (!roundTrips(Parse(File.ReadAllBytes(TempPath), _protector)))
                throw new IOException("Password file failed verification after writing.");

            File.Move(TempPath, FilePath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(TempPath)) File.Delete(TempPath); } catch (Exception) { /* best effort */ }
        }
    }

    internal static StoredSecret? Parse(byte[] blob, ISecretProtector protector)
    {
        // A crash can leave a correctly sized file full of zeros. Treat it as damaged.
        if (blob.AsSpan().IndexOfAnyExcept((byte)0) < 0) return null;

        if (blob.Length == V1Size && blob.AsSpan(0, 4).SequenceEqual(Magic))
        {
            int iterations = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(4));
            if (iterations < 1 || iterations > MaxIterations) return null;
            return new StoredPassword(iterations, blob[8..(8 + SaltSize)], blob[(8 + SaltSize)..]);
        }
        if (blob.Length == LegacySize)
            return new StoredPassword(LegacyIterations, blob[..SaltSize], blob[SaltSize..]);
        if (blob.Length > GridMagic.Length && blob.AsSpan(0, 4).SequenceEqual(GridMagic))
            return ParseGrid(blob[GridMagic.Length..], protector);
        return null;
    }

    private static GridSecret? ParseGrid(byte[] protectedPayload, ISecretProtector protector)
    {
        byte[] p;
        try { p = protector.Unprotect(protectedPayload); }
        catch (CryptographicException) { return null; }   // damaged, or made by another Windows account

        if (p.Length < 4 || p[0] != GridVersion || (p[1] & ~ShiftFlag) != 0 || p.Length != 4 + p[3]) return null;
        if (GridChallenge.Alphabet.IndexOf((char)p[2]) < 0) return null;
        string anchor = ((char)p[2]).ToString();
        string path = new(p[4..].Select(b => (char)b).ToArray());
        if (GridSecret.Validate(anchor, path) is not null || GridSecret.NormalizePath(path) != path) return null;
        return new GridSecret(anchor[0], path, (p[1] & ShiftFlag) != 0);
    }
}
