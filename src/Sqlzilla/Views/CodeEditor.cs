using Sqlzilla.Sql;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VirtualKey = Windows.System.VirtualKey;
using WinUIEditor;

namespace Sqlzilla.Views;

public enum EditorCommand { Execute, Save, ToggleResults, Docs, RefreshIntellisense }

/// <summary>
/// Native Scintilla code editor (WinUIEdit) with T-SQL highlighting. Offsets exposed to callers are UTF-16 offsets into LF-normalised <see cref="Text"/>.
/// </summary>
public sealed partial class CodeEditor : UserControl
{
    private const string FontName = "Cascadia Mono";
    private const int FontPoints = 10;
    private const char ListSeparator = '\n';
    private const string CompletionTriggers = ".@";

    private readonly CodeEditorControl _control = new();
    private readonly TextBlock _placeholderBlock;
    private string? _text = "";   // null = stale, re-read from the editor on demand
    private bool _hostEdit;
    private bool _closed;
    private int _version;
    private Dictionary<string, CompletionItem> _shownItems = new();
    private long _replaceStartPos;   // where an accepted completion starts replacing (may be before Scintilla's word start, e.g. a '[')

    public CodeEditor()
    {
        _placeholderBlock = new TextBlock
        {
            IsHitTestVisible = false,
            FontFamily = new FontFamily(FontName),
            FontSize = 13,
            Opacity = 0.5,
            TextWrapping = TextWrapping.Wrap,
        };
        Content = new Border
        {
            Background = (Brush)Application.Current.Resources["ControlFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(0, 1, 1, 1),
            Child = new Grid { Children = { _control, _placeholderBlock, CreateOverlay(), CreateFindBar() } },
        };
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        InitEditor();
        PreviewKeyDown += OnPreviewKeyDown;
        _control.Loaded += OnControlLoaded;
    }

    public event EventHandler? TextChanged;
    public event EventHandler<EditorCommand>? CommandInvoked;

    /// <summary>Supplies completions: (text, caret offset, explicitly requested) → result.</summary>
    public Func<string, int, bool, Task<CompletionResult>>? CompletionSource { get; set; }

    public string Text
    {
        get => _text ??= Normalize(Editor.GetText(Editor.Length));
        set
        {
            value = Normalize(value);
            if (value == Text) return;
            HostEdit(() =>
            {
                Editor.SetText(value);
                Editor.EmptyUndoBuffer();
                Editor.GotoPos(0);
            });
            _text = value;
            UpdatePlaceholder();
        }
    }

    public string Placeholder
    {
        get => _placeholderBlock.Text;
        set => _placeholderBlock.Text = value;
    }

    /// <summary>The selected text and the zero-based line it starts on, or null when nothing is selected.</summary>
    public (string Text, int Line)? Selection
    {
        get
        {
            if (Editor.SelectionEmpty) return null;
            var text = Normalize(Editor.GetSelText()).TrimEnd('\0');
            return text.Trim().Length == 0 ? null : (text, (int)Editor.LineFromPosition(Editor.SelectionStart));
        }
    }

    /// <summary>Moves the caret to a zero-based line and focuses the editor.</summary>
    public void GoToLine(int line)
    {
        Editor.GotoLine(Math.Clamp(line, 0, (int)Editor.LineCount - 1));
        Editor.ScrollCaret();
        FocusEditor();
    }

    public void FocusEditor()
    {
        _control.Focus(FocusState.Programmatic);
        Editor.Focus = true;
    }

    /// <summary>
    /// Detaches from WinUIEdit; required for the editor to be collected. WinUIEdit's events aren't visible to the
    /// XAML reference tracker, so their handlers strongly root this control in a cycle via <see cref="_control"/>.
    /// </summary>
    public void Close()
    {
        if (_closed) return;
        _closed = true;
        var e = Editor;
        e.AutoCCancel();
        e.Modified -= OnModified;
        e.CharAdded -= OnCharAdded;
        e.AutoCSelection -= OnAutoCSelection;
        e.AutoCCharDeleted -= OnAutoCCharDeleted;
        e.StyleNeeded -= OnStyleNeeded;
        e.ZoomChanged -= OnZoomChanged;
        CloseIntellisense();
        _control.SyntaxHighlightingApplied -= OnSyntaxHighlightingApplied;
        _control.DefaultColorsChanged -= OnDefaultColorsChanged;
        _control.Loaded -= OnControlLoaded;
        PreviewKeyDown -= OnPreviewKeyDown;
        CompletionSource = null;
        _shownItems = new();
    }

    private Editor Editor => _control.Editor;

    private static string Normalize(string? text) => (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');

    private void InitEditor()
    {
        var e = Editor;
        e.EOLMode = EndOfLine.Lf;
        e.PasteConvertEndings = true;
        e.TabWidth = 4;
        e.UseTabs = false;
        e.ModEventMask = ModificationFlags.InsertText | ModificationFlags.DeleteText;
        e.AutoCSeparator = ListSeparator;
        e.AutoCTypeSeparator = 0x1E;
        e.AutoCOrder = Ordering.Custom;
        e.AutoCIgnoreCase = true;
        e.AutoCAutoHide = false;
        e.AutoCDropRestOfWord = false;
        e.AutoCMaxHeight = 12;
        e.AutoCMaxWidth = 0;

        e.Modified += OnModified;
        e.CharAdded += OnCharAdded;
        e.AutoCSelection += OnAutoCSelection;
        e.AutoCCharDeleted += OnAutoCCharDeleted;
        e.StyleNeeded += OnStyleNeeded;
        e.ZoomChanged += OnZoomChanged;

        _control.SyntaxHighlightingApplied += OnSyntaxHighlightingApplied;
        _control.DefaultColorsChanged += OnDefaultColorsChanged;
        InitFindIndicator();
        InitIntellisense();
        // Unknown to WinUIEdit → container lexer, styled by StyleSql.
        _control.HighlightingLanguage = "tsql";
        ApplyStyles();
    }

    private void OnControlLoaded(object sender, RoutedEventArgs args)
    {
        UpdatePlaceholder();
        ExpandScrollBars(_control);
    }

    /// <summary>Keeps the editor's scroll bars full-size (as in the results grid) and lets the wheel scroll over them.</summary>
    private void ExpandScrollBars(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Microsoft.UI.Xaml.Controls.Primitives.ScrollBar bar)
            {
                if (bar.Tag != null) continue;
                bar.Tag = true;
                void Expand() => VisualStateManager.GoToState(bar, "ExpandedWithoutAnimation", false);
                Expand();
                bar.PointerExited += (_, _) => Expand();
                bar.PointerCaptureLost += (_, _) => Expand();
                bar.PointerWheelChanged += (_, e) =>
                {
                    var delta = e.GetCurrentPoint(bar).Properties.MouseWheelDelta;
                    var lines = -delta / 40;   // Scintilla's default: 3 lines per notch
                    if (bar.Orientation == Orientation.Vertical) Editor.LineScroll(0, lines);
                    else Editor.LineScroll(lines * 3, 0);
                    e.Handled = true;
                };
            }
            else ExpandScrollBars(child);
        }
    }

    private void OnAutoCCharDeleted(Editor sender, object args) => _ = ShowCompletionsAsync(false);

    private void OnStyleNeeded(Editor sender, StyleNeededEventArgs args) => StyleSql();

    private void OnZoomChanged(Editor sender, ZoomChangedEventArgs args) => DispatcherQueue.TryEnqueue(UpdatePlaceholder);

    private void OnSyntaxHighlightingApplied(object? sender, ElementTheme args) => ApplyStyles();

    private void OnDefaultColorsChanged(object? sender, ElementTheme args)
    {
        ApplyStyles();
        ApplyFindColors();
    }

    // Style slots match SqlTokenKind values (all < 32 so they don't clash with Scintilla's predefined styles).
    private static readonly (SqlTokenKind Kind, int Dark, int Light, bool Italic)[] SqlStyles =
    [
        (SqlTokenKind.Comment, 0x6A9955, 0x008000, true),
        (SqlTokenKind.String, 0xCE9178, 0xA31515, false),
        (SqlTokenKind.Number, 0xB5CEA8, 0x098658, false),
        (SqlTokenKind.Keyword, 0x569CD6, 0x0000FF, false),
        (SqlTokenKind.Type, 0x4EC9B0, 0x267F99, false),
        (SqlTokenKind.Function, 0xDCDCAA, 0x795E26, false),
        (SqlTokenKind.Variable, 0x9CDCFE, 0x001080, false),
        (SqlTokenKind.TempTable, 0x9CDCFE, 0x001080, false),
        (SqlTokenKind.Operator, 0xB4B4B4, 0x606060, false),
    ];

    private static int Bgr(int rgb) => ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);

    private void ApplyStyles()
    {
        var e = Editor;
        for (var style = 0; style < 40; style++)
        {
            e.StyleSetFont(style, FontName);
            e.StyleSetSize(style, FontPoints);
        }
        var dark = ActualTheme != ElementTheme.Light;
        foreach (var (kind, darkColor, lightColor, italic) in SqlStyles)
        {
            e.StyleSetFore((int)kind, Bgr(dark ? darkColor : lightColor));
            e.StyleSetItalic((int)kind, italic);
        }
        if (_tint is { } tint)
        {
            var baseColor = dark ? Windows.UI.Color.FromArgb(255, 0x1E, 0x1E, 0x1E) : Windows.UI.Color.FromArgb(255, 0xFF, 0xFF, 0xFF);
            var text = GroupColors.Blend(baseColor, tint, dark ? 0.11 : 0.07);
            var margin = GroupColors.Blend(baseColor, tint, dark ? 0.2 : 0.14);
            for (var style = 0; style < 40; style++) e.StyleSetBack(style, ToBgr(style == 33 ? margin : text));
        }
        StyleSql();
        DispatcherQueue.TryEnqueue(UpdatePlaceholder);
    }

    private Windows.UI.Color? _tint;

    /// <summary>Tints the editor background with the connection's group colour.</summary>
    public Windows.UI.Color? Tint
    {
        get => _tint;
        set
        {
            _tint = value;
            ApplyStyles();
        }
    }

    private static int ToBgr(Windows.UI.Color c) => c.R | (c.G << 8) | (c.B << 16);

    private void StyleSql()
    {
        // Tokenising the whole document keeps multi-line comments/strings correct; scripts are small enough for this to be cheap.
        var e = Editor;
        var text = e.GetText(e.Length);
        e.StartStyling(0, 0);
        var pos = 0L;
        var offset = 0;
        foreach (var token in SqlTokenizer.Tokenize(text))
        {
            if (token.Start > offset)
            {
                var gap = e.PositionRelativeCodeUnits(pos, token.Start - offset) - pos;
                e.SetStyling(gap, 0);
                pos += gap;
            }
            var len = e.PositionRelativeCodeUnits(pos, token.Length) - pos;
            e.SetStyling(len, token.Kind is SqlTokenKind.Identifier or SqlTokenKind.QuotedIdentifier ? 0 : (int)token.Kind);
            pos += len;
            offset = token.Start + token.Length;
        }
        if (e.Length > pos) e.SetStyling(e.Length - pos, 0);
    }

    private void HostEdit(Action edit)
    {
        _hostEdit = true;
        try { edit(); }
        finally { _hostEdit = false; }
    }

    private void OnModified(Editor sender, ModifiedEventArgs args)
    {
        if ((args.ModificationType & (int)(ModificationFlags.InsertText | ModificationFlags.DeleteText)) == 0) return;
        _text = null;
        _version++;
        QueueFindRefresh();
        OnModifiedIntellisense();
        if (_hostEdit) return;
        UpdatePlaceholder();
        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdatePlaceholder()
    {
        _placeholderBlock.Visibility = Editor.Length == 0 && !string.IsNullOrEmpty(_placeholderBlock.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (_placeholderBlock.Visibility == Visibility.Collapsed || XamlRoot == null) return;
        // Match the editor's font size, which Ctrl+wheel zoom changes by whole points.
        _placeholderBlock.FontSize = Math.Max(2, FontPoints + Editor.Zoom) * 96.0 / 72;
        var px = 0;
        for (var m = 0; m < 5; m++) px += Editor.GetMarginWidthN(m);
        _placeholderBlock.Margin = new Thickness((px + 4) / XamlRoot.RasterizationScale, 1, 4, 0);
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = IsDown(VirtualKey.Control);
        var shift = IsDown(VirtualKey.Shift);
        var alt = IsDown(VirtualKey.Menu);
        EditorCommand? command = e.Key switch
        {
            VirtualKey.Enter when ctrl && !shift && !alt => EditorCommand.Execute,
            VirtualKey.E when ctrl && !shift && !alt => EditorCommand.Execute,
            VirtualKey.F5 when !ctrl && !shift && !alt => EditorCommand.Execute,
            VirtualKey.S when ctrl && !shift && !alt => EditorCommand.Save,
            VirtualKey.R when ctrl && !shift && !alt => EditorCommand.ToggleResults,
            VirtualKey.F1 when !ctrl && !shift && !alt => EditorCommand.Docs,
            VirtualKey.R when !ctrl && shift && alt => EditorCommand.RefreshIntellisense,
            _ => null,
        };
        if (command != null)
        {
            e.Handled = true;
            Editor.AutoCCancel();
            CommandInvoked?.Invoke(this, command.Value);
        }
        else if (HandleFindKey(e.Key, ctrl, shift, alt))
        {
            e.Handled = true;
        }
        else if (ctrl && !shift && !alt && e.Key is (VirtualKey)191 or VirtualKey.Divide)
        {
            e.Handled = true;
            ToggleLineComments();
        }
        else if (e.Key == VirtualKey.Space && ctrl && !alt && _findBox.FocusState == FocusState.Unfocused)
        {
            e.Handled = true;
            if (shift) _ = ShowSignatureAsync();
            else _ = ShowCompletionsAsync(true);
        }
        else if (e.Key == VirtualKey.Escape && !Editor.AutoCActive() && (SignatureVisible || _hoverPopup.Visibility == Visibility.Visible))
        {
            e.Handled = true;
            HideSignature();
            HideHover();
        }
        else
        {
            HideHover();
        }
    }

    private static bool IsDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void OnCharAdded(Editor sender, CharAddedEventArgs args)
    {
        var c = (char)args.Ch;
        if (c is '(' or ',' || (c == ')' && SignatureVisible) || (c == ' ' && AfterExecName())) QueueSignatureUpdate();
        if (char.IsLetterOrDigit(c) || c == '_' || CompletionTriggers.Contains(c)) _ = ShowCompletionsAsync(false);
        else if (Editor.AutoCActive()) Editor.AutoCCancel();
    }

    private async Task ShowCompletionsAsync(bool explicitRequest)
    {
        if (CompletionSource == null || _closed) return;
        var version = _version;
        var text = Text;
        var offset = CaretOffset;
        CompletionResult result;
        try { result = await CompletionSource(text, offset, explicitRequest); }
        catch { return; }
        if (_closed || version != _version || offset != CaretOffset) return;

        var e = Editor;
        if (result.Items.Count == 0)
        {
            e.AutoCCancel();
            return;
        }

        var shown = new Dictionary<string, CompletionItem>();
        var lines = new List<string>(result.Items.Count);
        foreach (var item in result.Items)
        {
            var display = CompletionRow.Encode(item);
            if (shown.TryAdd(display, item)) lines.Add(display);
        }
        _shownItems = shown;

        var start = Math.Clamp(result.ReplaceStart, 0, offset);
        var startPos = e.PositionRelativeCodeUnits(0, start);
        _replaceStartPos = startPos;
        // Scintilla matches the typed text against labels, which never start with '[', so leave an opening bracket out.
        var matchPos = start < offset && text[start] == '[' ? startPos + 1 : startPos;
        e.AutoCShow(e.CurrentPos - matchPos, string.Join(ListSeparator, lines));
        DispatcherQueue.TryEnqueue(() => FitCompletionPopup(lines.Count));
    }

    // WinUIEdit sizes the popup (and its shadow) for 32 dip rows; our template uses compact rows, so shrink it to fit.
    private const double CompletionRowHeight = 22;
    private const int CompletionVisibleRows = 12;

    private void FitCompletionPopup(int itemCount)
    {
        if (XamlRoot == null || !Editor.AutoCActive()) return;
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot))
        {
            if (popup.Child is not AutocompletionControl list || double.IsNaN(list.Height)) continue;
            var fitted = Math.Min(itemCount, CompletionVisibleRows) * CompletionRowHeight + 6;
            var excess = list.Height - fitted;
            if (excess <= 0) continue;
            var caretTop = _control.TransformToVisual(null).TransformPoint(
                new Windows.Foundation.Point(0, Editor.PointYFromPosition(Editor.CurrentPos) / XamlRoot.RasterizationScale)).Y;
            if (popup.VerticalOffset < caretTop) popup.VerticalOffset += excess;   // shown above the caret: keep it attached
            list.Height = fitted;
        }
    }

    /// <summary>Ctrl+/ like VS Code: toggles <c>--</c> on every line the selection touches, as one undo step.</summary>
    private void ToggleLineComments()
    {
        var e = Editor;
        e.AutoCCancel();
        var selStart = e.SelectionStart;
        var selEnd = e.SelectionEnd;
        var first = e.LineFromPosition(selStart);
        var last = e.LineFromPosition(selEnd);
        // A selection ending at the very start of a line doesn't include that line.
        if (last > first && e.PositionFromLine(last) == selEnd) last--;

        var startPos = e.PositionFromLine(first);
        var endPos = e.GetLineEndPosition(last);
        var raw = e.GetText(e.Length) ?? "";
        var startChar = (int)e.CountCodeUnits(0, startPos);
        var endChar = (int)e.CountCodeUnits(0, endPos);
        var block = raw[startChar..endChar];
        var eol = block.Contains("\r\n") ? "\r\n" : "\n";
        var toggled = string.Join(eol, SqlComment.ToggleLines(block.Split(eol)));
        if (toggled == block) return;

        e.BeginUndoAction();
        e.SetTargetRange(startPos, endPos);
        e.ReplaceTarget(-1, toggled);
        e.EndUndoAction();

        var newEnd = e.PositionRelativeCodeUnits(startPos, toggled.Length);
        if (first == last && selStart == selEnd)
        {
            // Keep the caret on the same text it was next to.
            var caretChar = (int)e.CountCodeUnits(startPos, selStart) + (toggled.Length - block.Length);
            var caret = e.PositionRelativeCodeUnits(startPos, Math.Clamp(caretChar, 0, toggled.Length));
            e.SetSel(caret, caret);
        }
        else
        {
            e.SetSel(startPos, newEnd);
        }
    }

    private void OnAutoCSelection(Editor sender, AutoCSelectionEventArgs args)
    {
        if (!_shownItems.TryGetValue(args.Text, out var item)) return;
        var e = Editor;
        e.AutoCCancel();   // stop Scintilla inserting the display text; insert the real text instead
        var start = Math.Min(_replaceStartPos, args.Position);
        var end = e.CurrentPos;
        e.SetTargetRange(start, end);
        e.ReplaceTarget(-1, item.InsertText);
        var caret = e.PositionRelativeCodeUnits(start, item.InsertText.Length);
        e.SetSel(caret, caret);
    }
}
