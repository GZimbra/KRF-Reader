using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace KrfReader.UI.Controls;

/// <summary>
/// Escala de sintonia da banda UHF (MHz). Com <see cref="IsLive"/> o cursor salta entre canais de 500 kHz,
/// como no salto de frequência do leitor. É indicativo: o canal real não é informado pelo leitor.
/// </summary>
public sealed class FrequencyScale : AnimatedControl
{
    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<FrequencyScale, double>(nameof(Minimum), 902);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<FrequencyScale, double>(nameof(Maximum), 928);

    /// <summary>Faixa configurada no leitor (NaN = banda inteira). Destacada na escala; o cursor salta dentro dela.</summary>
    public static readonly StyledProperty<double> BandMinimumProperty =
        AvaloniaProperty.Register<FrequencyScale, double>(nameof(BandMinimum), double.NaN);

    public static readonly StyledProperty<double> BandMaximumProperty =
        AvaloniaProperty.Register<FrequencyScale, double>(nameof(BandMaximum), double.NaN);

    public static readonly StyledProperty<bool> IsLiveProperty =
        AvaloniaProperty.Register<FrequencyScale, bool>(nameof(IsLive));

    public static readonly StyledProperty<Color> ScaleColorProperty =
        AvaloniaProperty.Register<FrequencyScale, Color>(nameof(ScaleColor), Color.Parse("#7D786F"));

    public static readonly StyledProperty<Color> CursorColorProperty =
        AvaloniaProperty.Register<FrequencyScale, Color>(nameof(CursorColor), Color.Parse("#FFA600"));

    private const double ChannelMHz = 0.5;
    private const double HopSeconds = 0.2;

    private readonly Random _random = new(915);
    private double _cursor = 915;
    private double _hopTimer;
    private readonly double[] _trail = new double[4];

    static FrequencyScale()
    {
        AffectsRender<FrequencyScale>(MinimumProperty, MaximumProperty, BandMinimumProperty, BandMaximumProperty, IsLiveProperty, ScaleColorProperty, CursorColorProperty);
    }

    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double BandMinimum { get => GetValue(BandMinimumProperty); set => SetValue(BandMinimumProperty, value); }
    public double BandMaximum { get => GetValue(BandMaximumProperty); set => SetValue(BandMaximumProperty, value); }
    public bool IsLive { get => GetValue(IsLiveProperty); set => SetValue(IsLiveProperty, value); }
    public Color ScaleColor { get => GetValue(ScaleColorProperty); set => SetValue(ScaleColorProperty, value); }
    public Color CursorColor { get => GetValue(CursorColorProperty); set => SetValue(CursorColorProperty, value); }

    protected override bool WantsAnimation => IsLive;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsLiveProperty)
            UpdateAnimation();
    }

    protected override void OnFrame(double dt)
    {
        _hopTimer += dt;
        if (_hopTimer < HopSeconds)
            return;
        _hopTimer = 0;

        for (var i = _trail.Length - 1; i > 0; i--)
            _trail[i] = _trail[i - 1];
        _trail[0] = _cursor;

        var (lo, hi) = HopRange;
        var channels = (int)((hi - lo) / ChannelMHz);
        _cursor = lo + ChannelMHz * (0.5 + _random.Next(Math.Max(1, channels)));
    }

    private bool HasBand => !double.IsNaN(BandMinimum) && !double.IsNaN(BandMaximum) && BandMaximum > BandMinimum;

    private (double Lo, double Hi) HopRange => HasBand
        ? (Math.Max(Minimum, BandMinimum), Math.Min(Maximum, BandMaximum))
        : (Minimum, Maximum);

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        var range = Maximum - Minimum;
        if (w < 40 || h < 18 || range <= 0)
            return;

        var scale = new SolidColorBrush(ScaleColor);
        var baseline = h - 13;
        double X(double mhz) => Math.Round((mhz - Minimum) / range * w) + 0.5;

        context.DrawLine(new Pen(scale, 1), new Point(0, baseline + 0.5), new Point(w, baseline + 0.5));
        if (HasBand)
        {
            var (lo, hi) = HopRange;
            context.FillRectangle(new SolidColorBrush(WithAlpha(CursorColor, 0.10)), new Rect(X(lo), 0, X(hi) - X(lo), baseline));
            context.FillRectangle(new SolidColorBrush(CursorColor), new Rect(X(lo), baseline - 1, X(hi) - X(lo), 2));
        }

        var labelEvery = range > 20 ? 2.0 : 1.0;
        var steps = (int)Math.Round(range / ChannelMHz);
        for (var i = 0; i <= steps; i++)
        {
            var mhz = Minimum + i * ChannelMHz;
            var isLabel = Math.Abs(mhz / labelEvery - Math.Round(mhz / labelEvery)) < 1e-6;
            var isMajor = Math.Abs(mhz - Math.Round(mhz)) < 1e-6;
            var len = isLabel ? 9 : isMajor ? 6 : 3;
            var x = X(mhz);
            context.DrawLine(new Pen(scale, 1), new Point(x, baseline - len), new Point(x, baseline));

            if (!isLabel)
                continue;
            var text = InstrumentText.Format(mhz.ToString("0", CultureInfo.InvariantCulture), 9, ScaleColor);
            var tx = Math.Clamp(x - text.Width / 2, 0, w - text.Width);
            context.DrawText(text, new Point(tx, baseline + 2));
        }

        if (!IsLive)
            return;

        var cursor = CursorColor;
        for (var i = 0; i < _trail.Length; i++)
        {
            if (_trail[i] <= 0)
                continue;
            var tx = X(_trail[i]);
            context.DrawLine(new Pen(new SolidColorBrush(WithAlpha(cursor, 0.30 - 0.07 * i)), 1.5),
                new Point(tx, 0), new Point(tx, baseline));
        }

        var cx = X(_cursor);
        context.DrawLine(new Pen(new SolidColorBrush(WithAlpha(cursor, 0.25)), 7), new Point(cx, 0), new Point(cx, baseline));
        context.DrawLine(new Pen(new SolidColorBrush(cursor), 2), new Point(cx, 0), new Point(cx, baseline));
    }
}
