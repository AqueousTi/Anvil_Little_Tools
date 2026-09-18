using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace LittleTools.Assistant.Todo;

/// <summary>
/// The small tab that sits beside the expanded todo window and opens the backlog
/// drawer. It mirrors the Windows module's 46x66 side tab.
/// </summary>
internal sealed class BacklogTabWindow : Window
{
    private readonly TodoWindow _owner;
    private readonly TextBlock _count;

    public BacklogTabWindow(TodoWindow owner)
    {
        _owner = owner;
        Width = 46;
        Height = 66;
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;

        _count = TodoTheme.Label(string.Empty, 9.5, TodoTheme.SecondaryText);
        _count.HorizontalAlignment = HorizontalAlignment.Center;

        var stack = new StackPanel
        {
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        stack.Children.Add(TodoIcons.StackedItems(TodoTheme.SecondaryText, 15));
        stack.Children.Add(_count);

        var shell = new Border
        {
            Child = stack,
            CornerRadius = new CornerRadius(10),
            Background = TodoTheme.ShellBackground,
            BorderBrush = TodoTheme.ShellBorder,
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        shell.PointerPressed += (_, args) =>
        {
            args.Handled = true;
            _owner.ToggleBacklog();
        };
        Content = shell;
    }

    public void SetCount(int count) => _count.Text = count > 0 ? count.ToString() : string.Empty;

    /// <summary>Keeps the tab vertically centred next to the owner window.</summary>
    public void PositionBesideOwner()
    {
        var scaling = _owner.RenderScaling <= 0 ? 1 : _owner.RenderScaling;
        var origin = _owner.LogicalPosition;
        var x = (int)Math.Round((origin.X - Width + 1) * scaling);
        var y = (int)Math.Round((origin.Y + (_owner.Height - Height) / 2) * scaling);
        try
        {
            Position = new PixelPoint(x, y);
        }
        catch
        {
            // Wayland ignores client positioning; the tab then floats where the
            // compositor decides instead of failing.
        }
    }
}
