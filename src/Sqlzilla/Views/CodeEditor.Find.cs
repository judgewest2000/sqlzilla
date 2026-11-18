using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VirtualKey = Windows.System.VirtualKey;
using WinUIEditor;

namespace Sqlzilla.Views;

/// <summary>Ctrl+F find bar: highlights every match and steps through them with Enter / F3.</summary>
public sealed partial class CodeEditor
{
    private const int FindIndicator = 20;   // lexers use the low indicator numbers
    private const int MaxHighlightedMatches = 10_000;

    private Border _findBar = null!;
    private TextBox _findBox = null!;
    private TextBlock _findCount = null!;
    private ToggleButton _matchCase = null!;
    private readonly List<int> _matches = new();
    private int _findLength;
    private int _findAnchor;
    private bool _findRefreshQueued;

    private bool FindOpen => _findBar.Visibility == Visibility.Visible;

    private UIElement CreateFindBar()
    {
        _findBox = new TextBox
        {
            Width = 220,
            PlaceholderText = "Find",
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _findBox.TextChanged += (_, _) => RefreshMatches(selectFromAnchor: true);
        _findBox.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                FindNext(!IsDown(VirtualKey.Shift));
            }
            else if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                CloseFind();
            }
        };

        _findCount = new TextBlock
        {
            MinWidth = 76,
            Margin = new Thickness(8, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };

        _matchCase = new ToggleButton { Content = "Aa", Padding = new Thickness(6, 4, 6, 4), VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(_matchCase, "Match case");
        _matchCase.Click += (_, _) => RefreshMatches(selectFromAnchor: true);

        _findBar = new Border
        {
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 22, 0),
            Padding = new Thickness(6, 4, 4, 4),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorTertiaryBrush"],
            BorderBrush = (Brush)Application.Current.Resources["SurfaceStrokeColorDefaultBrush"],
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                Children =
                {
                    _findBox,
                    _findCount,
                    _matchCase,
                    FindButton("\uE70E", "Previous match (Shift+Enter)", () => FindNext(false)),
                    FindButton("\uE70D", "Next match (Enter)", () => FindNext(true)),
                    FindButton("\uE711", "Close (Escape)", CloseFind),
                },
            },
        };
        return _findBar;
    }

    private static Button FindButton(string glyph, string tip, Action click)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 12 },
            Padding = new Thickness(8, 6, 8, 6),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    private void InitFindIndicator()
    {
        var e = Editor;
        e.IndicSetStyle(FindIndicator, IndicatorStyle.RoundBox);
        e.IndicSetUnder(FindIndicator, true);
        ApplyFindColors();
    }

    private void ApplyFindColors()
    {
        var e = Editor;
        var dark = ActualTheme != ElementTheme.Light;
        e.IndicSetFore(FindIndicator, Bgr(dark ? 0xD18616 : 0xF2A60D));
        e.IndicSetAlpha(FindIndicator, (Alpha)(dark ? 110 : 90));
        e.IndicSetOutlineAlpha(FindIndicator, (Alpha)200);
    }

    private void OpenFind()
    {
        if (!Editor.SelectionEmpty)
        {
            var selected = Normalize(Editor.GetSelText()).TrimEnd('\0');
            if (selected.Length > 0 && !selected.Contains('\n')) _findBox.Text = selected;
        }
        _findAnchor = SelectionStartOffset;
        _findBar.Visibility = Visibility.Visible;
        RefreshMatches(selectFromAnchor: true);
        _findBox.Focus(FocusState.Keyboard);
        _findBox.SelectAll();
    }

    private void CloseFind()
    {
        if (!FindOpen) return;
        _findBar.Visibility = Visibility.Collapsed;
        _matches.Clear();
        ClearFindHighlights();
        FocusEditor();
    }

    private int SelectionStartOffset => (int)Editor.CountCodeUnits(0, Editor.SelectionStart);

    private void ClearFindHighlights()
    {
        Editor.IndicatorCurrent = FindIndicator;
        Editor.IndicatorClearRange(0, Editor.Length);
    }

    private void QueueFindRefresh()
    {
        if (!FindOpen || _findRefreshQueued) return;
        _findRefreshQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _findRefreshQueued = false;
            if (FindOpen && !_closed) RefreshMatches(selectFromAnchor: false);
        });
    }

    private void RefreshMatches(bool selectFromAnchor)
    {
        _matches.Clear();
        ClearFindHighlights();
        var needle = _findBox.Text.Replace("\r\n", "\n").Replace('\r', '\n');
        _findLength = needle.Length;
        if (needle.Length == 0)
        {
            _findCount.Text = "";
            return;
        }

        var text = Text;
        var comparison = _matchCase.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        for (var i = text.IndexOf(needle, comparison); i >= 0; i = text.IndexOf(needle, i + needle.Length, comparison))
        {
            _matches.Add(i);
            if (_matches.Count == MaxHighlightedMatches) break;
        }

        // Convert UTF-16 offsets to editor byte positions incrementally so large responses stay fast.
        var e = Editor;
        long pos = 0;
        var offset = 0;
        foreach (var match in _matches)
        {
            pos = e.PositionRelativeCodeUnits(pos, match - offset);
            offset = match;
            e.IndicatorFillRange(pos, e.PositionRelativeCodeUnits(pos, needle.Length) - pos);
        }

        if (_matches.Count == 0)
        {
            _findCount.Text = "No results";
            return;
        }
        if (selectFromAnchor)
        {
            var index = _matches.FindIndex(m => m >= _findAnchor);
            SelectMatch(index < 0 ? 0 : index);
        }
        else UpdateFindCount(_matches.IndexOf(SelectionStartOffset));
    }

    private void FindNext(bool forward)
    {
        if (!FindOpen)
        {
            OpenFind();
            return;
        }
        if (_matches.Count == 0) return;
        var from = SelectionStartOffset;
        var index = forward ? _matches.FindIndex(m => m > from) : _matches.FindLastIndex(m => m < from);
        if (index < 0) index = forward ? 0 : _matches.Count - 1;
        SelectMatch(index);
        _findAnchor = _matches[index];
    }

    private void SelectMatch(int index)
    {
        var e = Editor;
        var start = e.PositionRelativeCodeUnits(0, _matches[index]);
        var end = e.PositionRelativeCodeUnits(start, _findLength);
        e.SetSel(start, end);
        UpdateFindCount(index);
    }

    private void UpdateFindCount(int index)
    {
        var total = _matches.Count == MaxHighlightedMatches ? $"{MaxHighlightedMatches:N0}+" : _matches.Count.ToString("N0");
        _findCount.Text = index >= 0 ? $"{index + 1:N0} of {total}" : $"{total} found";
    }

    /// <summary>Handles find shortcuts; returns true when the key was consumed.</summary>
    private bool HandleFindKey(VirtualKey key, bool ctrl, bool shift, bool alt)
    {
        switch (key)
        {
            case VirtualKey.F when ctrl && !shift && !alt:
                OpenFind();
                return true;
            case VirtualKey.F3 when !ctrl && !alt:
                FindNext(!shift);
                return true;
            case VirtualKey.Escape when FindOpen && !Editor.AutoCActive():
                CloseFind();
                return true;
            default:
                return false;
        }
    }
}
