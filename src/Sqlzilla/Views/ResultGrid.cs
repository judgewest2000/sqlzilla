using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Sqlzilla.Services;
using Windows.ApplicationModel.DataTransfer;
using VirtualKey = Windows.System.VirtualKey;

namespace Sqlzilla.Views;

/// <summary>
/// Virtualised, read-only grid for one result set: sticky header, row numbers, resizable columns, NULLs in grey,
/// extended row selection, Ctrl+C (TSV) and CSV export.
/// </summary>
public sealed partial class ResultGrid : UserControl
{
    private const double RowHeight = 24;
    private const double RowNumberWidth = 56;
    private const double MinColumnWidth = 48;
    private const double MaxAutoColumnWidth = 420;
    private const string RowTemplateXaml =
        "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><StackPanel Orientation=\"Horizontal\" /></DataTemplate>";

    private static readonly FontFamily CellFont = new("Consolas");

    private readonly ResultSet _set;
    private readonly double[] _widths;
    private readonly ListView _list;
    private readonly ScrollViewer _headerScroll;
    private readonly StackPanel _header;
    private ScrollViewer? _listScroll;
    private int _contextColumn = -1;
    private int _contextRow = -1;

    private readonly Brush _gridLine;
    private readonly Brush _nullBrush;
    private readonly Brush _rowNumberBrush;
    private readonly Brush _headerBackground;

    public ResultGrid(ResultSet set)
    {
        _set = set;
        _gridLine = Res("DividerStrokeColorDefaultBrush");
        _nullBrush = Res("TextFillColorTertiaryBrush");
        _rowNumberBrush = Res("TextFillColorSecondaryBrush");
        _headerBackground = Res("SubtleFillColorSecondaryBrush");
        _widths = set.Columns.Select((c, i) => AutoWidth(set, i)).ToArray();

        _header = new StackPanel { Orientation = Orientation.Horizontal, Height = RowHeight + 2, Background = _headerBackground };
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

        _list = new ListView
        {
            ItemsSource = set.Rows,
            SelectionMode = ListViewSelectionMode.Extended,
            ItemTemplate = (DataTemplate)XamlReader.Load(RowTemplateXaml),
            ItemContainerStyle = RowStyle(),
            ItemContainerTransitions = null,
            IsItemClickEnabled = false,
            Padding = new Thickness(0),
        };
        ScrollViewer.SetHorizontalScrollMode(_list, ScrollMode.Enabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Auto);
        ScrollViewer.SetIsHorizontalRailEnabled(_list, true);
        _list.ContainerContentChanging += OnContainerContentChanging;
        _list.Loaded += OnListLoaded;
        _list.KeyDown += OnListKeyDown;
        _list.RightTapped += OnListRightTapped;

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
        Grid.SetRow(_list, 1);
        root.Children.Add(_headerScroll);
        root.Children.Add(_list);
        Content = root;
    }

    public ResultSet ResultSet => _set;

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    private static double AutoWidth(ResultSet set, int column)
    {
        // Measure the few longest sample values for real: proportional fonts make char counts unreliable.
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

    private static readonly TextBlock Ruler = new();

    private static double Measure(string text, Windows.UI.Text.FontWeight weight, FontFamily? font)
    {
        if (font != null) Ruler.FontFamily = font;
        else Ruler.ClearValue(TextBlock.FontFamilyProperty);
        Ruler.Text = text;
        Ruler.FontWeight = weight;
        Ruler.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return Ruler.DesiredSize.Width;
    }

    private static Style RowStyle()
    {
        var style = new Style(typeof(ListViewItem)) { BasedOn = (Style)Application.Current.Resources["DefaultListViewItemStyle"] };
        style.Setters.Add(new Setter(MinHeightProperty, RowHeight));
        style.Setters.Add(new Setter(HeightProperty, RowHeight));
        style.Setters.Add(new Setter(PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(MarginProperty, new Thickness(0)));
        style.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
        style.Setters.Add(new Setter(VerticalContentAlignmentProperty, VerticalAlignment.Stretch));
        style.Setters.Add(new Setter(CornerRadiusProperty, new CornerRadius(0)));
        return style;
    }

    // ------------------------------------------------------------------ header

    private void BuildHeader()
    {
        _header.Children.Clear();
        _header.Children.Add(CreateSelectAllCorner());
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

    private UIElement CreateSelectAllCorner()
    {
        // Excel-style corner: a small triangle in the bottom-right that selects every row.
        var triangle = new Microsoft.UI.Xaml.Shapes.Polygon
        {
            Points = { new Windows.Foundation.Point(10, 0), new Windows.Foundation.Point(10, 10), new Windows.Foundation.Point(0, 10) },
            Fill = _rowNumberBrush,
            Opacity = 0.6,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 4, 4),
        };
        var corner = new Border
        {
            Width = RowNumberWidth,
            BorderBrush = _gridLine,
            BorderThickness = new Thickness(0, 0, 1, 1),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Child = triangle,
        };
        ToolTipService.SetToolTip(corner, "Select all (Ctrl+A)");
        corner.PointerEntered += (_, _) => triangle.Opacity = 1;
        corner.PointerExited += (_, _) => triangle.Opacity = 0.6;
        corner.Tapped += (_, e) =>
        {
            SelectAll();
            _list.Focus(FocusState.Pointer);
            e.Handled = true;
        };
        return corner;
    }

    private UIElement CreateResizeGrip(int column)
    {
        var grip = new Border
        {
            Width = 8,
            Margin = new Thickness(0, 0, -4, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        var dragging = false;
        double startX = 0, startWidth = 0;
        grip.PointerEntered += (_, _) => ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast);
        grip.PointerExited += (_, _) => { if (!dragging) ProtectedCursor = null; };
        grip.PointerPressed += (_, e) =>
        {
            dragging = grip.CapturePointer(e.Pointer);
            startX = e.GetCurrentPoint(this).Position.X;
            startWidth = _widths[column];
            e.Handled = true;
        };
        grip.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            SetColumnWidth(column, Math.Max(MinColumnWidth / 2, startWidth + e.GetCurrentPoint(this).Position.X - startX));
            e.Handled = true;
        };
        void End(PointerRoutedEventArgs e)
        {
            if (!dragging) return;
            dragging = false;
            grip.ReleasePointerCapture(e.Pointer);
            ProtectedCursor = null;
        }
        grip.PointerReleased += (_, e) => End(e);
        grip.PointerCaptureLost += (_, e) => { dragging = false; ProtectedCursor = null; };
        grip.DoubleTapped += (_, e) =>
        {
            SetColumnWidth(column, AutoWidth(_set, column));
            e.Handled = true;
        };
        return grip;
    }

    private void SetColumnWidth(int column, double width)
    {
        _widths[column] = width;
        if (_header.Children[column + 1] is FrameworkElement h) h.Width = width;
        // Realised rows are few (virtualised); update them in place.
        for (var i = 0; i < _set.Rows.Count; i++)
        {
            if (_list.ContainerFromIndex(i) is not ListViewItem { ContentTemplateRoot: StackPanel panel }) continue;
            if (panel.Children.Count > column + 1 && panel.Children[column + 1] is FrameworkElement cell) cell.Width = width;
        }
    }

    // ------------------------------------------------------------------ rows

    private void OnListLoaded(object sender, RoutedEventArgs e)
    {
        if (_listScroll != null) return;
        _listScroll = FindChild<ScrollViewer>(_list);
        if (_listScroll != null)
            _listScroll.ViewChanging += (_, args) => _headerScroll.ChangeView(args.NextView.HorizontalOffset, null, null, true);
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.ItemContainer.ContentTemplateRoot is not StackPanel panel || args.Item is not string?[] row) return;
        args.Handled = true;

        var cellCount = _set.Columns.Count + 1;
        while (panel.Children.Count < cellCount) panel.Children.Add(CreateCell());
        while (panel.Children.Count > cellCount) panel.Children.RemoveAt(panel.Children.Count - 1);

        SetCell((Border)panel.Children[0], RowNumberWidth, (args.ItemIndex + 1).ToString("N0"), -1, isRowNumber: true);
        for (var c = 0; c < _set.Columns.Count; c++)
            SetCell((Border)panel.Children[c + 1], _widths[c], row[c], c, isRowNumber: false);
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

    private void SetCell(Border cell, double width, string? value, int column, bool isRowNumber)
    {
        cell.Width = width;
        cell.Tag = column;
        var text = (TextBlock)cell.Child;
        text.Text = value == null ? "NULL" : value.Length > 400 ? Flatten(value[..400]) + "…" : Flatten(value);
        text.FontStyle = value == null ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal;
        text.TextAlignment = isRowNumber ? TextAlignment.Right : TextAlignment.Left;
        if (value == null) text.Foreground = _nullBrush;
        else if (isRowNumber) text.Foreground = _rowNumberBrush;
        else text.ClearValue(TextBlock.ForegroundProperty);
    }

    private static string Flatten(string s) => s.IndexOfAny(['\r', '\n', '\t']) < 0 ? s : s.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');

    // ------------------------------------------------------------------ selection, copy, export

    private List<string?[]> SelectedRows()
    {
        var rows = new List<string?[]>();
        foreach (var range in _list.SelectedRanges.OrderBy(r => r.FirstIndex))
            for (var i = range.FirstIndex; i <= range.LastIndex; i++) rows.Add(_set.Rows[i]);
        if (rows.Count == 0 && _contextRow >= 0 && _contextRow < _set.Rows.Count) rows.Add(_set.Rows[_contextRow]);
        return rows;
    }

    private static void CopyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    public void CopySelection(bool withHeaders) => CopyText(ResultExport.ToTsv(_set.Columns, SelectedRows(), withHeaders));

    public void SelectAll()
    {
        if (_set.Rows.Count > 0) _list.SelectRange(new ItemIndexRange(0, (uint)_set.Rows.Count));
    }

    private void OnListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!ctrl) return;
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (e.Key == VirtualKey.C) { CopySelection(withHeaders: shift); e.Handled = true; }
        else if (e.Key == VirtualKey.A) { SelectAll(); e.Handled = true; }
    }

    private void OnListRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        _contextColumn = -1;
        _contextRow = -1;
        for (var d = e.OriginalSource as DependencyObject; d != null && d != _list; d = VisualTreeHelper.GetParent(d))
        {
            if (d is Border { Tag: int column } && _contextColumn < 0) _contextColumn = column;
            if (d is ListViewItem item)
            {
                _contextRow = _list.IndexFromContainer(item);
                if (_contextRow >= 0 && !item.IsSelected && _list.SelectedRanges.Count <= 1)
                {
                    _list.SelectedItems.Clear();
                    item.IsSelected = true;
                }
                break;
            }
        }
        e.Handled = true;

        var menu = new MenuFlyout();
        void Add(string text, string glyph, Action action, string? keys = null)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            if (keys != null) item.KeyboardAcceleratorTextOverride = keys;
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        if (_contextRow >= 0 && _contextColumn >= 0)
        {
            var row = _contextRow;
            var column = _contextColumn;
            Add("Copy cell", "\uE8C8", () => CopyText(_set.Rows[row][column] ?? "NULL"));
        }
        Add("Copy", "\uE8C8", () => CopySelection(false), "Ctrl+C");
        Add("Copy with headers", "\uE8C8", () => CopySelection(true), "Ctrl+Shift+C");
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Select all", "\uE8B3", SelectAll, "Ctrl+A");
        Add("Save as CSV…", "\uE74E", () => SaveCsvRequested?.Invoke(this, EventArgs.Empty));
        menu.ShowAt(_list, new FlyoutShowOptions { Position = e.GetPosition(_list) });
    }

    /// <summary>Raised from the context menu; the host shows the file picker (it owns the window handle).</summary>
    public event EventHandler? SaveCsvRequested;
}
