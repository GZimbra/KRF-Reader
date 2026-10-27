using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using KrfReader.Application.Readers;

namespace KrfReader.UI.ViewModels;

/// <summary>
/// Telemetria de RF para os instrumentos da aba Operação: taxa de leituras brutas (antes de filtro e dedup)
/// e RSSI mais forte do último intervalo. Alimentada direto pelo evento <see cref="IRfidReader.TagRead"/>.
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>Fundo de escala do mostrador de leituras/s.</summary>
    public const double ReadRateScale = 200;

    private static readonly long NoRssiBits = BitConverter.DoubleToInt64Bits(double.NegativeInfinity);
    private static readonly TimeSpan RssiHold = TimeSpan.FromSeconds(1);

    private readonly Stopwatch _signalClock = Stopwatch.StartNew();
    private long _tickReads;
    private long _tickMaxRssiBits = NoRssiBits;
    private TimeSpan _lastSignalTick;
    private TimeSpan _lastRssiAt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SignalLevel), nameof(ReadRateText))]
    private double _readRate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastRssiText))]
    private double _lastRssi = double.NaN;

    /// <summary>Leitor conectado e com inventário rodando (instrumentos animados).</summary>
    [ObservableProperty] private bool _isReading;

    public double SignalLevel => Math.Clamp(ReadRate / ReadRateScale, 0, 1);
    public string ReadRateText => $"{ReadRate:0}";
    public string LastRssiText => double.IsNaN(LastRssi) ? "--.-" : Display.Rssi(LastRssi);

    /// <summary>Faixa de frequência do modelo selecionado (ex.: "915–928 MHz"); NaN quando o modelo não define.</summary>
    public double BandMinimum => ParseBand(SelectedReaderModel.FrequencyBand).Min;

    public double BandMaximum => ParseBand(SelectedReaderModel.FrequencyBand).Max;

    private static (double Min, double Max) ParseBand(string? text)
    {
        var m = BandPattern().Match(text ?? "");
        if (!m.Success)
            return (double.NaN, double.NaN);
        static double Parse(string s) => double.Parse(s.Replace(',', '.'), CultureInfo.InvariantCulture);
        return (Parse(m.Groups[1].Value), Parse(m.Groups[2].Value));
    }

    [GeneratedRegex(@"(\d{3}(?:[.,]\d+)?)\s*[–-]\s*(\d{3}(?:[.,]\d+)?)")]
    private static partial Regex BandPattern();

    /// <summary>Thread do leitor: só contadores atômicos.</summary>
    private void OnReaderTagRead(object? sender, TagReadEventArgs e)
    {
        Interlocked.Increment(ref _tickReads);
        var rssi = e.Read.Rssi;
        var current = Interlocked.Read(ref _tickMaxRssiBits);
        while (rssi > BitConverter.Int64BitsToDouble(current))
        {
            var seen = Interlocked.CompareExchange(ref _tickMaxRssiBits, BitConverter.DoubleToInt64Bits(rssi), current);
            if (seen == current)
                break;
            current = seen;
        }
    }

    /// <summary>UI thread, a cada tick do timer.</summary>
    private void UpdateSignal(bool reading)
    {
        var now = _signalClock.Elapsed;
        var dt = (now - _lastSignalTick).TotalSeconds;
        _lastSignalTick = now;

        var reads = Interlocked.Exchange(ref _tickReads, 0);
        var maxRssi = BitConverter.Int64BitsToDouble(Interlocked.Exchange(ref _tickMaxRssiBits, NoRssiBits));

        IsReading = reading;
        if (dt > 0)
        {
            var instant = reads / dt;
            var smoothed = ReadRate * 0.55 + instant * 0.45;
            ReadRate = smoothed < 0.5 ? 0 : smoothed;
        }

        if (reads > 0 && !double.IsInfinity(maxRssi))
        {
            LastRssi = maxRssi;
            _lastRssiAt = now;
        }
        else if (now - _lastRssiAt > RssiHold)
        {
            LastRssi = double.NaN;
        }
    }

    private void ResetSignal()
    {
        Interlocked.Exchange(ref _tickReads, 0);
        Interlocked.Exchange(ref _tickMaxRssiBits, NoRssiBits);
        IsReading = false;
        ReadRate = 0;
        LastRssi = double.NaN;
    }
}
