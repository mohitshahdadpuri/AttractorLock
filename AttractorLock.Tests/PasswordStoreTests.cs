using System.Buffers.Binary;
using System.Security.Cryptography;
using Xunit;

namespace AttractorLock.Tests;

/// <summary>
/// Integration tests against real files in a temp folder. Key guarantee: the store is never
/// "Valid" while being unable to verify the right password. That combination would put the
/// user behind a curtain that nothing opens.
/// </summary>
public sealed class PasswordStoreTests : IDisposable
{
    private const int FastIterations = 1_000;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AttractorLockTests", Guid.NewGuid().ToString("N"));

    private PasswordStore NewStore(int iterations = FastIterations) => new(_dir, iterations);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ---------- Happy paths ----------

    [Fact]
    public void FreshStore_IsNotSet_AndVerifiesNothing()
    {
        var s = NewStore();
        Assert.Equal(PasswordStatus.NotSet, s.Status);
        Assert.False(s.Verify(""));
        Assert.False(s.Verify("anything"));
    }

    [Theory]
    [InlineData("hunter22")]
    [InlineData("pass word")]
    [InlineData(" padded ")]
    [InlineData("Ünïcödé-ß-€-пароль")]
    [InlineData("smile😀")]
    [InlineData("\"quotes\" & <xml> 'apos' \\ back")]
    public void SetThenVerify_RoundTrips(string pw)
    {
        var s = NewStore();
        s.Set(pw);
        Assert.Equal(PasswordStatus.Valid, s.Status);
        Assert.True(s.Verify(pw));
    }

    [Fact]
    public void VeryLongPassword_RoundTrips()
    {
        var s = NewStore();
        string pw = new('x', 10_000);
        s.Set(pw);
        Assert.True(s.Verify(pw));
        Assert.False(s.Verify(pw[..^1]));
    }

    [Theory]
    [InlineData("hunter22", "hunter23")]
    [InlineData("hunter22", "Hunter22")]   // case-sensitive
    [InlineData("hunter22", "hunter22 ")]  // whitespace matters
    [InlineData("hunter22", " hunter22")]
    [InlineData("hunter22", "")]
    [InlineData("hunter22", "hunter2")]
    [InlineData("café", "café")]     // different Unicode normalisation = different password
    public void WrongPassword_IsRejected(string set, string attempt)
    {
        var s = NewStore();
        s.Set(set);
        Assert.False(s.Verify(attempt));
    }

    [Fact]
    public void ChangingPassword_OldOneStopsWorking()
    {
        var s = NewStore();
        s.Set("first-pass");
        s.Set("second-pass");
        Assert.True(s.Verify("second-pass"));
        Assert.False(s.Verify("first-pass"));
    }

    [Fact]
    public void SamePassword_ProducesDifferentFiles_Salted()
    {
        var s = NewStore();
        s.Set("same-pass");
        var a = File.ReadAllBytes(s.FilePath);
        s.Set("same-pass");
        var b = File.ReadAllBytes(s.FilePath);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void FileNeverContainsThePlainPassword()
    {
        var s = NewStore();
        s.Set("PlainTextCanary");
        var bytes = File.ReadAllBytes(s.FilePath);
        var text = System.Text.Encoding.UTF8.GetString(bytes) + System.Text.Encoding.Unicode.GetString(bytes);
        Assert.DoesNotContain("PlainTextCanary", text);
    }

    [Fact]
    public void DefaultIterations_AreUsedAndStillVerify()
    {
        var s = NewStore(PasswordStore.DefaultIterations);
        s.Set("strong-pass");
        Assert.True(s.TryLoad(out var stored) == PasswordStatus.Valid);
        Assert.Equal(PasswordStore.DefaultIterations, stored!.Iterations);
        Assert.True(stored.Verify("strong-pass"));
    }

    [Fact]
    public void IterationCount_IsReadFromTheFile_NotTheStoreSettings()
    {
        NewStore(1_234).Set("pw-1234");
        var other = NewStore(5_678);
        Assert.True(other.Verify("pw-1234"));
    }

    [Fact]
    public void EmptyPassword_CannotBeSet()
    {
        var s = NewStore();
        Assert.Throws<ArgumentException>(() => s.Set(""));
        Assert.Equal(PasswordStatus.NotSet, s.Status);
    }

    // ---------- Old format from the original app ----------

    [Fact]
    public void LegacyFormat_FromOriginalApp_StillVerifies()
    {
        Directory.CreateDirectory(_dir);
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2("old-password", salt, 100_000, HashAlgorithmName.SHA256, 32);
        File.WriteAllBytes(Path.Combine(_dir, "cfg.dat"), salt.Concat(hash).ToArray());

        var s = NewStore();
        Assert.Equal(PasswordStatus.Valid, s.Status);
        Assert.True(s.Verify("old-password"));
        Assert.False(s.Verify("wrong"));
    }

    // ---------- Damaged files are never reported as Valid ----------

    public static IEnumerable<object[]> DamagedFiles() => new[]
    {
        new object[] { "empty", Array.Empty<byte>() },
        new object[] { "one byte", new byte[] { 1 } },
        new object[] { "truncated legacy", Enumerable.Repeat((byte)7, 47).ToArray() },
        new object[] { "legacy plus one", Enumerable.Repeat((byte)7, 49).ToArray() },
        new object[] { "all zeros (legacy size)", new byte[48] },
        new object[] { "all zeros (v1 size)", new byte[56] },
        new object[] { "truncated v1", "ALK1"u8.ToArray().Concat(new byte[40]).ToArray() },
        new object[] { "text file", "my password is hunter2\r\n"u8.ToArray() },
        new object[] { "random 1 KB", RandomNumberGenerator.GetBytes(1024) },
    };

    [Theory]
    [MemberData(nameof(DamagedFiles))]
    public void DamagedFile_IsCorrupt_NotValid(string name, byte[] content)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Path.Combine(_dir, "cfg.dat"), content);
        var s = NewStore();
        Assert.True(PasswordStatus.Corrupt == s.Status, name);
        Assert.False(s.Verify(""));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(int.MaxValue)]   // would hang the verifier for hours
    public void V1File_WithAbsurdIterationCount_IsCorrupt(int iterations)
    {
        Directory.CreateDirectory(_dir);
        var blob = new byte[56];
        "ALK1"u8.CopyTo(blob);
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(4), iterations);
        RandomNumberGenerator.Fill(blob.AsSpan(8));
        File.WriteAllBytes(Path.Combine(_dir, "cfg.dat"), blob);
        Assert.Equal(PasswordStatus.Corrupt, NewStore().Status);
    }

    [Fact]
    public void CorruptFile_CanBeRepairedBySettingANewPassword()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Path.Combine(_dir, "cfg.dat"), Array.Empty<byte>());
        var s = NewStore();
        s.Set("repaired-pass");
        Assert.Equal(PasswordStatus.Valid, s.Status);
        Assert.True(s.Verify("repaired-pass"));
    }

    // ---------- Atomic writes ----------

    [Fact]
    public void LeftoverTempFile_FromInterruptedWrite_DoesNotAffectCurrentPassword()
    {
        var s = NewStore();
        s.Set("current-pass");
        File.WriteAllBytes(s.FilePath + ".tmp", new byte[] { 1, 2, 3 });   // crash mid-write

        Assert.Equal(PasswordStatus.Valid, s.Status);
        Assert.True(s.Verify("current-pass"));

        s.Set("next-pass");                                                // and it recovers
        Assert.True(s.Verify("next-pass"));
        Assert.False(File.Exists(s.FilePath + ".tmp"));
    }

    [Fact]
    public void FailedWrite_LeavesPreviousPasswordWorking()
    {
        var s = NewStore();
        s.Set("keep-me");
        // Block the temp path: a directory with that name makes the write fail.
        Directory.CreateDirectory(s.FilePath + ".tmp");
        try
        {
            Assert.ThrowsAny<Exception>(() => s.Set("never-saved"));
            Assert.Equal(PasswordStatus.Valid, s.Status);
            Assert.True(s.Verify("keep-me"));
            Assert.False(s.Verify("never-saved"));
        }
        finally
        {
            Directory.Delete(s.FilePath + ".tmp");
        }
    }

    [Fact]
    public void NoTempFileLeftBehind_AfterSuccessfulSet()
    {
        var s = NewStore();
        s.Set("tidy-pass");
        Assert.Equal(new[] { "cfg.dat" }, Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    // ---------- File locked by another program ----------

    [Fact]
    public void ExclusivelyLockedFile_IsUnreadable_AndNeverThrows()
    {
        var s = NewStore();
        s.Set("locked-pass");
        using (new FileStream(s.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(PasswordStatus.Unreadable, s.TryLoad(out var stored));
            Assert.Null(stored);
            Assert.False(s.Verify("locked-pass"));
        }
        Assert.True(s.Verify("locked-pass"));   // fine again once released
    }

    [Fact]
    public async Task TransientLock_IsRiddenOutByRetry()
    {
        var s = NewStore();
        s.Set("transient-pass");
        var fs = new FileStream(s.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var release = Task.Run(async () => { await Task.Delay(120); fs.Dispose(); });
        Assert.Equal(PasswordStatus.Valid, s.TryLoad(out _));
        await release;
    }

    [Fact]
    public void LoadedPassword_KeepsWorking_EvenIfFileIsDeletedLater()
    {
        // The curtain loads the hash once at startup, so later file problems can't lock you out.
        var s = NewStore();
        s.Set("in-memory-pass");
        Assert.Equal(PasswordStatus.Valid, s.TryLoad(out var stored));
        File.Delete(s.FilePath);
        Assert.True(stored!.Verify("in-memory-pass"));
    }

    // ---------- Default location ----------

    [Fact]
    public void CreateDefault_HonoursDataDirOverride()
    {
        string? before = Environment.GetEnvironmentVariable(PasswordStore.DataDirEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(PasswordStore.DataDirEnvVar, _dir);
            Assert.Equal(_dir, PasswordStore.CreateDefault().DirectoryPath);

            Environment.SetEnvironmentVariable(PasswordStore.DataDirEnvVar, null);
            Assert.EndsWith(Path.Combine("AppData", "Roaming", "AttractorLock"), PasswordStore.CreateDefault().DirectoryPath,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PasswordStore.DataDirEnvVar, before);
        }
    }
}
