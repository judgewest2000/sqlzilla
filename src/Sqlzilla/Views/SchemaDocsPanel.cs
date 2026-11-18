using Sqlzilla.Sql;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace Sqlzilla.Views;

/// <summary>Schema browser beside the editor (F1): objects, columns, parameters and foreign keys, with search and back history.</summary>
public sealed class SchemaDocsPanel : UserControl
{
    private sealed record DocView(SchemaObject? Object = null, string? Column = null, BuiltinFunction? Builtin = null);

    private readonly Stack<DocView> _history = new();
    private readonly Button _back;
    private readonly TextBlock _title;
    private readonly TextBox _search;
    private readonly RichTextBlock _doc;
    private readonly ScrollViewer _scroll;
    private SchemaInfo _schema = SchemaInfo.Empty;
    private DocView _current = new();

    public SchemaDocsPanel()
    {
        _back = IconButton("\uE72B", "Back", 14, (_, _) => Back());
        _back.IsEnabled = false;
        _title = new TextBlock
        {
            Text = "Schema",
            FontSize = 18,
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var header = new Grid { ColumnSpacing = 4 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var home = IconButton("\uE80F", "All objects", 14, (_, _) => Navigate(new DocView()));
        var close = IconButton("\uE8BB", "Hide docs (F1 shows the item under the cursor)", 12, (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty));
        Grid.SetColumn(_title, 1);
        Grid.SetColumn(home, 2);
        Grid.SetColumn(close, 3);
        header.Children.Add(_back);
        header.Children.Add(_title);
        header.Children.Add(home);
        header.Children.Add(close);

        _search = new TextBox { PlaceholderText = "Search tables, views, procedures, columns", IsSpellCheckEnabled = false };
        _search.TextChanged += (_, _) => Render();
        _doc = new RichTextBlock { IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        _scroll = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 12, 0),
            Content = _doc,
        };

        var root = new Grid { RowSpacing = 8, Padding = new Thickness(4, 0, 0, 0) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(_search, 1);
        Grid.SetRow(_scroll, 2);
        root.Children.Add(header);
        root.Children.Add(_search);
        root.Children.Add(_scroll);
        Content = root;
        Render();
    }

    public event EventHandler? CloseRequested;

    public SchemaInfo Schema
    {
        get => _schema;
        set
        {
            _schema = value;
            // Keep showing the same object after a reload if it still exists.
            if (_current.Object is { Kind: not SchemaObjectKind.Local } shown)
                _current = _schema.Find(shown.Schema, shown.Name) is { } again ? _current with { Object = again } : new DocView();
            Render();
        }
    }

    public void ShowTarget(DocTarget target, bool pushHistory) =>
        Navigate(new DocView(target.Object, target.Column, target.Builtin), pushHistory);

    private void Navigate(DocView view, bool pushHistory = true)
    {
        if (view == _current && _search.Text.Length == 0) return;
        if (pushHistory) _history.Push(_current);
        _current = view;
        if (_search.Text.Length > 0) _search.Text = "";
        Render();
    }

    private void Back()
    {
        if (_history.Count == 0) return;
        _current = _history.Pop();
        _search.Text = "";
        Render();
    }

    private static Button IconButton(string glyph, string tip, double size, RoutedEventHandler click)
    {
        var b = new Button
        {
            Style = (Style)Application.Current.Resources["IconButtonStyle"],
            Content = new FontIcon { Glyph = glyph, FontSize = size },
        };
        ToolTipService.SetToolTip(b, tip);
        b.Click += click;
        return b;
    }

    // ------------------------------------------------------------------ rendering

    private void Render()
    {
        _back.IsEnabled = _history.Count > 0;
        _doc.Blocks.Clear();
        var search = _search.Text.Trim();
        if (search.Length > 0) RenderSearch(search);
        else if (_current.Builtin is { } builtin) RenderBuiltin(builtin);
        else if (_current.Object is { } obj) RenderObject(obj, _current.Column);
        else RenderHome();
        _scroll.ChangeView(null, 0, null, true);
    }

    private void RenderHome()
    {
        _title.Text = "Schema";
        if (_schema.Objects.Count == 0)
        {
            Para().Inlines.Add(new Run { Text = "No schema loaded yet. Connect to a database; press F1 in the editor to look up the item under the cursor." });
            return;
        }
        foreach (var group in _schema.Objects.Where(o => o.Kind != SchemaObjectKind.Local).GroupBy(o => GroupName(o.Kind)).OrderBy(g => GroupOrder(g.Key)))
        {
            Section($"{group.Key} ({group.Count()})");
            foreach (var o in group.OrderBy(o => o.DisplayName, StringComparer.OrdinalIgnoreCase))
                Para(2).Inlines.Add(ObjectLink(o));
        }
    }

    private void RenderSearch(string search)
    {
        _title.Text = "Search";
        var objects = _schema.Objects.Where(o => o.Kind != SchemaObjectKind.Local && o.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)).Take(200).ToList();
        var columns = _schema.Objects
            .Where(o => o.Kind != SchemaObjectKind.Local)
            .SelectMany(o => o.Columns.Where(c => c.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).Select(c => (o, c)))
            .Take(200).ToList();
        var builtins = SqlBuiltins.All.Values.Where(b => b.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).Take(50).ToList();
        if (objects.Count + columns.Count + builtins.Count == 0)
        {
            Para().Inlines.Add(Secondary("No matches."));
            return;
        }
        if (objects.Count > 0)
        {
            Section("Objects");
            foreach (var o in objects)
            {
                var p = Para(2);
                p.Inlines.Add(ObjectLink(o));
                p.Inlines.Add(Secondary("  " + o.KindLabel));
            }
        }
        if (columns.Count > 0)
        {
            Section("Columns");
            foreach (var (o, c) in columns)
            {
                var p = Para(2);
                p.Inlines.Add(Link(o.DisplayName, new DocView(o, c.Name)));
                p.Inlines.Add(new Run { Text = "." + c.Name, FontFamily = CodeFont });
                p.Inlines.Add(Secondary("  " + c.Type));
            }
        }
        if (builtins.Count > 0)
        {
            Section("Built-in functions");
            foreach (var b in builtins) Para(2).Inlines.Add(Link(b.Name, new DocView(Builtin: b)));
        }
    }

    private void RenderObject(SchemaObject obj, string? highlight)
    {
        _title.Text = obj.DisplayName;
        Para().Inlines.Add(Secondary(obj.KindLabel));

        if (obj.ReturnType != null)
        {
            var p = Para();
            p.Inlines.Add(Keyword("Returns "));
            p.Inlines.Add(Code(obj.ReturnType));
        }
        if (obj.Parameters.Count > 0)
        {
            Section("Parameters");
            foreach (var param in obj.Parameters)
            {
                var p = Para(2);
                p.Inlines.Add(new Run { Text = param.Name, FontFamily = CodeFont, FontWeight = FontWeights.SemiBold });
                p.Inlines.Add(Code("  " + param.Type));
                if (param.IsOutput) p.Inlines.Add(Keyword("  OUTPUT"));
                if (param.HasDefault) p.Inlines.Add(Secondary("  optional"));
            }
        }

        if (obj.Columns.Count > 0)
        {
            Section($"Columns ({obj.Columns.Count})");
            var outgoing = _schema.ForeignKeys.Where(f => f.From(obj)).ToList();
            foreach (var c in obj.Columns)
            {
                var p = Para(2);
                var isHighlight = c.Name.Equals(highlight, StringComparison.OrdinalIgnoreCase);
                var name = new Run { Text = c.Name, FontFamily = CodeFont, FontWeight = FontWeights.SemiBold };
                if (isHighlight) name.Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
                p.Inlines.Add(name);
                if (c.Type.Length > 0) p.Inlines.Add(Code("  " + c.Type));
                var flags = new List<string>();
                if (c.IsPrimaryKey) flags.Add("PK");
                if (c.IsIdentity) flags.Add("identity");
                if (c.IsComputed) flags.Add("computed");
                if (c.Type.Length > 0 && !c.IsNullable) flags.Add("not null");
                if (flags.Count > 0) p.Inlines.Add(Secondary("  " + string.Join(", ", flags)));
                foreach (var fk in outgoing)
                {
                    var at = fk.FromColumns.ToList().FindIndex(x => x.Equals(c.Name, StringComparison.OrdinalIgnoreCase));
                    if (at < 0 || FindTable(fk.ToSchema, fk.ToTable) is not { } target) continue;
                    p.Inlines.Add(Secondary("  → "));
                    p.Inlines.Add(Link($"{target.DisplayName}.{fk.ToColumns[at]}", new DocView(target, fk.ToColumns[at])));
                }
            }
        }
        else if (obj.Kind == SchemaObjectKind.Synonym)
        {
            Para().Inlines.Add(Secondary("Synonym: columns aren't known."));
        }

        var incoming = _schema.ForeignKeys.Where(f => f.To(obj)).ToList();
        if (incoming.Count > 0)
        {
            Section("Referenced by");
            foreach (var fk in incoming)
            {
                if (FindTable(fk.FromSchema, fk.FromTable) is not { } source) continue;
                var p = Para(2);
                p.Inlines.Add(ObjectLink(source));
                p.Inlines.Add(Secondary($"  ({string.Join(", ", fk.FromColumns)})"));
            }
        }
    }

    private void RenderBuiltin(BuiltinFunction f)
    {
        _title.Text = f.Name;
        Para().Inlines.Add(Secondary("built-in function"));
        Para().Inlines.Add(Code($"{f.Name}({string.Join(", ", f.Parameters)})"));
        var r = Para();
        r.Inlines.Add(Keyword("Returns "));
        r.Inlines.Add(Code(f.Returns));
        Para().Inlines.Add(new Run { Text = f.Description });
    }

    private SchemaObject? FindTable(string schema, string name) =>
        _schema.Objects.FirstOrDefault(o => o.Kind != SchemaObjectKind.Local
                                            && o.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase)
                                            && o.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string GroupName(SchemaObjectKind kind) => kind switch
    {
        SchemaObjectKind.Table => "Tables",
        SchemaObjectKind.View => "Views",
        SchemaObjectKind.Procedure => "Stored procedures",
        SchemaObjectKind.TableFunction => "Table-valued functions",
        SchemaObjectKind.Function => "Scalar functions",
        _ => "Synonyms",
    };

    private static int GroupOrder(string group) => group switch
    {
        "Tables" => 0,
        "Views" => 1,
        "Stored procedures" => 2,
        "Table-valued functions" => 3,
        "Scalar functions" => 4,
        _ => 5,
    };

    // ------------------------------------------------------------------ inline helpers

    private static FontFamily CodeFont => (FontFamily)Application.Current.Resources["CodeFont"];

    private Paragraph Para(double indent = 0)
    {
        var p = new Paragraph { Margin = new Thickness(indent * 6, 0, 0, 4) };
        _doc.Blocks.Add(p);
        return p;
    }

    private void Section(string title)
    {
        var p = new Paragraph { Margin = new Thickness(0, 10, 0, 4) };
        p.Inlines.Add(new Run { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14 });
        _doc.Blocks.Add(p);
    }

    private static Run Secondary(string text) =>
        new() { Text = text, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };

    private static Run Keyword(string text) =>
        new() { Text = text, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], FontStyle = Windows.UI.Text.FontStyle.Italic };

    private static Run Code(string text) => new() { Text = text, FontFamily = CodeFont, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x4E, 0xC9, 0xB0)) };

    private Hyperlink ObjectLink(SchemaObject o) => Link(o.DisplayName, new DocView(o));

    private Hyperlink Link(string text, DocView view)
    {
        var link = new Hyperlink { UnderlineStyle = UnderlineStyle.None, FontFamily = CodeFont };
        link.Inlines.Add(new Run { Text = text });
        link.Click += (_, _) => Navigate(view);
        return link;
    }
}
