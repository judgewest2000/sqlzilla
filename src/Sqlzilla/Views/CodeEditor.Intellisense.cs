using Sqlzilla.Sql;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using WinUIEditor;

namespace Sqlzilla.Views;

/// <summary>Signature help, hover quick info and diagnostic squiggles.</summary>
public sealed partial class CodeEditor
{
    private const int ErrorIndicator = 21;
    private const int WarningIndicator = 22;
    private const int DwellMilliseconds = 450;
    private const int ScrollFlags = 0x4 | 0x8;   // SC_UPDATE_V_SCROLL | SC_UPDATE_H_SCROLL
    private const int SelectionFlag = 0x2;       // SC_UPDATE_SELECTION

    private Canvas _overlay = null!;
    private Border _signaturePopup = null!;
    private TextBlock _signatureText = null!;
    private Border _hoverPopup = null!;
    private TextBlock _hoverTitle = null!;
    private TextBlock _hoverBody = null!;
    private DispatcherQueueTimer? _diagnosticsTimer;
    private IReadOnlyList<SqlDiagnostic> _diagnostics = [];
    private bool _signatureQueued;
    private int _hoverRequest;

    /// <summary>Raised when the caret or selection moves.</summary>
    public event EventHandler? CaretMoved;

    /// <summary>Supplies parameter hints for the call around a caret offset.</summary>
    public Func<string, int, Task<SignatureInfo?>>? SignatureSource { get; set; }

    /// <summary>Supplies hover text for the word at an offset.</summary>
    public Func<string, int, Task<QuickInfo?>>? QuickInfoSource { get; set; }

    /// <summary>Supplies squiggles for the whole text (run off the UI thread by the caller).</summary>
    public Func<string, Task<IReadOnlyList<SqlDiagnostic>>>? DiagnosticsSource { get; set; }

    public int CaretOffset => (int)Editor.CountCodeUnits(0, Editor.CurrentPos);

    private UIElement CreateOverlay()
    {
        _signatureText = new TextBlock { FontFamily = new FontFamily(FontName), FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        _signaturePopup = Popup(_signatureText);

        _hoverTitle = new TextBlock { FontFamily = new FontFamily(FontName), FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        _hoverBody = new TextBlock
        {
            FontFamily = new FontFamily(FontName),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 40,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        _hoverPopup = Popup(new StackPanel { Children = { _hoverTitle, _hoverBody } });

        _overlay = new Canvas { IsHitTestVisible = false, Children = { _signaturePopup, _hoverPopup } };
        return _overlay;
    }

    private static Border Popup(UIElement child) => new()
    {
        Visibility = Visibility.Collapsed,
        MaxWidth = 640,
        Padding = new Thickness(10, 6, 10, 6),
        CornerRadius = new CornerRadius(6),
        BorderThickness = new Thickness(1),
        Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorTertiaryBrush"],
        BorderBrush = (Brush)Application.Current.Resources["SurfaceStrokeColorFlyoutBrush"],
        Shadow = new ThemeShadow(),
        Translation = new System.Numerics.Vector3(0, 0, 16),
        Child = child,
    };

    private void InitIntellisense()
    {
        var e = Editor;
        e.MouseDwellTime = DwellMilliseconds;
        e.DwellStart += OnDwellStart;
        e.DwellEnd += OnDwellEnd;
        e.UpdateUI += OnUpdateUI;

        e.IndicSetStyle(ErrorIndicator, IndicatorStyle.Squiggle);
        e.IndicSetFore(ErrorIndicator, Bgr(0xF14C4C));
        e.IndicSetStyle(WarningIndicator, IndicatorStyle.Squiggle);
        e.IndicSetFore(WarningIndicator, Bgr(0xD7A400));

        _diagnosticsTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _diagnosticsTimer.Interval = TimeSpan.FromMilliseconds(600);
        _diagnosticsTimer.IsRepeating = false;
        _diagnosticsTimer.Tick += OnDiagnosticsTick;
    }

    private void CloseIntellisense()
    {
        var e = Editor;
        e.DwellStart -= OnDwellStart;
        e.DwellEnd -= OnDwellEnd;
        e.UpdateUI -= OnUpdateUI;
        if (_diagnosticsTimer != null)
        {
            _diagnosticsTimer.Stop();
            _diagnosticsTimer.Tick -= OnDiagnosticsTick;
            _diagnosticsTimer = null;
        }
        SignatureSource = null;
        QuickInfoSource = null;
        DiagnosticsSource = null;
    }

    /// <summary>Re-checks the script, e.g. after the schema it is validated against changed.</summary>
    public void RefreshDiagnostics()
    {
        _diagnosticsTimer?.Stop();
        _diagnosticsTimer?.Start();
    }

    private void OnDiagnosticsTick(DispatcherQueueTimer sender, object args) => _ = RunDiagnosticsAsync();

    private async Task RunDiagnosticsAsync()
    {
        if (DiagnosticsSource == null || _closed) return;
        var version = _version;
        IReadOnlyList<SqlDiagnostic> diagnostics;
        try { diagnostics = await DiagnosticsSource(Text); }
        catch { return; }
        if (_closed || version != _version) return;
        _diagnostics = diagnostics;

        var e = Editor;
        foreach (var indicator in new[] { ErrorIndicator, WarningIndicator })
        {
            e.IndicatorCurrent = indicator;
            e.IndicatorClearRange(0, e.Length);
        }
        foreach (var d in diagnostics)
        {
            e.IndicatorCurrent = d.Severity == DiagnosticSeverity.Error ? ErrorIndicator : WarningIndicator;
            var start = e.PositionRelativeCodeUnits(0, d.Start);
            var length = Math.Max(1, e.PositionRelativeCodeUnits(start, Math.Max(1, d.Length)) - start);
            if (start >= 0) e.IndicatorFillRange(start, Math.Min(length, e.Length - start));
        }
    }

    private void OnModifiedIntellisense()
    {
        HideHover();
        _diagnosticsTimer?.Stop();
        _diagnosticsTimer?.Start();
    }

    // ------------------------------------------------------------------ signature help

    private void QueueSignatureUpdate()
    {
        if (_signatureQueued) return;
        _signatureQueued = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _signatureQueued = false;
            _ = ShowSignatureAsync();
        });
    }

    private async Task ShowSignatureAsync()
    {
        if (SignatureSource == null || _closed) return;
        var version = _version;
        var offset = CaretOffset;
        SignatureInfo? info;
        try { info = await SignatureSource(Text, offset); }
        catch { return; }
        if (_closed || version != _version || offset != CaretOffset) return;
        if (info == null)
        {
            HideSignature();
            return;
        }

        _signatureText.Inlines.Clear();
        _signatureText.Inlines.Add(new Run { Text = info.Name + "(" });
        for (var i = 0; i < info.Parameters.Count; i++)
        {
            if (i > 0) _signatureText.Inlines.Add(new Run { Text = ", " });
            var run = new Run { Text = info.Parameters[i] };
            if (i == info.ActiveParameter)
            {
                run.FontWeight = FontWeights.Bold;
                run.Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
            }
            _signatureText.Inlines.Add(run);
        }
        _signatureText.Inlines.Add(new Run { Text = ")" + (info.Returns != null ? " → " + info.Returns : "") });
        if (!string.IsNullOrEmpty(info.Description))
        {
            _signatureText.Inlines.Add(new LineBreak());
            _signatureText.Inlines.Add(new Run
            {
                Text = info.Description,
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
        }
        PlaceAtPosition(_signaturePopup, Editor.CurrentPos, above: true);
    }

    private void HideSignature() => _signaturePopup.Visibility = Visibility.Collapsed;

    private static readonly System.Text.RegularExpressions.Regex ExecName =
        new(@"\bEXEC(UTE)?\s+(@\w+\s*=\s*)?[\w\[\]. ]+?\s$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.RightToLeft);

    /// <summary>"EXEC dbo.Proc " just typed: show the procedure's parameters.</summary>
    private bool AfterExecName()
    {
        var offset = CaretOffset;
        var text = Text;
        var start = Math.Max(0, offset - 200);
        var line = text[start..offset];
        var lineStart = line.LastIndexOf('\n');
        if (lineStart >= 0) line = line[(lineStart + 1)..];
        return ExecName.IsMatch(line);
    }

    private bool SignatureVisible => _signaturePopup.Visibility == Visibility.Visible;

    // ------------------------------------------------------------------ hover

    private void OnDwellStart(Editor sender, DwellStartEventArgs args)
    {
        if (args.Position < 0 || _closed || FindOpen) return;
        _ = ShowHoverAsync(args.Position);
    }

    private void OnDwellEnd(Editor sender, DwellEndEventArgs args) => HideHover();

    private async Task ShowHoverAsync(long position)
    {
        var request = ++_hoverRequest;
        var offset = (int)Editor.CountCodeUnits(0, position);
        var diagnostic = _diagnostics.FirstOrDefault(d => offset >= d.Start && offset < d.Start + Math.Max(1, d.Length));
        QuickInfo? info = null;
        if (QuickInfoSource != null)
        {
            try { info = await QuickInfoSource(Text, offset); }
            catch { }
        }
        if (_closed || request != _hoverRequest || (info == null && diagnostic == null)) return;

        if (diagnostic != null)
        {
            _hoverTitle.Text = diagnostic.Message;
            _hoverTitle.Foreground = new SolidColorBrush(diagnostic.Severity == DiagnosticSeverity.Error
                ? Windows.UI.Color.FromArgb(255, 0xF1, 0x4C, 0x4C)
                : Windows.UI.Color.FromArgb(255, 0xD7, 0xA4, 0x00));
            _hoverBody.Text = info == null ? "" : info.Title + (string.IsNullOrEmpty(info.Body) ? "" : "\n" + info.Body);
        }
        else
        {
            _hoverTitle.Text = info!.Title;
            _hoverTitle.ClearValue(TextBlock.ForegroundProperty);
            _hoverBody.Text = info.Body ?? "";
        }
        _hoverBody.Visibility = _hoverBody.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        PlaceAtPosition(_hoverPopup, position, above: false);
    }

    private void HideHover()
    {
        _hoverRequest++;
        if (_hoverPopup != null) _hoverPopup.Visibility = Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ placement

    private void PlaceAtPosition(Border popup, long position, bool above)
    {
        if (XamlRoot == null) return;
        var scale = XamlRoot.RasterizationScale;
        var x = Editor.PointXFromPosition(position) / scale;
        var y = Editor.PointYFromPosition(position) / scale;
        var lineHeight = Editor.TextHeight(Editor.LineFromPosition(position)) / scale;
        if (x < 0 || y < 0 || y > _overlay.ActualHeight)
        {
            popup.Visibility = Visibility.Collapsed;
            return;
        }

        popup.Visibility = Visibility.Visible;
        popup.Measure(new Windows.Foundation.Size(Math.Min(popup.MaxWidth, Math.Max(200, _overlay.ActualWidth - 8)), double.PositiveInfinity));
        var size = popup.DesiredSize;
        var left = Math.Max(4, Math.Min(x - 8, _overlay.ActualWidth - size.Width - 4));
        var top = above ? y - size.Height - 2 : y + lineHeight + 2;
        if (above && top < 0) top = y + lineHeight + 2;
        if (!above && top + size.Height > _overlay.ActualHeight && y - size.Height - 2 >= 0) top = y - size.Height - 2;
        Canvas.SetLeft(popup, left);
        Canvas.SetTop(popup, top);
    }

    private void OnUpdateUI(Editor sender, UpdateUIEventArgs args)
    {
        if ((args.Updated & SelectionFlag) != 0) CaretMoved?.Invoke(this, EventArgs.Empty);
        if ((args.Updated & ScrollFlags) != 0)
        {
            HideHover();
            HideSignature();
            return;
        }
        if (SignatureVisible) QueueSignatureUpdate();
    }
}
