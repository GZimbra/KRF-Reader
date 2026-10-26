using System.Globalization;
using KrfReader.Application.Configuration;
using KrfReader.Application.Readers;
using KrfReader.Domain;

namespace KrfReader.Infrastructure.Readers;

/// <summary>
/// Simulador de leitor UHF para rodar ponta a ponta sem hardware.
/// <para>
/// Mantém uma população de EPCs e um "campo" com algumas tags presentes ao mesmo tempo, que entram e saem
/// periodicamente. Gera ~<see cref="MockReaderOptions.ReadsPerSecond"/> leituras/s em micro-lotes (~20 ms),
/// relendo a mesma tag várias vezes por segundo como um leitor real. ~15% das leituras são "fantasmas":
/// RSSI abaixo de <see cref="PhantomRssiCeiling"/> e EPC de qualquer tag da população (fora da zona).
/// </para>
/// </summary>
public sealed class MockReader : IRfidReader
{
    /// <summary>RSSI máximo das leituras fantasmas (tags fora da zona de leitura).</summary>
    public const double PhantomRssiCeiling = -72;

    private const double PhantomRatio = 0.15;
    private const int EpcHexLength = 24;
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(20);

    [ThreadStatic] private static MockReader? t_raisingReader;

    private readonly ReaderOptions _options;
    private readonly MockReaderOptions _mock;
    private readonly TimeProvider _time;
    private readonly Random _random;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly int[] _antennas;

    private readonly string[] _population;
    private readonly int[] _field;
    private readonly double[] _fieldBaseRssi;
    private readonly bool[] _inField;

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private volatile bool _connected;
    private volatile bool _running;

    public MockReader(ReaderOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _mock = options.Mock ?? new MockReaderOptions();
        _time = timeProvider ?? TimeProvider.System;
        _random = _mock.Seed is { } seed ? new Random(seed) : new Random();
        _antennas = options.Antennas is { Length: > 0 } a ? a.Distinct().ToArray() : [1];

        _population = BuildPopulation(_mock.EpcPrefix, Math.Max(1, _mock.TagPopulation));
        var fieldSize = Math.Clamp(_mock.TagsInFieldAtOnce, 1, _population.Length);
        _field = new int[fieldSize];
        _fieldBaseRssi = new double[fieldSize];
        _inField = new bool[_population.Length];

        foreach (var (index, slot) in Enumerable.Range(0, _population.Length)
                     .OrderBy(_ => _random.Next()).Take(fieldSize).Select((idx, slot) => (idx, slot)))
        {
            _field[slot] = index;
            _inField[index] = true;
            _fieldBaseRssi[slot] = NextGoodBaseRssi();
        }
    }

    public string Name => "Mock";

    public bool IsConnected => _connected;

    public bool IsInventoryRunning => _running;

    /// <summary>EPCs simulados (útil para testes).</summary>
    public IReadOnlyList<string> Population => _population;

    public event EventHandler<TagReadEventArgs>? TagRead;

    public event EventHandler<ReaderErrorEventArgs>? Error;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _connected = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            _connected = false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StartInventoryAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_connected)
                throw new InvalidOperationException("Leitor não conectado. Chame ConnectAsync antes de iniciar o inventário.");
            if (_running)
                return;

            var cts = new CancellationTokenSource();
            _loopCts = cts;
            _running = true;
            _loopTask = Task.Run(() => RunLoopAsync(cts.Token), CancellationToken.None);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopInventoryAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);

    private async Task StopCoreAsync()
    {
        if (!_running)
            return;

        _running = false;
        var cts = _loopCts;
        var task = _loopTask;
        _loopCts = null;
        _loopTask = null;

        cts?.Cancel();
        // Chamado de dentro de um handler de TagRead (thread do loop): esperar o loop seria deadlock.
        // O token já cancelado garante que o lote corrente para antes da próxima leitura.
        if (task is not null && !ReferenceEquals(t_raisingReader, this))
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        cts?.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TickInterval, _time);
            var start = _time.GetTimestamp();
            var rate = Math.Max(0, _mock.ReadsPerSecond);
            var churnMs = Math.Max(1, _mock.FieldChurnMs);
            var nextChurnMs = NextChurnDelay(churnMs);
            long emitted = 0;

            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var elapsed = _time.GetElapsedTime(start);

                while (elapsed.TotalMilliseconds >= nextChurnMs)
                {
                    ChurnOne();
                    nextChurnMs += NextChurnDelay(churnMs);
                }

                var target = (long)(elapsed.TotalSeconds * rate);
                var count = target - emitted;
                // Após uma pausa longa (GC, máquina suspensa) não despeja um "atrasado" gigante.
                if (count > rate)
                {
                    emitted = target - rate;
                    count = rate;
                }

                if (count <= 0)
                    continue;

                var now = _time.GetUtcNow().UtcDateTime;
                var spreadTicks = TickInterval.Ticks / count;
                for (var i = 0; i < count; i++)
                {
                    if (ct.IsCancellationRequested)
                        return;
                    var timestamp = DateTime.SpecifyKind(now.AddTicks(-(count - 1 - i) * spreadTicks), DateTimeKind.Utc);
                    RaiseTagRead(NextRead(timestamp));
                }

                emitted += count;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _running = false;
            RaiseError("Falha inesperada no simulador; inventário interrompido.", ex);
        }
    }

    private TagRead NextRead(DateTime timestampUtc)
    {
        var antenna = _antennas[_random.Next(_antennas.Length)];
        var min = _mock.MinRssi;
        var max = Math.Max(_mock.MaxRssi, min);

        if (min < PhantomRssiCeiling && _random.NextDouble() < PhantomRatio)
        {
            var epc = _population[_random.Next(_population.Length)];
            var ceiling = Math.Min(PhantomRssiCeiling, max);
            return new TagRead(epc, Round(min + _random.NextDouble() * (ceiling - min) - 0.05), antenna, timestampUtc);
        }

        var slot = _random.Next(_field.Length);
        var jitter = (_random.NextDouble() + _random.NextDouble() - 1) * 3; // triangular ±3 dB
        var floor = Math.Max(min, Math.Min(PhantomRssiCeiling, max));
        var rssi = Math.Clamp(_fieldBaseRssi[slot] + jitter, floor, max);
        return new TagRead(_population[_field[slot]], Round(rssi), antenna, timestampUtc);
    }

    private void ChurnOne()
    {
        if (_field.Length >= _population.Length)
            return;

        var slot = _random.Next(_field.Length);
        int candidate;
        do
        {
            candidate = _random.Next(_population.Length);
        } while (_inField[candidate]);

        _inField[_field[slot]] = false;
        _inField[candidate] = true;
        _field[slot] = candidate;
        _fieldBaseRssi[slot] = NextGoodBaseRssi();
    }

    private double NextGoodBaseRssi()
    {
        var max = Math.Max(_mock.MaxRssi, _mock.MinRssi);
        var low = Math.Min(Math.Max(_mock.MinRssi, PhantomRssiCeiling + 4), max);
        return low + _random.NextDouble() * (max - low);
    }

    private double NextChurnDelay(int churnMs) => churnMs * (0.5 + _random.NextDouble());

    private static double Round(double rssi) => Math.Round(rssi, 1);

    private static string[] BuildPopulation(string? prefix, int count)
    {
        var normalizedPrefix = Epc.Normalize(prefix) ?? string.Empty;
        var suffixLength = Math.Max(EpcHexLength - normalizedPrefix.Length, 0);
        var minDigits = Math.Max(1, (int)Math.Ceiling(Math.Log(count, 16)));
        if (suffixLength < minDigits)
            suffixLength = minDigits;

        var result = new string[count];
        var format = "X" + suffixLength.ToString(CultureInfo.InvariantCulture);
        for (var i = 0; i < count; i++)
            result[i] = normalizedPrefix + (i + 1).ToString(format, CultureInfo.InvariantCulture);
        return result;
    }

    private void RaiseTagRead(TagRead read)
    {
        var handler = TagRead;
        if (handler is null)
            return;

        var previous = t_raisingReader;
        t_raisingReader = this;
        try
        {
            handler(this, new TagReadEventArgs(read));
        }
        catch (Exception ex)
        {
            RaiseError("Handler de TagRead lançou exceção.", ex);
        }
        finally
        {
            t_raisingReader = previous;
        }
    }

    private void RaiseError(string message, Exception? ex)
    {
        try
        {
            Error?.Invoke(this, new ReaderErrorEventArgs(message, ex));
        }
        catch
        {
            // Handler de erro com falha não pode derrubar o leitor.
        }
    }
}
