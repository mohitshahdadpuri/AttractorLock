using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Wpf;

namespace AttractorLock;

/// <summary>
/// The full-screen privacy curtain. Covers the whole virtual desktop, stays on top,
/// renders the attractor, and offers one Access button.
///
/// Safety contract (so you can never get stuck out of your own machine):
///   - Correct password        -> the app exits cleanly, desktop revealed.
///   - Wrong password           -> Windows locks; unlock with your normal password.
///   - App loses focus          -> Windows locks (false locks are harmless, never a loop).
///   - After ANY lock + unlock  -> SessionUnlock fires and the app shuts itself down.
///   - Alt+F4 / close request   -> locks instead of exiting (no free bypass).
/// </summary>
internal sealed class CurtainWindow : Window
{
    private readonly bool _testMode;
    private readonly KeyboardBlocker _blocker = new();
    private bool _ownDialogOpen;     // true while our own password box has focus
    private bool _unlocking;         // true once a correct password is accepted
    private WebView2 _web = null!;

    public CurtainWindow(bool testMode)
    {
        _testMode = testMode;

        // --- Window chrome: borderless, topmost, covering every monitor ---
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        Background = Brushes.Black;
        Cursor = Cursors.Arrow;

        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        Content = BuildLayout();

        Loaded += OnLoaded;
        Deactivated += OnDeactivated;
        Closing += OnClosing;
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    private Grid BuildLayout()
    {
        var grid = new Grid();

        _web = new WebView2();
        grid.Children.Add(_web);

        var accessBtn = new Button
        {
            Content = "Access",
            FontSize = 15,
            Padding = new Thickness(22, 10, 22, 10),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 28, 28),
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0xff, 0xff, 0xff)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xff, 0xff, 0xff)),
            Cursor = Cursors.Hand
        };
        accessBtn.Click += (_, _) => PromptForAccess();
        grid.Children.Add(accessBtn);

        if (_testMode)
        {
            grid.Children.Add(new TextBlock
            {
                Text = "TEST MODE — focus loss and wrong passwords are logged, not locked",
                Foreground = Brushes.Orange,
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 18, 0, 0)
            });
        }
        return grid;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        Activate();
        Focus();
        await _web.EnsureCoreWebView2Async();
        _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        _web.CoreWebView2.Settings.AreDevToolsEnabled = false;
        _web.CoreWebView2.Settings.IsStatusBarEnabled = false;
        _web.NavigateToString(LoadHtml());
    }

    private static string LoadHtml()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceName("AttractorLock.attractor.html")
                   ?? "AttractorLock.attractor.html";
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private void PromptForAccess()
    {
        if (!PasswordStore.IsSet)
        {
            MessageBox.Show(this, "No password has been set yet. Open the tray icon and choose \"Set password\" first.",
                "Access", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _ownDialogOpen = true;
        var dlg = new InputDialog("Enter password to unlock:", this);
        bool? ok = dlg.ShowDialog();
        _ownDialogOpen = false;

        if (ok == true && PasswordStore.Verify(dlg.Result ?? ""))
        {
            _unlocking = true;       // correct -> exit without locking
            CleanShutdown();
        }
        else if (ok == true)
        {
            LockOrLog("wrong password");
        }
        // Cancel: do nothing, stay on the curtain
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_unlocking || _ownDialogOpen) return;   // our own dialog isn't an intruder
        LockOrLog("focus lost");
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Alt+F4 or any close attempt must not be a free way out.
        if (_unlocking) return;      // correct-password path is allowed to close
        e.Cancel = true;
        LockOrLog("close attempted");
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        // After the machine is unlocked (by the real Windows password), the curtain's
        // job is done — shut down so you're never stuck behind it.
        if (e.Reason == SessionSwitchReason.SessionUnlock)
        {
            _unlocking = true;
            Dispatcher.Invoke(CleanShutdown);
        }
    }

    private void LockOrLog(string why)
    {
        if (_testMode)
        {
            Dispatcher.Invoke(() => MessageBox.Show(this,
                $"[TEST] Would lock now. Reason: {why}", "Test mode",
                MessageBoxButton.OK, MessageBoxImage.Warning));
            return;
        }
        Native.LockWorkStation();
        // We do NOT exit here. We wait for SessionUnlock, which then shuts us down.
    }

    private void CleanShutdown()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _blocker.Dispose();
        Application.Current.Shutdown();
    }
}
