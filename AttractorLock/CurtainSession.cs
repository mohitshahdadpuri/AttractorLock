using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace AttractorLock;

/// <summary>
/// Wires the curtain together: one <see cref="CurtainWindow"/> per monitor, the keyboard
/// hook, the foreground guard, the hang watchdog, and Windows session events. All of them
/// feed the <see cref="CurtainController"/>, which decides what happens.
/// </summary>
internal sealed class CurtainSession
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SimulatedLockDelay = TimeSpan.FromMilliseconds(1500);

    private readonly Application _app;
    private readonly StoredPassword _password;
    private readonly bool _testMode;
    private readonly CurtainController _controller;
    private readonly List<CurtainWindow> _windows = new();
    private readonly DispatcherTimer _sampler;
    private IReadOnlyList<MonitorInfo> _layout = Array.Empty<MonitorInfo>();
    private Task<CoreWebView2Environment>? _webEnv;
    private KeyboardBlocker? _blocker;
    private HangWatchdog? _watchdog;
    private bool _dialogOpen;
    private bool _shutDown;

    public CurtainSession(Application app, StoredPassword password, ILockAction lockAction, bool testMode)
    {
        _app = app;
        _password = password;
        _testMode = testMode;
        _controller = new CurtainController(lockAction, OnControllerExit, DateTime.UtcNow);
        _sampler = new DispatcherTimer(DispatcherPriority.Normal, app.Dispatcher) { Interval = SampleInterval };
        _sampler.Tick += OnSample;

        if (lockAction is SimulatedLockAction sim)
            sim.Locked += reason => app.Dispatcher.BeginInvoke(() => SimulateWindowsLock(reason));
    }

    public CurtainController Controller => _controller;

    public void Start()
    {
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _app.SessionEnding += OnSessionEnding;

        _blocker = new KeyboardBlocker();
        if (!_blocker.Installed) CurtainLog.Write("keyboard hook could NOT be installed");

        BuildWindows();
        if (_testMode && !_blocker.Installed)
            SetBanner("TEST MODE — WARNING: keyboard hook could not be installed");

        _watchdog = new HangWatchdog(() => _controller.Hung());
        _sampler.Start();
    }

    // ---------- Windows ----------

    private static IReadOnlyList<MonitorInfo> CurrentMonitors()
    {
        var monitors = Forms.Screen.AllScreens.Select(s =>
            new MonitorInfo(new ScreenRect(s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height), s.Primary));
        var v = Forms.SystemInformation.VirtualScreen;
        return ScreenLayout.Compute(monitors, new ScreenRect(v.X, v.Y, v.Width, v.Height));
    }

    private void BuildWindows()
    {
        _layout = CurrentMonitors();
        CurtainLog.Write("monitors: " + string.Join(", ", _layout.Select(m =>
            $"{m.Bounds.Width}x{m.Bounds.Height}@{m.Bounds.X},{m.Bounds.Y}{(m.IsPrimary ? " primary" : "")}")));
        var old = _windows.ToList();
        _windows.Clear();

        int i = 0;
        foreach (var m in _layout)
        {
            var w = new CurtainWindow(m.Bounds, i++, _testMode);
            w.AccessRequested += (s, _) => OnAccessRequested((CurtainWindow)s!);
            w.CloseAttempted += OnCloseAttempted;
            _windows.Add(w);
            w.Show();
            _ = w.InitWebAsync(_webEnv ??= CreateWebEnvironment());
        }

        // New windows are up before old ones go, so the screen is never uncovered.
        foreach (var w in old) w.CloseForExit();

        ActivatePrimary();
    }

    private static Task<CoreWebView2Environment> CreateWebEnvironment()
    {
        // Keep the browser profile in LocalAppData. The default (next to the exe) fails
        // when the exe lives somewhere read-only like Program Files.
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AttractorLock", "WebView2");
        var task = CoreWebView2Environment.CreateAsync(null, dir);
        task.ContinueWith(t => CurtainLog.Write(t.IsFaulted
            ? $"WebView2 environment failed: {t.Exception?.GetBaseException().GetType().Name}: {t.Exception?.GetBaseException().Message}"
            : $"WebView2 environment created ({dir})"), TaskScheduler.Default);
        return task;
    }

    private void ActivatePrimary()
    {
        if (_windows.Count == 0) return;
        _windows[0].Activate();
        Native.ForceForeground(new System.Windows.Interop.WindowInteropHelper(_windows[0]).Handle);
        _windows[0].Focus();
    }

    private void SetBanner(string text)
    {
        foreach (var w in _windows) w.SetBanner(text);
    }

    // ---------- Inputs -> controller ----------

    private void OnSample(object? sender, EventArgs e)
    {
        _watchdog?.Beat();
        var now = DateTime.UtcNow;
        bool ours = Native.ForegroundIsCurrentProcess();
        if (!ours && _controller.InGrace(now)) ActivatePrimary();   // still coming up: keep trying
        _controller.ForegroundSample(ours, now);
    }

    private async void OnAccessRequested(CurtainWindow owner)
    {
        if (_dialogOpen || _controller.State == CurtainState.Exited) return;
        _dialogOpen = true;
        try
        {
            var dlg = new InputDialog("Enter password to unlock:", owner);
            bool? ok = dlg.ShowDialog();
            if (ok != true) { _controller.Cancelled(); return; }

            string typed = dlg.Result ?? "";
            // PBKDF2 off the UI thread so the curtain stays responsive.
            bool correct = await Task.Run(() => _password.Verify(typed));
            _controller.PasswordSubmitted(correct, DateTime.UtcNow);
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private void OnCloseAttempted(object? sender, CancelEventArgs e)
    {
        e.Cancel = _controller.CloseRequested(DateTime.UtcNow);
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        CurtainLog.Write("session switch: " + e.Reason);
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect:
                _controller.SessionLocked();
                break;
            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.SessionLogoff:
                _controller.SessionUnlocked();
                break;
        }
    }

    private void OnSessionEnding(object? sender, SessionEndingCancelEventArgs e) => _controller.SessionEnding();

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        _app.Dispatcher.BeginInvoke(() => RefreshLayout(TimeSpan.FromSeconds(1)));

    /// <summary>Rebuilds the curtains if the monitors changed, then takes the foreground.</summary>
    private void RefreshLayout(TimeSpan grace)
    {
        if (_shutDown) return;
        _controller.ExtendGrace(DateTime.UtcNow, grace);
        if (!ScreenLayout.SameLayout(_layout, CurrentMonitors()))
        {
            CurtainLog.Write("display layout changed: rebuilding curtains");
            BuildWindows();
        }
        else
        {
            ActivatePrimary();
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        CurtainLog.Write("power: " + e.Mode);
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                // Nothing is decided while the machine goes to sleep. If Windows asks for a
                // password on wake it locks now, and SessionLock closes the curtain.
                _controller.ExtendGrace(DateTime.UtcNow, CurtainController.SuspendGrace);
                break;
            case PowerModes.Resume:
                // No password on wake: the curtain is still up. Monitors reconnect and focus
                // bounces around for a moment, so take it back instead of locking for nothing.
                _app.Dispatcher.BeginInvoke(() => RefreshLayout(CurtainController.ResumeGrace));
                break;
        }
    }

    /// <summary>Test mode: show what would have happened, then behave as if Windows had locked.</summary>
    private void SimulateWindowsLock(string reason)
    {
        if (_shutDown) return;
        SetBanner($"TEST MODE — would LOCK Windows now ({reason}). Closing as a real lock would.");
        var t = new DispatcherTimer(DispatcherPriority.Normal, _app.Dispatcher) { Interval = SimulatedLockDelay };
        t.Tick += (_, _) => { t.Stop(); _controller.SessionLocked(); };
        t.Start();
    }

    /// <summary>Unhandled exception: lock first, then quit.</summary>
    public void OnFatalError(Exception ex)
    {
        CurtainLog.Write($"FATAL: {ex}");
        _controller.FatalError(ex.GetType().Name);
    }

    // ---------- Exit ----------

    private void OnControllerExit(int code)
    {
        CurtainLog.Write($"exit requested: code {code}" +
                         (_controller.LastLockReason is { } r ? $" (last lock reason: {r})" : ""));
        switch (code)
        {
            case ExitCodes.Hung:
                // UI thread is stuck, so an orderly shutdown can't run. The lock was already
                // requested; terminate. The tray sees a non-clean exit and locks too.
                Process.GetCurrentProcess().Kill();
                return;
            case ExitCodes.Crashed:
                Environment.Exit(code);
                return;
            default:
                _app.Dispatcher.BeginInvoke(() => Shutdown(code));
                return;
        }
    }

    private void Shutdown(int code)
    {
        if (_shutDown) return;
        _shutDown = true;
        _sampler.Stop();
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _watchdog?.Dispose();
        _blocker?.Dispose();
        foreach (var w in _windows) w.CloseForExit();
        _windows.Clear();
        _app.Shutdown(code);
    }
}
