using System.Collections.Concurrent;
using System.Threading.Channels;
using KrfReader.Application.Configuration;
using KrfReader.Application.Persistence;
using KrfReader.Application.Readers;
using KrfReader.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrfReader.Application.Pipeline;

/// <summary>
/// Implementação de <see cref="IInventoryPipeline"/>:
/// produtor (evento do leitor, nunca bloqueia) → fila de entrada bounded → dispatcher (partição por hash do EPC)
/// → N workers (geração, filtro, dedup) → fila de persistência → 1 task de lote (único escritor no banco).
/// <para>
/// Garantia central: toda leitura carrega a geração vigente no recebimento; qualquer estágio descarta itens
/// de geração diferente da atual, então nada é aplicado no banco com um modo diferente daquele em que foi lido.
/// </para>
/// </summary>
public sealed class InventoryPipeline : IInventoryPipeline
{
    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000)];

    private static readonly TimeSpan MinPurgeInterval = TimeSpan.FromSeconds(1);

    private readonly IRfidReader _reader;
    private readonly IInventoryRepository _repository;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly ModeState _modeState = new();
    private readonly ReadFilter _filter;
    private readonly SemaphoreSlim _control = new(1, 1);

    private readonly int _workerCount;
    private readonly int _queueCapacity;
    private readonly int _batchSize;
    private readonly TimeSpan _dedupWindow;
    private readonly TimeSpan _purgeInterval;
    private readonly TimeSpan _flushInterval;
    private readonly TimeSpan _shutdownTimeout;

    private long _rawReads;
    private long _filteredOut;
    private long _duplicates;
    private long _staleDiscarded;
    private long _queueDropped;
    private long _unmapped;
    private long _crossReads;
    private long _persisted;
    private ConcurrentDictionary<string, byte> _distinctEpcs = new(StringComparer.Ordinal);

    private volatile Runtime? _runtime;
    private volatile CaptureSession? _capture;
    private bool _disposed;

    public InventoryPipeline(
        IRfidReader reader,
        IInventoryRepository repository,
        PipelineOptions options,
        ILogger<InventoryPipeline>? logger = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.WorkerCount, 1, nameof(options.WorkerCount));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.QueueCapacity, 1, nameof(options.QueueCapacity));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.BatchSize, 1, nameof(options.BatchSize));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.FlushIntervalMs, 1, nameof(options.FlushIntervalMs));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.ShutdownTimeoutMs, 1, nameof(options.ShutdownTimeoutMs));
        ArgumentOutOfRangeException.ThrowIfNegative(options.DedupWindowSeconds, nameof(options.DedupWindowSeconds));

        _reader = reader;
        _repository = repository;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _time = timeProvider ?? TimeProvider.System;
        _filter = new ReadFilter(options.MinRssi, options.AllowedAntennas ?? []);

        _workerCount = options.WorkerCount;
        _queueCapacity = options.QueueCapacity;
        _batchSize = options.BatchSize;
        _dedupWindow = TimeSpan.FromSeconds(options.DedupWindowSeconds);
        _purgeInterval = _dedupWindow > MinPurgeInterval ? _dedupWindow : MinPurgeInterval;
        _flushInterval = TimeSpan.FromMilliseconds(options.FlushIntervalMs);
        _shutdownTimeout = TimeSpan.FromMilliseconds(options.ShutdownTimeoutMs);
    }

    public OperationMode? CurrentMode => _modeState.Mode;

    public InventoryOperation? CurrentOperation => _modeState.Operation;

    public long Generation => _modeState.Generation;

    public bool IsRunning => _runtime is not null;

    public event EventHandler<BatchResult>? BatchPersisted;

    public event EventHandler<OperationMode?>? ModeChanged;

    public event EventHandler<InventoryOperation?>? OperationChanged;

    public event EventHandler<AcceptedRead>? ReadAccepted;

    public event EventHandler<string>? Error;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _control.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_runtime is not null)
                return;

            ResetStats();
            _reader.TagRead += OnTagRead;
            _reader.Error += OnReaderError;
            try
            {
                await _reader.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _reader.TagRead -= OnTagRead;
                _reader.Error -= OnReaderError;
                throw;
            }

            var rt = CreateRuntime();
            var tasks = new List<Task>(_workerCount + 2)
            {
                Task.Run(() => DispatchLoopAsync(rt)),
                Task.Run(() => PersistLoopAsync(rt)),
            };
            for (var i = 0; i < _workerCount; i++)
            {
                var index = i;
                tasks.Add(Task.Run(() => WorkerLoopAsync(rt, index)));
            }

            rt.Tasks = [.. tasks];
            _runtime = rt;
            _logger.LogInformation("Pipeline iniciado com {Workers} workers no leitor {Reader}", _workerCount, _reader.Name);
        }
        finally
        {
            _control.Release();
        }
    }

    public Task SetModeAsync(OperationMode mode, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Modo inválido.");
        return SetOperationAsync(InventoryOperation.Manual(mode), cancellationToken);
    }

    public Task SetAntennaModeAsync(AntennaDirectionMap map, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(map);
        return SetOperationAsync(InventoryOperation.ByAntenna(map), cancellationToken);
    }

    private async Task SetOperationAsync(InventoryOperation operation, CancellationToken cancellationToken)
    {
        await _control.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var rt = _runtime ?? throw new InvalidOperationException("Pipeline não iniciado: chame StartAsync antes.");
            if (_modeState.Operation == operation)
                return;

            var generation = _modeState.SetOperation(operation);
            rt.Persist.Writer.TryWrite(PipelineMessage.GenerationChanged);
            _logger.LogInformation("Operação {Operation} ativa (geração {Generation})", operation, generation);
            RaiseOperationChanged(operation);

            if (!_reader.IsInventoryRunning)
            {
                try
                {
                    await _reader.StartInventoryAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _modeState.SetOperation(null);
                    RaiseOperationChanged(null);
                    ReportError($"Falha ao iniciar inventário: {ex.Message}", ex);
                    throw;
                }
            }
        }
        finally
        {
            _control.Release();
        }
    }

    public async Task StopInventoryAsync(CancellationToken cancellationToken = default)
    {
        await _control.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopReaderInventoryAsync(cancellationToken).ConfigureAwait(false);

            if (_modeState.Operation is null)
                return;

            try
            {
                if (_runtime is { } rt)
                    await FlushBarrierAsync(rt, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // Só depois da barreira: as leituras pendentes da geração atual já foram persistidas com o modo certo.
                _modeState.SetOperation(null);
                _logger.LogInformation("Inventário parado");
                RaiseOperationChanged(null);
            }
        }
        finally
        {
            _control.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _control.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ShutdownCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _control.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _control.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            await ShutdownCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _control.Release();
        }
    }

    public async Task<TagCaptureResult> CaptureAsync(TimeSpan window, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero, nameof(window));

        await _control.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_runtime is null)
                throw new InvalidOperationException("Pipeline não iniciado: chame StartAsync antes.");
            if (_modeState.Operation is not null)
                throw new InvalidOperationException("Captura exige o inventário PARADO.");

            var session = new CaptureSession();
            _capture = session;
            try
            {
                await _reader.StartInventoryAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await Task.Delay(window, _time, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    await StopReaderInventoryAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                _capture = null;
            }

            var result = session.ToResult();
            _logger.LogInformation("Captura: {Count} tag(s) em {Window} ms", result.Tags.Count, window.TotalMilliseconds);
            return result;
        }
        finally
        {
            _control.Release();
        }
    }

    public void UpdateFilter(double minRssi, IReadOnlyCollection<int> allowedAntennas) =>
        _filter.Update(minRssi, allowedAntennas);

    public PipelineStats GetStats() => new(
        Interlocked.Read(ref _rawReads),
        Interlocked.Read(ref _filteredOut),
        Interlocked.Read(ref _duplicates),
        Interlocked.Read(ref _staleDiscarded),
        Interlocked.Read(ref _queueDropped),
        Interlocked.Read(ref _persisted),
        Volatile.Read(ref _distinctEpcs).Count)
    {
        UnmappedAntenna = Interlocked.Read(ref _unmapped),
        CrossReads = Interlocked.Read(ref _crossReads),
    };

    // ---------------------------------------------------------------- produtor

    /// <summary>Roda na thread do leitor: carimba e enfileira sem bloquear.</summary>
    private void OnTagRead(object? sender, TagReadEventArgs e)
    {
        try
        {
            Interlocked.Increment(ref _rawReads);
            var rt = _runtime;
            var (operation, generation) = _modeState.SnapshotOperation();
            if (rt is not null && operation is null && _capture is { } capture)
            {
                OnCaptureRead(capture, e.Read);
                return;
            }

            if (rt is null || operation is null)
            {
                Interlocked.Increment(ref _staleDiscarded);
                return;
            }

            // POR ANTENA: a direção é a da doca da porta; porta sem doca não movimenta estoque.
            if (operation.Resolve(e.Read.AntennaId) is not { } mode)
            {
                Interlocked.Increment(ref _unmapped);
                return;
            }

            // Drivers de terceiros podem não normalizar; EPC inválido derrubaria o lote inteiro no repositório.
            var epc = Epc.Normalize(e.Read.Epc);
            if (epc is null)
            {
                Interlocked.Increment(ref _filteredOut);
                return;
            }

            var read = epc == e.Read.Epc ? e.Read : e.Read with { Epc = epc };
            // POR ANTENA o evento sai do worker, depois da arbitragem: leitura cruzada não pode abrir passagem na outra doca.
            if (!operation.IsByAntenna && ReadAccepted is { } accepted && _filter.Accept(read))
                Raise(accepted, new AcceptedRead(read, mode));

            if (!rt.Input.Writer.TryWrite(PipelineMessage.ForRead(new StampedRead(read, mode, generation))))
                Interlocked.Increment(ref _queueDropped);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro inesperado ao enfileirar leitura");
        }
    }

    /// <summary>Captura: mesmo filtro do inventário, sem dedup (a contagem de leituras é informação útil).</summary>
    private void OnCaptureRead(CaptureSession capture, TagRead raw)
    {
        var epc = Epc.Normalize(raw.Epc);
        if (epc is null)
        {
            Interlocked.Increment(ref _filteredOut);
            return;
        }

        var read = raw with { Epc = epc };
        if (!_filter.Accept(read))
        {
            Interlocked.Increment(ref _filteredOut);
            return;
        }

        capture.Add(read);
    }

    private void OnReaderError(object? sender, ReaderErrorEventArgs e) =>
        ReportError($"Leitor: {e.Message}", e.Exception);

    // ---------------------------------------------------------------- dispatcher

    private async Task DispatchLoopAsync(Runtime rt)
    {
        var ct = rt.Cts.Token;
        try
        {
            while (await rt.Input.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (rt.Input.Reader.TryRead(out var msg))
                {
                    try
                    {
                        if (msg.Kind == PipelineMessageKind.Read)
                        {
                            var writer = rt.Workers[Partition(msg.Read.Read.Epc)].Writer;
                            if (!writer.TryWrite(msg))
                                await writer.WriteAsync(msg, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            foreach (var worker in rt.Workers)
                                await worker.Writer.WriteAsync(msg, ct).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        ReportError($"Dispatcher: {ex.Message}", ex);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogWarning("Dispatcher cancelado no shutdown");
        }
        catch (Exception ex)
        {
            ReportError($"Dispatcher encerrado por erro: {ex.Message}", ex);
        }
        finally
        {
            foreach (var worker in rt.Workers)
                worker.Writer.TryComplete();
        }
    }

    private int Partition(string epc) => (int)((uint)epc.GetHashCode() % (uint)_workerCount);

    // ---------------------------------------------------------------- workers

    private async Task WorkerLoopAsync(Runtime rt, int index)
    {
        var ct = rt.Cts.Token;
        var input = rt.Workers[index].Reader;
        var output = rt.Persist.Writer;
        var dedup = new Deduplicator(_dedupWindow);
        var arbiter = new DirectionArbiter();
        var released = new List<StampedRead>();
        var lastPurge = _time.GetUtcNow();
        try
        {
            while (true)
            {
                while (input.TryRead(out var msg))
                {
                    try
                    {
                        if (msg.Kind != PipelineMessageKind.Read)
                        {
                            // Barreira do PARAR: decide as arbitragens pendentes antes, para gravar tudo o que foi lido.
                            if (msg.Kind == PipelineMessageKind.Barrier)
                                await ReleaseAsync(arbiter.FlushAll(released)).ConfigureAwait(false);
                            await output.WriteAsync(msg, ct).ConfigureAwait(false);
                            continue;
                        }

                        var now = _time.GetUtcNow();
                        if (now - lastPurge >= _purgeInterval)
                        {
                            dedup.Purge(now.UtcDateTime);
                            arbiter.Purge(now.UtcDateTime);
                            lastPurge = now;
                        }

                        var stamped = msg.Read;
                        var (operation, generation) = _modeState.SnapshotOperation();
                        if (stamped.Generation != generation)
                        {
                            Interlocked.Increment(ref _staleDiscarded);
                            continue;
                        }

                        if (!_filter.Accept(stamped.Read))
                        {
                            Interlocked.Increment(ref _filteredOut);
                            continue;
                        }

                        if (operation?.Antennas is { } map)
                        {
                            var cross = arbiter.Offer(stamped, map, now.UtcDateTime, released);
                            await ReleaseAsync(cross).ConfigureAwait(false);
                            continue;
                        }

                        await AcceptAsync(stamped, raiseAccepted: false).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        released.Clear();
                        ReportError($"Worker {index}: {ex.Message}", ex);
                    }
                }

                await ReleaseAsync(arbiter.Expire(_time.GetUtcNow().UtcDateTime, released)).ConfigureAwait(false);

                bool more;
                if (arbiter.NextDeadline is not { } deadline)
                {
                    more = await input.WaitToReadAsync(ct).ConfigureAwait(false);
                }
                else
                {
                    var remaining = deadline - _time.GetUtcNow().UtcDateTime;
                    if (remaining <= TimeSpan.Zero)
                        continue;
                    using var timeout = new CancellationTokenSource(remaining, _time);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                    try
                    {
                        more = await input.WaitToReadAsync(linked.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        continue;
                    }
                }

                if (!more)
                    break;
            }

            // Fila encerrada (shutdown): arbitragens pendentes ainda são da geração atual e devem ser gravadas.
            await ReleaseAsync(arbiter.FlushAll(released)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogWarning("Worker {Index} cancelado no shutdown", index);
        }
        catch (Exception ex)
        {
            ReportError($"Worker {index} encerrado por erro: {ex.Message}", ex);
        }
        finally
        {
            if (Interlocked.Decrement(ref rt.ActiveWorkers) == 0)
                output.TryComplete();
        }

        // Leituras liberadas pela arbitragem: descarte de geração velha, ReadAccepted (passagem) e dedup.
        async Task ReleaseAsync(int crossReads)
        {
            if (crossReads > 0)
                Interlocked.Add(ref _crossReads, crossReads);
            if (released.Count == 0)
                return;
            try
            {
                foreach (var r in released)
                {
                    if (r.Generation != _modeState.Generation)
                    {
                        Interlocked.Increment(ref _staleDiscarded);
                        continue;
                    }

                    await AcceptAsync(r, raiseAccepted: true).ConfigureAwait(false);
                }
            }
            finally
            {
                released.Clear();
            }
        }

        async Task AcceptAsync(StampedRead r, bool raiseAccepted)
        {
            if (raiseAccepted && ReadAccepted is { } accepted)
                Raise(accepted, new AcceptedRead(r.Read, r.Mode));

            if (!dedup.ShouldAccept(r.Read.Epc, r.Generation, r.Mode, r.Read.TimestampUtc))
            {
                Interlocked.Increment(ref _duplicates);
                return;
            }

            rt.DistinctEpcs.TryAdd(r.Read.Epc, 0);
            var msg = PipelineMessage.ForRead(r);
            if (!output.TryWrite(msg))
                await output.WriteAsync(msg, ct).ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------- persistência

    private async Task PersistLoopAsync(Runtime rt)
    {
        var ct = rt.Cts.Token;
        var input = rt.Persist.Reader;
        var buffer = new BatchBuffer();
        var pendingBarriers = new List<FlushBarrier>();
        try
        {
            while (true)
            {
                while (input.TryRead(out var msg))
                {
                    try
                    {
                        await HandlePersistMessageAsync(msg, buffer, pendingBarriers, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        ReportError($"Persistência: {ex.Message}", ex);
                    }
                }

                DiscardIfStale(buffer);

                bool more;
                if (buffer.Count == 0)
                {
                    more = await input.WaitToReadAsync(ct).ConfigureAwait(false);
                }
                else
                {
                    var remaining = buffer.FirstItemAt + _flushInterval - _time.GetUtcNow();
                    if (remaining <= TimeSpan.Zero)
                    {
                        await FlushAsync(buffer, ct).ConfigureAwait(false);
                        continue;
                    }

                    // Timer de flush sem busy-wait; os CTS são descartados a cada espera (sem vazar timers).
                    using var timeout = new CancellationTokenSource(remaining, _time);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                    try
                    {
                        more = await input.WaitToReadAsync(linked.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        await FlushAsync(buffer, ct).ConfigureAwait(false);
                        continue;
                    }
                }

                if (!more)
                    break;
            }

            await FlushAsync(buffer, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (buffer.Count > 0)
                _logger.LogError("Persistência cancelada no shutdown: {Count} leituras pendentes perdidas", buffer.Count);
        }
        catch (Exception ex)
        {
            ReportError($"Persistência encerrada por erro: {ex.Message}", ex);
        }
        finally
        {
            foreach (var barrier in pendingBarriers)
                barrier.Complete();
        }
    }

    private async Task HandlePersistMessageAsync(
        PipelineMessage msg, BatchBuffer buffer, List<FlushBarrier> pendingBarriers, CancellationToken ct)
    {
        switch (msg.Kind)
        {
            case PipelineMessageKind.Read:
                var stamped = msg.Read;
                if (stamped.Generation != _modeState.Generation)
                {
                    Interlocked.Increment(ref _staleDiscarded);
                    return;
                }

                DiscardIfStale(buffer);
                if (buffer.Count > 0 && buffer.Generation != stamped.Generation)
                    DiscardBuffer(buffer);

                buffer.Add(stamped, _time.GetUtcNow());
                if (buffer.Count >= _batchSize)
                    await FlushAsync(buffer, ct).ConfigureAwait(false);
                return;

            case PipelineMessageKind.Barrier:
                var barrier = msg.Barrier!;
                if (!pendingBarriers.Contains(barrier))
                    pendingBarriers.Add(barrier);
                if (barrier.Arrive())
                {
                    pendingBarriers.Remove(barrier);
                    try
                    {
                        await FlushAsync(buffer, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        barrier.Complete();
                    }
                }

                return;

            case PipelineMessageKind.GenerationChanged:
                DiscardIfStale(buffer);
                return;
        }
    }

    private void DiscardIfStale(BatchBuffer buffer)
    {
        if (buffer.Count > 0 && buffer.Generation != _modeState.Generation)
            DiscardBuffer(buffer);
    }

    private void DiscardBuffer(BatchBuffer buffer)
    {
        Interlocked.Add(ref _staleDiscarded, buffer.Count);
        _logger.LogDebug("Descartadas {Count} leituras pendentes da geração {Generation}", buffer.Count, buffer.Generation);
        buffer.Clear();
    }

    private async Task FlushAsync(BatchBuffer buffer, CancellationToken ct)
    {
        if (buffer.Count == 0)
            return;
        if (buffer.Generation != _modeState.Generation)
        {
            DiscardBuffer(buffer);
            return;
        }

        var generation = buffer.Generation;
        // POR ANTENA o lote mistura direções: aplica trechos consecutivos de mesma direção, em ordem de chegada,
        // para o mesmo EPC entrar e depois sair (ou o contrário) na sequência em que foi lido.
        foreach (var (mode, reads) in buffer.DrainRuns())
        {
            if (!await ApplyRunAsync(mode, reads, generation, ct).ConfigureAwait(false))
                return;
        }
    }

    /// <summary>Aplica um trecho com retentativa. false = geração trocou (o resto do lote também é descartado).</summary>
    private async Task<bool> ApplyRunAsync(OperationMode mode, TagRead[] reads, long generation, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (generation != _modeState.Generation)
            {
                Interlocked.Add(ref _staleDiscarded, reads.Length);
                _logger.LogWarning("Modo trocou antes da gravação: lote de {Count} leituras descartado", reads.Length);
                return false;
            }

            try
            {
                var result = await _repository.ApplyBatchAsync(mode, reads, ct).ConfigureAwait(false);
                Interlocked.Add(ref _persisted, reads.Length);
                Raise(BatchPersisted, result ?? BatchResult.Empty);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.LogError("Persistência cancelada: lote de {Count} leituras perdido", reads.Length);
                throw;
            }
            catch (Exception ex)
            {
                ReportError(
                    $"Falha ao persistir lote de {reads.Length} leituras (tentativa {attempt + 1}/{RetryDelays.Length + 1}): {ex.Message}",
                    ex);
                if (attempt >= RetryDelays.Length)
                {
                    _logger.LogError("Lote descartado após {Attempts} tentativas: {Count} leituras perdidas",
                        attempt + 1, reads.Length);
                    return true;
                }
            }

            await Task.Delay(RetryDelays[attempt], _time, ct).ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------- parada / shutdown

    private async Task FlushBarrierAsync(Runtime rt, CancellationToken cancellationToken)
    {
        var barrier = new FlushBarrier(_workerCount);
        try
        {
            await rt.Input.Writer.WriteAsync(PipelineMessage.ForBarrier(barrier), cancellationToken).ConfigureAwait(false);
            await barrier.Completion.WaitAsync(_shutdownTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            ReportError($"PARAR: pendentes não persistidos em {_shutdownTimeout.TotalMilliseconds} ms", null);
        }
        catch (ChannelClosedException)
        {
            _logger.LogWarning("PARAR: fila de entrada já encerrada");
        }
    }

    private async Task StopReaderInventoryAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _reader.StopInventoryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ReportError($"Falha ao parar inventário: {ex.Message}", ex);
        }
    }

    private async Task ShutdownCoreAsync(CancellationToken cancellationToken)
    {
        var rt = _runtime;
        if (rt is null)
            return;

        try
        {
            await StopReaderInventoryAsync(cancellationToken).ConfigureAwait(false);
            _reader.TagRead -= OnTagRead;
            _reader.Error -= OnReaderError;
            try
            {
                await _reader.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ReportError($"Falha ao desconectar leitor: {ex.Message}", ex);
            }

            rt.Input.Writer.TryComplete();
            var all = Task.WhenAll(rt.Tasks);
            try
            {
                await all.WaitAsync(_shutdownTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                _logger.LogError("Shutdown não drenou em {Timeout} ms ou foi cancelado; cancelando tasks", _shutdownTimeout.TotalMilliseconds);
                rt.Cts.Cancel();
                try
                {
                    await all.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception inner)
                {
                    _logger.LogError(inner, "Tasks do pipeline não encerraram após cancelamento");
                }

                if (ex is OperationCanceledException)
                    throw;
            }
        }
        finally
        {
            _reader.TagRead -= OnTagRead;
            _reader.Error -= OnReaderError;
            _runtime = null;
            if (rt.Tasks.All(t => t.IsCompleted))
                rt.Cts.Dispose();

            if (_modeState.Operation is not null)
            {
                _modeState.SetOperation(null);
                RaiseOperationChanged(null);
            }

            _logger.LogInformation("Pipeline encerrado. {Stats}", GetStats());
        }
    }

    // ---------------------------------------------------------------- utilidades

    private Runtime CreateRuntime()
    {
        var workerCapacity = Math.Max(256, _queueCapacity / _workerCount);
        var workers = new Channel<PipelineMessage>[_workerCount];
        for (var i = 0; i < workers.Length; i++)
        {
            workers[i] = Channel.CreateBounded<PipelineMessage>(new BoundedChannelOptions(workerCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
            });
        }

        return new Runtime(
            Channel.CreateBounded<PipelineMessage>(new BoundedChannelOptions(_queueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            }),
            workers,
            Channel.CreateBounded<PipelineMessage>(new BoundedChannelOptions(Math.Max(1024, _batchSize * 4))
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            }),
            _distinctEpcs);
    }

    private void ResetStats()
    {
        Interlocked.Exchange(ref _rawReads, 0);
        Interlocked.Exchange(ref _filteredOut, 0);
        Interlocked.Exchange(ref _duplicates, 0);
        Interlocked.Exchange(ref _staleDiscarded, 0);
        Interlocked.Exchange(ref _queueDropped, 0);
        Interlocked.Exchange(ref _unmapped, 0);
        Interlocked.Exchange(ref _crossReads, 0);
        Interlocked.Exchange(ref _persisted, 0);
        Volatile.Write(ref _distinctEpcs, new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
    }

    private void RaiseOperationChanged(InventoryOperation? operation)
    {
        Raise(OperationChanged, operation);
        Raise(ModeChanged, operation?.FixedMode);
    }

    private void ReportError(string message, Exception? exception)
    {
        _logger.LogError(exception, "{Message}", message);
        Raise(Error, message);
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
            _logger.LogError(ex, "Handler de evento do pipeline lançou exceção");
        }
    }

    private sealed class Runtime(
        Channel<PipelineMessage> input,
        Channel<PipelineMessage>[] workers,
        Channel<PipelineMessage> persist,
        ConcurrentDictionary<string, byte> distinctEpcs)
    {
        public Channel<PipelineMessage> Input { get; } = input;
        public Channel<PipelineMessage>[] Workers { get; } = workers;
        public Channel<PipelineMessage> Persist { get; } = persist;
        public ConcurrentDictionary<string, byte> DistinctEpcs { get; } = distinctEpcs;
        public CancellationTokenSource Cts { get; } = new();
        public Task[] Tasks { get; set; } = [];
        public int ActiveWorkers = workers.Length;
    }

    /// <summary>Buffer de lote: todas as leituras são da mesma geração; a direção pode variar (modo POR ANTENA).</summary>
    private sealed class BatchBuffer
    {
        private readonly List<StampedRead> _items = [];

        public int Count => _items.Count;
        public long Generation { get; private set; }
        public DateTimeOffset FirstItemAt { get; private set; }

        public void Add(in StampedRead read, DateTimeOffset now)
        {
            if (_items.Count == 0)
            {
                Generation = read.Generation;
                FirstItemAt = now;
            }
            else if (read.Generation != Generation)
            {
                throw new InvalidOperationException("Lote misturando gerações.");
            }

            _items.Add(read);
        }

        /// <summary>Esvazia o buffer em trechos consecutivos de mesma direção, na ordem de chegada.</summary>
        public List<(OperationMode Mode, TagRead[] Reads)> DrainRuns()
        {
            var runs = new List<(OperationMode, TagRead[])>();
            var start = 0;
            for (var i = 1; i <= _items.Count; i++)
            {
                if (i < _items.Count && _items[i].Mode == _items[start].Mode)
                    continue;
                var reads = new TagRead[i - start];
                for (var j = start; j < i; j++)
                    reads[j - start] = _items[j].Read;
                runs.Add((_items[start].Mode, reads));
                start = i;
            }

            _items.Clear();
            return runs;
        }

        public void Clear() => _items.Clear();
    }

    /// <summary>Acumulador da captura. Escrito na thread do leitor, lido após o leitor parar.</summary>
    private sealed class CaptureSession
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, CapturedTag> _tags = new(StringComparer.Ordinal);

        public void Add(in TagRead read)
        {
            lock (_gate)
            {
                _tags[read.Epc] = _tags.TryGetValue(read.Epc, out var seen)
                    ? read.Rssi > seen.MaxRssi
                        ? new CapturedTag(read.Epc, read.Rssi, seen.Reads + 1, read.AntennaId)
                        : seen with { Reads = seen.Reads + 1 }
                    : new CapturedTag(read.Epc, read.Rssi, 1, read.AntennaId);
            }
        }

        public TagCaptureResult ToResult()
        {
            lock (_gate)
            {
                return new TagCaptureResult(_tags.Values
                    .OrderByDescending(t => t.MaxRssi)
                    .ThenByDescending(t => t.Reads)
                    .ThenBy(t => t.Epc, StringComparer.Ordinal)
                    .ToArray());
            }
        }
    }
}
