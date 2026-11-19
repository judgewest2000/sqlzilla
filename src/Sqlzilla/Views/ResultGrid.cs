using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Sqlzilla.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using VirtualKey = Windows.System.VirtualKey;

namespace Sqlzilla.Views;

/// <summary>
/// Virtualised, read-only grid for one result set with Excel-style selection: drag a box over cells, click or drag
/// row numbers and column headers, Shift extends, Ctrl adds ranges, the corner selects everything. Sticky header,
/// resizable columns (including row numbers), NULLs in grey, Ctrl+C (TSV) and CSV export.
/// </summary>
public sealed partial class ResultGrid : UserControl
{
    private const double RowHeight = 24;
    private const double MinColumnWidth = 48;
    private const double MaxAutoColumnWidth = 420;
    private const double GripWidth = 6;
    private const string RowTemplateXaml =
        "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><StackPanel Orientation=\"Horizontal\" /></DataTemplate>";

    private static readonly FontFamily CellFont = new("Consolas");

    private enum DragMode { None, Cells, Rows, Columns }

    private readonly ResultSet _set;
    private readonly double[] _widths;
    private double _rowNumberWidth;

    private readonly StackPanel _header;
    private readonly ScrollViewer _headerScroll;
    private readonly ItemsRepeater _repeater;
    private readonly Grid _content;
    private readonly Rectangle _outline;
    private readonly ScrollViewer _scroll;

    private readonly GridSelection _selection = new();
    private (int Row, int Column) _anchor;
    private (int Row, int Column) _active;
    private DragMode _drag;
    private Point _dragViewportPoint;
    private readonly DispatcherTimer _autoScroll = new() { Interval = TimeSpan.FromMilliseconds(40) };

    private readonly Brush _gridLine;
    private readonly Brush _nullBrush;
    private readonly Brush _rowNumberBrush;
    private readonly Brush _headerBackground;
    private readonly Brush _selectedBrush;
    private readonly Brush _headerSelectedBrush;
    private readonly Brush _accentBrush;

    public ResultGrid(ResultSet set)
    {
        _set = set;
        _gridLine = Res("DividerStrokeColorDefaultBrush");
        _nullBrush = Res("TextFillColorTertiaryBrush");
        _rowNumberBrush = Res("TextFillColorSecondaryBrush");
        _headerBackground = Res("SubtleFillColorSecondaryBrush");
        var accent = Application.Current.Resources.TryGetValue("SystemAccentColorLight1", out var a) && a is Windows.UI.Color c
            ? c : Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xA0, 0xE0);
        _selectedBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x48, accent.R, accent.G, accent.B));
        _headerSelectedBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x60, accent.R, accent.G, accent.B));
        _accentBrush = new SolidColorBrush(accent);
        _widths = set.Columns.Select((c, i) => AutoWidth(set, i)).ToArray();
        _rowNumberWidth = RowNumberAutoWidth(set);

        IsTabStop = true;
        UseSystemFocusVisuals = false;

        _header = new StackPanel { Orientation = Orientation.Horizontal, Height = RowHeight + 2, Background = _headerBackground };
        _header.PointerPressed += OnHeaderPointerPressed;
        _header.PointerMoved += OnDragPointerMoved;
        _header.PointerReleased += OnPointerReleased;
        _header.PointerCaptureLost += (_, _) => EndDrag();
        _header.RightTapped += OnRightTapped;
        _headerScroll = new ScrollViewer
        {
            Content = _header,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsTabStop = false,
        };
        BuildHeader();

        _repeater = new ItemsRepeater
        {
            ItemsSource = set.Rows,
            ItemTemplate = (DataTemplate)XamlReader.Load(RowTemplateXaml),
            Layout = new StackLayout { Orientation = Orientation.Vertical },
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _repeater.ElementPrepared += OnElementPrepared;

        _outline = new Rectangle { Stroke = _accentBrush, StrokeThickness = 2, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        var overlay = new Canvas { IsHitTestVisible = false };
        overlay.Children.Add(_outline);
        _content = new Grid
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _content.Children.Add(_repeater);
        _content.Children.Add(overlay);
        UpdateContentSize();

        _scroll = new ScrollViewer
        {
            Content = _content,
            HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Enabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            ZoomMode = ZoomMode.Disabled,
            IsTabStop = false,
        };
        _scroll.ViewChanging += (_, args) => _headerScroll.ChangeView(args.NextView.HorizontalOffset, null, null, true);
        _scroll.ViewChanged += (_, _) => _headerScroll.ChangeView(_scroll.HorizontalOffset, null, null, true);
        _scroll.AddHandler(PointerPressedEvent, new PointerEventHandler(OnCellsPointerPressed), true);
        _scroll.AddHandler(PointerMovedEvent, new PointerEventHandler(OnDragPointerMoved), true);
        _scroll.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnPointerReleased), true);
        _scroll.PointerCaptureLost += (_, _) => EndDrag();
        _scroll.RightTapped += OnRightTapped;

        _autoScroll.Tick += OnAutoScrollTick;
        KeyDown += OnGridKeyDown;
        Unloaded += (_, _) => _autoScroll.Stop();

        var root = new Grid
        {
            BorderBrush = _gridLine,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Background = Res("LayerFillColorDefaultBrush"),
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        if (set.Truncated)
        {
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var note = new TextBlock
            {
                Text = $"Showing the first {set.Rows.Count:N0} rows; the rest were discarded.",
                Margin = new Thickness(8, 4, 8, 4),
                Foreground = Res("SystemFillColorCautionBrush"),
            };
            Grid.SetRow(note, 2);
            root.Children.Add(note);
        }
        Grid.SetRow(_scroll, 1);
        root.Children.Add(_headerScroll);
        root.Children.Add(_scroll);
        Content = root;
    }

    public ResultSet ResultSet => _set;

    /// <summary>Raised from the context menu; the host shows the file picker (it owns the window handle).</summary>
    public event EventHandler? SaveCsvRequested;

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    // ------------------------------------------------------------------ sizing

    private static double AutoWidth(ResultSet set, int column)
    {
        // Measure the few longest sample values for real rather than trusting character counts.
        var candidates = Enumerable.Range(0, Math.Min(set.Rows.Count, 300))
            .Select(r => set.Rows[r][column] ?? "NULL")
            .Select(v => v.Length > 200 ? v[..200] : v)
            .Distinct()
            .OrderByDescending(v => v.Length)
            .Take(5);

        var name = set.Columns[column].Name.Length > 0 ? set.Columns[column].Name : "(No column name)";
        var widest = Measure(name, Microsoft.UI.Text.FontWeights.SemiBold, null) + 8;
        foreach (var value in candidates)
            widest = Math.Max(widest, Measure(value, Microsoft.UI.Text.FontWeights.Normal, CellFont));
        return Math.Clamp(widest + 14, MinColumnWidth, MaxAutoColumnWidth);
    }

    private static double RowNumberAutoWidth(ResultSet set) =>
        Math.Max(40, Measure(Math.Max(1, set.Rows.Count).ToString("N0"), Microsoft.UI.Text.FontWeights.Normal, CellFont) + 18);

    private static readonly TextBlock Ruler = new();

    private static double Measure(string text, Windows.UI.Text.FontWeight weight, FontFamily? font)
    {
        if (font != null) Ruler.FontFamily = font;
        else Ruler.ClearValue(TextBlock.FontFamilyProperty);
        Ruler.Text = text;
        Ruler.FontWeight = weight;
        Ruler.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Ruler.DesiredSize.Width;
    }

    private double TotalWidth => _rowNumberWidth + _widths.Sum();

    private void UpdateContentSize()
    {
        _content.Width = TotalWidth;
        _content.Height = Math.Max(RowHeight, _set.Rows.Count * RowHeight);
        _repeater.Width = TotalWidth;
    }

    private double ColumnLeft(int column)
    {
        var x = _rowNumberWidth;
        for (var i = 0; i < column; i++) x += _widths[i];
        return x;
    }

    // ------------------------------------------------------------------ header

    private void BuildHeader()
    {
        _header.Children.Clear();
        _header.Children.Add(CreateCorner());
        for (var i = 0; i < _set.Columns.Count; i++)
        {
            var column = _set.Columns[i];
            var cell = new Grid { Width = _widths[i], BorderBrush = _gridLine, BorderThickness = new Thickness(0, 0, 1, 1) };
            var text = new TextBlock
            {
                Text = column.Name.Length > 0 ? column.Name : "(No column name)",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontStyle = column.Name.Length > 0 ? Windows.UI.Text.FontStyle.Normal : Windows.UI.Text.FontStyle.Italic,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(6, 0, 6, 0),
            };
            ToolTipService.SetToolTip(cell, $"{text.Text} ({column.TypeName})");
            cell.Children.Add(text);
            cell.Children.Add(CreateResizeGrip(i));
            _header.Children.Add(cell);
        }
    }

    private UIElement CreateCorner()
    {
        // Excel-style corner: a small triangle in the bottom-right; clicking it selects everything.
        var triangle = new Polygon
        {
            Points = { new Point(10, 0), new Point(10, 10), new Point(0, 10) },
            Fill = _rowNumberBrush,
            Opacity = 0.6,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, GripWidth + 2, 4),
            IsHitTestVisible = false,
        };
        var corner = new Grid
        {
            Width = _rowNumberWidth,
            BorderBrush = _gridLine,
            BorderThickness = new Thickness(0, 0, 1, 1),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        corner.Children.Add(triangle);
        corner.Children.Add(CreateResizeGrip(-1));
        ToolTipService.SetToolTip(corner, "Select all (Ctrl+A)");
        corner.PointerEntered += (_, _) => triangle.Opacity = 1;
        corner.PointerExited += (_, _) => triangle.Opacity = 0.6;
        return corner;
    }

    /// <param name="column">Column index, or -1 for the row-number column.</param>
    private UIElement CreateResizeGrip(int column)
    {
        var grip = new Border
        {
            Width = GripWidth,
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        ToolTipService.SetToolTip(grip, "Drag to resize, double-click to fit");
        var dragging = false;
        double startX = 0, startWidth = 0;
        var sizeCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast);
        grip.PointerEntered += (_, _) => ProtectedCursor = sizeCursor;
        grip.PointerExited += (_, _) => { if (!dragging) ProtectedCursor = null; };
        grip.PointerPressed += (_, e) =>
        {
            dragging = grip.CapturePointer(e.Pointer);
            startX = e.GetCurrentPoint(this).Position.X;
            startWidth = column < 0 ? _rowNumberWidth : _widths[column];
            e.Handled = true;
        };
        grip.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            SetColumnWidth(column, Math.Max(24, startWidth + e.GetCurrentPoint(this).Position.X - startX));
            e.Handled = true;
        };
        void End(PointerRoutedEventArgs e)
        {
            if (!dragging) return;
            dragging = false;
            grip.ReleasePointerCapture(e.Pointer);
            ProtectedCursor = null;
            e.Handled = true;
        }
        grip.PointerReleased += (_, e) => End(e);
        grip.PointerCaptureLost += (_, _) => { dragging = false; ProtectedCursor = null; };
        grip.DoubleTapped += (_, e) =>
        {
            SetColumnWidth(column, column < 0 ? RowNumberAutoWidth(_set) : AutoWidth(_set, column));
            e.Handled = true;
        };
        return grip;
    }

    private void SetColumnWidth(int column, double width)
    {
        if (column < 0) _rowNumberWidth = width;
        else _widths[column] = width;
        if (_header.Children[column + 1] is FrameworkElement h) h.Width = width;
        foreach (var panel in RealisedRows())
            if (panel.Children.Count > column + 1 && panel.Children[column + 1] is FrameworkElement cell) cell.Width = width;
        UpdateContentSize();
        UpdateOutline();
    }

    // ------------------------------------------------------------------ rows

    private IEnumerable<StackPanel> RealisedRows()
    {
        var count = VisualTreeHelper.GetChildrenCount(_repeater);
        for (var i = 0; i < count; i++)
            if (VisualTreeHelper.GetChild(_repeater, i) is StackPanel panel && _repeater.GetElementIndex(panel) >= 0) yield return panel;
    }

    private void OnElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not StackPanel panel || args.Index < 0 || args.Index >= _set.Rows.Count) return;
        var row = _set.Rows[args.Index];
        panel.Height = RowHeight;

        var cellCount = _set.Columns.Count + 1;
        while (panel.Children.Count < cellCount) panel.Children.Add(CreateCell());
        while (panel.Children.Count > cellCount) panel.Children.RemoveAt(panel.Children.Count - 1);

        SetCell((Border)panel.Children[0], _rowNumberWidth, (args.Index + 1).ToString("N0"), isRowNumber: true);
        for (var c = 0; c < _set.Columns.Count; c++)
            SetCell((Border)panel.Children[c + 1], _widths[c], row[c], isRowNumber: false);
        ApplySelection(panel, args.Index);
    }

    private Border CreateCell() => new()
    {
        BorderBrush = _gridLine,
        BorderThickness = new Thickness(0, 0, 1, 1),
        Child = new TextBlock
        {
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            MaxLines = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(6, 0, 6, 0),
            FontFamily = CellFont,
            IsTextScaleFactorEnabled = false,
        },
    };

    private void SetCell(Border cell, double width, string? value, bool isRowNumber)
    {
        cell.Width = width;
        var text = (TextBlock)cell.Child;
        text.Text = value == null ? "NULL" : value.Length > 400 ? Flatten(value[..400]) + "…" : Flatten(value);
        text.FontStyle = value == null ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal;
        text.TextAlignment = isRowNumber ? TextAlignment.Right : TextAlignment.Left;
        if (value == null) text.Foreground = _nullBrush;
        else if (isRowNumber) text.Foreground = _rowNumberBrush;
        else text.ClearValue(TextBlock.ForegroundProperty);
    }

    private static string Flatten(string s) => s.IndexOfAny(['\r', '\n', '\t']) < 0 ? s : s.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');

    // ------------------------------------------------------------------ selection visuals

    private void ApplySelection(StackPanel panel, int row)
    {
        ((Border)panel.Children[0]).Background = _selection.RowTouched(row) ? _headerSelectedBrush : _headerBackground;
        for (var c = 0; c < _set.Columns.Count && c + 1 < panel.Children.Count; c++)
            ((Border)panel.Children[c + 1]).Background = _selection.Contains(row, c) ? _selectedBrush : null;
    }

    private void RefreshSelection()
    {
        foreach (var panel in RealisedRows()) ApplySelection(panel, _repeater.GetElementIndex(panel));
        for (var c = 0; c < _set.Columns.Count; c++)
            if (_header.Children[c + 1] is Panel cell) cell.Background = _selection.ColumnTouched(c) ? _headerSelectedBrush : null;
        UpdateOutline();
    }

    private void UpdateOutline()
    {
        if (_selection.IsEmpty || _set.Rows.Count == 0)
        {
            _outline.Visibility = Visibility.Collapsed;
            return;
        }
        var r = _selection.Ranges[^1];
        var top = Math.Max(0, r.Top);
        var bottom = Math.Min(_set.Rows.Count - 1, r.Bottom);
        var left = ColumnLeft(r.Left);
        Canvas.SetLeft(_outline, left - 1);
        Canvas.SetTop(_outline, top * RowHeight - 1);
        _outline.Width = ColumnLeft(r.Right + 1) - left + 1;
        _outline.Height = (bottom - top + 1) * RowHeight + 1;
        _outline.Visibility = Visibility.Visible;
    }

    // ------------------------------------------------------------------ mouse selection

    private static bool IsDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>Column under a content x coordinate; -1 for the row-number column.</summary>
    private int ColumnAt(double x)
    {
        if (x < _rowNumberWidth) return -1;
        x -= _rowNumberWidth;
        for (var i = 0; i < _widths.Length; i++)
        {
            if (x < _widths[i]) return i;
            x -= _widths[i];
        }
        return _widths.Length - 1;
    }

    private (int Row, int Column) HitTest(Point viewport)
    {
        var x = viewport.X + _scroll.HorizontalOffset;
        var y = viewport.Y + _scroll.VerticalOffset;
        var row = Math.Clamp((int)Math.Floor(y / RowHeight), 0, Math.Max(0, _set.Rows.Count - 1));
        return (row, ColumnAt(x));
    }

    private CellRange RangeFor(DragMode mode, (int Row, int Column) a, (int Row, int Column) b) => mode switch
    {
        DragMode.Rows => new CellRange(a.Row, 0, b.Row, _set.Columns.Count - 1),
        DragMode.Columns => new CellRange(0, a.Column, _set.Rows.Count - 1, b.Column),
        _ => new CellRange(a.Row, a.Column, b.Row, b.Column),
    };

    private void Begin(DragMode mode, int row, int column, bool shift, bool ctrl)
    {
        if (shift && !_selection.IsEmpty)
        {
            _active = (row, column);
            _selection.ReplaceLast(RangeFor(mode, _anchor, _active));
        }
        else
        {
            _anchor = _active = (row, column);
            var range = RangeFor(mode, _anchor, _active);
            if (ctrl) _selection.Add(range);
            else _selection.Set(range);
        }
        RefreshSelection();
    }

    private void OnCellsPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_set.Rows.Count == 0 || _set.Columns.Count == 0) return;
        var point = e.GetCurrentPoint(_scroll);
        // Leave the scroll bars alone.
        if (point.Position.X >= _scroll.ViewportWidth || point.Position.Y >= _scroll.ViewportHeight) return;
        ClaimFocus();

        var (row, column) = HitTest(point.Position);
        var mode = column < 0 ? DragMode.Rows : DragMode.Cells;
        column = Math.Max(0, column);
        if (point.Properties.IsRightButtonPressed)
        {
            // Like Excel, right-clicking inside the selection keeps it; elsewhere it selects the clicked cell or row.
            if (!(mode == DragMode.Rows ? _selection.RowTouched(row) : _selection.Contains(row, column))) Begin(mode, row, column, false, false);
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;

        Begin(mode, row, column, IsDown(VirtualKey.Shift), IsDown(VirtualKey.Control));
        _drag = mode;
        _dragViewportPoint = point.Position;
        _scroll.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnHeaderPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_set.Columns.Count == 0) return;
        var point = e.GetCurrentPoint(_header);
        ClaimFocus();
        var column = ColumnAt(point.Position.X);
        if (column < 0)
        {
            if (point.Properties.IsLeftButtonPressed) SelectAll();
            e.Handled = true;
            return;
        }
        if (point.Properties.IsRightButtonPressed)
        {
            if (!_selection.ColumnTouched(column)) Begin(DragMode.Columns, 0, column, false, false);
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;

        Begin(DragMode.Columns, 0, column, IsDown(VirtualKey.Shift), IsDown(VirtualKey.Control));
        _drag = DragMode.Columns;
        _dragViewportPoint = e.GetCurrentPoint(_scroll).Position;
        _header.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnDragPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_drag == DragMode.None) return;
        _dragViewportPoint = e.GetCurrentPoint(_scroll).Position;
        ExtendDrag();
        var outside = _dragViewportPoint.X < 0 || _dragViewportPoint.X > _scroll.ViewportWidth
            || (_drag != DragMode.Columns && (_dragViewportPoint.Y < 0 || _dragViewportPoint.Y > _scroll.ViewportHeight));
        if (outside) _autoScroll.Start();
        else _autoScroll.Stop();
    }

    private void ExtendDrag()
    {
        var (row, column) = HitTest(_dragViewportPoint);
        if (_drag == DragMode.Columns) row = 0;
        column = Math.Max(0, column);
        if ((row, column) == _active) return;
        _active = (row, column);
        _selection.ReplaceLast(RangeFor(_drag, _anchor, _active));
        RefreshSelection();
    }

    private void OnAutoScrollTick(object? sender, object e)
    {
        if (_drag == DragMode.None)
        {
            _autoScroll.Stop();
            return;
        }
        static double Step(double pos, double size) =>
            pos < 0 ? Math.Max(-200, pos - 10) : pos > size ? Math.Min(200, pos - size + 10) : 0;
        var dx = _drag == DragMode.Rows ? 0 : Step(_dragViewportPoint.X, _scroll.ViewportWidth);
        var dy = _drag == DragMode.Columns ? 0 : Step(_dragViewportPoint.Y, _scroll.ViewportHeight);
        if (dx == 0 && dy == 0)
        {
            _autoScroll.Stop();
            return;
        }
        _scroll.ChangeView(_scroll.HorizontalOffset + dx, _scroll.VerticalOffset + dy, null, true);
        ExtendDrag();
    }

    private void EndDrag()
    {
        if (_drag == DragMode.None) return;
        _drag = DragMode.None;
        _autoScroll.Stop();
        _scroll.ReleasePointerCaptures();
        _header.ReleasePointerCaptures();
    }

    // ------------------------------------------------------------------ keyboard

    private void ClaimFocus() => Focus(FocusState.Pointer);

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndDrag();
        // An unhandled release lets the window's root ScrollViewer take focus, so Ctrl+C after a single click would
        // go nowhere (WinUIEdit does the same for the editor).
        if (e.GetCurrentPoint(_scroll).Properties.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.LeftButtonReleased) e.Handled = true;
    }
    private void OnGridKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = IsDown(VirtualKey.Control);
        var shift = IsDown(VirtualKey.Shift);
        if (ctrl && e.Key == VirtualKey.C) { CopySelection(withHeaders: shift); e.Handled = true; return; }
        if (ctrl && e.Key == VirtualKey.A) { SelectAll(); e.Handled = true; return; }
        if (_set.Rows.Count == 0 || _set.Columns.Count == 0) return;

        var lastRow = _set.Rows.Count - 1;
        var lastColumn = _set.Columns.Count - 1;
        var page = Math.Max(1, (int)(_scroll.ViewportHeight / RowHeight) - 1);
        var (row, column) = _selection.IsEmpty ? (0, 0) : _active;
        (int, int)? target = e.Key switch
        {
            VirtualKey.Up => (ctrl ? 0 : row - 1, column),
            VirtualKey.Down => (ctrl ? lastRow : row + 1, column),
            VirtualKey.Left => (row, ctrl ? 0 : column - 1),
            VirtualKey.Right => (row, ctrl ? lastColumn : column + 1),
            VirtualKey.PageUp => (row - page, column),
            VirtualKey.PageDown => (row + page, column),
            VirtualKey.Home => (ctrl ? 0 : row, 0),
            VirtualKey.End => (ctrl ? lastRow : row, lastColumn),
            _ => null,
        };
        if (target is not { } t) return;
        e.Handled = true;
        var next = (Math.Clamp(t.Item1, 0, lastRow), Math.Clamp(t.Item2, 0, lastColumn));
        if (shift && !_selection.IsEmpty)
        {
            _active = next;
            _selection.ReplaceLast(RangeFor(DragMode.Cells, _anchor, _active));
        }
        else
        {
            _anchor = _active = next;
            _selection.Set(RangeFor(DragMode.Cells, _anchor, _active));
        }
        RefreshSelection();
        EnsureVisible(_active.Row, _active.Column);
    }

    private void EnsureVisible(int row, int column)
    {
        var top = row * RowHeight;
        double? v = top < _scroll.VerticalOffset ? top
            : top + RowHeight > _scroll.VerticalOffset + _scroll.ViewportHeight ? top + RowHeight - _scroll.ViewportHeight
            : null;
        var left = ColumnLeft(column) - _rowNumberWidth;
        var right = ColumnLeft(column + 1);
        double? h = left < _scroll.HorizontalOffset ? left
            : right > _scroll.HorizontalOffset + _scroll.ViewportWidth ? right - _scroll.ViewportWidth
            : null;
        if (v != null || h != null) _scroll.ChangeView(h, v, null, true);
    }

    // ------------------------------------------------------------------ copy, export, menu

    public void SelectAll()
    {
        if (_set.Rows.Count == 0 || _set.Columns.Count == 0) return;
        _anchor = (0, 0);
        _active = (_set.Rows.Count - 1, _set.Columns.Count - 1);
        _selection.Set(new CellRange(0, 0, _active.Row, _active.Column));
        RefreshSelection();
    }

    public void CopySelection(bool withHeaders)
    {
        if (_selection.IsEmpty) return;
        var (columns, rows) = _selection.Extract(_set.Columns, _set.Rows);
        var package = new DataPackage();
        package.SetText(ResultExport.ToTsv(columns, rows, withHeaders));
        Clipboard.SetContent(package);
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var menu = new MenuFlyout();
        void Add(string text, string glyph, Action action, string? keys = null, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
            if (keys != null) item.KeyboardAcceleratorTextOverride = keys;
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add("Copy", "\uE8C8", () => CopySelection(false), "Ctrl+C", !_selection.IsEmpty);
        Add("Copy with headers", "\uE8C8", () => CopySelection(true), "Ctrl+Shift+C", !_selection.IsEmpty);
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Select all", "\uE8B3", SelectAll, "Ctrl+A");
        Add("Save as CSV…", "\uE74E", () => SaveCsvRequested?.Invoke(this, EventArgs.Empty));
        menu.ShowAt(this, new FlyoutShowOptions { Position = e.GetPosition(this) });
    }
}
