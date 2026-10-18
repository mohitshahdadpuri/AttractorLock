using System;
using System.Diagnostics;
using System.Threading;

namespace AttractorLock;

internal enum HangVerdict { Ok, ResetAfterSuspend, Hung }

internal static class HangPolicy
{
    public static readonly TimeSpan Threshold = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan SuspendGap = TimeSpan.FromSeconds(4);

    /// <param name="sinceHeartbeat">Time since the UI thread last checked in.</param>
    /// <param name="sinceLastCheck">Time since the watchdog itself last ran. A big gap means the
    /// PC was asleep, not that the UI hung.</param>
    public static HangVerdict Evaluate(TimeSpan sinceHeartbeat, TimeSpan sinceLastCheck)
    {
        if (sinceLastCheck > SuspendGap) return HangVerdict.ResetAfterSuspend;
        return sinceHeartbeat > Threshold ? HangVerdict.Hung : HangVerdict.Ok;
    }
}

/// <summary>
/// Background thread that fires <c>onHung</c> once if the UI thread stops calling
/// <see cref="Beat"/>. A hung curtain is turned into "lock Windows and quit" instead of a
/// frozen screen in an undefined state.
/// </summary>
internal sealed class HangWatchdog : IDisposable
{
    private readonly Action _onHung;
    private readonly Thread _thread;
    private long _lastBeat = Stopwatch.GetTimestamp();
    private volatile bool _stop;

    public HangWatchdog(Action onHung)
    {
        _onHung = onHung;
        _thread = new Thread(Run) { IsBackground = true, Name = "AttractorLock hang watchdog" };
        _thread.Start();
    }

    public void Beat() => Interlocked.Exchange(ref _lastBeat, Stopwatch.GetTimestamp());

    private void Run()
    {
        long lastCheck = Stopwatch.GetTimestamp();
        while (!_stop)
        {
            Thread.Sleep(HangPolicy.CheckInterval);
            if (_stop) return;
            long now = Stopwatch.GetTimestamp();
            var verdict = HangPolicy.Evaluate(
                Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastBeat), now),
                Stopwatch.GetElapsedTime(lastCheck, now));
            lastCheck = now;

            if (verdict == HangVerdict.ResetAfterSuspend) Beat();
            else if (verdict == HangVerdict.Hung) { _onHung(); return; }
        }
    }

    public void Dispose() => _stop = true;
}
