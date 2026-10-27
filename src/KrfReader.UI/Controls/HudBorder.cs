using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace KrfReader.UI.Controls;

/// <summary>
/// Border com cantoneiras de painel de instrumento. Usa os estilos de <see cref="Border"/>
/// (ex.: <c>Border.card</c>), só acrescenta os cantos, desenhados por cima do conteúdo.
/// </summary>
public sealed class HudBorder : Border
{
    public static readonly StyledProperty<IBrush?> CornerBrushProperty =
        AvaloniaProperty.Register<HudBorder, IBrush?>(nameof(CornerBrush));

    public static readonly StyledProperty<double> CornerLengthProperty =
        AvaloniaProperty.Register<HudBorder, double>(nameof(CornerLength), 10);

    // Border.Render é sealed: os cantos ficam num filho visual próprio, fora do Child.
    private readonly Corners _corners = new() { ZIndex = 1 };

    public HudBorder() => VisualChildren.Add(_corners);

    public IBrush? CornerBrush { get => GetValue(CornerBrushProperty); set => SetValue(CornerBrushProperty, value); }
    public double CornerLength { get => GetValue(CornerLengthProperty); set => SetValue(CornerLengthProperty, value); }

    protected override Type StyleKeyOverride => typeof(Border);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CornerBrushProperty || change.Property == CornerLengthProperty)
        {
            _corners.Brush = CornerBrush;
            _corners.Length = CornerLength;
            _corners.InvalidateVisual();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _corners.Measure(availableSize);
        return base.MeasureOverride(availableSize);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _corners.Arrange(new Rect(finalSize));
        return base.ArrangeOverride(finalSize);
    }

    private sealed class Corners : Control
    {
        public IBrush? Brush { get; set; }
        public double Length { get; set; } = 10;

        public Corners() => IsHitTestVisible = false;

        protected override Size MeasureOverride(Size availableSize) => default;

        public override void Render(DrawingContext context)
        {
            if (Brush is not { } brush)
                return;

            var pen = new Pen(brush, 2);
            var w = Bounds.Width;
            var h = Bounds.Height;
            var l = Math.Min(Length, Math.Min(w, h) / 3);
            const double o = 1; // metade da espessura: o traço fica dentro do controle

            void Corner(double x, double y, double dx, double dy)
            {
                context.DrawLine(pen, new Point(x - dx * o, y), new Point(x + dx * l, y));
                context.DrawLine(pen, new Point(x, y - dy * o), new Point(x, y + dy * l));
            }

            Corner(o, o, 1, 1);
            Corner(w - o, o, -1, 1);
            Corner(o, h - o, 1, -1);
            Corner(w - o, h - o, -1, -1);
        }
    }
}

/// <summary>Linhas de varredura horizontais sobre a janela (efeito de tela CRT). Não recebe cliques.</summary>
public sealed class ScanlineOverlay : Control
{
    public static readonly StyledProperty<double> PitchProperty =
        AvaloniaProperty.Register<ScanlineOverlay, double>(nameof(Pitch), 3);

    public static readonly StyledProperty<Color> LineColorProperty =
        AvaloniaProperty.Register<ScanlineOverlay, Color>(nameof(LineColor), Color.Parse("#22000000"));

    static ScanlineOverlay()
    {
        AffectsRender<ScanlineOverlay>(PitchProperty, LineColorProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<ScanlineOverlay>(false);
    }

    public double Pitch { get => GetValue(PitchProperty); set => SetValue(PitchProperty, value); }
    public Color LineColor { get => GetValue(LineColorProperty); set => SetValue(LineColorProperty, value); }

    public override void Render(DrawingContext context)
    {
        var pitch = Math.Max(2, Pitch);
        var brush = new SolidColorBrush(LineColor);
        var w = Bounds.Width;
        for (var y = 0.0; y < Bounds.Height; y += pitch)
            context.FillRectangle(brush, new Rect(0, Math.Round(y), w, 1));
    }
}
