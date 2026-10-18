using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace AttractorLock;

/// <summary>
/// A minimal always-on-top password box. Its owner is the curtain window for one monitor,
/// so CenterOwner always lands on a real screen. Returns the typed text, or null if cancelled.
/// (Esc is swallowed by the keyboard hook, so Cancel is the button.)
/// In grid mode it also shows the grid the code has to be read from.
/// </summary>
internal sealed class InputDialog : Window
{
    private readonly PasswordBox _box = new() { FontSize = 18, Padding = new Thickness(8), MinWidth = 260 };
    private readonly TextBlock _capsWarning = new()
    {
        Text = "Caps Lock is on",
        Foreground = Brushes.Orange,
        FontSize = 13,
        Margin = new Thickness(0, 8, 0, 0),
        Visibility = Visibility.Collapsed
    };
    public string? Result { get; private set; }

    public InputDialog(string prompt, Window owner, GridChallenge? grid = null)
    {
        Owner = owner;
        Title = "Access";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x18));
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x3a, 0x3a, 0x44));
        BorderThickness = new Thickness(1);
        AutomationProperties.SetAutomationId(this, "AccessDialog");
        AutomationProperties.SetAutomationId(_box, "PasswordBox");

        var label = new TextBlock
        {
            Text = prompt,
            Foreground = Brushes.White,
            FontSize = 15,
            Margin = new Thickness(0, 0, 0, 12)
        };

        var ok = new Button { Content = "OK", MinWidth = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true };
        AutomationProperties.SetAutomationId(ok, "OkButton");
        AutomationProperties.SetAutomationId(cancel, "CancelButton");
        ok.Click += (_, _) => { Result = _box.Password; DialogResult = true; };
        cancel.Click += (_, _) => { DialogResult = false; };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(label);
        if (grid is not null) panel.Children.Add(BuildGrid(grid));
        panel.Children.Add(_box);
        panel.Children.Add(_capsWarning);
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (_, _) => { _box.Focus(); UpdateCapsWarning(); };
        PreviewKeyUp += (_, _) => UpdateCapsWarning();
    }

    private static UIElement BuildGrid(GridChallenge grid)
    {
        var cells = new UniformGrid
        {
            Rows = GridChallenge.Size,
            Columns = GridChallenge.Size,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16)
        };
        AutomationProperties.SetAutomationId(cells, "ChallengeGrid");
        var cellBrush = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x2a));
        for (int r = 0; r < GridChallenge.Size; r++)
            for (int c = 0; c < GridChallenge.Size; c++)
            {
                var text = new TextBlock
                {
                    Text = grid[r, c].ToString(),
                    Foreground = Brushes.White,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 24,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                AutomationProperties.SetAutomationId(text, $"GridCell{r}{c}");
                cells.Children.Add(new Border
                {
                    Width = 44,
                    Height = 44,
                    Margin = new Thickness(2),
                    Background = cellBrush,
                    CornerRadius = new CornerRadius(4),
                    Child = text
                });
            }
        return cells;
    }

    private void UpdateCapsWarning() =>
        _capsWarning.Visibility = Keyboard.IsKeyToggled(Key.CapsLock) ? Visibility.Visible : Visibility.Collapsed;
}
