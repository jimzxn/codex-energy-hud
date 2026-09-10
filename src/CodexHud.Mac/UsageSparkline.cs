using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CodexHud.Core;

namespace CodexHud.Mac;

/// <summary>
/// A fixed 60-second view of completed observation ticks. Missing ticks leave gaps.
/// EndTime is the exclusive end of the newest completed second.
/// </summary>
internal sealed class UsageSparkline : Control
{
    private const int TickCount = UsageSmoothing.DisplaySeconds;
    private const long TickDuration = TimeSpan.TicksPerSecond;
    private static readonly Pen GridPen = MakePen(Color.FromArgb(45, 157, 169, 151), .65);
    private static readonly Pen BaselinePen = MakePen(Color.FromArgb(90, 157, 169, 151), .8);
    private static readonly SeriesStyle OverallStyle = new(Color.FromRgb(221, 236, 99));
    private static readonly SeriesStyle InputStyle = new(Color.FromRgb(83, 184, 255));
    private static readonly SeriesStyle CacheStyle = new(Color.FromRgb(120, 219, 136), dashed: true);
    private static readonly SeriesStyle OutputStyle = new(Color.FromRgb(221, 236, 99));

    public static readonly StyledProperty<IReadOnlyList<UsageRateTick>> HistoryProperty =
        AvaloniaProperty.Register<UsageSparkline, IReadOnlyList<UsageRateTick>>(nameof(History), Array.Empty<UsageRateTick>());
    public static readonly StyledProperty<bool> BreakdownProperty =
        AvaloniaProperty.Register<UsageSparkline, bool>(nameof(Breakdown));
    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<UsageSparkline, double>(nameof(Maximum));
    public static readonly StyledProperty<DateTimeOffset> EndTimeProperty =
        AvaloniaProperty.Register<UsageSparkline, DateTimeOffset>(nameof(EndTime));

    static UsageSparkline() => AffectsRender<UsageSparkline>(HistoryProperty, BreakdownProperty, MaximumProperty, EndTimeProperty);
    public UsageSparkline() => ClipToBounds = true;

    public IReadOnlyList<UsageRateTick> History { get => GetValue(HistoryProperty); set => SetValue(HistoryProperty, value); }
    public bool Breakdown { get => GetValue(BreakdownProperty); set => SetValue(BreakdownProperty, value); }
    /// <summary>Positive finite values fix the vertical scale across rows; zero selects a local scale.</summary>
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public DateTimeOffset EndTime { get => GetValue(EndTimeProperty); set => SetValue(EndTimeProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(
        Math.Min(80, double.IsFinite(availableSize.Width) ? availableSize.Width : 80),
        Math.Min(42, double.IsFinite(availableSize.Height) ? availableSize.Height : 42));

    public static double NiceMaximum(double maximum)
    {
        if (!double.IsFinite(maximum) || maximum <= 1) return 1;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(maximum)));
        var normalized = maximum / magnitude;
        var result = (normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10) * magnitude;
        return double.IsFinite(result) ? result : maximum;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (!double.IsFinite(Bounds.Width) || !double.IsFinite(Bounds.Height) || Bounds.Width <= 4 || Bounds.Height <= 4) return;
        var bounds = new Rect(0, 0, Bounds.Width, Bounds.Height);
        using var clip = context.PushClip(bounds);
        // Make the whole chart a target for its parent's click and tooltip handlers.
        context.DrawRectangle(Brushes.Transparent, null, bounds);
        var plot = new Rect(2, 2, Bounds.Width - 4, Bounds.Height - 4);
        DrawGrid(context, plot);
        var history = History;
        if (history.Count == 0) return;
        var endTicks = EndTime == default ? history.Max(item => item.TickStart.UtcTicks) + TickDuration : EndTime.UtcTicks;
        endTicks -= endTicks % TickDuration;
        var firstTicks = endTicks - TickCount * TickDuration;
        var samples = new UsageRateTick?[TickCount];
        foreach (var tick in history)
        {
            var offset = tick.TickStart.UtcTicks - firstTicks;
            if (offset >= 0 && offset < TickCount * TickDuration) samples[(int)(offset / TickDuration)] = tick;
        }
        var maximum = Maximum;
        if (!double.IsFinite(maximum) || maximum <= 0)
        {
            maximum = 0;
            foreach (var tick in samples)
            {
                if (!IsUsable(tick)) continue;
                if (Breakdown)
                    for (int channel = 0; channel < 3; channel++) maximum = Math.Max(maximum, Value(tick!.Rates!, channel) ?? 0);
                else maximum = Math.Max(maximum, Value(tick!.Rates!, 3) ?? 0);
            }
            maximum = NiceMaximum(maximum);
        }
        if (Breakdown)
        {
            DrawSeries(context, plot, samples, maximum, 0, InputStyle);
            DrawSeries(context, plot, samples, maximum, 1, CacheStyle);
            DrawSeries(context, plot, samples, maximum, 2, OutputStyle);
        }
        else DrawSeries(context, plot, samples, maximum, 3, OverallStyle);
    }

    private static void DrawGrid(DrawingContext context, Rect plot)
    {
        for (int division = 0; division <= 4; division++)
        {
            var x = plot.Left + plot.Width * division / 4;
            context.DrawLine(GridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var y = plot.Top + plot.Height * division / 4;
            context.DrawLine(division == 4 ? BaselinePen : GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
        }
    }

    private static void DrawSeries(DrawingContext context, Rect plot, UsageRateTick?[] samples, double maximum,
        int channel, SeriesStyle style)
    {
        Point? previous = null;
        bool previousPartial = false;
        for (int index = 0; index < samples.Length; index++)
        {
            var tick = samples[index];
            var value = IsUsable(tick) ? Value(tick!.Rates!, channel) : null;
            if (value is null) { previous = null; continue; }
            var point = new Point(plot.Left + plot.Width * index / (TickCount - 1),
                plot.Bottom - plot.Height * Math.Clamp(value.Value / maximum, 0, 1));
            var partial = tick!.Health == SampleHealth.Partial;
            if (previous is { } before) context.DrawLine(partial || previousPartial ? style.PartialPen : style.Pen, before, point);
            if (partial) context.DrawEllipse(null, style.PartialPen, new Rect(point.X - 1.7, point.Y - 1.7, 3.4, 3.4));
            else if (previous is null && (index + 1 == samples.Length || !IsUsable(samples[index + 1])
                || Value(samples[index + 1]!.Rates!, channel) is null))
                context.DrawEllipse(style.Pen.Brush, null, new Rect(point.X - 1.3, point.Y - 1.3, 2.6, 2.6));
            previous = point;
            previousPartial = partial;
        }
    }

    private static bool IsUsable(UsageRateTick? tick) => tick?.Rates is not null
        && tick.Health is SampleHealth.Fresh or SampleHealth.Partial;
    private static double? Value(TokenRates counts, int channel)
    {
        var value = channel switch { 0 => counts.InputTokens, 1 => counts.CachedInputTokens, 2 => counts.OutputTokens, _ => counts.TotalTokens };
        return value is >= 0 && double.IsFinite(value.Value) ? value : null;
    }
    private static Pen MakePen(Color color, double thickness, bool dashed = false) =>
        new(new SolidColorBrush(color), thickness, dashed ? new DashStyle(new double[] { 3, 2 }, 0) : null);
    private sealed class SeriesStyle(Color color, bool dashed = false)
    {
        public Pen Pen { get; } = MakePen(color, 1.35, dashed);
        public Pen PartialPen { get; } = MakePen(Color.FromArgb(130, color.R, color.G, color.B), 1.2, dashed: true);
    }
}
