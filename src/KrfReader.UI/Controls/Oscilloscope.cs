using Avalonia;
using Avalonia.Media;

namespace KrfReader.UI.Controls;

/// <summary>
/// Tela de osciloscópio com retícula e traço de fósforo. A amplitude acompanha <see cref="Level"/>
/// (0–1, taxa de leituras normalizada). Com <see cref="IsLive"/> false o traço fica reto e o timer para.
/// O desenho é ilustrativo: não é a forma de onda real do leitor.
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
    private const int Persistence = 3;

    private readonly double[] _phaseHistory = new double[Persistence];
    private double _phase;
    private double _shownLevel;
    private int _frame;

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

    protected override bool WantsAnimation => IsLive || _shownLevel > 0.002;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsLiveProperty || change.Property == LevelProperty)
        {
            if (VisualRoot is null)
                _shownLevel = TargetLevel;
            UpdateAnimation();
        }
    }

    private double TargetLevel => IsLive ? Math.Clamp(Level, 0, 1) : 0;

    protected override void OnFrame(double dt)
    {
        for (var i = Persistence - 1; i > 0; i--)
            _phaseHistory[i] = _phaseHistory[i - 1];
        _phaseHistory[0] = _phase;

        _shownLevel += (TargetLevel - _shownLevel) * Math.Min(1, dt * 6);
        _phase += dt * (2.2 + 5 * _shownLevel);
        _frame++;
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 4 || h < 4)
            return;

        DrawGraticule(context, w, h);

        var trace = TraceColor;
        if (!IsLive && _shownLevel <= 0.002)
        {
            var flat = new Pen(new SolidColorBrush(WithAlpha(trace, 0.45)), 1.5);
            context.DrawLine(flat, new Point(0, h / 2), new Point(w, h / 2));
            return;
        }

        for (var i = Persistence - 1; i >= 0; i--)
        {
            var alpha = 0.10 + 0.08 * (Persistence - 1 - i);
            context.DrawGeometry(null, new Pen(new SolidColorBrush(WithAlpha(trace, alpha)), 1.2), BuildTrace(w, h, _phaseHistory[i], _frame - i - 1));
        }

        var geometry = BuildTrace(w, h, _phase, _frame);
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

    private StreamGeometry BuildTrace(double w, double h, double phase, int frame)
    {
        var level = _shownLevel;
        var amp = h * 0.42 * (0.05 + 0.95 * level);
        var steps = Math.Max(16, (int)(w / 3));

        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        for (var i = 0; i <= steps; i++)
        {
            var t = (double)i / steps;
            var carrier = Math.Sin(2 * Math.PI * t * 7 + phase * 3.1);
            var envelope = 0.55 + 0.45 * Math.Sin(2 * Math.PI * t * 1.25 - phase * 0.8);
            var harmonic = 0.15 * Math.Sin(2 * Math.PI * t * 23 + phase * 5.3) * level;
            var noise = (Hash(i, frame) - 0.5) * (0.06 + 0.10 * level);
            var y = h / 2 - amp * (carrier * envelope + harmonic + noise);
            var p = new Point(w * t, Math.Clamp(y, 1, h - 1));
            if (i == 0)
                ctx.BeginFigure(p, false);
            else
                ctx.LineTo(p);
        }
        ctx.EndFigure(false);
        return geometry;
    }

    private static double Hash(int i, int frame)
    {
        unchecked
        {
            var n = (uint)(i * 374761393 + frame * 668265263);
            n = (n ^ (n >> 13)) * 1274126177;
            return (n ^ (n >> 16)) / (double)uint.MaxValue;
        }
    }

    private static Color Lighten(Color c, double amount) => Color.FromArgb(c.A,
        (byte)Math.Min(255, c.R + 255 * amount),
        (byte)Math.Min(255, c.G + 255 * amount),
        (byte)Math.Min(255, c.B + 255 * amount));
}
