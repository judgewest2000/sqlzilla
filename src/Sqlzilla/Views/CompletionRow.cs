using Sqlzilla.Sql;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Sqlzilla.Views;

/// <summary>
/// One row of the editor's completion popup. The list only carries strings, so <see cref="CodeEditor"/> encodes
/// each item as <c>label \t detail \t kind</c> and this control lays it out.
/// </summary>
public sealed partial class CompletionRow : Grid
{
    public const char FieldSeparator = '\t';

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(CompletionRow), new PropertyMetadata("", (d, _) => ((CompletionRow)d).Update()));

    private readonly TextBlock _icon = new() { Width = 18, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _label = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 0, 12, 0) };
    private readonly TextBlock _detail = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0.65, MaxWidth = 180 };

    public CompletionRow()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        SetColumn(_label, 1);
        SetColumn(_detail, 2);
        Children.Add(_icon);
        Children.Add(_label);
        Children.Add(_detail);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public static string Encode(CompletionItem item) => string.Join(FieldSeparator,
        Clean(item.Label), Clean(item.Detail ?? ""), item.Kind.ToString());

    private static string Clean(string s) => s.Replace(FieldSeparator, ' ').Replace('\n', ' ').Replace('\r', ' ').Replace((char)0x1E, ' ');

    private void Update()
    {
        var parts = (Text ?? "").Split(FieldSeparator);
        _label.Text = parts[0];
        _detail.Text = parts.Length > 1 ? parts[1] : "";

        var kind = parts.Length > 2 && Enum.TryParse<CompletionKind>(parts[2], out var k) ? k : CompletionKind.Keyword;
        var (glyph, color, symbolFont) = kind switch
        {
            CompletionKind.Column => ("\uE7B8", 0x75BEFF, false),
            CompletionKind.Table => ("\uE80A", 0xEE9D28, false),
            CompletionKind.View => ("\uE8A1", 0xEE9D28, false),
            CompletionKind.Schema => ("\uE8B7", 0xC5C5C5, false),
            CompletionKind.Procedure => ("\uE768", 0xB180D7, false),
            CompletionKind.Function => ("\uE945", 0xB180D7, false),
            CompletionKind.Variable => ("@", 0x75BEFF, true),
            CompletionKind.Join => ("\uE71B", 0x4EC9B0, false),
            CompletionKind.Local => ("\uE8FD", 0x4EC9B0, false),
            _ => ("\uE943", 0xC5C5C5, false),
        };
        _icon.Text = glyph;
        _icon.FontFamily = symbolFont ? CodeFont : new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        _icon.FontSize = symbolFont ? 13 : 12;
        _icon.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, (byte)(color >> 16), (byte)(color >> 8), (byte)color));
    }

    private static FontFamily CodeFont => (FontFamily)Application.Current.Resources["CodeFont"];
}
