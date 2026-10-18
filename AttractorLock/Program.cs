using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Threading;
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
    private static void Main(string[] args)
    {
        if (Array.Exists(args, a => a.Equals("--curtain", StringComparison.OrdinalIgnoreCase)))
        {
            bool test = Array.Exists(args, a => a.Equals("--test", StringComparison.OrdinalIgnoreCase));
            RunCurtain(test);
        }
        else
        {
            RunTray();
        }
    }

    // ---------- Curtain process ----------
    private static void RunCurtain(bool test)
    {
        var app = new WpfApp { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        var window = new CurtainWindow(test);
        window.Show();
        app.Run();
    }

    // ---------- Tray listener process ----------
    private static void RunTray()
    {
        // Only one listener at a time.
        using var mutex = new Mutex(true, "AttractorLock.Tray.SingleInstance", out bool fresh);
        if (!fresh) return;

        ApplicationConfiguration.Initialize();
        using var ctx = new TrayContext();
        System.Windows.Forms.Application.Run(ctx);
    }
}

/// <summary>Owns the tray icon, the hidden message window, and the Ctrl+Break hotkey.</summary>
internal sealed class TrayContext : ApplicationContext
{
    private const int HotkeyId = 0xA17;
    private readonly NotifyIcon _tray;
    private readonly HotkeyWindow _msgWindow;
    private Process? _curtain;
    private bool _testMode;

    public TrayContext()
    {
        _msgWindow = new HotkeyWindow();
        _msgWindow.HotkeyPressed += (_, _) => LaunchCurtain();

        bool ok = Native.RegisterHotKey(_msgWindow.Handle, HotkeyId,
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
            Text = ok ? "AttractorLock — press Ctrl+Break to cover the screen"
                      : "AttractorLock — HOTKEY IN USE, use the menu",
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => LaunchCurtain();

        if (!PasswordStore.IsSet)
            SetPassword();   // first run: make the user pick a password
    }

    private void LaunchCurtain()
    {
        if (_curtain is { HasExited: false }) return;   // already up
        if (!PasswordStore.IsSet) { SetPassword(); if (!PasswordStore.IsSet) return; }

        string exe = Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location;
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        psi.ArgumentList.Add("--curtain");
        if (_testMode) psi.ArgumentList.Add("--test");
        _curtain = Process.Start(psi);
    }

    private void SetPassword()
    {
        using var f = new PasswordSetupForm();
        if (f.ShowDialog() == DialogResult.OK && !string.IsNullOrEmpty(f.Password))
            PasswordStore.Set(f.Password);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Native.UnregisterHotKey(_msgWindow.Handle, HotkeyId);
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
    public event EventHandler? HotkeyPressed;

    public HotkeyWindow() => CreateHandle(new CreateParams());

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY)
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
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
            if (_p1.Text.Length == 0) { MessageBox.Show("Password can't be empty."); return; }
            if (_p1.Text != _p2.Text) { MessageBox.Show("Passwords don't match."); return; }
            Password = _p1.Text;
            DialogResult = DialogResult.OK;
        };
        Controls.Add(ok); Controls.Add(cancel);
        AcceptButton = ok; CancelButton = cancel;
    }
}
