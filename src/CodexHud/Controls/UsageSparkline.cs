using System.Windows;
using System.Windows.Media;
using CodexHud.Core;

namespace CodexHud.Controls;

/// <summary>
/// A fixed 60-second view of completed observation ticks. Missing ticks are gaps, never zeroes.
/// EndTime is the exclusive end of the newest completed tick; zero selects the history's end.
/// </summary>
public sealed class UsageSparkline : FrameworkElement
{
    private const int TickCount = 60;
    private const long TickDuration = TimeSpan.TicksPerSecond;
    private static readonly Pen GridPen = MakePen(Color.FromArgb(45, 157, 169, 151), .65);
    private static readonly Pen BaselinePen = MakePen(Color.FromArgb(90, 157, 169, 151), .8);
    private static readonly SeriesStyle OverallStyle = new(Color.FromRgb(221, 236, 99));
    private static readonly SeriesStyle InputStyle = new(Color.FromRgb(83, 184, 255));
    private static readonly SeriesStyle CacheStyle = new(Color.FromRgb(120, 219, 136), dashed: true);
    private static readonly SeriesStyle OutputStyle = new(Color.FromRgb(221, 236, 99));

    public static readonly DependencyProperty HistoryProperty = DependencyProperty.Register(
        nameof(History), typeof(IReadOnlyList<UsageTick>), typeof(UsageSparkline),
        new FrameworkPropertyMetadata(Array.Empty<UsageTick>(), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BreakdownProperty = DependencyProperty.Register(
        nameof(Breakdown), typeof(bool), typeof(UsageSparkline),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(UsageSparkline),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty EndTimeProperty = DependencyProperty.Register(
        nameof(EndTime), typeof(DateTimeOffset), typeof(UsageSparkline),
        new FrameworkPropertyMetadata(default(DateTimeOffset), FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<UsageTick> History
    {
        get => (IReadOnlyList<UsageTick>)GetValue(HistoryProperty);
        set => SetValue(HistoryProperty, value);
    }
    public bool Breakdown { get => (bool)GetValue(BreakdownProperty); set => SetValue(BreakdownProperty, value); }
    /// <summary>Positive finite values fix the vertical scale across task rows; zero uses a nice local scale.</summary>
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public DateTimeOffset EndTime { get => (DateTimeOffset)GetValue(EndTimeProperty); set => SetValue(EndTimeProperty, value); }

    static UsageSparkline()
    {
        ClipToBoundsProperty.OverrideMetadata(typeof(UsageSparkline), new FrameworkPropertyMetadata(true));
        SnapsToDevicePixelsProperty.OverrideMetadata(typeof(UsageSparkline), new FrameworkPropertyMetadata(true));
    }

    protected override Size MeasureOverride(Size availableSize) => new(
        Math.Min(80, double.IsFinite(availableSize.Width) ? availableSize.Width : 80),
        Math.Min(42, double.IsFinite(availableSize.Height) ? availableSize.Height : 42));

    /// <summary>Round up to 1, 2, 5, or 10 times a power of ten, with a nonzero idle scale.</summary>
    public static double NiceMaximum(double maximum)
    {
        if (!double.IsFinite(maximum) || maximum <= 1) return 1;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(maximum)));
        var normalized = maximum / magnitude;
        var scale = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        var result = scale * magnitude;
        return double.IsFinite(result) ? result : maximum;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (!double.IsFinite(ActualWidth) || !double.IsFinite(ActualHeight)
            || ActualWidth <= 4 || ActualHeight <= 4) return;

        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.PushClip(new RectangleGeometry(bounds));
        // Give the entire plot a hit-test surface for its parent's click and tooltip handlers.
        dc.DrawRectangle(Brushes.Transparent, null, bounds);
        var plot = new Rect(2, 2, ActualWidth - 4, ActualHeight - 4);
        DrawGrid(dc, plot);

        var history = History;
        if (history is { Count: > 0 })
        {
            var endTicks = EndTime == default
                ? history.Max(item => item.TickStart.UtcTicks) + TickDuration
                : EndTime.UtcTicks;
            // Match provider-aligned seconds, including callers supplying a subsecond boundary.
            endTicks -= endTicks % TickDuration;
            var firstTicks = endTicks - TickCount * TickDuration;
            var samples = new UsageTick?[TickCount];
            foreach (var tick in history)
            {
                var offset = tick.TickStart.UtcTicks - firstTicks;
                if (offset >= 0 && offset < TickCount * TickDuration)
                    samples[(int)(offset / TickDuration)] = tick;
            }

            var maximum = Maximum;
            if (!double.IsFinite(maximum) || maximum <= 0)
            {
                maximum = 0;
                foreach (var tick in samples)
                {
                    if (!IsUsable(tick)) continue;
                    if (Breakdown)
                    {
                        maximum = Math.Max(maximum, Value(tick!.Counts!, 0) ?? 0);
                        maximum = Math.Max(maximum, Value(tick.Counts!, 1) ?? 0);
                        maximum = Math.Max(maximum, Value(tick.Counts!, 2) ?? 0);
                    }
                    else maximum = Math.Max(maximum, Value(tick!.Counts!, 3) ?? 0);
                }
                maximum = NiceMaximum(maximum);
            }

            if (Breakdown)
            {
                DrawSeries(dc, plot, samples, maximum, 0, InputStyle);
                DrawSeries(dc, plot, samples, maximum, 1, CacheStyle);
                DrawSeries(dc, plot, samples, maximum, 2, OutputStyle);
            }
            else DrawSeries(dc, plot, samples, maximum, 3, OverallStyle);
        }
        dc.Pop();
    }

    private static void DrawGrid(DrawingContext dc, Rect plot)
    {
        for (var division = 0; division <= 4; division++)
        {
            var x = plot.Left + plot.Width * division / 4;
            dc.DrawLine(GridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var y = plot.Top + plot.Height * division / 4;
            dc.DrawLine(division == 4 ? BaselinePen : GridPen,
                new Point(plot.Left, y), new Point(plot.Right, y));
        }
    }

    private static void DrawSeries(DrawingContext dc, Rect plot, UsageTick?[] samples,
        double maximum, int channel, SeriesStyle style)
    {
        Point? previous = null;
        var previousPartial = false;
        for (var index = 0; index < samples.Length; index++)
        {
            var tick = samples[index];
            var value = IsUsable(tick) ? Value(tick!.Counts!, channel) : null;
            if (value is null)
            {
                previous = null;
                continue;
            }
            var point = new Point(plot.Left + plot.Width * index / (TickCount - 1),
                plot.Bottom - plot.Height * Math.Clamp(value.Value / maximum, 0, 1));
            var partial = tick!.Health == SampleHealth.Partial;
            if (previous is { } before)
                dc.DrawLine(partial || previousPartial ? style.PartialPen : style.Pen, before, point);

            // Partial readings remain visible but use a dim dashed stroke and hollow marker.
            if (partial) dc.DrawEllipse(null, style.PartialPen, point, 1.7, 1.7);
            else if (previous is null && (index + 1 == samples.Length
                || !IsUsable(samples[index + 1]) || Value(samples[index + 1]!.Counts!, channel) is null))
                dc.DrawEllipse(style.Pen.Brush, null, point, 1.3, 1.3);
            previous = point;
            previousPartial = partial;
        }
    }

    private static bool IsUsable(UsageTick? tick) => tick?.Counts is not null
        && tick.Health is SampleHealth.Fresh or SampleHealth.Partial;

    private static double? Value(TokenCounts counts, int channel)
    {
        static double? Valid(long? value) => value is >= 0 ? value.Value : null;
        return channel switch
        {
            0 => Valid(counts.InputTokens),
            1 => Valid(counts.CachedInputTokens),
            2 => Valid(counts.OutputTokens),
            // Cached input is already contained in input. Sum as doubles to avoid long overflow.
            _ => counts.InputTokens is >= 0 && counts.OutputTokens is >= 0
                ? (double)counts.InputTokens.Value + counts.OutputTokens.Value : null
        };
    }

    private static Pen MakePen(Color color, double thickness, bool dashed = false)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var pen = new Pen(brush, thickness);
        if (dashed) pen.DashStyle = DashStyles.Dash;
        pen.Freeze();
        return pen;
    }

    private sealed class SeriesStyle(Color color, bool dashed = false)
    {
        public Pen Pen { get; } = MakePen(color, 1.35, dashed);
        public Pen PartialPen { get; } = MakePen(Color.FromArgb(130, color.R, color.G, color.B), 1.2, dashed: true);
    }
}
