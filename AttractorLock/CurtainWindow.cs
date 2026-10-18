using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AttractorLock;

/// <summary>
/// One curtain window, covering exactly one monitor (bounds in physical pixels). Shows the
/// attractor and an Access button. This class only draws; all decisions are made by
/// <see cref="CurtainSession"/> and <see cref="CurtainController"/>.
///
/// Layout note: WebView2 is an HwndHost, and HwndHost content always draws over WPF content
/// in the same window (the "airspace" rule). The Access button and banner therefore sit in
/// their own rows and never overlap the browser. In the original single-grid layout they
/// were hidden behind the attractor, so the password could never be entered.
/// </summary>
internal sealed class CurtainWindow : Window
{
    private readonly ScreenRect _bounds;
    private readonly TextBlock _banner;
    private readonly WebView2 _web;
    private IntPtr _hwnd;
    private bool _allowClose;
    private readonly bool _testMode;

    public event EventHandler? AccessRequested;
    /// <summary>Raised for Alt+F4 or any other close attempt. Handlers set Cancel.</summary>
    public event EventHandler<CancelEventArgs>? CloseAttempted;

    public ScreenRect Bounds => _bounds;

    public CurtainWindow(ScreenRect bounds, int index, bool testMode)
    {
        _bounds = bounds;
        _testMode = testMode;

        Title = "AttractorLock";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Topmost = true;
        ShowInTaskbar = false;
        Background = Brushes.Black;
        Cursor = Cursors.Arrow;
        AutomationProperties.SetAutomationId(this, $"Curtain{index}");

        // Rough placement so the window is created on the right monitor. The exact physical
        // bounds are applied (and enforced) in SourceInitialized / WM_WINDOWPOSCHANGING.
        Left = bounds.X; Top = bounds.Y; Width = Math.Max(1, bounds.Width); Height = Math.Max(1, bounds.Height);

        _banner = new TextBlock
        {
            Text = testMode ? "TEST MODE — locks are simulated and logged, never real" : "",
            Foreground = Brushes.Orange,
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 6),
            Visibility = testMode ? Visibility.Visible : Visibility.Collapsed
        };
        AutomationProperties.SetAutomationId(_banner, "Banner");

        _web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.Black };
        Content = BuildLayout();

        SourceInitialized += OnSourceInitialized;
        StateChanged += (_, _) => { if (WindowState != WindowState.Normal) WindowState = WindowState.Normal; };
    }

    private Grid BuildLayout()
    {
        var grid = new Grid { Background = Brushes.Black };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(_banner, 0);
        grid.Children.Add(_banner);

        Grid.SetRow(_web, 1);
        grid.Children.Add(_web);

        var accessBtn = new Button
        {
            Content = "Access",
            FontSize = 15,
            Padding = new Thickness(22, 10, 22, 10),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 14, 28, 24),
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0xff, 0xff, 0xff)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xff, 0xff, 0xff)),
            Cursor = Cursors.Hand
        };
        AutomationProperties.SetAutomationId(accessBtn, "AccessButton");
        accessBtn.Click += (_, _) => AccessRequested?.Invoke(this, EventArgs.Empty);
        Grid.SetRow(accessBtn, 2);
        grid.Children.Add(accessBtn);

        return grid;
    }

    public void SetBanner(string text)
    {
        _banner.Text = text;
        _banner.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Closes for real (exit or display rebuild). Bypasses the close-attempt lock.</summary>
    public void CloseForExit()
    {
        _allowClose = true;
        try { _web.Dispose(); } catch (Exception) { /* closing anyway */ }
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_allowClose) return;
        CloseAttempted?.Invoke(this, e);
    }

    // ---------- Pinning the window to its monitor ----------

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height, Native.SWP_NOACTIVATE);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Native.WM_WINDOWPOSCHANGING when !_allowClose:
                // Whoever moves, resizes or hides us (window menu, DPI change, another app),
                // we stay exactly on our monitor.
                var wp = Marshal.PtrToStructure<Native.WINDOWPOS>(lParam);
                wp.x = _bounds.X; wp.y = _bounds.Y; wp.cx = _bounds.Width; wp.cy = _bounds.Height;
                wp.flags &= ~(Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_HIDEWINDOW);
                Marshal.StructureToPtr(wp, lParam, false);
                break;

            case Native.WM_SYSCOMMAND:
                int sc = (int)(wParam.ToInt64() & 0xFFF0);
                if (sc is Native.SC_MOVE or Native.SC_SIZE or Native.SC_MINIMIZE or Native.SC_MAXIMIZE or Native.SC_RESTORE)
                    handled = true;   // SC_CLOSE still goes through -> Closing -> lock
                break;
        }
        return IntPtr.Zero;
    }

    // ---------- Attractor ----------

    /// <summary>
    /// Only our own embedded page may ever load. NavigateToString shows up as a data: URI,
    /// and WebView2 may do an internal about:blank first. Everything else is refused.
    /// </summary>
    internal static bool IsAllowedNavigation(string? uri) =>
        uri is not null &&
        (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
         uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase));

    /// <summary>Loads the attractor. If WebView2 is missing or broken the curtain stays plain black; it never throws.</summary>
    public async Task InitWebAsync(Task<CoreWebView2Environment> environment)
    {
        string where = (string)AutomationProperties.GetAutomationId(this);
        // Belt and braces: make sure the awaits below resume on this window's UI thread.
        if (System.Threading.SynchronizationContext.Current is not System.Windows.Threading.DispatcherSynchronizationContext)
            System.Threading.SynchronizationContext.SetSynchronizationContext(
                new System.Windows.Threading.DispatcherSynchronizationContext(Dispatcher));
        try
        {
            await _web.EnsureCoreWebView2Async(await environment);
            var core = _web.CoreWebView2;
            CurtainLog.Write($"{where}: WebView2 ready (browser {core.Environment.BrowserVersionString})");
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;   // no Ctrl+P print dialog, Ctrl+F, F5...
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsSwipeNavigationEnabled = false;
            core.NewWindowRequested += (_, a) => a.Handled = true;    // no popups
            core.NavigationStarting += (_, a) =>
            {
                if (IsAllowedNavigation(a.Uri)) return;
                a.Cancel = true;
                CurtainLog.Write($"{where}: blocked navigation to {Shorten(a.Uri)}");
            };
            core.NavigationCompleted += (_, a) =>
            {
                if (a.IsSuccess)
                {
                    CurtainLog.Write($"{where}: navigation ok");
                    return;
                }
                CurtainLog.Write($"{where}: navigation failed ({a.WebErrorStatus})");
                ReportWebProblem($"page failed to load ({a.WebErrorStatus})");
                RetryNavigation(core);
            };
            core.ProcessFailed += (_, a) =>
            {
                CurtainLog.Write($"{where}: browser process failed ({a.ProcessFailedKind})");
                ReportWebProblem($"browser process failed ({a.ProcessFailedKind})");
                RetryNavigation(core);
            };
            core.NavigateToString(LoadHtml());
        }
        catch (Exception ex)
        {
            // Missing runtime, unwritable profile folder, ...: keep the black curtain, but say why.
            CurtainLog.Write($"{where}: WebView2 failed: {ex.GetType().Name}: {ex.Message}");
            ReportWebProblem(ex.GetType().Name);
        }
    }

    private int _retries;

    private void RetryNavigation(CoreWebView2 core)
    {
        if (_retries++ >= 2) return;
        try { core.NavigateToString(LoadHtml()); }
        catch (Exception ex) { CurtainLog.Write($"retry failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    private void ReportWebProblem(string what)
    {
        if (_testMode) SetBanner($"TEST MODE — attractor failed to load: {what} (see curtain.log)");
    }

    private static string Shorten(string? s) => s is null ? "(null)" : s.Length <= 80 ? s : s[..80] + "…";

    private static string LoadHtml()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var s = asm.GetManifestResourceStream("AttractorLock.attractor.html")
                      ?? throw new InvalidOperationException("attractor.html resource missing");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
