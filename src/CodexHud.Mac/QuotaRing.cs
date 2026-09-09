using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace CodexHud.Mac;

internal sealed class QuotaRing : Control
{
    private double? _value;
    private double _shown, _from;
    private DateTimeOffset _animationAt;
    private bool _stale;
    private readonly DispatcherTimer _animation = new() { Interval = TimeSpan.FromMilliseconds(25) };

    public QuotaRing()
    {
        Width = Height = 96;
        _animation.Tick += (_, _) =>
        {
            var fraction = Math.Clamp((DateTimeOffset.UtcNow - _animationAt).TotalMilliseconds / 260, 0, 1);
            _shown = _from + ((_value ?? 0) - _from) * (1 - Math.Pow(1 - fraction, 2));
            InvalidateVisual();
            if (fraction >= 1) _animation.Stop();
        };
        DetachedFromVisualTree += (_, _) => _animation.Stop();
    }

    public void Update(double? value, bool stale)
    {
        value = value is { } v && double.IsFinite(v) ? Math.Clamp(v, 0, 100) : null;
        if (_value != value)
        {
            bool animate = _value is not null && value is not null;
            _value = value;
            _from = _shown;
            _animationAt = DateTimeOffset.UtcNow;
            if (animate) _animation.Start(); else { _animation.Stop(); _shown = value ?? 0; }
        }
        _stale = stale;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = Math.Min(Bounds.Width, Bounds.Height) / 2 - 5;
        if (radius <= 0) return;
        var color = _value is null ? Color.Parse("#89968E") : PercentageColor(_shown);
        var accent = new SolidColorBrush(color, _stale ? .45 : 1);
        var dim = new SolidColorBrush(Color.Parse("#354238"));
        for (int i = 0; i < 40; i++)
        {
            var start = (i * 9 - 88.5) * Math.PI / 180;
            var end = (i * 9 - 82.5) * Math.PI / 180;
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new Point(center.X + radius * Math.Cos(start), center.Y + radius * Math.Sin(start)), false);
                g.ArcTo(new Point(center.X + radius * Math.Cos(end), center.Y + radius * Math.Sin(end)),
                    new Size(radius, radius), 0, false, SweepDirection.Clockwise);
                g.EndFigure(false);
            }
            context.DrawGeometry(null, new Pen(_value is not null && i < _shown / 2.5 ? accent : dim, 4), geometry);
        }
        var text = new FormattedText(_value is { } value ? $"{value:0}%" : "—", CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface("Menlo"), 21, accent);
        context.DrawText(text, new Point(center.X - text.Width / 2, center.Y - 17));
        var label = new FormattedText(_stale ? "STALE" : "REMAIN", CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface("Menlo"), 8, dim);
        context.DrawText(label, new Point(center.X - label.Width / 2, center.Y + 11));
    }

    private static Color PercentageColor(double value)
    {
        var low = Color.Parse("#F06960"); var half = Color.Parse("#A5D76C"); var full = Color.Parse("#5FADED");
        if (value <= 25) return low;
        var from = value <= 50 ? low : half; var to = value <= 50 ? half : full;
        var fraction = value <= 50 ? (value - 25) / 25 : (value - 50) / 50;
        byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * fraction);
        return Color.FromRgb(Mix(from.R, to.R), Mix(from.G, to.G), Mix(from.B, to.B));
    }
}
