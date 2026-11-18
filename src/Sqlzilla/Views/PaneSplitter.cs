using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Sqlzilla.Views;

/// <summary>
/// Drag handle that resizes a column (vertical splitter) or row (<see cref="IsHorizontal"/>) of its parent Grid.
/// Resizes <see cref="Target"/> (defaults to the column/row before the splitter; one after it grows when dragged towards it);
/// double-click restores the original size.
/// </summary>
public sealed class PaneSplitter : Grid
{
    private readonly Border _line;
    private readonly Border _grip;
    private bool _horizontal;
    private bool _dragging;
    private bool _hover;
    private double _start;
    private double _startSize;
    private GridLength? _original;

    /// <summary>Index of the column (or row) to resize; -1 means the one before the splitter.</summary>
    public int Target { get; set; } = -1;

    public bool ShowLine
    {
        get => _line.Visibility == Visibility.Visible;
        set => _line.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>True for a horizontal bar that resizes rows.</summary>
    public bool IsHorizontal
    {
        get => _horizontal;
        set
        {
            _horizontal = value;
            ApplyOrientation();
        }
    }

    public PaneSplitter()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _line = new Border();
        _grip = new Border { CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Children.Add(_line);
        Children.Add(_grip);
        ApplyOrientation();
        ActualThemeChanged += (_, _) => UpdateVisual();

        PointerEntered += (_, _) => { _hover = true; UpdateVisual(); };
        PointerExited += (_, _) => { _hover = false; UpdateVisual(); };
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += (_, e) => EndDrag(e);
        PointerCaptureLost += (_, _) => { _dragging = false; UpdateVisual(); };
        DoubleTapped += OnDoubleTapped;
    }

    private void ApplyOrientation()
    {
        ProtectedCursor = InputSystemCursor.Create(_horizontal ? InputSystemCursorShape.SizeNorthSouth : InputSystemCursorShape.SizeWestEast);
        if (_horizontal)
        {
            Width = double.NaN; Height = 12;
            _line.Width = double.NaN; _line.VerticalAlignment = VerticalAlignment.Center; _line.HorizontalAlignment = HorizontalAlignment.Stretch;
            _grip.Width = 40; _grip.Height = 4;
        }
        else
        {
            Height = double.NaN; Width = 12;
            _line.Height = double.NaN; _line.HorizontalAlignment = HorizontalAlignment.Center; _line.VerticalAlignment = VerticalAlignment.Stretch;
            _grip.Width = 4; _grip.Height = 40;
        }
        UpdateVisual();
    }

    private Grid? Host => Parent as Grid;

    private int Position => _horizontal ? Grid.GetRow(this) : Grid.GetColumn(this);

    private int Index => Target >= 0 ? Target : Position - 1;

    // Rows and columns are wrapped so one code path handles both.
    private (Func<GridLength> Get, Action<GridLength> Set, double Actual, double Min, double Max)? Def(Grid host, int i)
    {
        if (_horizontal)
        {
            if (i < 0 || i >= host.RowDefinitions.Count) return null;
            var r = host.RowDefinitions[i];
            return (() => r.Height, v => r.Height = v, r.ActualHeight, r.MinHeight, r.MaxHeight);
        }
        if (i < 0 || i >= host.ColumnDefinitions.Count) return null;
        var c = host.ColumnDefinitions[i];
        return (() => c.Width, v => c.Width = v, c.ActualWidth, c.MinWidth, c.MaxWidth);
    }

    private int Count(Grid host) => _horizontal ? host.RowDefinitions.Count : host.ColumnDefinitions.Count;

    private Brush Res(string key) => (Brush)Application.Current.Resources[key];

    private void UpdateVisual()
    {
        var active = _dragging || _hover;
        _line.Background = active ? Res("AccentFillColorDefaultBrush") : Res("DividerStrokeColorDefaultBrush");
        if (_horizontal) _line.Height = active ? 2 : 1;
        else _line.Width = active ? 2 : 1;
        _grip.Background = active ? Res("AccentFillColorDefaultBrush") : Res("ControlStrongFillColorDefaultBrush");
    }

    private double Coordinate(PointerRoutedEventArgs e, Grid host)
    {
        var p = e.GetCurrentPoint(host).Position;
        return _horizontal ? p.Y : p.X;
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (Host is not { } host || Def(host, Index) is not { } d) return;
        _original ??= d.Get();
        _dragging = CapturePointer(e.Pointer);
        _start = Coordinate(e, host);
        _startSize = d.Actual;
        UpdateVisual();
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || Host is not { } host || Def(host, Index) is not { } d) return;
        var delta = Coordinate(e, host) - _start;
        if (Index > Position) delta = -delta; // target is after the splitter
        var size = Math.Clamp(_startSize + delta, d.Min, Math.Max(d.Min, MaxSizeFor(host, Index)));
        d.Set(new GridLength(size));
        e.Handled = true;
    }

    /// <summary>Space left once every other column/row keeps at least its minimum (or fixed) size.</summary>
    private double MaxSizeFor(Grid host, int target)
    {
        var others = 0.0;
        var n = Count(host);
        for (var i = 0; i < n; i++)
        {
            if (i == target || Def(host, i) is not { } d) continue;
            others += d.Get().IsStar ? d.Min : d.Actual;
        }
        var spacing = (_horizontal ? host.RowSpacing : host.ColumnSpacing) * Math.Max(0, n - 1);
        var total = _horizontal
            ? host.ActualHeight - host.Padding.Top - host.Padding.Bottom
            : host.ActualWidth - host.Padding.Left - host.Padding.Right;
        return Math.Min(Def(host, target)!.Value.Max, total - others - spacing);
    }

    private void EndDrag(PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        ReleasePointerCapture(e.Pointer);
        _dragging = false;
        UpdateVisual();
        e.Handled = true;
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (Host is { } host && Def(host, Index) is { } d && _original is { } o) d.Set(o);
        e.Handled = true;
    }
}