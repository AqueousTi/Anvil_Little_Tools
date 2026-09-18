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
    private readonly Border _shell;
    private readonly StackedItemsIcon _icon = new() { Width = 18, Height = 18 };
    private Rect _logicalBounds;

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
        stack.Children.Add(_icon);
        stack.Children.Add(_count);

        _shell = new Border
        {
            Child = stack,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        _shell.PointerPressed += (_, args) =>
        {
            args.Handled = true;
            _owner.ToggleBacklog();
        };
        Content = _shell;
        ApplyTabState(false, false);
    }

    public void SetCount(int count) => _count.Text = count > 0 ? count.ToString() : string.Empty;

    /// <summary>Red tab while a card is dragged over it, white while the drawer is open.</summary>
    public void SetDropHighlight(bool highlighted) => ApplyTabState(highlighted, _drawerOpen);

    public void SetDrawerOpen(bool open)
    {
        _drawerOpen = open;
        ApplyTabState(false, open);
    }

    private bool _drawerOpen;

    private void ApplyTabState(bool dropHighlight, bool drawerOpen)
    {
        if (dropHighlight)
        {
            _shell.Background = new SolidColorBrush(Color.FromRgb(231, 70, 63));
            _shell.BorderBrush = Brushes.White;
            _icon.Inverted = true;
            _count.Foreground = Brushes.White;
            return;
        }

        _shell.Background = drawerOpen
            ? Brushes.White
            : new SolidColorBrush(Color.FromArgb(225, 18, 20, 25));
        _shell.BorderBrush = drawerOpen
            ? new SolidColorBrush(Color.FromArgb(210, 255, 255, 255))
            : new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));
        _icon.Inverted = drawerOpen;
        _count.Foreground = drawerOpen ? Brushes.Black : TodoTheme.SecondaryText;
    }

    /// <summary>
    /// Hit test for the drag gesture. Coordinates come from the drag pointer events
    /// rather than a global cursor query, so this also works under Wayland.
    /// </summary>
    public bool ContainsLogicalPoint(Point point) =>
        IsVisible && _logicalBounds.Contains(point);


    /// <summary>Keeps the tab vertically centred next to the owner window.</summary>
    public void PositionBesideOwner()
    {
        var scaling = _owner.RenderScaling <= 0 ? 1 : _owner.RenderScaling;
        var origin = _owner.LogicalPosition;
        var logicalX = origin.X - Width + 1;
        var logicalY = origin.Y + (_owner.Height - Height) / 2;
        _logicalBounds = new Rect(logicalX, logicalY, Width, Height);
        var x = (int)Math.Round(logicalX * scaling);
        var y = (int)Math.Round(logicalY * scaling);
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
