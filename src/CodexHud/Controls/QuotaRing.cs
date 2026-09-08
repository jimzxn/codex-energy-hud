using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CodexHud.Controls;

public sealed class QuotaRing : FrameworkElement
{
    private static readonly Color FullColor = Color.FromRgb(83, 184, 255);
    private static readonly Color HalfColor = Color.FromRgb(120, 219, 136);
    private static readonly Color LowColor = Color.FromRgb(239, 102, 91);
    private static readonly Color UnknownColor = Color.FromRgb(126, 139, 132);
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(QuotaRing), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender, Changed));
    public static readonly DependencyProperty IsStaleProperty = DependencyProperty.Register(nameof(IsStale), typeof(bool), typeof(QuotaRing), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty AnimatedValueProperty = DependencyProperty.Register(nameof(AnimatedValue), typeof(double), typeof(QuotaRing), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public bool IsStale { get => (bool)GetValue(IsStaleProperty); set => SetValue(IsStaleProperty, value); }
    private double AnimatedValue { get => (double)GetValue(AnimatedValueProperty); set => SetValue(AnimatedValueProperty, value); }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (QuotaRing)d;
        var value = (double)e.NewValue;
        var previous = c.AnimatedValue;
        c.BeginAnimation(AnimatedValueProperty, null);
        if (!double.IsFinite(value)) { c.AnimatedValue = 0; return; }
        var next = Math.Clamp(value, 0, 100);
        c.AnimatedValue = next;
        if (double.IsFinite((double)e.OldValue) && SystemParameters.ClientAreaAnimation && previous != next)
            c.BeginAnimation(AnimatedValueProperty, new DoubleAnimation(previous, next, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new QuadraticEase(),
                FillBehavior = FillBehavior.Stop
            });
    }
    private static Color ColorForPercentage(double value)
    {
        if (value <= 25) return LowColor;
        var from = value <= 50 ? LowColor : HalfColor;
        var to = value <= 50 ? HalfColor : FullColor;
        var fraction = value <= 50 ? (value - 25) / 25 : (value - 50) / 50;
        static byte Mix(byte start, byte end, double fraction) => (byte)Math.Round(start + (end - start) * fraction);
        return Color.FromRgb(Mix(from.R, to.R, fraction), Mix(from.G, to.G, fraction), Mix(from.B, to.B, fraction));
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = Math.Min(ActualWidth, ActualHeight) / 2 - 5;
        if (!double.IsFinite(radius) || radius <= 0) return;
        var known = double.IsFinite(Value);
        var displayedValue = Math.Clamp(AnimatedValue, 0, 100);
        var color = known ? ColorForPercentage(displayedValue) : UnknownColor;
        var accent = new SolidColorBrush(color) { Opacity = IsStale ? .45 : 1 };
        var dim = new SolidColorBrush(Color.FromRgb(55, 66, 56));
        for (int i = 0; i < 40; i++)
        {
            var a = (i * 9 - 88.5) * Math.PI / 180;
            var b = (i * 9 - 82.5) * Math.PI / 180;
            var g = new StreamGeometry();
            using (var s = g.Open())
            {
                s.BeginFigure(new Point(center.X + radius * Math.Cos(a), center.Y + radius * Math.Sin(a)), false, false);
                s.ArcTo(new Point(center.X + radius * Math.Cos(b), center.Y + radius * Math.Sin(b)), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            dc.DrawGeometry(null, new Pen(known && i < displayedValue / 2.5 ? accent : dim, 4), g);
        }
        if (radius > 7)
            dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(70, 175, 190, 148)), .65), center, radius - 7, radius - 7);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var number = known ? Math.Clamp(Value, 0, 100).ToString("0", CultureInfo.InvariantCulture) : "—";
        var text = new FormattedText(number + (known ? "%" : ""), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Bahnschrift"), 22, accent, dpi);
        dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y - 16));
        var label = new FormattedText(IsStale ? "STALE" : "REMAIN", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Bahnschrift"), 7.5, new SolidColorBrush(Color.FromRgb(157, 169, 151)), dpi);
        dc.DrawText(label, new Point(center.X - label.Width / 2, center.Y + 11));
    }
}
