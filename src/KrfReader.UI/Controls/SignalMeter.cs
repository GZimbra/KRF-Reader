using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace KrfReader.UI.Controls;

/// <summary>
/// Medidor de RSSI em barra segmentada, com retenção de pico e marca do limiar do filtro.
/// <see cref="Value"/> = NaN apaga o medidor (sem sinal).
/// </summary>
public sealed class SignalMeter : AnimatedControl
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<SignalMeter, double>(nameof(Value), double.NaN);

    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<SignalMeter, double>(nameof(Minimum), -100);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<SignalMeter, double>(nameof(Maximum), -20);

    public static readonly StyledProperty<double> ThresholdProperty =
        AvaloniaProperty.Register<SignalMeter, double>(nameof(Threshold), double.NaN);

    public static readonly StyledProperty<int> SegmentsProperty =
        AvaloniaProperty.Register<SignalMeter, int>(nameof(Segments), 40);

    public static readonly StyledProperty<bool> ShowScaleProperty =
        AvaloniaProperty.Register<SignalMeter, bool>(nameof(ShowScale), true);

    public static readonly StyledProperty<Color> LitColorProperty =
        AvaloniaProperty.Register<SignalMeter, Color>(nameof(LitColor), Color.Parse("#FDCB98"));

    public static readonly StyledProperty<Color> HotColorProperty =
        AvaloniaProperty.Register<SignalMeter, Color>(nameof(HotColor), Color.Parse("#FFA600"));

    public static readonly StyledProperty<Color> OffColorProperty =
        AvaloniaProperty.Register<SignalMeter, Color>(nameof(OffColor), Color.Parse("#2A2A2A"));

    public static readonly StyledProperty<Color> ScaleColorProperty =
        AvaloniaProperty.Register<SignalMeter, Color>(nameof(ScaleColor), Color.Parse("#7D786F"));

    private const double ScaleHeight = 14;
    private const double PeakDecayDbPerSecond = 12;
    private const double PeakHoldSeconds = 0.8;

    private double _peak = double.NaN;
    private double _peakAge;

    static SignalMeter()
    {
        AffectsRender<SignalMeter>(ValueProperty, MinimumProperty, MaximumProperty, ThresholdProperty, SegmentsProperty,
            ShowScaleProperty, LitColorProperty, HotColorProperty, OffColorProperty, ScaleColorProperty);
    }

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Threshold { get => GetValue(ThresholdProperty); set => SetValue(ThresholdProperty, value); }
    public int Segments { get => GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }
    public bool ShowScale { get => GetValue(ShowScaleProperty); set => SetValue(ShowScaleProperty, value); }
    public Color LitColor { get => GetValue(LitColorProperty); set => SetValue(LitColorProperty, value); }
    public Color HotColor { get => GetValue(HotColorProperty); set => SetValue(HotColorProperty, value); }
    public Color OffColor { get => GetValue(OffColorProperty); set => SetValue(OffColorProperty, value); }
    public Color ScaleColor { get => GetValue(ScaleColorProperty); set => SetValue(ScaleColorProperty, value); }

    protected override bool WantsAnimation => !double.IsNaN(_peak) && (double.IsNaN(Value) || _peak > Value);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ValueProperty)
            return;

        var v = Value;
        if (!double.IsNaN(v) && (double.IsNaN(_peak) || v >= _peak))
        {
            _peak = v;
            _peakAge = 0;
        }
        UpdateAnimation();
    }

    protected override void OnFrame(double dt)
    {
        _peakAge += dt;
        if (_peakAge < PeakHoldSeconds)
            return;

        _peak -= PeakDecayDbPerSecond * dt;
        var floor = double.IsNaN(Value) ? Minimum : Value;
        if (_peak <= floor)
            _peak = double.IsNaN(Value) ? double.NaN : Value;
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height - (ShowScale ? ScaleHeight : 0);
        var n = Math.Max(4, Segments);
        var range = Maximum - Minimum;
        if (w < n * 2 || h < 2 || range <= 0)
            return;

        const double gap = 2;
        var segW = (w - gap * (n - 1)) / n;
        var value = Value;
        var threshold = Threshold;

        var off = new SolidColorBrush(OffColor);
        var lit = new SolidColorBrush(LitColor);
        var dim = new SolidColorBrush(WithAlpha(LitColor, 0.35));
        var hot = new SolidColorBrush(HotColor);
        var hotFrom = (int)Math.Floor(n * 0.8);

        var peakIndex = double.IsNaN(_peak) ? -1 : (int)Math.Clamp(Math.Floor((_peak - Minimum) / range * n), 0, n - 1);

        for (var i = 0; i < n; i++)
        {
            var segStart = Minimum + range * i / n;
            var rect = new Rect(i * (segW + gap), 0, segW, h);
            IBrush brush = off;
            if (!double.IsNaN(value) && value >= segStart)
                brush = i >= hotFrom ? hot : (!double.IsNaN(threshold) && segStart < threshold ? dim : lit);
            else if (i == peakIndex)
                brush = i >= hotFrom ? hot : lit;
            context.FillRectangle(brush, rect);
        }

        if (!double.IsNaN(threshold) && threshold > Minimum && threshold < Maximum)
        {
            var x = Math.Round((threshold - Minimum) / range * w) + 0.5;
            var marker = new Pen(hot, 2);
            context.DrawLine(marker, new Point(x, -3), new Point(x, h + 3));
        }

        if (!ShowScale)
            return;

        var scale = ScaleColor;
        var step = range >= 60 ? 20 : 10;
        var first = Math.Ceiling(Minimum / step) * step;
        for (var db = first; db <= Maximum + 0.001; db += step)
        {
            var x = (db - Minimum) / range * w;
            var text = InstrumentText.Format(db.ToString("0", CultureInfo.InvariantCulture), 9, scale);
            var tx = Math.Clamp(x - text.Width / 2, 0, w - text.Width);
            context.DrawText(text, new Point(tx, h + 3));
        }
    }
}
