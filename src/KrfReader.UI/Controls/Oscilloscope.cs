using Avalonia;
using Avalonia.Media;

namespace KrfReader.UI.Controls;

/// <summary>
/// Tela de osciloscópio com retícula e traço de fósforo, usada como registrador: o traço é o histórico de
/// <see cref="Level"/> (0–1, taxa real de leituras normalizada) nos últimos <see cref="WindowSeconds"/> s,
/// com a amostra mais nova à direita. Com <see cref="IsLive"/> false a amostragem para e o traço congela.
/// </summary>
public sealed class Oscilloscope : AnimatedControl
{
    public static readonly StyledProperty<double> LevelProperty =
        AvaloniaProperty.Register<Oscilloscope, double>(nameof(Level));

    public static readonly StyledProperty<bool> IsLiveProperty =
        AvaloniaProperty.Register<Oscilloscope, bool>(nameof(IsLive));

    public static readonly StyledProperty<Color> TraceColorProperty =
        AvaloniaProperty.Register<Oscilloscope, Color>(nameof(TraceColor), Color.Parse("#FFA600"));

    public static readonly StyledProperty<Color> GridColorProperty =
        AvaloniaProperty.Register<Oscilloscope, Color>(nameof(GridColor), Color.Parse("#2C2C2C"));

    private const int Divisions = 12;
    private const double SampleSeconds = 0.1;
    public const double WindowSeconds = 12;
    private const int SampleCount = (int)(WindowSeconds / SampleSeconds);

    private readonly double[] _samples = new double[SampleCount];
    private int _newest = -1;
    private int _filled;
    private double _sampleTimer;

    static Oscilloscope()
    {
        AffectsRender<Oscilloscope>(LevelProperty, IsLiveProperty, TraceColorProperty, GridColorProperty);
    }

    public double Level
    {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public bool IsLive
    {
        get => GetValue(IsLiveProperty);
        set => SetValue(IsLiveProperty, value);
    }

    public Color TraceColor
    {
        get => GetValue(TraceColorProperty);
        set => SetValue(TraceColorProperty, value);
    }

    public Color GridColor
    {
        get => GetValue(GridColorProperty);
        set => SetValue(GridColorProperty, value);
    }

    protected override bool WantsAnimation => IsLive;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsLiveProperty)
        {
            _sampleTimer = 0;
            UpdateAnimation();
        }
    }

    protected override void OnFrame(double dt)
    {
        _sampleTimer += dt;
        while (_sampleTimer >= SampleSeconds)
        {
            _sampleTimer -= SampleSeconds;
            _newest = (_newest + 1) % SampleCount;
            _samples[_newest] = Math.Clamp(Level, 0, 1);
            _filled = Math.Min(_filled + 1, SampleCount);
        }
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 4 || h < 4)
            return;

        DrawGraticule(context, w, h);

        var trace = IsLive ? TraceColor : WithAlpha(TraceColor, 0.45);
        if (_filled < 2)
        {
            context.DrawLine(new Pen(new SolidColorBrush(WithAlpha(TraceColor, 0.45)), 1.5), new Point(0, h - 1.5), new Point(w, h - 1.5));
            return;
        }

        var geometry = BuildTrace(w, h);
        if (IsLive)
            context.DrawGeometry(null, new Pen(new SolidColorBrush(WithAlpha(trace, 0.18)), 6, lineCap: PenLineCap.Round), geometry);
        context.DrawGeometry(null, new Pen(new SolidColorBrush(trace), 1.6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
    }

    private void DrawGraticule(DrawingContext context, double w, double h)
    {
        var grid = new Pen(new SolidColorBrush(GridColor), 1);
        var axis = new Pen(new SolidColorBrush(Lighten(GridColor, 0.08)), 1);

        var rows = Math.Max(2, (int)Math.Round(Divisions * h / w) & ~1);
        for (var i = 1; i < Divisions; i++)
        {
            var x = Math.Round(w * i / Divisions) + 0.5;
            context.DrawLine(grid, new Point(x, 0), new Point(x, h));
        }
        for (var j = 1; j < rows; j++)
        {
            var y = Math.Round(h * j / rows) + 0.5;
            context.DrawLine(grid, new Point(0, y), new Point(w, y));
        }

        // Eixos centrais com marcações menores, como na retícula de um osciloscópio de bancada.
        var cy = Math.Round(h / 2) + 0.5;
        var cx = Math.Round(w / 2) + 0.5;
        context.DrawLine(axis, new Point(0, cy), new Point(w, cy));
        context.DrawLine(axis, new Point(cx, 0), new Point(cx, h));
        var minor = w / (Divisions * 5);
        for (var x = minor; x < w; x += minor)
            context.DrawLine(axis, new Point(Math.Round(x) + 0.5, cy - 2), new Point(Math.Round(x) + 0.5, cy + 3));
        var minorY = h / (rows * 5);
        for (var y = minorY; y < h; y += minorY)
            context.DrawLine(axis, new Point(cx - 2, Math.Round(y) + 0.5), new Point(cx + 3, Math.Round(y) + 0.5));
    }

    /// <summary>Linha do histórico: x = tempo (mais novo à direita), y = nível (0 embaixo, 1 no topo).</summary>
    private StreamGeometry BuildTrace(double w, double h)
    {
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        var step = w / (SampleCount - 1);
        for (var k = 0; k < _filled; k++)
        {
            var index = (_newest - k + SampleCount) % SampleCount;
            var x = w - k * step;
            var y = (h - 2) - _samples[index] * (h - 4) + 0.5;
            var p = new Point(x, y);
            if (k == 0)
                ctx.BeginFigure(p, false);
            else
                ctx.LineTo(p);
        }
        ctx.EndFigure(false);
        return geometry;
    }

    private static Color Lighten(Color c, double amount) => Color.FromArgb(c.A,
        (byte)Math.Min(255, c.R + 255 * amount),
        (byte)Math.Min(255, c.G + 255 * amount),
        (byte)Math.Min(255, c.B + 255 * amount));
}
