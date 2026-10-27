using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace KrfReader.UI.Controls;

/// <summary>
/// Mostrador analógico de ponteiro (estilo VU). O ponteiro tem inércia de mola; acima de
/// <see cref="Maximum"/> fica encostado no fim da escala.
/// </summary>
public sealed class VuDial : AnimatedControl
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<VuDial, double>(nameof(Value));

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<VuDial, double>(nameof(Maximum), 100);

    public static readonly StyledProperty<string> UnitProperty =
        AvaloniaProperty.Register<VuDial, string>(nameof(Unit), "");

    public static readonly StyledProperty<Color> ScaleColorProperty =
        AvaloniaProperty.Register<VuDial, Color>(nameof(ScaleColor), Color.Parse("#A39E97"));

    public static readonly StyledProperty<Color> HotColorProperty =
        AvaloniaProperty.Register<VuDial, Color>(nameof(HotColor), Color.Parse("#FFA600"));

    public static readonly StyledProperty<Color> NeedleColorProperty =
        AvaloniaProperty.Register<VuDial, Color>(nameof(NeedleColor), Color.Parse("#F4F1EC"));

    private const double SweepDegrees = 100;
    private const int MajorTicks = 5;

    private double _position; // 0..1, posição exibida do ponteiro
    private double _velocity;

    static VuDial()
    {
        AffectsRender<VuDial>(ValueProperty, MaximumProperty, UnitProperty, ScaleColorProperty, HotColorProperty, NeedleColorProperty);
    }

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public string Unit { get => GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public Color ScaleColor { get => GetValue(ScaleColorProperty); set => SetValue(ScaleColorProperty, value); }
    public Color HotColor { get => GetValue(HotColorProperty); set => SetValue(HotColorProperty, value); }
    public Color NeedleColor { get => GetValue(NeedleColorProperty); set => SetValue(NeedleColorProperty, value); }

    private double Target => Maximum <= 0 ? 0 : Math.Clamp(Value / Maximum, 0, 1.03);

    protected override bool WantsAnimation => Math.Abs(Target - _position) > 0.001 || Math.Abs(_velocity) > 0.001;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty || change.Property == MaximumProperty)
        {
            if (VisualRoot is null)
                _position = Target;
            UpdateAnimation();
        }
    }

    protected override void OnFrame(double dt)
    {
        // Mola amortecida (ligeiramente subamortecida, como um galvanômetro).
        const double stiffness = 60;
        const double damping = 11;
        var accel = stiffness * (Target - _position) - damping * _velocity;
        _velocity += accel * dt;
        _position += _velocity * dt;
        if (Math.Abs(Target - _position) < 0.001 && Math.Abs(_velocity) < 0.001)
        {
            _position = Target;
            _velocity = 0;
        }
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 40 || h < 30)
            return;

        // Pivô abaixo da área visível, como num VU de painel: só o arco superior aparece.
        var radius = Math.Min(w * 0.5 / Math.Sin(Rad(SweepDegrees / 2)) * 0.92, h * 1.05);
        var pivot = new Point(w / 2, radius * 0.98 + 8);
        var scale = new SolidColorBrush(ScaleColor);
        var hot = new SolidColorBrush(HotColor);
        var hotFrom = 0.8;

        // Arco da escala: trecho final em laranja.
        DrawArc(context, pivot, radius, 0, hotFrom, new Pen(scale, 1.5));
        DrawArc(context, pivot, radius, hotFrom, 1, new Pen(hot, 4));

        var minors = MajorTicks * 4;
        for (var i = 0; i <= minors; i++)
        {
            var t = (double)i / minors;
            var major = i % 4 == 0;
            var len = major ? 10 : 5;
            var a = Angle(t);
            var p1 = Polar(pivot, radius, a);
            var p2 = Polar(pivot, radius - len, a);
            context.DrawLine(new Pen(t >= hotFrom ? hot : scale, major ? 1.6 : 1), p1, p2);

            if (!major)
                continue;
            var label = (Maximum * t).ToString("0", CultureInfo.InvariantCulture);
            var text = InstrumentText.Format(label, 10, t >= hotFrom ? HotColor : ScaleColor);
            var lp = Polar(pivot, radius - 22, a);
            context.DrawText(text, new Point(lp.X - text.Width / 2, lp.Y - text.Height / 2));
        }

        if (!string.IsNullOrEmpty(Unit))
        {
            var unit = InstrumentText.Format(Unit, 10, ScaleColor);
            context.DrawText(unit, new Point(w / 2 - unit.Width / 2, h - unit.Height - 2));
        }

        // Ponteiro com sombra e brilho.
        var angle = Angle(Math.Clamp(_position, -0.02, 1.03));
        var tip = Polar(pivot, radius + 4, angle);
        var visibleBase = Polar(pivot, radius * 0.55, angle);
        context.DrawLine(new Pen(new SolidColorBrush(WithAlpha(HotColor, 0.25)), 6, lineCap: PenLineCap.Round), visibleBase, tip);
        context.DrawLine(new Pen(new SolidColorBrush(NeedleColor), 1.8, lineCap: PenLineCap.Round), visibleBase, tip);
    }

    private static double Angle(double t) => Rad(-90 - SweepDegrees / 2 + SweepDegrees * t);

    private static double Rad(double deg) => deg * Math.PI / 180;

    private static Point Polar(Point c, double r, double a) => new(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));

    private static void DrawArc(DrawingContext context, Point c, double r, double from, double to, IPen pen)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(Polar(c, r, Angle(from)), false);
            ctx.ArcTo(Polar(c, r, Angle(to)), new Size(r, r), 0, false, SweepDirection.Clockwise);
            ctx.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);
    }
}
