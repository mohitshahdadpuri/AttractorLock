using System.Security.Cryptography;
using Xunit;

namespace AttractorLock.Tests;

public class GridChallengeTests
{
    [Fact]
    public void Create_ContainsTheAnchorExactlyOnce_AndOnlyGridCharacters()
    {
        foreach (char anchor in GridChallenge.Alphabet)
            for (int i = 0; i < 50; i++)
            {
                var g = GridChallenge.Create(anchor);
                Assert.Equal(GridChallenge.Size * GridChallenge.Size, g.Cells.Length);
                Assert.Single(g.Cells, c => c == anchor);
                Assert.All(g.Cells, c => Assert.Contains(c, GridChallenge.Alphabet));
            }
    }

    [Fact]
    public void Create_MovesTheAnchorAround()
    {
        var spots = Enumerable.Range(0, 300).Select(_ => GridChallenge.Create('K').Cells.IndexOf('K')).ToHashSet();
        Assert.True(spots.Count > 15, $"anchor only ever landed in {spots.Count} cells");
    }

    [Fact]
    public void Alphabet_LeavesOut_LookalikesOfZeroAndOne()
    {
        Assert.DoesNotContain('O', GridChallenge.Alphabet);
        Assert.DoesNotContain('I', GridChallenge.Alphabet);
    }

    [Theory]
    [InlineData('A', 'B')]
    [InlineData('N', 'O')]   // O isn't shown in grids but is still a valid answer
    [InlineData('H', 'I')]
    [InlineData('Y', 'Z')]
    [InlineData('Z', 'A')]
    [InlineData('0', '1')]
    [InlineData('8', '9')]
    [InlineData('9', '0')]
    public void ShiftByOne_WrapsWithinLettersAndWithinDigits(char from, char to) =>
        Assert.Equal(to, GridChallenge.ShiftByOne(from));

    [Fact]
    public void Constructor_RejectsWrongSize() =>
        Assert.Throws<ArgumentException>(() => new GridChallenge("ABC"));

    [Fact]
    public void Create_RejectsAnchorThatCanNeverAppear() =>
        Assert.Throws<ArgumentException>(() => GridChallenge.Create('O'));
}

public class GridSecretTests
{
    // Row by row:
    //   Q 7 M 4 X
    //   B K 2 Z 9
    //   R E W 5 T
    //   3 H N 8 C
    //   P 6 J Y A
    private static readonly GridChallenge Sample = new("Q7M4XBK2Z9REW5T3HN8CP6JYA");

    [Fact]
    public void FollowsThePathFromTheAnchor()
    {
        var s = new GridSecret('K', "RDDL", shift: false);
        Assert.Equal("2WNH", s.ExpectedAnswer(Sample));
    }

    [Fact]
    public void ShiftByOne_TypesTheNextCharacter()
    {
        var s = new GridSecret('K', "RDDL", shift: true);
        Assert.Equal("3XOI", s.ExpectedAnswer(Sample));   // 2→3, W→X, N→O, H→I
    }

    [Fact]
    public void PathWrapsAroundEveryEdge()
    {
        // A is bottom-right (4,4). R wraps to (4,0)=P, D wraps to (0,0)=Q, L wraps to (0,4)=X, U wraps to (4,4)=A.
        var s = new GridSecret('A', "RDLU", shift: false);
        Assert.Equal("PQXA", s.ExpectedAnswer(Sample));
    }

    [Theory]
    [InlineData("2WNH")]
    [InlineData("2wnh")]       // case doesn't matter
    [InlineData(" 2W NH ")]    // nor spaces
    public void Verify_AcceptsTheRightCode(string typed) =>
        Assert.True(new GridSecret('K', "RDDL", shift: false).Verify(Sample, typed));

    [Theory]
    [InlineData("")]
    [InlineData("2WN")]
    [InlineData("2WNHH")]
    [InlineData("HNW2")]
    [InlineData("3XOI")]       // shifted code while shift is off
    [InlineData(null)]
    public void Verify_RejectsAnythingElse(string? typed) =>
        Assert.False(new GridSecret('K', "RDDL", shift: false).Verify(Sample, typed));

    [Fact]
    public void Verify_WithShift_RejectsTheUnshiftedCode()
    {
        var s = new GridSecret('K', "RDDL", shift: true);
        Assert.False(s.Verify(Sample, "2WNH"));
        Assert.True(s.Verify(Sample, s.ExpectedAnswer(Sample)));
    }

    [Fact]
    public void SameSecret_DifferentGrids_UsuallyDifferentCodes()
    {
        var s = new GridSecret('K', "RDDL", shift: true);
        var codes = Enumerable.Range(0, 50).Select(_ => s.ExpectedAnswer(GridChallenge.Create('K'))).ToHashSet();
        Assert.True(codes.Count > 40);
    }

    [Fact]
    public void ExpectedAnswer_NeedsTheAnchorExactlyOnce()
    {
        var s = new GridSecret('K', "RDDL", shift: false);
        Assert.Throws<ArgumentException>(() => s.ExpectedAnswer(new GridChallenge(new string('A', 25))));
        Assert.Throws<ArgumentException>(() => s.ExpectedAnswer(new GridChallenge("KK" + new string('A', 23))));
    }

    [Theory]
    [InlineData("rddl", "RDDL")]
    [InlineData("R D D L", "RDDL")]
    [InlineData("r, d, d, l", "RDDL")]
    [InlineData("→↓↓←", "RDDL")]
    [InlineData("u-u-l", "UUL")]
    public void NormalizePath_AcceptsLettersAndArrows(string typed, string moves) =>
        Assert.Equal(moves, GridSecret.NormalizePath(typed));

    [Theory]
    [InlineData("RDX")]
    [InlineData("north")]
    [InlineData("1234")]
    public void NormalizePath_RejectsOtherCharacters(string typed) =>
        Assert.Null(GridSecret.NormalizePath(typed));

    [Theory]
    [InlineData("K", "RDD")]
    [InlineData("k", "rddl")]
    [InlineData("7", "UUUUUUUU")]
    [InlineData(" Z ", "L R L")]
    public void Validate_Acceptable(string anchor, string path) =>
        Assert.Null(GridSecret.Validate(anchor, path));

    [Theory]
    [InlineData("", "RDD")]
    [InlineData("KK", "RDD")]
    [InlineData("O", "RDD")]       // never appears in a grid
    [InlineData("I", "RDD")]
    [InlineData("#", "RDD")]
    [InlineData("é", "RDD")]
    [InlineData("K", "")]
    [InlineData("K", "RD")]        // too short
    [InlineData("K", "RRRRRRRRR")] // too long
    [InlineData("K", "RDQ")]
    public void Validate_Rejected(string anchor, string path) =>
        Assert.NotNull(GridSecret.Validate(anchor, path));

    [Fact]
    public void Constructor_NormalizesAndRejectsInvalid()
    {
        var s = new GridSecret('k', "r d ↓", shift: true);
        Assert.Equal('K', s.Anchor);
        Assert.Equal("RDD", s.Path);
        Assert.Throws<ArgumentException>(() => new GridSecret('O', "RDD", false));
        Assert.Throws<ArgumentException>(() => new GridSecret('K', "RD", false));
    }
}

/// <summary>Grid secrets in cfg.dat. Same guarantee as for passwords: never Valid unless it really works.</summary>
public sealed class GridStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AttractorLockTests", Guid.NewGuid().ToString("N"));
    private static readonly GridSecret Secret = new('K', "RDDL", shift: true);

    private PasswordStore NewStore(ISecretProtector? protector = null) => new(_dir, 1_000, protector ?? new FakeProtector());

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Theory]
    [InlineData('K', "RDDL", true)]
    [InlineData('7', "UUULLLDD", false)]
    [InlineData('Z', "LRL", true)]
    public void SetGridThenLoad_RoundTrips(char anchor, string path, bool shift)
    {
        var secret = new GridSecret(anchor, path, shift);
        var s = NewStore();
        s.SetGrid(secret);
        Assert.Equal(PasswordStatus.Valid, s.TryLoad(out var stored));
        Assert.Equal(secret, Assert.IsType<GridSecret>(stored));
    }

    [Fact]
    public void GridReplacesPassword_AndPasswordReplacesGrid()
    {
        var s = NewStore();
        s.Set("fixed-pass");
        s.SetGrid(Secret);
        Assert.IsType<GridSecret>(Load(s));
        Assert.False(s.Verify("fixed-pass"));

        s.Set("fixed-again");
        Assert.IsType<StoredPassword>(Load(s));
        Assert.True(s.Verify("fixed-again"));
    }

    [Fact]
    public void Dpapi_FileNeverContainsTheSecretInTheClear()
    {
        var s = NewStore(new DpapiProtector());
        s.SetGrid(new GridSecret('Q', "UUDDLRLR", shift: true));
        Assert.DoesNotContain("UUDDLRLR", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(s.FilePath)));
    }

    [Fact]
    public void Dpapi_RoundTrips_AndIsTheDefault()
    {
        var s = NewStore(new DpapiProtector());
        s.SetGrid(Secret);
        Assert.Equal(Secret, Load(s));
        Assert.Equal(Secret, Load(new PasswordStore(_dir)));
    }

    [Fact]
    public void Dpapi_TamperedFile_IsCorrupt()
    {
        var s = NewStore(new DpapiProtector());
        s.SetGrid(Secret);
        var bytes = File.ReadAllBytes(s.FilePath);
        bytes[^5] ^= 0xFF;
        File.WriteAllBytes(s.FilePath, bytes);
        Assert.Equal(PasswordStatus.Corrupt, s.Status);
    }

    public static IEnumerable<object[]> DamagedGridFiles()
    {
        var p = new FakeProtector();
        byte[] Grid(params byte[] payload) => [.. "ALG1"u8.ToArray(), .. p.Protect(payload)];
        return new[]
        {
            new object[] { "magic only", "ALG1"u8.ToArray() },
            new object[] { "undecryptable", "ALG1"u8.ToArray().Concat(RandomNumberGenerator.GetBytes(64)).ToArray() },
            new object[] { "empty payload", Grid() },
            new object[] { "wrong version", Grid(2, 1, (byte)'K', 3, (byte)'R', (byte)'D', (byte)'D') },
            new object[] { "unknown flag", Grid(1, 3, (byte)'K', 3, (byte)'R', (byte)'D', (byte)'D') },
            new object[] { "anchor O", Grid(1, 1, (byte)'O', 3, (byte)'R', (byte)'D', (byte)'D') },
            new object[] { "lowercase anchor", Grid(1, 1, (byte)'k', 3, (byte)'R', (byte)'D', (byte)'D') },
            new object[] { "path too short", Grid(1, 1, (byte)'K', 2, (byte)'R', (byte)'D') },
            new object[] { "length mismatch", Grid(1, 1, (byte)'K', 4, (byte)'R', (byte)'D', (byte)'D') },
            new object[] { "bad move", Grid(1, 1, (byte)'K', 3, (byte)'R', (byte)'D', (byte)'X') },
            new object[] { "lowercase move", Grid(1, 1, (byte)'K', 3, (byte)'R', (byte)'D', (byte)'d') },
        };
    }

    [Theory]
    [MemberData(nameof(DamagedGridFiles))]
    public void DamagedGridFile_IsCorrupt_NotValid(string name, byte[] content)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Path.Combine(_dir, "cfg.dat"), content);
        Assert.True(PasswordStatus.Corrupt == NewStore().Status, name);
    }

    [Fact]
    public void Dpapi_FileFromAnotherProtector_IsCorrupt()
    {
        // Like copying cfg.dat to another Windows account: DPAPI can't decrypt it.
        new PasswordStore(_dir, 1_000, new DpapiProtector()).SetGrid(Secret);
        Assert.Equal(PasswordStatus.Corrupt, NewStore(new FakeProtector()).Status);
    }

    [Fact]
    public void FailedGridWrite_LeavesPreviousPasswordWorking()
    {
        var s = NewStore();
        s.Set("keep-me");
        Directory.CreateDirectory(s.FilePath + ".tmp");
        try
        {
            Assert.ThrowsAny<Exception>(() => s.SetGrid(Secret));
            Assert.True(s.Verify("keep-me"));
        }
        finally
        {
            Directory.Delete(s.FilePath + ".tmp");
        }
    }

    private static StoredSecret? Load(PasswordStore s)
    {
        Assert.Equal(PasswordStatus.Valid, s.TryLoad(out var stored));
        return stored;
    }
}
