using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace KrfReader.UI.Controls;

/// <summary>
/// Base dos instrumentos desenhados à mão. O timer (~30 fps) só roda enquanto o controle está na
/// árvore visual e <see cref="WantsAnimation"/> é true; parado, não custa nada.
/// </summary>
public abstract class AnimatedControl : Control
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(33);

    private DispatcherTimer? _timer;
    private DateTime _lastFrameUtc;

    protected abstract bool WantsAnimation { get; }

    /// <summary>Avança o estado do instrumento. <paramref name="dt"/> em segundos.</summary>
    protected abstract void OnFrame(double dt);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateAnimation();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        StopTimer();
    }

    protected void UpdateAnimation()
    {
        if (WantsAnimation && VisualRoot is not null)
        {
            if (_timer is not null)
                return;
            _lastFrameUtc = DateTime.UtcNow;
            _timer = new DispatcherTimer(FrameInterval, DispatcherPriority.Render, OnTick);
            _timer.Start();
        }
        else
        {
            StopTimer();
        }
    }

    private void StopTimer()
    {
        _timer?.Stop();
        _timer = null;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var dt = Math.Clamp((now - _lastFrameUtc).TotalSeconds, 0, 0.25);
        _lastFrameUtc = now;
        OnFrame(dt);
        InvalidateVisual();
        UpdateAnimation();
    }

    protected static Color WithAlpha(Color c, double alpha) =>
        Color.FromArgb((byte)Math.Clamp(alpha * c.A, 0, 255), c.R, c.G, c.B);
}

/// <summary>Fonte monoespaçada dos instrumentos (mesma lista do recurso MonoFont em App.axaml).</summary>
internal static class InstrumentText
{
    public static readonly Typeface Mono = new(new FontFamily("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace"));

    public static FormattedText Format(string text, double size, Color color) =>
        new(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, size,
            new SolidColorBrush(color));
}
