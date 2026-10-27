using KrfReader.Application.Configuration;
using KrfReader.Application.Readers;
using KrfReader.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrfReader.Application.Pipeline;

/// <summary>
/// Controlador de leitura de alta densidade + sessão de passagem. A cada rodada (<see cref="InventoryOptions.TargetDwellMs"/>):
/// <list type="number">
/// <item><description>fecha a rodada no <see cref="PassageTracker"/> (convergência, estimador, fechamento por silêncio);</description></item>
/// <item><description>contabiliza perdas fora do RF (queda do leitor, fila cheia, quadros corrompidos);</description></item>
/// <item><description>se o leitor aceita ajuste (<see cref="IInventoryTuning"/>), alterna o target A/B e recalcula Q para a
/// população que respondeu na rodada anterior (2^Q ≈ n: no ALOHA com quadro de L slots e n tags, a eficiência
/// n/L·(1−1/L)^(n−1) é máxima em L = n).</description></item>
/// </list>
/// Só atua com modo ativo (ENTRADA/SAÍDA); a passagem fecha no PARAR ou na troca de modo.
/// </summary>
public sealed class PassageMonitor : IAsyncDisposable
{
    private readonly IInventoryPipeline _pipeline;
    private readonly IRfidReader _reader;
    private readonly InventoryOptions _inventory;
    private readonly PassageTracker _tracker;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly TimeSpan _round;
    private readonly SemaphoreSlim _tickGate = new(1, 1);

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _lastQueueDropped;
    private ReaderDiagnostics _lastDiagnostics;
    private int _pendingErrors;

    public PassageMonitor(
        IInventoryPipeline pipeline,
        IRfidReader reader,
        InventoryOptions inventory,
        PassageOptions passage,
        double minRssi,
        ILogger<PassageMonitor>? logger = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(passage);
        ArgumentOutOfRangeException.ThrowIfLessThan(inventory.TargetDwellMs, 50, nameof(inventory.TargetDwellMs));
        ArgumentOutOfRangeException.ThrowIfLessThan(inventory.MaxQ, inventory.MinQ, nameof(inventory.MaxQ));

        _pipeline = pipeline;
        _reader = reader;
        _inventory = inventory;
        _tracker = new PassageTracker(passage, minRssi);
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _time = timeProvider ?? TimeProvider.System;
        _round = TimeSpan.FromMilliseconds(inventory.TargetDwellMs);
        _lastDiagnostics = Diagnostics();

        _pipeline.ReadAccepted += OnReadAccepted;
        _pipeline.ModeChanged += OnModeChanged;
        _pipeline.Error += OnPipelineError;
    }

    /// <summary>Retrato da passagem aberta a cada rodada (thread do timer).</summary>
    public event EventHandler<PassageSnapshot>? PassageUpdated;

    /// <summary>Passagem fechada, com veredito final (thread do timer ou de quem chamou PARAR).</summary>
    public event EventHandler<PassageSnapshot>? PassageClosed;

    public PassageTracker Tracker => _tracker;

    public InventoryControlState ControlState
    {
        get
        {
            var d = Diagnostics();
            return _reader is IInventoryTuning { IsTuningSupported: true } t
                ? new InventoryControlState(true, t.CurrentParameters, d.EmptyRounds, d.InvalidFrames)
                : new InventoryControlState(false, default, d.EmptyRounds, d.InvalidFrames);
        }
    }

    public void Start()
    {
        if (_loop is not null)
            return;
        _lastQueueDropped = _pipeline.GetStats().QueueDropped;
        _lastDiagnostics = Diagnostics();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(() => LoopAsync(ct), CancellationToken.None);
    }

    public void SetMinRssi(double minRssi) => _tracker.SetMinRssi(minRssi);

    /// <summary>
    /// Uma rodada: fecha a rodada no tracker e ajusta Q/target. Chamado pelo timer; público para teste determinístico.
    /// </summary>
    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        await _tickGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CollectIntegrity();
            var (live, closed) = _tracker.CompleteRound(_time.GetUtcNow().UtcDateTime);
            if (closed is not null)
                RaiseClosed(closed);
            if (live is not null)
                Raise(PassageUpdated, live);

            await TuneAsync(live, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _tickGate.Release();
        }
    }

    /// <summary>Próximo Q para <paramref name="responding"/> tags: ⌈log2 n⌉ limitado a [min, max]; sem tags volta ao inicial.</summary>
    public static int NextQ(int responding, int initialQ, int minQ, int maxQ)
    {
        if (responding <= 0)
            return Math.Clamp(initialQ, minQ, maxQ);
        var q = (int)Math.Ceiling(Math.Log2(responding));
        return Math.Clamp(q, minQ, maxQ);
    }

    public async ValueTask DisposeAsync()
    {
        _pipeline.ReadAccepted -= OnReadAccepted;
        _pipeline.ModeChanged -= OnModeChanged;
        _pipeline.Error -= OnPipelineError;

        if (_cts is { } cts)
        {
            cts.Cancel();
            if (_loop is { } loop)
            {
                try
                {
                    await loop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            cts.Dispose();
        }

        if (_tracker.Close(_time.GetUtcNow().UtcDateTime, "leitor encerrado") is { } closed)
            RaiseClosed(closed);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_round, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await TickAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Falha na rodada do monitor de passagem");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task TuneAsync(PassageSnapshot? live, CancellationToken ct)
    {
        if (_reader is not IInventoryTuning { IsTuningSupported: true } tuning
            || _pipeline.CurrentMode is null
            || !_reader.IsInventoryRunning
            || !_reader.IsConnected)
        {
            return;
        }

        var current = tuning.CurrentParameters;
        var target = _inventory.Target == TargetStrategy.DualAB
            ? (current.Target == InventoryTarget.A ? InventoryTarget.B : InventoryTarget.A)
            : InventoryTarget.A;
        var q = _inventory.AdaptiveQ
            ? NextQ(live?.RespondingLastRound ?? 0, _inventory.InitialQ, _inventory.MinQ, _inventory.MaxQ)
            : Math.Clamp(_inventory.InitialQ, InventoryParameters.MinQ, InventoryParameters.MaxQ);
        var next = current with { Session = _inventory.Session, Target = target, Q = q };
        if (next == current)
            return;

        await tuning.ApplyInventoryParametersAsync(next, ct).ConfigureAwait(false);
    }

    private void CollectIntegrity()
    {
        var dropped = _pipeline.GetStats().QueueDropped;
        var newDrops = dropped - Interlocked.Exchange(ref _lastQueueDropped, dropped);
        if (newDrops > 0)
            _tracker.ReportDataLoss($"{newDrops} leitura(s) descartada(s) por fila cheia.");

        var d = Diagnostics();
        if (d.LinkDrops > _lastDiagnostics.LinkDrops)
            _tracker.ReportDataLoss("Conexão com o leitor caiu durante a passagem.");
        _tracker.ReportInvalidFrames(d.InvalidFrames - _lastDiagnostics.InvalidFrames);
        _lastDiagnostics = d;

        if (Interlocked.Exchange(ref _pendingErrors, 0) > 0)
            _tracker.ReportDataLoss("Erro de leitor/gravação durante a passagem (ver barra de status).");
    }

    private ReaderDiagnostics Diagnostics() =>
        _reader is IReaderDiagnosticsSource source ? source.GetDiagnostics() : default;

    private void OnReadAccepted(object? sender, AcceptedRead e) => _tracker.OnRead(e.Read, e.Mode);

    private void OnModeChanged(object? sender, OperationMode? mode)
    {
        var reason = mode is null ? "PARAR" : "troca de modo";
        if (_tracker.Close(_time.GetUtcNow().UtcDateTime, reason) is { } closed)
            RaiseClosed(closed);
    }

    private void OnPipelineError(object? sender, string message)
    {
        if (_tracker.IsActive)
            Interlocked.Increment(ref _pendingErrors);
    }

    private void RaiseClosed(PassageSnapshot closed)
    {
        _logger.LogInformation(
            "Passagem {Id} fechada ({Reason}): {Tags} tag(s), {Rounds} rodada(s), {Stable} estável(is), estimador {Missed:0.0}, veredito {Verdict}",
            closed.Id, closed.CloseReason, closed.DistinctTags, closed.Rounds, closed.StableRounds, closed.EstimatedMissed, closed.Verdict);
        Raise(PassageClosed, closed);
    }

    private void Raise<T>(EventHandler<T>? handler, T args)
    {
        if (handler is null)
            return;
        try
        {
            handler(this, args);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Handler de evento do monitor de passagem lançou exceção");
        }
    }
}
