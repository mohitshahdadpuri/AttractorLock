using System.Diagnostics;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Xunit;
using FlaApp = FlaUI.Core.Application;

namespace AttractorLock.Tests;

/// <summary>Skipped unless ATTRACTORLOCK_E2E=1: these put a real curtain on your screen for a few seconds each.</summary>
public sealed class E2EFactAttribute : FactAttribute
{
    public E2EFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ATTRACTORLOCK_E2E") != "1")
            Skip = "Set ATTRACTORLOCK_E2E=1 to run. Covers the screen for a few seconds per test; never locks Windows.";
    }
}

/// <summary>
/// End-to-end: real exe, real windows, real keyboard hook, driven through UI Automation.
/// Always in --test mode, so every "lock" is only written to a log file.
/// </summary>
[Trait("Category", "E2E")]
public sealed class E2ETests : IDisposable
{
    private const string Password = "correct horse 42";
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(10);
    private readonly UIA3Automation _automation = new();

    public void Dispose() => _automation.Dispose();

    // ---------- helpers ----------

    private static T WaitFor<T>(Func<T?> probe, TimeSpan timeout, string what) where T : class
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try { if (probe() is { } r) return r; } catch (Exception) { /* element not ready yet */ }
            Thread.Sleep(100);
        }
        throw new TimeoutException("Timed out waiting for " + what);
    }

    private Window[] TopWindows(CurtainProcess p) =>
        FlaApp.Attach(p.Process.Id).GetAllTopLevelWindows(_automation);

    private Window Curtain(CurtainProcess p, int index = 0) =>
        WaitFor(() => TopWindows(p).FirstOrDefault(w => w.AutomationId == $"Curtain{index}"), Short, "curtain window");

    // UIA lists an owned window (our dialog) as a child of its owner, not as a top-level window.
    private Window Dialog(CurtainProcess p) =>
        WaitFor(() => TopWindows(p)
            .Select(w => w.AutomationId == "AccessDialog" ? w : w.FindFirstChild(cf => cf.ByAutomationId("AccessDialog"))?.AsWindow())
            .FirstOrDefault(w => w is not null), Short, "password dialog");

    private CurtainProcess StartAndWait(TestDataDir data)
    {
        var p = CurtainProcess.Start(data);
        var c = Curtain(p);
        try { c.SetForeground(); } catch (Exception) { /* the curtain also activates itself */ }
        WaitForForeground(p);
        return p;
    }

    private static void WaitForForeground(CurtainProcess p) =>
        WaitFor(() => ForegroundPid() == p.Process.Id ? "ok" : null, Short, "curtain to get the foreground");

    private void OpenDialog(CurtainProcess p, int index = 0)
    {
        var access = WaitFor(() => Curtain(p, index).FindFirstDescendant(cf => cf.ByAutomationId("AccessButton")), Short, "Access button");
        access.AsButton().Invoke();
    }

    private void SubmitPassword(CurtainProcess p, string pw)
    {
        var dlg = Dialog(p);
        var box = WaitFor(() => dlg.FindFirstDescendant(cf => cf.ByAutomationId("PasswordBox")), Short, "password box");
        box.Patterns.Value.Pattern.SetValue(pw);
        dlg.FindFirstDescendant(cf => cf.ByAutomationId("OkButton")).AsButton().Invoke();
    }

    private static void AssertExitsCleanly(CurtainProcess p, TimeSpan? timeout = null)
    {
        Assert.True(p.WaitForExit(timeout ?? Short), "curtain should have exited");
        Assert.Equal(ExitCodes.Clean, p.Process.ExitCode);
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    private static int ForegroundPid()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
        return (int)pid;
    }

    // ---------- The core promise: correct password always gets you out ----------

    [E2EFact]
    public void CorrectPassword_ExitsCleanly_AndNeverLocks()
    {
        using var data = new TestDataDir(Password);
        using var p = StartAndWait(data);

        OpenDialog(p);
        SubmitPassword(p, Password);

        AssertExitsCleanly(p);
        Assert.Empty(p.LockLogReasons());
    }

    [E2EFact]
    public void CorrectPassword_TypedOnRealKeyboard_ThroughTheHook_IncludingUnicode()
    {
        const string pw = "Ünï-€ pass 7!";
        using var data = new TestDataDir(pw);
        using var p = StartAndWait(data);

        OpenDialog(p);
        var dlg = Dialog(p);
        var box = WaitFor(() => dlg.FindFirstDescendant(cf => cf.ByAutomationId("PasswordBox")), Short, "password box");
        box.Focus();
        WaitForForeground(p);
        Keyboard.Type(pw);
        Keyboard.Type(VirtualKeyShort.ENTER);

        AssertExitsCleanly(p);
        Assert.Empty(p.LockLogReasons());
    }

    [E2EFact]
    public void Cancel_KeepsCurtainUp_ThenCorrectPasswordStillWorks()
    {
        using var data = new TestDataDir(Password);
        using var p = StartAndWait(data);

        OpenDialog(p);
        Dialog(p).FindFirstDescendant(cf => cf.ByAutomationId("CancelButton")).AsButton().Invoke();
        Thread.Sleep(1500);
        Assert.False(p.Process.HasExited);
        Assert.Empty(p.LockLogReasons());

        OpenDialog(p);
        SubmitPassword(p, Password);
        AssertExitsCleanly(p);
        Assert.Empty(p.LockLogReasons());
    }

    [E2EFact]
    public void CorrectPassword_WorksFromEveryMonitorsAccessButton()
    {
        int monitors = System.Windows.Forms.Screen.AllScreens.Length;
        for (int i = 0; i < monitors; i++)
        {
            using var data = new TestDataDir(Password);
            using var p = StartAndWait(data);
            OpenDialog(p, i);
            SubmitPassword(p, Password);
            AssertExitsCleanly(p);
            Assert.Empty(p.LockLogReasons());
        }
    }

    // ---------- Grid mode ----------

    private static readonly GridSecret Grid = new('K', "RDDL", shift: true);

    private static TestDataDir GridDataDir()
    {
        var data = new TestDataDir();
        data.Store.SetGrid(Grid);
        return data;
    }

    private GridChallenge ReadGrid(CurtainProcess p)
    {
        var dlg = Dialog(p);
        var cells = new char[GridChallenge.Size * GridChallenge.Size];
        for (int r = 0; r < GridChallenge.Size; r++)
            for (int c = 0; c < GridChallenge.Size; c++)
            {
                var cell = WaitFor(() => dlg.FindFirstDescendant(cf => cf.ByAutomationId($"GridCell{r}{c}")), Short, "grid cell");
                cells[r * GridChallenge.Size + c] = cell.Name.Single();
            }
        return new GridChallenge(new string(cells));
    }

    [E2EFact]
    public void GridMode_CodeReadFromTheGrid_ExitsCleanly()
    {
        using var data = GridDataDir();
        using var p = StartAndWait(data);

        OpenDialog(p);
        SubmitPassword(p, Grid.ExpectedAnswer(ReadGrid(p)));

        AssertExitsCleanly(p);
        Assert.Empty(p.LockLogReasons());
    }

    [E2EFact]
    public void GridMode_EachAttemptGetsANewGrid_AndAStaleCodeLocks()
    {
        using var data = GridDataDir();
        using var p = StartAndWait(data);

        OpenDialog(p);
        var first = ReadGrid(p);
        Dialog(p).FindFirstDescendant(cf => cf.ByAutomationId("CancelButton")).AsButton().Invoke();
        Thread.Sleep(500);

        OpenDialog(p);
        var second = ReadGrid(p);
        Assert.NotEqual(first.Cells, second.Cells);

        string stale = Grid.ExpectedAnswer(first);
        if (stale == Grid.ExpectedAnswer(second)) stale = "ZZZZ";   // astronomically unlikely
        SubmitPassword(p, stale);

        AssertExitsCleanly(p);
        Assert.Equal(new[] { "wrong password" }, p.LockLogReasons());
    }

    // ---------- Wrong password / close / focus ----------

    [E2EFact]
    public void WrongPassword_Locks_ThenCurtainCloses()
    {
        using var data = new TestDataDir(Password);
        using var p = StartAndWait(data);

        OpenDialog(p);
        SubmitPassword(p, "not the password");

        AssertExitsCleanly(p);   // simulated lock -> simulated SessionLock -> exit
        Assert.Equal(new[] { "wrong password" }, p.LockLogReasons());
    }

    [E2EFact]
    public void CloseRequest_Locks()
    {
        using var data = new TestDataDir(Password);
        using var p = StartAndWait(data);

        Curtain(p).Patterns.Window.Pattern.Close();

        AssertExitsCleanly(p);
        Assert.Equal("close attempted", p.LockLogReasons().First());
    }

    [E2EFact]
    public void AltF4_Locks()
    {
        using var data = new TestDataDir(Password);
        using var p = StartAndWait(data);

        Keyboard.TypeSimultaneously(VirtualKeyShort.ALT, VirtualKeyShort.F4);

        AssertExitsCleanly(p);
        Assert.Equal("close attempted", p.LockLogReasons().First());
    }

    [E2EFact]
    public void AnotherAppTakingForeground_Locks()
    {
        using var data = new TestDataDir(Password);
        using var p = StartAndWait(data);
        using var thief = Process.Start("notepad.exe")!;
        try
        {
            StealForeground(thief);

            AssertExitsCleanly(p);
            Assert.Equal("focus lost", p.LockLogReasons().First());
        }
        finally
        {
            try { thief.Kill(); } catch (Exception) { }
        }
    }

    [E2EFact]
    public void AnotherAppTakingForeground_WhilePasswordDialogIsOpen_Locks()
    {
        using var data = new TestDataDir(Password);
        using var p = StartAndWait(data);
        OpenDialog(p);
        Dialog(p);

        using var thief = Process.Start("notepad.exe")!;
        try
        {
            StealForeground(thief);

            AssertExitsCleanly(p);
            Assert.Equal("focus lost", p.LockLogReasons().First());
        }
        finally
        {
            try { thief.Kill(); } catch (Exception) { }
        }
    }

    [E2EFact]
    public void FocusGrabbedDuringStartup_IsTakenBack_WithoutLocking()
    {
        using var data = new TestDataDir(Password);
        using var thief = Process.Start("notepad.exe")!;
        try
        {
            var w = WaitFor(() => FlaApp.Attach(thief.Id).GetMainWindow(_automation, TimeSpan.FromSeconds(1)), Short, "notepad");
            using var p = CurtainProcess.Start(data);
            Curtain(p);
            Native.ForceForeground(w.Properties.NativeWindowHandle.Value);   // inside the startup grace

            WaitForForeground(p);
            Thread.Sleep(CurtainController.StartupGrace + TimeSpan.FromSeconds(1));
            Assert.False(p.Process.HasExited);
            Assert.Empty(p.LockLogReasons());

            OpenDialog(p);
            SubmitPassword(p, Password);
            AssertExitsCleanly(p);
        }
        finally
        {
            try { thief.Kill(); } catch (Exception) { }
        }
    }

    /// <summary>Makes another app the foreground window (proved by checking it really is).</summary>
    private void StealForeground(Process thief)
    {
        var w = WaitFor(() => FlaApp.Attach(thief.Id).GetMainWindow(_automation, TimeSpan.FromSeconds(1)), Short, "notepad");
        IntPtr hwnd = w.Properties.NativeWindowHandle.Value;
        // During the startup grace the curtain deliberately grabs the foreground back; steal after it.
        Thread.Sleep(CurtainController.StartupGrace + TimeSpan.FromMilliseconds(500));
        WaitFor(() =>
        {
            Native.ForceForeground(hwnd);
            return ForegroundPid() == thief.Id ? "ok" : null;
        }, Short, "notepad to take the foreground");
    }

    // ---------- Shortcuts are blocked ----------

    public static IEnumerable<object[]> BlockedShortcuts() => new[]
    {
        new object[] { new[] { VirtualKeyShort.LWIN } },
        new object[] { new[] { VirtualKeyShort.LWIN, VirtualKeyShort.KEY_D } },
        new object[] { new[] { VirtualKeyShort.LWIN, VirtualKeyShort.TAB } },
        new object[] { new[] { VirtualKeyShort.ALT, VirtualKeyShort.TAB } },
        new object[] { new[] { VirtualKeyShort.ALT, VirtualKeyShort.ESCAPE } },
        new object[] { new[] { VirtualKeyShort.CONTROL, VirtualKeyShort.ESCAPE } },
        new object[] { new[] { VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.ESCAPE } },
        new object[] { new[] { VirtualKeyShort.ALT, VirtualKeyShort.SPACE } },
        new object[] { new[] { VirtualKeyShort.APPS } },
    };

    [E2EFact]
    public void BlockedShortcuts_DoNotEscapeTheCurtain()
    {
        foreach (var combo in BlockedShortcuts().Select(o => (VirtualKeyShort[])o[0]))
        {
            using var data = new TestDataDir(Password);
            using var p = StartAndWait(data);

            Keyboard.TypeSimultaneously(combo);
            Thread.Sleep(1500);

            string name = string.Join("+", combo);
            Assert.False(p.Process.HasExited, $"{name}: curtain exited");
            Assert.True(ForegroundPid() == p.Process.Id, $"{name}: curtain lost the foreground");
            Assert.True(p.LockLogReasons().Length == 0, $"{name}: triggered a lock ({string.Join(",", p.LockLogReasons())})");

            // Still fully usable afterwards.
            OpenDialog(p);
            SubmitPassword(p, Password);
            AssertExitsCleanly(p);
        }
    }

    // ---------- Layout ----------

    [E2EFact]
    public void EveryMonitor_IsCoveredExactly_AndEveryAccessButtonIsOnScreen()
    {
        SetThreadDpiAwarenessContext(new IntPtr(-4));   // per-monitor v2: compare in physical pixels
        using var data = new TestDataDir(Password);
        using var p = StartAndWait(data);

        int monitors = System.Windows.Forms.Screen.AllScreens
            .Select(s => s.Bounds).Distinct().Count();
        var curtains = WaitFor(() =>
        {
            var c = TopWindows(p).Where(w => w.AutomationId.StartsWith("Curtain")).ToArray();
            return c.Length == monitors ? c : null;
        }, Short, $"{monitors} curtain windows");

        foreach (var c in curtains)
        {
            IntPtr hwnd = c.Properties.NativeWindowHandle.Value;
            GetWindowRect(hwnd, out var wr);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            Assert.True(GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref mi));
            Assert.Equal((mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom),
                         (wr.Left, wr.Top, wr.Right, wr.Bottom));

            var btn = c.FindFirstDescendant(cf => cf.ByAutomationId("AccessButton"));
            Assert.NotNull(btn);
            var r = btn.BoundingRectangle;
            Assert.False(btn.IsOffscreen, "Access button reported offscreen");
            foreach (var (x, y) in new[] { (r.Left, r.Top), (r.Right - 1, r.Top), (r.Left, r.Bottom - 1), (r.Right - 1, r.Bottom - 1) })
                Assert.NotEqual(IntPtr.Zero, MonitorFromPoint(new POINT { X = x, Y = y }, 0 /*DEFAULTTONULL*/));
            Assert.True(r.Left >= mi.rcMonitor.Left && r.Right <= mi.rcMonitor.Right &&
                        r.Top >= mi.rcMonitor.Top && r.Bottom <= mi.rcMonitor.Bottom,
                "Access button not inside its own monitor");
        }

        OpenDialog(p);
        var dlgRect = Dialog(p).BoundingRectangle;
        Assert.NotEqual(IntPtr.Zero, MonitorFromPoint(new POINT { X = dlgRect.Left + dlgRect.Width / 2, Y = dlgRect.Top + dlgRect.Height / 2 }, 0));
    }

    // ---------- The attractor is really drawn ----------

    /// <summary>
    /// Counts attractor-coloured pixels (teal to pink, blended on near-black). It ignores the
    /// grey Access button and the orange test-mode banner, which has no blue.
    /// </summary>
    private static (int count, HashSet<int> where) ColourfulPixels(System.Drawing.Bitmap bmp)
    {
        var where = new HashSet<int>();
        for (int y = 0; y < bmp.Height; y += 3)
        for (int x = 0; x < bmp.Width; x += 3)
        {
            var c = bmp.GetPixel(x, y);
            int max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
            if (c.B > 50 && max - min > 30) where.Add(y * bmp.Width + x);
        }
        return (where.Count, where);
    }

    private System.Drawing.Bitmap Snapshot(Window w) => Capture.Element(w).Bitmap;

    [E2EFact]
    public void Attractor_IsActuallyDrawn_OnEveryMonitor()
    {
        using var data = new TestDataDir(Password);
        using var p = StartAndWait(data);
        int monitors = System.Windows.Forms.Screen.AllScreens.Select(s => s.Bounds).Distinct().Count();

        for (int i = 0; i < monitors; i++)
        {
            var curtain = Curtain(p, i);
            int best = 0;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(15) && best < 500)
            {
                using var bmp = Snapshot(curtain);
                best = Math.Max(best, ColourfulPixels(bmp).count);
                if (best < 500) Thread.Sleep(500);
            }
            Assert.True(best >= 500, $"Curtain{i}: attractor not visible ({best} coloured samples).{Environment.NewLine}curtain.log:{Environment.NewLine}{data.CurtainLogText}");
        }
        Assert.Empty(p.LockLogReasons());
    }

    [E2EFact]
    public void Attractor_ChangesOverTime()
    {
        using var data = new TestDataDir(Password);
        using var p = StartAndWait(data);
        var curtain = Curtain(p);

        HashSet<int> first = new();
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(15) && first.Count < 500)
        {
            using var bmp = Snapshot(curtain);
            first = ColourfulPixels(bmp).where;
            if (first.Count < 500) Thread.Sleep(500);
        }
        Assert.True(first.Count >= 500, "attractor never appeared" + Environment.NewLine + data.CurtainLogText);

        Thread.Sleep(TimeSpan.FromSeconds(6));   // the page draws a new attractor every 5 s
        using var later = Snapshot(curtain);
        var second = ColourfulPixels(later).where;
        int changed = first.Count + second.Count - 2 * first.Intersect(second).Count();
        Assert.True(changed > 200, $"attractor did not change after 6 s (changed samples: {changed})");
        Assert.Empty(p.LockLogReasons());
    }

    [E2EFact]
    public void CurtainLog_RecordsSuccessfulLoad_AndNoWebErrors()
    {
        using var data = new TestDataDir(Password);
        using var p = StartAndWait(data);

        WaitFor(() => data.CurtainLogText.Contains("navigation ok") ? "ok" : null, TimeSpan.FromSeconds(15), "navigation ok in curtain.log");
        string log = data.CurtainLogText;
        Assert.DoesNotContain("WebView2 failed", log);
        Assert.DoesNotContain("navigation failed", log);
        Assert.DoesNotContain("browser process failed", log);
        Assert.Contains("TEST mode", log);

        OpenDialog(p);
        SubmitPassword(p, Password);
        AssertExitsCleanly(p);
        Assert.Contains("curtain exited with code 0", data.CurtainLogText);
    }

    // ---------- Process lifetime ----------

    [E2EFact]
    public void SecondCurtain_ExitsImmediately_FirstKeepsGuarding()
    {
        using var data = new TestDataDir(Password);
        using var first = StartAndWait(data);
        using var second = CurtainProcess.Start(data);

        Assert.True(second.WaitForExit(Short));
        Assert.Equal(ExitCodes.AlreadyRunning, second.Process.ExitCode);
        Assert.Equal(TrayAction.None, CurtainExitPolicy.Decide(second.Process.ExitCode));
        Assert.False(first.Process.HasExited);
    }

    [E2EFact]
    public void KilledCurtain_IsTreatedAsAbnormal_SoTheTrayLocks()
    {
        using var data = new TestDataDir(Password);
        using var p = StartAndWait(data);

        p.Process.Kill();
        Assert.True(p.WaitForExit(Short));
        Assert.Equal(TrayAction.LockWorkstation, CurtainExitPolicy.Decide(p.Process.ExitCode));
    }
}
