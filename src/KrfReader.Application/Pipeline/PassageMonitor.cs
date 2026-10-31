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
/// Só atua com operação ativa; a passagem fecha no PARAR ou na troca de modo. Há um <see cref="PassageTracker"/> por
/// direção: no modo POR ANTENA as duas docas têm passagens independentes e simultâneas (numeração compartilhada).
/// </summary>
public sealed class PassageMonitor : IAsyncDisposable
{
    private readonly IInventoryPipeline _pipeline;
    private readonly IRfidReader _reader;
    private readonly InventoryOptions _inventory;
    private readonly PassageTracker _entrada;
    private readonly PassageTracker _saida;
    private readonly PassageTracker[] _trackers;
    private long _passageIds;
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
        TimeProvider? timeProvider = null,
        PackageDirectory? packages = null)
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
        _entrada = new PassageTracker(passage, minRssi, packages, NextPassageId);
        _saida = new PassageTracker(passage, minRssi, packages, NextPassageId);
        _trackers = [_entrada, _saida];
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _time = timeProvider ?? TimeProvider.System;
        _round = TimeSpan.FromMilliseconds(inventory.TargetDwellMs);
        _lastDiagnostics = Diagnostics();

        _pipeline.ReadAccepted += OnReadAccepted;
        _pipeline.OperationChanged += OnOperationChanged;
        _pipeline.Error += OnPipelineError;
    }

    /// <summary>Retrato da passagem aberta a cada rodada (thread do timer).</summary>
    public event EventHandler<PassageSnapshot>? PassageUpdated;

    /// <summary>Passagem fechada, com veredito final (thread do timer ou de quem chamou PARAR).</summary>
    public event EventHandler<PassageSnapshot>? PassageClosed;

    /// <summary>Tracker da direção (doca).</summary>
    public PassageTracker TrackerFor(OperationMode mode) => mode == OperationMode.Entrada ? _entrada : _saida;

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

    public void SetMinRssi(double minRssi)
    {
        foreach (var t in _trackers)
            t.SetMinRssi(minRssi);
    }

    /// <summary>
    /// Uma rodada: fecha a rodada no tracker e ajusta Q/target. Chamado pelo timer; público para teste determinístico.
    /// </summary>
    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        await _tickGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CollectIntegrity();
            var now = _time.GetUtcNow().UtcDateTime;
            var responding = 0;
            foreach (var tracker in _trackers)
            {
                var (live, closed) = tracker.CompleteRound(now);
                if (closed is not null)
                    RaiseClosed(closed);
                if (live is not null)
                {
                    Raise(PassageUpdated, live);
                    // Leitor multiporta inventaria uma porta por vez: Q dimensionado para a maior população de uma doca.
                    responding = Math.Max(responding, live.RespondingLastRound);
                }
            }

            await TuneAsync(responding, cancellationToken).ConfigureAwait(false);
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
        _pipeline.OperationChanged -= OnOperationChanged;
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

        CloseAll("leitor encerrado");
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

    private async Task TuneAsync(int responding, CancellationToken ct)
    {
        if (_reader is not IInventoryTuning { IsTuningSupported: true } tuning
            || _pipeline.CurrentOperation is null
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
            ? NextQ(responding, _inventory.InitialQ, _inventory.MinQ, _inventory.MaxQ)
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
        var d = Diagnostics();
        var errors = Interlocked.Exchange(ref _pendingErrors, 0) > 0;
        // Perda fora do RF não tem doca: vale para todas as passagens abertas.
        foreach (var tracker in _trackers)
        {
            if (newDrops > 0)
                tracker.ReportDataLoss($"{newDrops} leitura(s) descartada(s) por fila cheia.");
            if (d.LinkDrops > _lastDiagnostics.LinkDrops)
                tracker.ReportDataLoss("Conexão com o leitor caiu durante a passagem.");
            tracker.ReportInvalidFrames(d.InvalidFrames - _lastDiagnostics.InvalidFrames);
            if (errors)
                tracker.ReportDataLoss("Erro de leitor/gravação durante a passagem (ver barra de status).");
        }

        _lastDiagnostics = d;
    }

    private ReaderDiagnostics Diagnostics() =>
        _reader is IReaderDiagnosticsSource source ? source.GetDiagnostics() : default;

    private long NextPassageId() => Interlocked.Increment(ref _passageIds);

    private void OnReadAccepted(object? sender, AcceptedRead e) => TrackerFor(e.Mode).OnRead(e.Read, e.Mode);

    private void OnOperationChanged(object? sender, InventoryOperation? operation) =>
        CloseAll(operation is null ? "PARAR" : "troca de modo");

    private void CloseAll(string reason)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var tracker in _trackers)
        {
            if (tracker.Close(now, reason) is { } closed)
                RaiseClosed(closed);
        }
    }

    private void OnPipelineError(object? sender, string message)
    {
        if (_trackers.Any(t => t.IsActive))
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
