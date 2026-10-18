using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
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
            Icon = AppIcon.Load(SystemInformation.SmallIconSize),
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
        if (f.ShowDialog() != DialogResult.OK) return;
        try
        {
            if (f.Grid is not null)
            {
                _store.SetGrid(f.Grid);
                Notify("Grid code saved.", ToolTipIcon.Info);
            }
            else if (!string.IsNullOrEmpty(f.Password))
            {
                _store.Set(f.Password);
                Notify("Password saved.", ToolTipIcon.Info);
            }
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

/// <summary>
/// WinForms dialog to choose how the curtain unlocks: a fixed password (typed twice), or a
/// grid code (anchor + path + shift), with a practice grid showing what you'd type.
/// </summary>
internal sealed class PasswordSetupForm : Form
{
    private readonly RadioButton _fixedMode = new() { Text = "Fixed password", AutoSize = true, Checked = true };
    private readonly RadioButton _gridMode = new() { Text = "Grid code (different every time)", AutoSize = true };
    private readonly TextBox _p1 = new() { UseSystemPasswordChar = true, Width = 240 };
    private readonly TextBox _p2 = new() { UseSystemPasswordChar = true, Width = 240 };
    private readonly TextBox _anchor = new() { Width = 40, MaxLength = 1, CharacterCasing = CharacterCasing.Upper };
    private readonly TextBox _path = new() { Width = 160, CharacterCasing = CharacterCasing.Upper };
    private readonly CheckBox _shift = new() { Text = "Shift by one (type the next character: A→B, Z→A, 9→0)", AutoSize = true, Checked = true };
    private readonly Label _sample = new() { AutoSize = true, Font = new Font(FontFamily.GenericMonospace, 14f), Margin = new Padding(3, 6, 3, 6) };
    private readonly Label _answer = new() { AutoSize = true };
    private readonly FlowLayoutPanel _fixedPanel;
    private readonly FlowLayoutPanel _gridPanel;
    private GridChallenge? _sampleGrid;

    public string? Password { get; private set; }
    public GridSecret? Grid { get; private set; }

    public PasswordSetupForm()
    {
        Text = "Set access password";
        Icon = AppIcon.Load(SystemInformation.IconSize);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false; MinimizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(9);
        TopMost = true;

        _fixedPanel = Column(
            new Label { Text = "New password:", AutoSize = true },
            _p1,
            new Label { Text = "Confirm password:", AutoSize = true, Margin = new Padding(3, 10, 3, 0) },
            _p2);

        _gridPanel = Column(
            new Label
            {
                Text = "Each time you click Access, a new grid appears with your anchor in it once.\n" +
                       "Start at the anchor, follow your path (it wraps around the edges) and type\n" +
                       "the character you land on after each move.",
                AutoSize = true
            },
            Row(new Label { Text = "Anchor:", AutoSize = true, Anchor = AnchorStyles.Left }, _anchor,
                new Label { Text = "Path:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(12, 3, 3, 3) }, _path),
            new Label { Text = $"Path moves: U D L R (or arrows), {GridSecret.MinPathLength}–{GridSecret.MaxPathLength} of them, e.g. R D D L", AutoSize = true, ForeColor = SystemColors.GrayText },
            _shift,
            new Label { Text = "Practice grid:", AutoSize = true, Margin = new Padding(3, 10, 3, 0) },
            _sample,
            Row(_answer, NewButton("New sample", (_, _) => { _sampleGrid = null; UpdatePreview(); })));
        _gridPanel.Visible = false;

        var save = NewButton("Save", (_, _) => Save());
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };

        var root = Column(
            Row(_fixedMode, _gridMode),
            _fixedPanel,
            _gridPanel,
            Row(save, cancel));
        Controls.Add(root);
        AcceptButton = save; CancelButton = cancel;

        _fixedMode.CheckedChanged += (_, _) => UpdateMode();
        _anchor.TextChanged += (_, _) => { _sampleGrid = null; UpdatePreview(); };
        _path.TextChanged += (_, _) => UpdatePreview();
        _shift.CheckedChanged += (_, _) => UpdatePreview();
        UpdatePreview();
    }

    private void UpdateMode()
    {
        _fixedPanel.Visible = _fixedMode.Checked;
        _gridPanel.Visible = _gridMode.Checked;
        (_fixedMode.Checked ? _p1 : _anchor).Focus();
    }

    private void UpdatePreview()
    {
        string? error = GridSecret.Validate(_anchor.Text, _path.Text);
        if (error is not null)
        {
            _sample.Text = string.Join("\n", Enumerable.Repeat(string.Join("  ", Enumerable.Repeat("·", GridChallenge.Size)), GridChallenge.Size));
            _answer.Text = _anchor.Text.Length == 0 && _path.Text.Length == 0 ? "Pick an anchor and a path." : error;
            return;
        }
        var secret = new GridSecret(_anchor.Text.Trim()[0], _path.Text, _shift.Checked);
        if (_sampleGrid is null || _sampleGrid.Cells.IndexOf(secret.Anchor) < 0)
            _sampleGrid = GridChallenge.Create(secret.Anchor);
        _sample.Text = _sampleGrid.ToString();
        _answer.Text = "You would type: " + secret.ExpectedAnswer(_sampleGrid);
    }

    private void Save()
    {
        if (_gridMode.Checked)
        {
            string? error = GridSecret.Validate(_anchor.Text, _path.Text);
            if (error is not null)
            {
                MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Grid = new GridSecret(_anchor.Text.Trim()[0], _path.Text, _shift.Checked);
        }
        else
        {
            string? error = PasswordPolicy.Validate(_p1.Text, _p2.Text);
            if (error is not null)
            {
                MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Password = _p1.Text;
        }
        DialogResult = DialogResult.OK;
    }

    private static Button NewButton(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, AutoSize = true };
        b.Click += onClick;
        return b;
    }

    private static FlowLayoutPanel Column(params Control[] children) => Panel(FlowDirection.TopDown, children);
    private static FlowLayoutPanel Row(params Control[] children) => Panel(FlowDirection.LeftToRight, children);

    private static FlowLayoutPanel Panel(FlowDirection direction, Control[] children)
    {
        var p = new FlowLayoutPanel
        {
            FlowDirection = direction,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 3, 0, 3)
        };
        p.Controls.AddRange(children);
        return p;
    }
}
