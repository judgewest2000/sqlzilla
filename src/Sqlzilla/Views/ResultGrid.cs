using System.Collections;
using System.Collections.Specialized;
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
/// frozen row numbers, optional frozen first column, scroll bars outside the cells, resizable columns (including row numbers), NULLs in grey, Ctrl+C (TSV) and CSV export.
/// </summary>
public sealed partial class ResultGrid : UserControl
{
    private const double RowHeight = 24;
    private const double MinColumnWidth = 48;
    private const double MaxAutoColumnWidth = 420;
    private const double GripWidth = 6;
    private const int MeasuredValueLength = 200;
    private const string RowTemplateXaml =
        "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><StackPanel Orientation=\"Horizontal\" /></DataTemplate>";

    private static readonly FontFamily CellFont = new("Consolas");

    private enum DragMode { None, Cells, Rows, Columns }

    private readonly ResultSet _set;
    private readonly RowSource _rows;
    private readonly double[] _widths;
    private readonly int[] _longest;   // per column: length of the longest value already measured (cells use a monospaced font)
    private double _rowNumberWidth;
    private readonly HashSet<int> _manuallySized = new();   // column indexes; -1 is the row-number column
    private readonly TextBlock _truncatedNote;

    /// <summary>App-wide and in-memory only: lasts until Sqlzilla closes.</summary>
    public static bool FreezeFirstColumn
    {
        get => _freezeFirstColumn;
        set
        {
            if (_freezeFirstColumn == value) return;
            _freezeFirstColumn = value;
            FreezeFirstColumnChanged?.Invoke();
        }
    }
    private static bool _freezeFirstColumn;
    public static event Action? FreezeFirstColumnChanged;

    // Columns before _frozen live in the frozen pane beside the row numbers; the rest scroll.
    private int _frozen;

    private readonly StackPanel _header;
    private readonly StackPanel _frozenHeader;    // corner + frozen column headers
    private readonly ScrollViewer _headerScroll;
    private readonly ItemsRepeater _repeater;
    private readonly ItemsRepeater _numbers;      // row numbers (+ frozen cells): scroll vertically with the rows but never sideways
    private readonly ScrollViewer _numberScroll;
    private readonly FrameworkElement _corner;
    private readonly ScrollBar _hBar;
    private readonly ScrollBar _vBar;
    private bool _syncingBars;
    private readonly Grid _content;
    private readonly Border _outline;
    private readonly Border _frozenOutline;
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
        _widths = set.Columns.Select((_, i) => HeaderWidth(set, i)).ToArray();
        _longest = new int[set.Columns.Count];
        FitRows(0, set.Rows.Count, apply: false);
        _rowNumberWidth = RowNumberAutoWidth(set);

        IsTabStop = true;
        UseSystemFocusVisuals = false;

        _frozen = Frozen;
        _header = new StackPanel { Orientation = Orientation.Horizontal, Height = RowHeight + 2, Background = _headerBackground };
        _frozenHeader = new StackPanel { Orientation = Orientation.Horizontal, Height = RowHeight + 2, Background = _headerBackground };
        _corner = CreateCorner();
        _frozenHeader.Children.Add(_corner);
        foreach (var header in new[] { _header, _frozenHeader })
        {
            header.PointerPressed += OnHeaderPointerPressed;
            header.PointerMoved += OnDragPointerMoved;
            header.PointerReleased += OnPointerReleased;
            header.PointerCaptureLost += (_, _) => EndDrag();
            header.RightTapped += OnRightTapped;
        }
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

        _rows = new RowSource(set.Rows);
        _repeater = new ItemsRepeater
        {
            ItemsSource = _rows,
            ItemTemplate = (DataTemplate)XamlReader.Load(RowTemplateXaml),
            Layout = new StackLayout { Orientation = Orientation.Vertical },
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _repeater.ElementPrepared += OnElementPrepared;

        _numbers = new ItemsRepeater
        {
            ItemsSource = _rows,
            ItemTemplate = (DataTemplate)XamlReader.Load(RowTemplateXaml),
            Layout = new StackLayout { Orientation = Orientation.Vertical },
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _numbers.ElementPrepared += OnNumberPrepared;
        _frozenOutline = new Border { BorderBrush = _accentBrush, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        var frozenOverlay = new Canvas { IsHitTestVisible = false };
        frozenOverlay.Children.Add(_frozenOutline);
        var numbersContent = new Grid { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        numbersContent.Children.Add(_numbers);
        numbersContent.Children.Add(frozenOverlay);
        _numberScroll = new ScrollViewer
        {
            Content = numbersContent,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            IsTabStop = false,
        };

        _outline = new Border { BorderBrush = _accentBrush, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
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
            // The scroll bars live outside the cells (_hBar/_vBar), so they never cover the last row or column.
            HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Enabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            ZoomMode = ZoomMode.Disabled,
            IsTabStop = false,
        };
        _scroll.ViewChanging += (_, args) =>
        {
            _headerScroll.ChangeView(args.NextView.HorizontalOffset, null, null, true);
            _numberScroll.ChangeView(null, args.NextView.VerticalOffset, null, true);
        };
        _scroll.ViewChanged += (_, _) =>
        {
            _headerScroll.ChangeView(_scroll.HorizontalOffset, null, null, true);
            _numberScroll.ChangeView(null, _scroll.VerticalOffset, null, true);
            SyncScrollBars();
        };
        _scroll.AddHandler(PointerPressedEvent, new PointerEventHandler(OnCellsPointerPressed), true);
        _scroll.AddHandler(PointerMovedEvent, new PointerEventHandler(OnDragPointerMoved), true);
        _scroll.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnPointerReleased), true);
        _scroll.PointerCaptureLost += (_, _) => EndDrag();
        _scroll.RightTapped += OnRightTapped;
        _numberScroll.AddHandler(PointerPressedEvent, new PointerEventHandler(OnCellsPointerPressed), true);
        _numberScroll.AddHandler(PointerMovedEvent, new PointerEventHandler(OnDragPointerMoved), true);
        _numberScroll.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnPointerReleased), true);
        _numberScroll.PointerCaptureLost += (_, _) => EndDrag();
        _numberScroll.RightTapped += OnRightTapped;

        _hBar = new ScrollBar { Orientation = Orientation.Horizontal, IndicatorMode = ScrollingIndicatorMode.MouseIndicator, SmallChange = 40, Visibility = Visibility.Collapsed };
        _vBar = new ScrollBar { Orientation = Orientation.Vertical, IndicatorMode = ScrollingIndicatorMode.MouseIndicator, SmallChange = RowHeight, Visibility = Visibility.Collapsed, Margin = new Thickness(2, RowHeight + 3, 0, 0) };
        foreach (var bar in new[] { _hBar, _vBar })
        {
            // Always the full-size bar, not the thin indicator that only widens on hover.
            void Expand() => VisualStateManager.GoToState(bar, "ExpandedWithoutAnimation", false);
            bar.Loaded += (_, _) => Expand();
            bar.PointerExited += (_, _) => Expand();
            bar.PointerCaptureLost += (_, _) => Expand();
        }
        _hBar.ValueChanged += (_, e) => { if (!_syncingBars) _scroll.ChangeView(e.NewValue, null, null, true); };
        _vBar.ValueChanged += (_, e) => { if (!_syncingBars) _scroll.ChangeView(null, e.NewValue, null, true); };
        foreach (var dp in new[] { ScrollViewer.ScrollableWidthProperty, ScrollViewer.ScrollableHeightProperty, ScrollViewer.ViewportWidthProperty, ScrollViewer.ViewportHeightProperty })
            _scroll.RegisterPropertyChangedCallback(dp, (_, _) => SyncScrollBars());

        _autoScroll.Tick += OnAutoScrollTick;
        UpdateHBarMargin();
        KeyDown += OnGridKeyDown;
        Loaded += (_, _) =>
        {
            FreezeFirstColumnChanged += ApplyFreeze;
            ApplyFreeze();
        };
        Unloaded += (_, _) =>
        {
            FreezeFirstColumnChanged -= ApplyFreeze;
            _autoScroll.Stop();
        };

        var box = new Grid
        {
            BorderBrush = _gridLine,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Background = Res("LayerFillColorDefaultBrush"),
        };
        box.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        box.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        box.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        box.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_headerScroll, 1);
        Grid.SetRow(_numberScroll, 1);
        Grid.SetRow(_scroll, 1);
        Grid.SetColumn(_scroll, 1);
        box.Children.Add(_frozenHeader);
        box.Children.Add(_headerScroll);
        box.Children.Add(_numberScroll);
        box.Children.Add(_scroll);

        // Outside the box: with hundreds of thousands of rows the scroll viewer stops clipping its content to the
        // viewport, so rows spill to the bottom of the box (whose rounded corners do clip) and would cover the note.
        _truncatedNote = new TextBlock
        {
            Text = $"Showing the first {QuerySession.MaxRowsPerResultSet:N0} rows; the rest were discarded.",
            Margin = new Thickness(8, 4, 8, 0),
            Foreground = Res("SystemFillColorCautionBrush"),
            Visibility = Visibility.Collapsed,
        };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_vBar, 1);
        Grid.SetRow(_hBar, 1);
        Grid.SetRow(_truncatedNote, 2);
        Grid.SetColumnSpan(_truncatedNote, 2);
        root.Children.Add(box);
        root.Children.Add(_vBar);
        root.Children.Add(_hBar);
        root.Children.Add(_truncatedNote);
        // The wheel over the row numbers, header or scroll bars scrolls the cells too.
        root.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnWheelOutsideCells), false);
        Content = root;
        Refresh();
    }

    public ResultSet ResultSet => _set;

    /// <summary>
    /// Shows rows that arrived since the last call (the query may still be running) and returns how many there were.
    /// Columns keep widening to fit every row that arrives, unless resized by hand.
    /// </summary>
    public int Refresh()
    {
        var start = _set.Rows.Count;
        var added = _set.Commit();
        if (added > 0)
        {
            _rows.NotifyAppended(start, added);
            FitRows(start, start + added, apply: true);
            if (!_manuallySized.Contains(-1) && RowNumberAutoWidth(_set) is var rn && rn > _rowNumberWidth) SetColumnWidth(-1, rn);
            UpdateContentSize();
        }
        if (_set.Truncated) _truncatedNote.Visibility = Visibility.Visible;
        return added;
    }

    /// <summary>Raised from the context menu; the host shows the file picker (it owns the window handle).</summary>
    public event EventHandler? SaveCsvRequested;

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    // ------------------------------------------------------------------ sizing

    private static string CellText(string? value) =>
        value == null ? "NULL" : value.Length > MeasuredValueLength ? value[..MeasuredValueLength] : value;

    private static double HeaderWidth(ResultSet set, int column)
    {
        var name = set.Columns[column].Name.Length > 0 ? set.Columns[column].Name : "(No column name)";
        return Math.Clamp(Measure(name, Microsoft.UI.Text.FontWeights.SemiBold, null) + 22, MinColumnWidth, MaxAutoColumnWidth);
    }

    private static double ValueWidth(string value) =>
        Math.Clamp(Measure(value, Microsoft.UI.Text.FontWeights.Normal, CellFont) + 14, MinColumnWidth, MaxAutoColumnWidth);

    /// <summary>
    /// Widens auto-sized columns for rows [from, to). Cells are monospaced, so only a value longer than any seen
    /// so far needs measuring — cheap enough to cover every row, not just the first screenful.
    /// </summary>
    private void FitRows(int from, int to, bool apply)
    {
        for (var c = 0; c < _widths.Length; c++)
        {
            if (_manuallySized.Contains(c)) continue;
            string? longest = null;
            for (var r = from; r < to; r++)
            {
                var value = _set.Rows[r][c];
                var length = value == null ? 4 : Math.Min(value.Length, MeasuredValueLength);
                if (length <= _longest[c]) continue;
                _longest[c] = length;
                longest = CellText(value);
            }
            if (longest == null || ValueWidth(longest) is var w && w <= _widths[c]) continue;
            if (apply) SetColumnWidth(c, w);
            else _widths[c] = w;
        }
    }

    /// <summary>Best fit over every row (double-clicking a column's resize grip).</summary>
    private double AutoWidth(int column)
    {
        var longest = _set.Rows.Select(r => CellText(r[column])).MaxBy(v => v.Length);
        return Math.Max(HeaderWidth(_set, column), longest == null ? 0 : ValueWidth(longest));
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

    private int Frozen => FreezeFirstColumn && _set.Columns.Count > 1 ? 1 : 0;

    private void ApplyFreeze()
    {
        if (Frozen == _frozen) return;
        _frozen = Frozen;
        BuildHeader();
        UpdateContentSize();
        UpdateHBarMargin();
        // Re-prepare every row so cells move between the frozen and scrolling panes.
        _repeater.ItemsSource = null;
        _repeater.ItemsSource = _rows;
        _numbers.ItemsSource = null;
        _numbers.ItemsSource = _rows;
        RefreshSelection();
    }

    private double FrozenColumnsWidth => _widths.Take(_frozen).Sum();

    private void UpdateHBarMargin() => _hBar.Margin = new Thickness(_rowNumberWidth + FrozenColumnsWidth + 1, 2, 0, 0);

    private double TotalWidth => _widths.Skip(_frozen).Sum();

    private void UpdateContentSize()
    {
        _content.Width = TotalWidth;
        _content.Height = Math.Max(RowHeight, _set.Rows.Count * RowHeight);
        _repeater.Width = TotalWidth;
        _numbers.Height = _content.Height;
    }

    private void SyncScrollBars()
    {
        _syncingBars = true;
        Configure(_hBar, _scroll.ScrollableWidth, _scroll.ViewportWidth, _scroll.HorizontalOffset);
        Configure(_vBar, _scroll.ScrollableHeight, _scroll.ViewportHeight, _scroll.VerticalOffset);
        _syncingBars = false;

        static void Configure(ScrollBar bar, double scrollable, double viewport, double offset)
        {
            bar.Visibility = scrollable > 0.5 ? Visibility.Visible : Visibility.Collapsed;
            bar.Maximum = Math.Max(0, scrollable);
            bar.ViewportSize = viewport;
            bar.LargeChange = Math.Max(1, viewport * 0.9);
            bar.Value = offset;
        }
    }

    private void OnWheelOutsideCells(object sender, PointerRoutedEventArgs e)
    {
        var properties = e.GetCurrentPoint(this).Properties;
        var delta = properties.MouseWheelDelta;
        var sideways = properties.IsHorizontalMouseWheel || IsDown(VirtualKey.Shift) || IsWithin(e.OriginalSource, _hBar) || IsWithin(e.OriginalSource, _headerScroll);
        if (sideways)
        {
            var dx = properties.IsHorizontalMouseWheel ? delta : -delta;
            var x = Math.Clamp(_scroll.HorizontalOffset + dx, 0, _scroll.ScrollableWidth);
            if (x == _scroll.HorizontalOffset) return;   // let an outer scroller have it
            _scroll.ChangeView(x, null, null, true);
        }
        else
        {
            var y = Math.Clamp(_scroll.VerticalOffset - delta, 0, _scroll.ScrollableHeight);
            if (y == _scroll.VerticalOffset) return;
            _scroll.ChangeView(null, y, null, true);
        }
        e.Handled = true;
    }

    private static bool IsWithin(object source, DependencyObject ancestor)
    {
        for (var d = source as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
            if (d == ancestor) return true;
        return false;
    }

    /// <summary>Left edge of a scrolling column in the cells' content coordinates.</summary>
    private double ColumnLeft(int column)
    {
        var x = 0.0;
        for (var i = _frozen; i < column; i++) x += _widths[i];
        return x;
    }

    /// <summary>Left edge of a frozen column in the frozen pane (after the row numbers).</summary>
    private double FrozenLeft(int column) => _rowNumberWidth + _widths.Take(column).Sum();

    private Panel HeaderCell(int column) =>
        (Panel)(column < _frozen ? _frozenHeader.Children[1 + column] : _header.Children[column - _frozen]);

    // ------------------------------------------------------------------ header

    private void BuildHeader()
    {
        _header.Children.Clear();
        while (_frozenHeader.Children.Count > 1) _frozenHeader.Children.RemoveAt(1);
        for (var i = 0; i < _set.Columns.Count; i++)
        {
            var column = _set.Columns[i];
            var cell = new Grid { Width = _widths[i], BorderBrush = _gridLine, BorderThickness = new Thickness(0, 0, i == _frozen - 1 ? 2 : 1, 1) };
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
            (i < _frozen ? _frozenHeader : _header).Children.Add(cell);
        }
    }

    private FrameworkElement CreateCorner()
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
            Height = RowHeight + 2,
            BorderBrush = _gridLine,
            BorderThickness = new Thickness(0, 0, 1, 1),
            Background = _headerBackground,
        };
        corner.Children.Add(triangle);
        corner.Children.Add(CreateResizeGrip(-1));
        ToolTipService.SetToolTip(corner, "Select all (Ctrl+A)");
        corner.PointerEntered += (_, _) => triangle.Opacity = 1;
        corner.PointerExited += (_, _) => triangle.Opacity = 0.6;
        corner.PointerPressed += (_, e) =>
        {
            ClaimFocus();
            if (e.GetCurrentPoint(corner).Properties.IsLeftButtonPressed) SelectAll();
            e.Handled = true;
        };
        corner.RightTapped += OnRightTapped;
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
            _manuallySized.Add(column);
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
            SetColumnWidth(column, column < 0 ? RowNumberAutoWidth(_set) : AutoWidth(column));
            e.Handled = true;
        };
        return grip;
    }

    private void SetColumnWidth(int column, double width)
    {
        if (column < 0)
        {
            _rowNumberWidth = width;
            _corner.Width = width;
            foreach (var panel in RealisedRows(_numbers))
                if (panel.Children.Count > 0 && panel.Children[0] is FrameworkElement number) number.Width = width;
        }
        else
        {
            _widths[column] = width;
            HeaderCell(column).Width = width;
            var (repeater, index) = column < _frozen ? (_numbers, 1 + column) : (_repeater, column - _frozen);
            foreach (var panel in RealisedRows(repeater))
                if (panel.Children.Count > index && panel.Children[index] is FrameworkElement cell) cell.Width = width;
        }
        UpdateHBarMargin();
        UpdateContentSize();
        UpdateOutline();
    }

    // ------------------------------------------------------------------ rows

    private static IEnumerable<StackPanel> RealisedRows(ItemsRepeater repeater)
    {
        var count = VisualTreeHelper.GetChildrenCount(repeater);
        for (var i = 0; i < count; i++)
            if (VisualTreeHelper.GetChild(repeater, i) is StackPanel panel && repeater.GetElementIndex(panel) >= 0) yield return panel;
    }

    private void OnElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not StackPanel panel || args.Index < 0 || args.Index >= _set.Rows.Count) return;
        var row = _set.Rows[args.Index];
        panel.Height = RowHeight;

        var cellCount = _set.Columns.Count - _frozen;
        while (panel.Children.Count < cellCount) panel.Children.Add(CreateCell());
        while (panel.Children.Count > cellCount) panel.Children.RemoveAt(panel.Children.Count - 1);

        for (var c = _frozen; c < _set.Columns.Count; c++)
            SetCell((Border)panel.Children[c - _frozen], _widths[c], row[c], isRowNumber: false);
        ApplySelection(panel, args.Index);
    }

    private void OnNumberPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not StackPanel panel || args.Index < 0 || args.Index >= _set.Rows.Count) return;
        panel.Height = RowHeight;
        while (panel.Children.Count < 1 + _frozen) panel.Children.Add(CreateCell());
        while (panel.Children.Count > 1 + _frozen) panel.Children.RemoveAt(panel.Children.Count - 1);
        SetCell((Border)panel.Children[0], _rowNumberWidth, (args.Index + 1).ToString("N0"), isRowNumber: true);
        var row = _set.Rows[args.Index];
        for (var c = 0; c < _frozen; c++)
        {
            var cell = (Border)panel.Children[1 + c];
            SetCell(cell, _widths[c], row[c], isRowNumber: false);
            // A heavier line marks where the frozen columns end, like Excel's freeze panes.
            cell.BorderThickness = new Thickness(0, 0, c == _frozen - 1 ? 2 : 1, 1);
        }
        ApplyNumberSelection(panel, args.Index);
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
        for (var c = _frozen; c < _set.Columns.Count && c - _frozen < panel.Children.Count; c++)
            ((Border)panel.Children[c - _frozen]).Background = _selection.Contains(row, c) ? _selectedBrush : null;
    }

    private void ApplyNumberSelection(StackPanel panel, int row)
    {
        ((Border)panel.Children[0]).Background = _selection.RowTouched(row) ? _headerSelectedBrush : _headerBackground;
        for (var c = 0; c < _frozen && 1 + c < panel.Children.Count; c++)
            ((Border)panel.Children[1 + c]).Background = _selection.Contains(row, c) ? _selectedBrush : null;
    }

    private void RefreshSelection()
    {
        foreach (var panel in RealisedRows(_repeater)) ApplySelection(panel, _repeater.GetElementIndex(panel));
        foreach (var panel in RealisedRows(_numbers)) ApplyNumberSelection(panel, _numbers.GetElementIndex(panel));
        for (var c = 0; c < _set.Columns.Count; c++)
            HeaderCell(c).Background = _selection.ColumnTouched(c) ? _headerSelectedBrush : null;
        UpdateOutline();
    }

    private void UpdateOutline()
    {
        if (_selection.IsEmpty || _set.Rows.Count == 0)
        {
            _outline.Visibility = Visibility.Collapsed;
            _frozenOutline.Visibility = Visibility.Collapsed;
            return;
        }
        var r = _selection.Ranges[^1];
        var top = Math.Max(0, r.Top);
        var bottom = Math.Min(_set.Rows.Count - 1, r.Bottom);
        var height = (bottom - top + 1) * RowHeight + 1;

        // A range across the frozen edge is drawn in two parts, with no border where they meet.
        var inFrozen = r.Left < _frozen;
        var inScrolling = r.Right >= _frozen;
        Place(_frozenOutline, inFrozen, () => FrozenLeft(r.Left), () => FrozenLeft(Math.Min(r.Right, _frozen - 1) + 1), 2, inScrolling ? 0 : 2);
        Place(_outline, inScrolling, () => ColumnLeft(Math.Max(r.Left, _frozen)), () => ColumnLeft(r.Right + 1), inFrozen ? 0 : 2, 2);

        void Place(Border outline, bool show, Func<double> left, Func<double> right, double leftEdge, double rightEdge)
        {
            outline.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) return;
            var x = left();
            Canvas.SetLeft(outline, x - 1);
            Canvas.SetTop(outline, top * RowHeight - 1);
            outline.Width = right() - x + 1;
            outline.Height = height;
            outline.BorderThickness = new Thickness(leftEdge, 2, rightEdge, 2);
        }
    }

    // ------------------------------------------------------------------ mouse selection

    private static bool IsDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>Scrolling column under a content x coordinate (row numbers and frozen columns are outside the content).</summary>
    private int ColumnAt(double x)
    {
        for (var i = _frozen; i < _widths.Length; i++)
        {
            if (x < _widths[i]) return i;
            x -= _widths[i];
        }
        return _widths.Length - 1;
    }

    private (int Row, int Column) HitTest(Point viewport)
    {
        var y = viewport.Y + _scroll.VerticalOffset;
        var row = Math.Clamp((int)Math.Floor(y / RowHeight), 0, Math.Max(0, _set.Rows.Count - 1));
        // Left of the cells are the frozen columns, then the row numbers (-1).
        if (viewport.X >= 0) return (row, ColumnAt(viewport.X + _scroll.HorizontalOffset));
        var x = viewport.X + FrozenColumnsWidth;
        for (var c = 0; c < _frozen; c++)
        {
            if (x >= 0 && x < _widths[c]) return (row, c);
            x -= _widths[c];
        }
        return (row, -1);
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
        ((UIElement)sender).CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnHeaderPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_set.Columns.Count == 0) return;
        var viewport = e.GetCurrentPoint(_scroll).Position;
        var column = HitTest(viewport).Column;
        if (column < 0) return;
        var point = e.GetCurrentPoint(_header);
        ClaimFocus();
        if (point.Properties.IsRightButtonPressed)
        {
            if (!_selection.ColumnTouched(column)) Begin(DragMode.Columns, 0, column, false, false);
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;

        Begin(DragMode.Columns, 0, column, IsDown(VirtualKey.Shift), IsDown(VirtualKey.Control));
        _drag = DragMode.Columns;
        _dragViewportPoint = viewport;
        ((UIElement)sender).CapturePointer(e.Pointer);
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
        _numberScroll.ReleasePointerCaptures();
        _header.ReleasePointerCaptures();
        _frozenHeader.ReleasePointerCaptures();
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
        var left = ColumnLeft(column);
        var right = ColumnLeft(column + 1);
        double? h = column < _frozen ? null
            : left < _scroll.HorizontalOffset ? left
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

    /// <summary>
    /// Read-only view of the committed rows that tells the ItemsRepeater when a batch is appended,
    /// so the grid grows while the query streams.
    /// </summary>
    private sealed partial class RowSource(List<string?[]> rows) : IList, INotifyCollectionChanged
    {
        public event NotifyCollectionChangedEventHandler? CollectionChanged;

        public void NotifyAppended(int start, int count) =>
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, rows.GetRange(start, count), start));

        public object? this[int index]
        {
            get => rows[index];
            set => throw new NotSupportedException();
        }

        public int Count => rows.Count;
        public bool IsReadOnly => true;
        public bool IsFixedSize => false;
        public bool IsSynchronized => false;
        public object SyncRoot => this;
        public bool Contains(object? value) => IndexOf(value) >= 0;
        public int IndexOf(object? value) => value is string?[] row ? rows.IndexOf(row) : -1;
        public void CopyTo(Array array, int index) => ((ICollection)rows).CopyTo(array, index);
        public IEnumerator GetEnumerator() => rows.GetEnumerator();
        public int Add(object? value) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public void Insert(int index, object? value) => throw new NotSupportedException();
        public void Remove(object? value) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();
    }
}