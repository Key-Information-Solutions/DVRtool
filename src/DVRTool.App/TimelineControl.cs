using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DVRTool.Core;

namespace DVRTool.App;

/// <summary>Where the operator asked the playhead to go.</summary>
public sealed class SeekRequestedEventArgs(DateTime time) : EventArgs
{
    public DateTime Time { get; } = time;
}

/// <summary>
/// The playback timeline: a day (or a zoomed part of one) as a strip, footage drawn where it
/// exists, a playhead, and a selection for export.
/// </summary>
/// <remarks>
/// <para>
/// Drawn directly (<see cref="OnRender"/>) rather than assembled from elements: a day of
/// motion recording is hundreds of runs, and the whole strip is redrawn on every zoom, pan
/// and playhead tick. All the geometry is <see cref="TimelineWindow"/>'s, which is what the
/// tests hold; this class only paints it and turns mouse events into times.
/// </para>
/// <para>
/// Gestures: click seeks; drag selects a range (a drag shorter than a few pixels is a click);
/// wheel zooms about the cursor; right-drag pans. Footage colour follows the recording type —
/// continuous in one shade, event-triggered in another, and mixed or unknown in a third — so
/// a motion-only camera reads differently from a continuous one at a glance.
/// </para>
/// </remarks>
public sealed class TimelineControl : FrameworkElement
{
    private const double DragThreshold = 4;
    private const double LabelHeight = 16;

    private static readonly Brush BackgroundBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x22));
    private static readonly Brush ContinuousBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x8D, 0xDE));
    private static readonly Brush EventBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x2E));
    private static readonly Brush MixedBrush = new SolidColorBrush(Color.FromRgb(0x6F, 0x9E, 0x7A));
    private static readonly Brush SelectionBrush = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF));
    private static readonly Pen TickPen = new(new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x5C)), 1);
    private static readonly Pen MinorTickPen = new(new SolidColorBrush(Color.FromRgb(0x38, 0x38, 0x3E)), 1);
    private static readonly Pen PlayheadPen = new(Brushes.White, 2);
    private static readonly Pen SelectionPen = new(Brushes.White, 1);
    private static readonly Brush LabelBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xCC));
    private static readonly Typeface LabelFace = new("Segoe UI");

    private TimelineWindow _window = TimelineWindow.Day(DateTime.Today);
    private IReadOnlyList<FootageSpan> _coverage = [];
    private DateTime? _playhead;
    private (DateTime Start, DateTime End)? _selection;

    private Point? _pressAt;
    private DateTime _pressTime;
    private bool _selecting;
    private bool _panning;
    private TimelineWindow _panFrom;

    static TimelineControl()
    {
        foreach (var b in new[] { BackgroundBrush, ContinuousBrush, EventBrush, MixedBrush, SelectionBrush, LabelBrush })
            b.Freeze();
        foreach (var p in new[] { TickPen, MinorTickPen, PlayheadPen, SelectionPen })
            p.Freeze();
    }

    public TimelineControl()
    {
        Focusable = true;
        ClipToBounds = true;
        ToolTip = "";
        ToolTipService.SetInitialShowDelay(this, 300);
    }

    public TimelineWindow Window
    {
        get => _window;
        set
        {
            if (_window == value)
                return;
            _window = value;
            WindowChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
        }
    }

    public IReadOnlyList<FootageSpan> Coverage
    {
        get => _coverage;
        set
        {
            _coverage = value;
            InvalidateVisual();
        }
    }

    public DateTime? Playhead
    {
        get => _playhead;
        set
        {
            if (_playhead == value)
                return;
            _playhead = value;
            InvalidateVisual();
        }
    }

    /// <summary>The operator's dragged range, for export; null when none.</summary>
    public (DateTime Start, DateTime End)? Selection
    {
        get => _selection;
        set
        {
            _selection = value;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
        }
    }

    public event EventHandler<SeekRequestedEventArgs>? SeekRequested;
    public event EventHandler? SelectionChanged;
    public event EventHandler? WindowChanged;

    // ----- input -----

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var p = e.GetPosition(this);
        _pressAt = p;
        _pressTime = _window.TimeAt(p.X, ActualWidth);
        _selecting = false;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        _pressAt = e.GetPosition(this);
        _panFrom = _window;
        _panning = true;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        var t = _window.TimeAt(p.X, ActualWidth);
        ToolTip = DescribeAt(t);

        if (_pressAt is not Point pressed)
            return;

        if (_panning)
        {
            double dx = p.X - pressed.X;
            var by = TimeSpan.FromTicks((long)(-dx / Math.Max(1, ActualWidth) * _panFrom.Length.Ticks));
            Window = _panFrom.Pan(by);
            return;
        }

        if (!_selecting && Math.Abs(p.X - pressed.X) >= DragThreshold)
            _selecting = true;
        if (_selecting)
        {
            var a = _pressTime;
            var b = t;
            Selection = a <= b ? (a, b) : (b, a);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_pressAt is null)
            return;
        // Read the gesture before releasing capture: ReleaseMouseCapture raises
        // LostMouseCapture synchronously, and that handler resets these flags — checking
        // _selecting after the release turned every drag into a click (and a seek), which
        // is how the export selection was unreachable until 2026-09-11.
        bool wasSelecting = _selecting;
        var pressTime = _pressTime;
        _pressAt = null;
        _selecting = false;
        ReleaseMouseCapture();
        e.Handled = true;
        if (wasSelecting)
            return;
        // A click: a plain one seeks and clears any selection, so the selection never
        // silently outlives the range the operator was looking at.
        Selection = null;
        SeekRequested?.Invoke(this, new SeekRequestedEventArgs(pressTime));
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (_panning)
        {
            _panning = false;
            _pressAt = null;
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _pressAt = null;
        _selecting = false;
        _panning = false;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        double fraction = ActualWidth > 0 ? e.GetPosition(this).X / ActualWidth : 0.5;
        Window = _window.Zoom(e.Delta > 0 ? +1 : -1, fraction);
        e.Handled = true;
    }

    /// <summary>Zoom from a button, about the playhead when it is visible, else the middle.</summary>
    public void ZoomBy(int steps)
    {
        double fraction = 0.5;
        if (_playhead is DateTime ph && _window.Contains(ph) && ActualWidth > 0)
            fraction = _window.PixelOf(ph, ActualWidth) / ActualWidth;
        Window = _window.Zoom(steps, fraction);
    }

    private string DescribeAt(DateTime t)
    {
        var span = FootageCoverage.SpanAt(_coverage, t);
        string when = t.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        if (span is null)
            return $"{when} — no footage";
        string type = span.Type == RecordingType.Unknown ? "recorded" : span.Type.ToString().ToLowerInvariant();
        return $"{when} — {type} {span.Start:HH:mm:ss} → {span.End:HH:mm:ss}";
    }

    // ----- painting -----

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 64 : availableSize.Height);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0 || h <= 0)
            return;

        dc.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, w, h));

        double barTop = LabelHeight + 4;
        double barHeight = Math.Max(8, h - barTop - 4);

        // Footage. A run narrower than a pixel is still drawn one pixel wide: a two-second
        // motion clip on a day-wide strip must be visible, or the operator will not find it.
        foreach (var run in _coverage)
        {
            if (run.End <= _window.Start || run.Start >= _window.End)
                continue;
            double x0 = Math.Max(0, _window.PixelOf(run.Start, w));
            double x1 = Math.Min(w, _window.PixelOf(run.End, w));
            double width = Math.Max(1, x1 - x0);
            dc.DrawRectangle(BrushFor(run.Type), null, new Rect(x0, barTop, width, barHeight));
        }

        // Ticks: labelled at the interval the width allows, with an unlabelled one halfway.
        var step = _window.TickInterval(w);
        var format = _window.TickFormat(w);
        var half = TimeSpan.FromTicks(step.Ticks / 2);
        foreach (var tick in _window.Ticks(w))
        {
            double x = Math.Round(_window.PixelOf(tick, w)) + 0.5;
            dc.DrawLine(TickPen, new Point(x, LabelHeight), new Point(x, h));
            var text = new FormattedText(tick.ToString(format, CultureInfo.CurrentCulture),
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, 11, LabelBrush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(x + 3, 0));
            var minor = tick + half;
            if (_window.Contains(minor))
            {
                double mx = Math.Round(_window.PixelOf(minor, w)) + 0.5;
                dc.DrawLine(MinorTickPen, new Point(mx, barTop + barHeight / 2), new Point(mx, h));
            }
        }

        if (_selection is { } sel && sel.End > _window.Start && sel.Start < _window.End)
        {
            double x0 = Math.Max(0, _window.PixelOf(sel.Start, w));
            double x1 = Math.Min(w, _window.PixelOf(sel.End, w));
            dc.DrawRectangle(SelectionBrush, SelectionPen, new Rect(x0, barTop - 2, Math.Max(1, x1 - x0), barHeight + 4));
        }

        if (_playhead is DateTime ph && _window.Contains(ph))
        {
            double x = Math.Round(_window.PixelOf(ph, w)) + 0.5;
            dc.DrawLine(PlayheadPen, new Point(x, 0), new Point(x, h));
        }
    }

    private static Brush BrushFor(RecordingType type) => type switch
    {
        RecordingType.Continuous or RecordingType.Manual => ContinuousBrush,
        RecordingType.Motion or RecordingType.Alarm or RecordingType.Event => EventBrush,
        _ => MixedBrush,
    };
}
