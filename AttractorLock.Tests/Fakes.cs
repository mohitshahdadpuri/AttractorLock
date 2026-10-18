namespace AttractorLock.Tests;

/// <summary>Records lock requests. Never touches Windows.</summary>
internal sealed class FakeLock : ILockAction
{
    public List<string> Reasons { get; } = new();
    public bool Succeeds { get; set; } = true;
    public bool Throws { get; set; }

    public bool Lock(string reason)
    {
        Reasons.Add(reason);
        if (Throws) throw new InvalidOperationException("lock failed");
        return Succeeds;
    }
}

/// <summary>A controller plus its fakes and a controllable clock.</summary>
internal sealed class ControllerHarness
{
    public FakeLock Lock { get; } = new();
    public List<int> Exits { get; } = new();
    public DateTime Now { get; private set; } = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    public CurtainController C { get; }

    public ControllerHarness()
    {
        C = new CurtainController(Lock, Exits.Add, Now);
    }

    public DateTime Advance(TimeSpan by) => Now += by;
    public DateTime Advance(double ms) => Advance(TimeSpan.FromMilliseconds(ms));

    /// <summary>Moves past the startup grace period with our window in the foreground.</summary>
    public void SettleActive()
    {
        Advance(CurtainController.StartupGrace + TimeSpan.FromMilliseconds(100));
        C.ForegroundSample(true, Now);
    }

    /// <summary>Feeds "not ours" samples every 100 ms for the given duration.</summary>
    public void Foreign(TimeSpan duration)
    {
        var end = Now + duration;
        C.ForegroundSample(false, Now);
        while (Now < end)
        {
            Advance(100);
            C.ForegroundSample(false, Now);
        }
    }
}

/// <summary>
/// Stands in for DPAPI in store tests: scrambles the bytes and adds a check byte, so a
/// damaged file fails to decrypt the way a DPAPI blob would.
/// </summary>
internal sealed class FakeProtector : ISecretProtector
{
    public byte[] Protect(byte[] data)
    {
        var result = data.Select(b => (byte)(b ^ 0x5A)).ToList();
        result.Add(Check(data));
        return result.ToArray();
    }

    public byte[] Unprotect(byte[] data)
    {
        if (data.Length == 0) throw new System.Security.Cryptography.CryptographicException("empty");
        byte[] plain = data[..^1].Select(b => (byte)(b ^ 0x5A)).ToArray();
        if (Check(plain) != data[^1]) throw new System.Security.Cryptography.CryptographicException("bad check byte");
        return plain;
    }

    private static byte Check(byte[] data) => (byte)data.Aggregate(0x33, (acc, b) => (acc * 31 + b) & 0xFF);
}
