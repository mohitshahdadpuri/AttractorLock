using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WpfApp = System.Windows.Application;

namespace AttractorLock;

/// <summary>
/// Entry point and mode dispatcher.
///
///   (no args)          -> tray listener: sits in the system tray, waits for Ctrl+Break,
///                         and launches the curtain. This is what starts with Windows.
///   --curtain          -> the full-screen privacy curtain (launched by the listener).
///   --curtain --test   -> curtain in test mode (logs instead of locking).
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var options = CommandLineOptions.Parse(args);
        return options.Curtain ? RunCurtain(options) : RunTray();
    }

    // ---------- Curtain process ----------
    private static int RunCurtain(CommandLineOptions options)
    {
        if (options.Test && !string.IsNullOrWhiteSpace(options.LockLogPath))
            CurtainLog.UseDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(options.LockLogPath))!);
        CurtainLog.Write($"curtain starting ({(options.Test ? "TEST mode" : "real mode")})");

        ILockAction lockAction = options.Test
            ? new SimulatedLockAction(options.LockLogPath)
            : new WindowsLockAction();

        // Last line of defence: if anything blows up before the session exists, lock rather
        // than leave the desktop exposed (the tray locks too, on the abnormal exit code).
        CurtainSession? session = null;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (session is not null) session.OnFatalError(e.ExceptionObject as Exception ?? new Exception("unknown"));
            else lockAction.Lock("error during startup");
        };

        // Only one curtain at a time; a second one would fight the first for focus.
        using var mutex = new Mutex(true, @"Local\AttractorLock.Curtain", out bool fresh);
        if (!fresh) { CurtainLog.Write("another curtain is already up; exiting"); return ExitCodes.AlreadyRunning; }

        // Refuse to start, without ever showing a window, unless a password can actually
        // be verified. Otherwise the user could be put behind a curtain nothing opens.
        var store = PasswordStore.CreateDefault();
        var status = store.TryLoad(out var stored);
        if (status != PasswordStatus.Valid || stored is null)
        {
            CurtainLog.Write($"refusing to start: password store is {status} ({store.FilePath})");
            return ExitCodes.ConfigInvalid;
        }

        var app = new WpfApp { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        session = new CurtainSession(app, stored, lockAction, options.Test);
        app.DispatcherUnhandledException += (_, e) =>
        {
            e.Handled = true;
            session.OnFatalError(e.Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();

        // Start inside the running dispatcher, so async continuations (WebView2 setup) come back
        // to the UI thread. Starting before app.Run() left the attractor permanently black.
        app.Startup += (_, _) => session.Start();
        int code = app.Run();
        CurtainLog.Write($"curtain exited with code {code}");
        return code;
    }

    // ---------- Tray listener process ----------
    private static int RunTray()
    {
        // Only one listener at a time.
        using var mutex = new Mutex(true, "AttractorLock.Tray.SingleInstance", out bool fresh);
        if (!fresh) return 0;

        ApplicationConfiguration.Initialize();
        using var ctx = new TrayContext(PasswordStore.CreateDefault());
        System.Windows.Forms.Application.Run(ctx);
        return 0;
    }
}

/// <summary>Owns the tray icon, the hidden message window, and the Ctrl+Break hotkey.</summary>
internal sealed class TrayContext : ApplicationContext
{
    // Ctrl+Break arrives as VK_CANCEL on most keyboards; VK_PAUSE is registered as a fallback.
    private const int HotkeyCancelId = 0xA17;
    private const int HotkeyPauseId = 0xA18;
    private readonly PasswordStore _store;
    private readonly NotifyIcon _tray;
    private readonly HotkeyWindow _msgWindow;
    private readonly SynchronizationContext _ui;
    private Process? _curtain;
    private bool _testMode;

    public TrayContext(PasswordStore store)
    {
        _store = store;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _msgWindow = new HotkeyWindow();
        _msgWindow.HotkeyPressed += (_, id) =>
        {
            if (id is HotkeyCancelId or HotkeyPauseId) LaunchCurtain();
        };

        bool ok1 = Native.RegisterHotKey(_msgWindow.Handle, HotkeyCancelId,
            Native.MOD_CONTROL | Native.MOD_NOREPEAT, Native.VK_CANCEL_U);
        bool ok2 = Native.RegisterHotKey(_msgWindow.Handle, HotkeyPauseId,
            Native.MOD_CONTROL | Native.MOD_NOREPEAT, Native.VK_PAUSE);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Launch now (Ctrl+Break)", null, (_, _) => LaunchCurtain());
        menu.Items.Add("Set password…", null, (_, _) => SetPassword());
        var testItem = new ToolStripMenuItem("Test mode") { CheckOnClick = true };
        testItem.CheckedChanged += (_, _) => _testMode = testItem.Checked;
        menu.Items.Add(testItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Visible = true,
            Text = ok1 || ok2 ? "AttractorLock — press Ctrl+Break to cover the screen"
                              : "AttractorLock — HOTKEY IN USE, use the menu",
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => LaunchCurtain();

        if (_store.Status != PasswordStatus.Valid)
            EnsurePassword();   // first run (or damaged file): make the user pick a password
    }

    /// <summary>Returns true once a verifiable password is stored.</summary>
    private bool EnsurePassword()
    {
        switch (_store.Status)
        {
            case PasswordStatus.Valid:
                return true;
            case PasswordStatus.Unreadable:
                Notify("Couldn't read the saved password file right now (in use by another program?). Try again in a moment.", ToolTipIcon.Warning);
                return false;
            case PasswordStatus.Corrupt:
                MessageBox.Show("The saved password file is damaged, so it could never unlock the curtain. Please set a new password.",
                    "AttractorLock", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                break;
        }
        SetPassword();
        return _store.Status == PasswordStatus.Valid;
    }

    private void LaunchCurtain()
    {
        if (_curtain is { HasExited: false }) return;   // already up
        if (!EnsurePassword()) return;

        // (Assembly.Location is empty in a single-file exe, so it can't be the fallback.)
        string exe = Environment.ProcessPath ?? System.IO.Path.Combine(AppContext.BaseDirectory, "AttractorLock.exe");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        psi.ArgumentList.Add("--curtain");
        bool test = _testMode;
        if (test) psi.ArgumentList.Add("--test");

        // We just received the hotkey, so we are allowed to hand foreground rights on.
        // Without this, the curtain can appear without keyboard focus.
        Native.AllowSetForegroundWindow(Native.ASFW_ANY);

        Process? p;
        try { p = Process.Start(psi); }
        catch (Exception ex) { Notify("Couldn't start the curtain: " + ex.Message, ToolTipIcon.Error); return; }
        if (p is null) { Notify("Couldn't start the curtain.", ToolTipIcon.Error); return; }

        p.EnableRaisingEvents = true;
        p.Exited += (_, _) => OnCurtainExited(p, test);
        _curtain = p;
    }

    /// <summary>Runs on a thread-pool thread. Locks immediately if the curtain died without a clean exit.</summary>
    private void OnCurtainExited(Process p, bool test)
    {
        int code;
        try { code = p.ExitCode; } catch (Exception) { code = -1; }

        TrayAction action = CurtainExitPolicy.Decide(code);
        if (action == TrayAction.LockWorkstation && !test)
            Native.LockWorkStation();   // don't wait for the UI thread

        _ui.Post(_ =>
        {
            if (ReferenceEquals(_curtain, p)) _curtain = null;
            p.Dispose();
            switch (action)
            {
                case TrayAction.NotifyConfigInvalid:
                    Notify("The curtain didn't start: no valid password is saved. Use \"Set password…\".", ToolTipIcon.Warning);
                    break;
                case TrayAction.LockWorkstation when test:
                    Notify($"[TEST] The curtain exited abnormally (code {code}). Windows would be locked now.", ToolTipIcon.Warning);
                    break;
            }
        }, null);
    }

    private void SetPassword()
    {
        using var f = new PasswordSetupForm();
        if (f.ShowDialog() != DialogResult.OK || string.IsNullOrEmpty(f.Password)) return;
        try
        {
            _store.Set(f.Password);
            Notify("Password saved.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Couldn't save the password: " + ex.Message + "\nYour previous password (if any) is unchanged.",
                "AttractorLock", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void Notify(string text, ToolTipIcon icon) =>
        _tray.ShowBalloonTip(5000, "AttractorLock", text, icon);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Native.UnregisterHotKey(_msgWindow.Handle, HotkeyCancelId);
            Native.UnregisterHotKey(_msgWindow.Handle, HotkeyPauseId);
            _tray.Visible = false;
            _tray.Dispose();
            _msgWindow.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Invisible window that receives WM_HOTKEY.</summary>
internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    public event EventHandler<int>? HotkeyPressed;

    public HotkeyWindow() => CreateHandle(new CreateParams());

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY)
            HotkeyPressed?.Invoke(this, (int)m.WParam);
        base.WndProc(ref m);
    }

    public void Dispose() => DestroyHandle();
}

/// <summary>Simple WinForms dialog to set/confirm the access password.</summary>
internal sealed class PasswordSetupForm : Form
{
    private readonly TextBox _p1 = new() { UseSystemPasswordChar = true, Width = 240 };
    private readonly TextBox _p2 = new() { UseSystemPasswordChar = true, Width = 240 };
    public string? Password { get; private set; }

    public PasswordSetupForm()
    {
        Text = "Set access password";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false; MinimizeBox = false;
        ClientSize = new Size(300, 170);
        TopMost = true;

        Controls.Add(new Label { Text = "New password:", Left = 12, Top = 15, AutoSize = true });
        _p1.Left = 12; _p1.Top = 35; Controls.Add(_p1);
        Controls.Add(new Label { Text = "Confirm password:", Left = 12, Top = 70, AutoSize = true });
        _p2.Left = 12; _p2.Top = 90; Controls.Add(_p2);

        var ok = new Button { Text = "Save", Left = 60, Top = 128, DialogResult = DialogResult.None };
        var cancel = new Button { Text = "Cancel", Left = 160, Top = 128, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) =>
        {
            string? error = PasswordPolicy.Validate(_p1.Text, _p2.Text);
            if (error is not null)
            {
                MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Password = _p1.Text;
            DialogResult = DialogResult.OK;
        };
        Controls.Add(ok); Controls.Add(cancel);
        AcceptButton = ok; CancelButton = cancel;
    }
}
