using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AttractorLock;

/// <summary>
/// One 5×5 grid of random characters, shown on the Access dialog in grid mode. A new grid
/// is made every time the dialog opens. It contains the user's anchor character exactly once.
/// </summary>
internal sealed class GridChallenge
{
    public const int Size = 5;

    /// <summary>
    /// What a grid can show. O and I are left out so they can't be mistaken for 0 and 1.
    /// They can still be part of an answer (N shifted by one is O).
    /// </summary>
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ0123456789";

    /// <summary>Row by row, Size × Size characters.</summary>
    public string Cells { get; }

    public GridChallenge(string cells)
    {
        if (cells.Length != Size * Size) throw new ArgumentException($"A grid has {Size * Size} cells.", nameof(cells));
        Cells = cells;
    }

    public char this[int row, int col] => Cells[row * Size + col];

    /// <summary>A fresh grid with <paramref name="anchor"/> in exactly one random cell.</summary>
    /// <param name="nextInt">Returns a random int in [0, n). Defaults to a cryptographic RNG.</param>
    public static GridChallenge Create(char anchor, Func<int, int>? nextInt = null)
    {
        nextInt ??= RandomNumberGenerator.GetInt32;
        if (Alphabet.IndexOf(anchor) < 0) throw new ArgumentException("The anchor isn't a grid character.", nameof(anchor));

        string others = Alphabet.Replace(anchor.ToString(), "");
        var cells = new char[Size * Size];
        for (int i = 0; i < cells.Length; i++) cells[i] = others[nextInt(others.Length)];
        cells[nextInt(cells.Length)] = anchor;
        return new GridChallenge(new string(cells));
    }

    /// <summary>The next character: A→B … Z→A, 0→1 … 9→0. Anything else is returned unchanged.</summary>
    public static char ShiftByOne(char c) => c switch
    {
        >= 'A' and < 'Z' or >= '0' and < '9' => (char)(c + 1),
        'Z' => 'A',
        '9' => '0',
        _ => c,
    };

    public override string ToString()
    {
        var sb = new StringBuilder();
        for (int r = 0; r < Size; r++)
        {
            if (r > 0) sb.AppendLine();
            sb.Append(string.Join("  ", Enumerable.Range(0, Size).Select(c => this[r, c])));
        }
        return sb.ToString();
    }
}

/// <summary>
/// The secret for grid mode: an anchor character, a path of moves from it, and whether each
/// character is shifted by one. Find the anchor, take each move in turn (wrapping around the
/// edges) and type the character you land on, or the next one if shifting.
///
/// Example: anchor K, path R D D L, shift on. If K is at row 1, column 1, you land on (1,2),
/// (2,2), (3,2), (3,1); if those show 2 W N H, the answer is 3 X O I.
///
/// The answer is different for every grid, so watching one unlock doesn't give the next one away.
/// </summary>
internal sealed class GridSecret : StoredSecret, IEquatable<GridSecret>
{
    public const int MinPathLength = 3;
    public const int MaxPathLength = 8;
    private const string Moves = "UDLR";

    public char Anchor { get; }
    /// <summary>Moves, each one of U (up), D (down), L (left), R (right).</summary>
    public string Path { get; }
    public bool Shift { get; }

    public GridSecret(char anchor, string path, bool shift)
    {
        if (Validate(anchor.ToString(), path) is { } error) throw new ArgumentException(error);
        Anchor = char.ToUpperInvariant(anchor);
        Path = NormalizePath(path)!;
        Shift = shift;
    }

    /// <summary>The code to type for this grid.</summary>
    public string ExpectedAnswer(GridChallenge grid)
    {
        int at = grid.Cells.IndexOf(Anchor);
        if (at < 0 || grid.Cells.IndexOf(Anchor, at + 1) >= 0)
            throw new ArgumentException("The grid must contain the anchor exactly once.", nameof(grid));

        int row = at / GridChallenge.Size, col = at % GridChallenge.Size;
        var answer = new StringBuilder(Path.Length);
        foreach (char move in Path)
        {
            switch (move)
            {
                case 'U': row--; break;
                case 'D': row++; break;
                case 'L': col--; break;
                case 'R': col++; break;
            }
            row = (row + GridChallenge.Size) % GridChallenge.Size;
            col = (col + GridChallenge.Size) % GridChallenge.Size;
            char c = grid[row, col];
            answer.Append(Shift ? GridChallenge.ShiftByOne(c) : c);
        }
        return answer.ToString();
    }

    /// <summary>Case and spaces don't matter.</summary>
    public bool Verify(GridChallenge grid, string? typed)
    {
        string cleaned = new string((typed ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(cleaned), Encoding.UTF8.GetBytes(ExpectedAnswer(grid)));
    }

    /// <summary>
    /// Turns what the user typed for a path into moves: U/D/L/R in any case or arrows (↑↓←→),
    /// with spaces and commas ignored. Returns null if anything else is in it.
    /// </summary>
    public static string? NormalizePath(string path)
    {
        var sb = new StringBuilder();
        foreach (char c in path)
        {
            if (char.IsWhiteSpace(c) || c is ',' or '-') continue;
            char m = c switch
            {
                '↑' => 'U',
                '↓' => 'D',
                '←' => 'L',
                '→' => 'R',
                _ => char.ToUpperInvariant(c),
            };
            if (Moves.IndexOf(m) < 0) return null;
            sb.Append(m);
        }
        return sb.ToString();
    }

    /// <summary>Returns an error message, or null if the anchor and path are usable.</summary>
    public static string? Validate(string anchor, string path)
    {
        string a = anchor.Trim().ToUpperInvariant();
        if (a.Length != 1) return "The anchor must be a single letter or digit.";
        if (a is "O" or "I") return "O and I never appear in the grid (they look like 0 and 1). Pick another anchor.";
        if (GridChallenge.Alphabet.IndexOf(a[0]) < 0) return "The anchor must be a letter (A–Z) or a digit (0–9).";

        string? moves = NormalizePath(path);
        if (moves is null) return "The path can only contain U, D, L, R (or arrows).";
        if (moves.Length < MinPathLength) return $"Use at least {MinPathLength} moves.";
        if (moves.Length > MaxPathLength) return $"Use at most {MaxPathLength} moves.";
        return null;
    }

    public bool Equals(GridSecret? other) =>
        other is not null && Anchor == other.Anchor && Path == other.Path && Shift == other.Shift;

    public override bool Equals(object? obj) => Equals(obj as GridSecret);
    public override int GetHashCode() => HashCode.Combine(Anchor, Path, Shift);
}
