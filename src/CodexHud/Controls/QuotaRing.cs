using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CodexHud.Controls;

public sealed class QuotaRing : FrameworkElement
{
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
        if (double.IsNaN(value)) { c.BeginAnimation(AnimatedValueProperty, null); c.AnimatedValue = 0; return; }
        var next = Math.Clamp(value, 0, 100);
        if (double.IsNaN((double)e.OldValue) || !SystemParameters.ClientAreaAnimation) { c.BeginAnimation(AnimatedValueProperty, null); c.AnimatedValue = next; }
        else c.BeginAnimation(AnimatedValueProperty, new DoubleAnimation(next, TimeSpan.FromMilliseconds(260)) { EasingFunction = new QuadraticEase() });
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = Math.Min(ActualWidth, ActualHeight) / 2 - 5;
        var color = double.IsNaN(Value) ? Color.FromRgb(126, 139, 132) : Value < 10 ? Color.FromRgb(239, 102, 91) : Value < 20 ? Color.FromRgb(244, 174, 69) : Color.FromRgb(221, 236, 99);
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
            dc.DrawGeometry(null, new Pen(!double.IsNaN(Value) && i < AnimatedValue / 2.5 ? accent : dim, 4), g);
        }
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(70, 175, 190, 148)), .65), center, radius - 7, radius - 7);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var number = double.IsNaN(Value) ? "—" : Math.Clamp(Value, 0, 100).ToString("0", CultureInfo.InvariantCulture);
        var text = new FormattedText(number + (double.IsNaN(Value) ? "" : "%"), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Bahnschrift"), 22, accent, dpi);
        dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y - 16));
        var label = new FormattedText(IsStale ? "STALE" : "REMAIN", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Bahnschrift"), 7.5, new SolidColorBrush(Color.FromRgb(157, 169, 151)), dpi);
        dc.DrawText(label, new Point(center.X - label.Width / 2, center.Y + 11));
    }
}
