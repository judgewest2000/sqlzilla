using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Sqlzilla.Import;
using Sqlzilla.Models;
using Sqlzilla.Services;
using Sqlzilla.Sql;
using Windows.Storage.Pickers;

namespace Sqlzilla.Views;

/// <summary>
/// Imports a CSV/delimited, JSON or Excel file into a new or existing table, like SSMS's Import Flat File:
/// pick a file, check the guessed column types (or the mapping onto an existing table), preview, import.
/// </summary>
public sealed class ImportDialog
{
    private const int PreviewRows = 200;
    private const string Skip = "(skip)";

    private static readonly string[] CommonTypes =
    [
        "int", "bigint", "smallint", "tinyint", "bit", "decimal(18,2)", "decimal(38,10)", "float", "money",
        "date", "datetime2", "datetimeoffset", "time", "uniqueidentifier",
        "nvarchar(50)", "nvarchar(100)", "nvarchar(255)", "nvarchar(4000)", "nvarchar(max)", "varchar(255)", "varchar(max)",
    ];

    private readonly ContentDialog _dialog;
    private readonly string _connectionString;
    private readonly string _database;

    private readonly TextBox _path = new() { IsReadOnly = true, PlaceholderText = "Choose a CSV, text, JSON or Excel file", Width = 560 };
    private readonly ComboBox _delimiter = new() { Header = "Delimiter", Width = 130, ItemsSource = new[] { "Comma", "Tab", "Semicolon", "Pipe" }, SelectedIndex = 0 };
    private readonly ComboBox _sheet = new() { Header = "Sheet", MinWidth = 160 };
    private readonly CheckBox _header = new() { Content = "First row has column names", IsChecked = true, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly StackPanel _options = new() { Orientation = Orientation.Horizontal, Spacing = 16, Visibility = Visibility.Collapsed };

    private readonly RadioButton _newTable = new() { Content = "New table", GroupName = "ImportTarget", IsChecked = true };
    private readonly RadioButton _existingTable = new() { Content = "Append to existing table", GroupName = "ImportTarget" };
    private readonly TextBox _schemaBox = new() { Header = "Schema", Text = "dbo", Width = 120 };
    private readonly TextBox _tableBox = new() { Header = "Table name", Width = 300 };
    private readonly ComboBox _existingBox = new() { Header = "Table", Width = 432, PlaceholderText = "Loading tables…", IsEnabled = false };
    private readonly StackPanel _newPanel = new() { Orientation = Orientation.Horizontal, Spacing = 12 };

    private readonly Grid _mapping = new() { ColumnSpacing = 10, RowSpacing = 4 };
    private readonly Border _previewHost = new() { Height = 220 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly ProgressBar _progress = new() { Visibility = Visibility.Collapsed };
    private readonly ScrollViewer _scroller = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

    private readonly List<ColumnRow> _rows = new();
    private List<SchemaObject> _tables = new();
    private string? _file;
    private ImportFormat _format;
    private string? _text;
    private ImportData? _data;
    private List<(string SqlType, bool Nullable)> _inferred = new();
    private CancellationTokenSource? _importCts;
    private bool _busy;
    private bool _suppress;

    private sealed class ColumnRow
    {
        public CheckBox? Include;
        public TextBox? Name;
        public ComboBox? Type;
        public CheckBox? Nullable;
        public ComboBox? Target;
    }

    private ImportDialog(XamlRoot root, ServerConnection server, string database)
    {
        _database = database;
        _connectionString = ConnectionFactory.Build(server, database);

        var browse = new Button { Content = "Browse…", VerticalAlignment = VerticalAlignment.Bottom };
        browse.Click += async (_, _) => await BrowseAsync();
        var fileRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        fileRow.Children.Add(_path);
        fileRow.Children.Add(browse);

        _options.Children.Add(_delimiter);
        _options.Children.Add(_sheet);
        _options.Children.Add(_header);
        _delimiter.SelectionChanged += async (_, _) => await ReparseAsync();
        _sheet.SelectionChanged += async (_, _) => await ReparseAsync();
        _header.Checked += async (_, _) => await ReparseAsync();
        _header.Unchecked += async (_, _) => await ReparseAsync();

        var targetRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        targetRow.Children.Add(_newTable);
        targetRow.Children.Add(_existingTable);
        _newPanel.Children.Add(_schemaBox);
        _newPanel.Children.Add(_tableBox);
        _existingBox.Visibility = Visibility.Collapsed;
        _newTable.Checked += (_, _) => TargetChanged();
        _existingTable.Checked += (_, _) => TargetChanged();
        _existingBox.SelectionChanged += (_, _) => BuildMapping();
        var targetFields = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        targetFields.Children.Add(_newPanel);
        targetFields.Children.Add(_existingBox);

        var panel = new StackPanel { Spacing = 12, Width = Math.Min(1040, Math.Max(600, root.Size.Width - 160)) };
        panel.Children.Add(Section("File"));
        panel.Children.Add(fileRow);
        panel.Children.Add(_options);
        panel.Children.Add(Section($"Destination: {database}"));
        panel.Children.Add(targetRow);
        panel.Children.Add(targetFields);
        panel.Children.Add(Section("Columns"));
        panel.Children.Add(new ScrollViewer { Content = _mapping, MaxHeight = 220, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Auto });
        panel.Children.Add(Section($"Preview (first {PreviewRows} rows)"));
        panel.Children.Add(_previewHost);
        panel.Children.Add(_progress);
        panel.Children.Add(_status);

        _scroller.Content = panel;
        _dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Import data",
            Content = _scroller,
            PrimaryButtonText = "Import",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        _dialog.Resources["ContentDialogMaxWidth"] = 1200.0;
        _dialog.Resources["ContentDialogMaxHeight"] = 1000.0;
        _dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;   // stay open to show the outcome
            await ImportAsync();
        };
        _dialog.Closing += (_, args) =>
        {
            if (!_busy) return;
            args.Cancel = true;
            _importCts?.Cancel();
            SetStatus("Stopping…");
        };
    }

    public static async Task ShowAsync(XamlRoot root, ServerConnection server, string database)
    {
        var dialog = new ImportDialog(root, server, database);
        _ = dialog.LoadTablesAsync();
        await dialog._dialog.ShowAsync();
    }

    private static TextBlock Section(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 0) };

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    private void SetStatus(string text, string brush = "TextFillColorSecondaryBrush")
    {
        _status.Text = text;
        _status.Foreground = Res(brush);
        _status.UpdateLayout();
        _scroller.ChangeView(null, _scroller.ScrollableHeight, null);
    }

    // ------------------------------------------------------------------ loading

    private async Task LoadTablesAsync()
    {
        try
        {
            await using var session = new QuerySession(_connectionString);
            var schema = await Task.Run(() => session.LoadSchemaAsync(CancellationToken.None));
            _tables = schema.Objects.Where(o => o.Kind == SchemaObjectKind.Table).OrderBy(o => o.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
            _existingBox.ItemsSource = _tables.Select(t => t.DisplayName).ToList();
            _existingBox.PlaceholderText = _tables.Count == 0 ? "No tables in this database" : "Choose a table";
            _existingBox.IsEnabled = _tables.Count > 0;
        }
        catch (Exception ex)
        {
            _existingBox.PlaceholderText = "Couldn't load tables";
            SetStatus($"Couldn't load the database's tables: {ex.Message}", "SystemFillColorCriticalBrush");
        }
    }

    private async Task BrowseAsync()
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Handle);
        foreach (var ext in new[] { ".csv", ".txt", ".tsv", ".json", ".jsonl", ".xlsx", ".xls", "*" }) picker.FileTypeFilter.Add(ext);
        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        _file = file.Path;
        _path.Text = file.Path;
        _format = ImportReaders.FormatFromPath(file.Path);
        _tableBox.Text = Path.GetFileNameWithoutExtension(file.Path);
        _text = null;
        _suppress = true;
        try
        {
            SetStatus("Reading file…");
            if (_format == ImportFormat.Delimited)
            {
                _text = await File.ReadAllTextAsync(file.Path);
                _delimiter.SelectedIndex = Array.IndexOf(ImportReaders.Delimiters, ImportReaders.DetectDelimiter(_text));
            }
            else if (_format == ImportFormat.Excel)
            {
                var path = file.Path;
                _sheet.ItemsSource = await Task.Run(() => ImportReaders.ExcelSheets(path));
                _sheet.SelectedIndex = 0;
            }
        }
        catch (Exception ex)
        {
            Fail($"Couldn't read the file: {ex.Message}");
            return;
        }
        finally { _suppress = false; }

        _options.Visibility = _format == ImportFormat.Json ? Visibility.Collapsed : Visibility.Visible;
        _delimiter.Visibility = _format == ImportFormat.Delimited ? Visibility.Visible : Visibility.Collapsed;
        _sheet.Visibility = _format == ImportFormat.Excel ? Visibility.Visible : Visibility.Collapsed;
        await ParseAsync();
    }

    private async Task ReparseAsync()
    {
        if (!_suppress && _file != null && !_busy) await ParseAsync();
    }

    private async Task ParseAsync()
    {
        var (file, format, text) = (_file!, _format, _text);
        var delimiter = ImportReaders.Delimiters[Math.Max(0, _delimiter.SelectedIndex)];
        var sheet = Math.Max(0, _sheet.SelectedIndex);
        var header = _header.IsChecked == true;
        SetStatus("Reading file…");
        _dialog.IsPrimaryButtonEnabled = false;
        try
        {
            var (data, inferred) = await Task.Run(async () =>
            {
                var d = format switch
                {
                    ImportFormat.Delimited => ImportReaders.ReadDelimited(text!, delimiter, header),
                    ImportFormat.Json => ImportReaders.ReadJson(await File.ReadAllTextAsync(file)),
                    _ => ImportReaders.ReadExcel(file, sheet, header),
                };
                var types = d.Columns.Select((_, i) => ImportTypes.Infer(d.Rows.Select(r => r[i]))).ToList();
                return (d, types);
            });
            _data = data;
            _inferred = inferred;
        }
        catch (Exception ex)
        {
            Fail($"Couldn't read the file: {ex.Message}");
            return;
        }

        BuildMapping();
        var columns = _data.Columns.Select(c => new ResultColumn(c, "")).ToList();
        _previewHost.Child = new ResultGrid(ResultSet.FromRows(columns, _data.Rows.Take(PreviewRows)));
        SetStatus($"{_data.Rows.Count:N0} rows, {_data.Columns.Count} columns.");
        _dialog.IsPrimaryButtonEnabled = _data.Columns.Count > 0;
    }

    private void Fail(string message)
    {
        _data = null;
        _mapping.Children.Clear();
        _previewHost.Child = null;
        _dialog.IsPrimaryButtonEnabled = false;
        SetStatus(message, "SystemFillColorCriticalBrush");
    }

    // ------------------------------------------------------------------ mapping

    private void TargetChanged()
    {
        var existing = _existingTable.IsChecked == true;
        _newPanel.Visibility = existing ? Visibility.Collapsed : Visibility.Visible;
        _existingBox.Visibility = existing ? Visibility.Visible : Visibility.Collapsed;
        BuildMapping();
    }

    private SchemaObject? ExistingTable => _existingBox.SelectedIndex >= 0 && _existingBox.SelectedIndex < _tables.Count ? _tables[_existingBox.SelectedIndex] : null;

    private void BuildMapping()
    {
        _mapping.Children.Clear();
        _mapping.RowDefinitions.Clear();
        _mapping.ColumnDefinitions.Clear();
        _rows.Clear();
        if (_data == null) return;

        var existing = _existingTable.IsChecked == true;
        var target = ExistingTable;
        if (existing && target == null)
        {
            _mapping.Children.Add(new TextBlock { Text = "Choose a table to map the file's columns onto.", Foreground = Res("TextFillColorSecondaryBrush") });
            return;
        }

        string[] headers = existing ? ["File column", "", "Table column", "Type", "Sample"] : ["", "File column", "", "Name", "Type", "Null", "Sample"];
        foreach (var _ in headers) _mapping.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _mapping.ColumnDefinitions[^1].Width = new GridLength(1, GridUnitType.Star);
        _mapping.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var c = 0; c < headers.Length; c++)
            Place(new TextBlock { Text = headers[c], FontSize = 12, Foreground = Res("TextFillColorSecondaryBrush") }, 0, c);

        var targets = existing ? target!.Columns.Where(c => !c.IsComputed).ToList() : [];
        var targetNames = new List<string> { Skip };
        targetNames.AddRange(targets.Select(c => c.Name));

        for (var i = 0; i < _data.Columns.Count; i++)
        {
            var name = _data.Columns[i];
            var sample = _data.Rows.Select(r => r[i]).FirstOrDefault(v => v != null) ?? "";
            var sampleText = new TextBlock
            {
                Text = sample.Length > 60 ? sample[..60] + "…" : sample, MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center, Foreground = Res("TextFillColorTertiaryBrush"),
            };
            var source = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 200, TextTrimming = TextTrimming.CharacterEllipsis };
            var arrow = new TextBlock { Text = "→", VerticalAlignment = VerticalAlignment.Center, Foreground = Res("TextFillColorSecondaryBrush") };
            var row = new ColumnRow();
            _mapping.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var r = i + 1;

            if (existing)
            {
                var match = targets.FindIndex(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                var typeText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 120, Foreground = Res("TextFillColorSecondaryBrush") };
                row.Target = new ComboBox { ItemsSource = targetNames, SelectedIndex = match + 1, Width = 240 };
                void ShowType() => typeText.Text = row.Target.SelectedIndex > 0 ? targets[row.Target.SelectedIndex - 1].Describe() : "";
                row.Target.SelectionChanged += (_, _) => ShowType();
                ShowType();
                Place(source, r, 0);
                Place(arrow, r, 1);
                Place(row.Target, r, 2);
                Place(typeText, r, 3);
                Place(sampleText, r, 4);
            }
            else
            {
                row.Include = new CheckBox { IsChecked = true, MinWidth = 0 };
                row.Name = new TextBox { Text = name, Width = 200 };
                var inferredType = _inferred[i].SqlType;
                var types = CommonTypes.Contains(inferredType) ? CommonTypes : [inferredType, .. CommonTypes];
                // Editable ComboBox doesn't draw Text set before it loads, so pick the item instead.
                row.Type = new ComboBox { IsEditable = true, ItemsSource = types, SelectedItem = inferredType, Width = 170 };
                row.Nullable = new CheckBox { IsChecked = _inferred[i].Nullable, MinWidth = 0 };
                Place(row.Include, r, 0);
                Place(source, r, 1);
                Place(arrow, r, 2);
                Place(row.Name, r, 3);
                Place(row.Type, r, 4);
                Place(row.Nullable, r, 5);
                Place(sampleText, r, 6);
            }
            _rows.Add(row);
        }
    }

    private void Place(FrameworkElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        _mapping.Children.Add(element);
    }

    /// <summary>The import to run, or an error message explaining what to fix.</summary>
    private (ImportPlan? Plan, string? Error) BuildPlan()
    {
        if (_data == null) return (null, "Choose a file first.");
        if (_existingTable.IsChecked == true)
        {
            if (ExistingTable is not { } table) return (null, "Choose the table to append to.");
            var targets = table.Columns.Where(c => !c.IsComputed).ToList();
            var columns = _rows
                .Select((r, i) => (Row: r, Source: i))
                .Where(x => x.Row.Target!.SelectedIndex > 0)
                .Select(x =>
                {
                    var t = targets[x.Row.Target!.SelectedIndex - 1];
                    return new ImportColumn(x.Source, t.Name, t.Type, t.IsNullable, t.IsIdentity);
                })
                .ToList();
            if (columns.Count == 0) return (null, "Map at least one file column to a table column.");
            if (columns.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } dup)
                return (null, $"Table column {dup.Key} is mapped more than once.");
            return (new ImportPlan(table.Schema, table.Name, false, columns), null);
        }

        var schema = _schemaBox.Text.Trim();
        var name = _tableBox.Text.Trim();
        if (schema.Length == 0 || name.Length == 0) return (null, "Enter a schema and table name.");
        if (_tables.Any(t => t.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase) && t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return (null, $"{schema}.{name} already exists. Choose another name, or append to the existing table.");
        var newColumns = new List<ImportColumn>();
        for (var i = 0; i < _rows.Count; i++)
        {
            var r = _rows[i];
            if (r.Include!.IsChecked != true) continue;
            var columnName = r.Name!.Text.Trim();
            var type = (string.IsNullOrWhiteSpace(r.Type!.Text) ? r.Type.SelectedItem as string ?? "" : r.Type.Text).Trim();
            if (columnName.Length == 0) return (null, $"File column {_data.Columns[i]} needs a name.");
            if (type.Length == 0) return (null, $"Column {columnName} needs a type.");
            newColumns.Add(new ImportColumn(i, columnName, type, r.Nullable!.IsChecked == true));
        }
        if (newColumns.Count == 0) return (null, "Include at least one column.");
        if (newColumns.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } same)
            return (null, $"Column name {same.Key} is used more than once.");
        return (new ImportPlan(schema, name, true, newColumns), null);
    }

    // ------------------------------------------------------------------ import

    private async Task ImportAsync()
    {
        if (_busy) return;
        var (plan, error) = BuildPlan();
        if (plan == null)
        {
            SetStatus(error!, "SystemFillColorCriticalBrush");
            return;
        }

        var rows = _data!.Rows;
        _busy = true;
        _importCts = new CancellationTokenSource();
        _dialog.IsPrimaryButtonEnabled = false;
        _dialog.CloseButtonText = "Stop";
        _progress.Visibility = Visibility.Visible;
        _progress.Maximum = Math.Max(1, rows.Count);
        _progress.Value = 0;
        SetStatus($"Importing {rows.Count:N0} rows…");
        var progress = new Progress<int>(n =>
        {
            _progress.Value = n;
            if (!_importCts.IsCancellationRequested) SetStatus($"Imported {n:N0} of {rows.Count:N0} rows…");
        });
        var connectionString = _connectionString;
        var ct = _importCts.Token;
        try
        {
            var count = await Task.Run(() => DataImporter.ImportAsync(connectionString, plan, rows, progress, ct));
            ShowSuccess(count, $"{_database}.{plan.Schema}.{plan.Table}", plan.CreateTable);
            _dialog.PrimaryButtonText = "";
            _dialog.CloseButtonText = "Close";
            if (plan.CreateTable)
                _tables.Add(new SchemaObject(plan.Schema, plan.Table, SchemaObjectKind.Table, []));
        }
        catch (OperationCanceledException)
        {
            SetStatus("Stopped. Nothing was imported.", "SystemFillColorCautionBrush");
            Reset();
        }
        catch (Exception ex)
        {
            SetStatus($"{ex.Message}\nNothing was imported.", "SystemFillColorCriticalBrush");
            Reset();
        }
        finally
        {
            _busy = false;
            _progress.Visibility = Visibility.Collapsed;
        }

        void Reset()
        {
            _dialog.IsPrimaryButtonEnabled = true;
            _dialog.CloseButtonText = "Cancel";
        }
    }

    private void ShowSuccess(int count, string target, bool created)
    {
        var panel = new StackPanel { Spacing = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(48, 40, 48, 40) };
        var badge = new Border
        {
            Width = 120, Height = 120, CornerRadius = new CornerRadius(60),
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = Res("SystemFillColorSuccessBrush"),
            Child = new FontIcon { Glyph = "\uE73E", FontSize = 64, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) },
        };
        panel.Children.Add(badge);
        panel.Children.Add(new TextBlock { Text = "🎉 Import complete! 🎉", FontSize = 32, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = $"{count:N0} rows", FontSize = 48, FontWeight = FontWeights.SemiBold, Foreground = Res("SystemFillColorSuccessBrush"), HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = $"{(created ? "imported into new table" : "appended to")} {target}", FontSize = 18, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, IsTextSelectionEnabled = true, HorizontalAlignment = HorizontalAlignment.Center });
        _scroller.Content = panel;
        _dialog.Title = "";
        _dialog.DefaultButton = ContentDialogButton.Close;

        badge.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        var scale = new ScaleTransform { ScaleX = 0.2, ScaleY = 0.2 };
        badge.RenderTransform = scale;
        var ease = new Microsoft.UI.Xaml.Media.Animation.ElasticEase { Oscillations = 2, Springiness = 4, EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut };
        var story = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        foreach (var prop in new[] { "ScaleX", "ScaleY" })
        {
            var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { From = 0.2, To = 1, Duration = TimeSpan.FromMilliseconds(900), EasingFunction = ease };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(anim, scale);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(anim, prop);
            story.Children.Add(anim);
        }
        story.Begin();
    }
}