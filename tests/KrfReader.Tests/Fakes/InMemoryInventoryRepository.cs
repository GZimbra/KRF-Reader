using KrfReader.Application.Persistence;
using KrfReader.Application.Pipeline;
using KrfReader.Domain;

namespace KrfReader.Tests.Fakes;

/// <summary>
/// Repositório em memória que aplica <see cref="InventoryRules"/> e registra cada chamada de
/// <see cref="ApplyBatchAsync"/>. <see cref="Block"/> segura os lotes seguintes até <see cref="Unblock"/>
/// (simula banco lento).
/// </summary>
public sealed class InMemoryInventoryRepository : IInventoryRepository
{
    public sealed record BatchCall(OperationMode Mode, IReadOnlyList<TagRead> Reads, BatchResult Result);

    private readonly object _sync = new();
    private readonly Dictionary<string, TagState> _tags = new(StringComparer.Ordinal);
    private readonly List<InventoryEvent> _events = [];
    private readonly List<BatchCall> _calls = [];
    private readonly List<PassageSnapshot> _passages = [];
    private TaskCompletionSource? _gate;
    private TaskCompletionSource _batchEntered = NewTcs();
    private long _nextEventId;

    public int InitializeCalls { get; private set; }

    /// <summary>Completa quando um lote entra em <see cref="ApplyBatchAsync"/> (antes do gate).</summary>
    public Task BatchEntered
    {
        get { lock (_sync) return _batchEntered.Task; }
    }

    public IReadOnlyList<BatchCall> Calls
    {
        get { lock (_sync) return _calls.ToArray(); }
    }

    public IReadOnlyList<AppliedRead> Applied
    {
        get { lock (_sync) return _calls.SelectMany(c => c.Result.Applied).ToArray(); }
    }

    public IReadOnlyList<InventoryEvent> Events
    {
        get { lock (_sync) return _events.ToArray(); }
    }

    public IReadOnlyList<PassageSnapshot> Passages
    {
        get { lock (_sync) return _passages.ToArray(); }
    }

    public void Block()
    {
        lock (_sync)
        {
            _gate ??= NewTcs();
            _batchEntered = NewTcs();
        }
    }

    public void Unblock()
    {
        TaskCompletionSource? gate;
        lock (_sync)
        {
            gate = _gate;
            _gate = null;
        }
        gate?.TrySetResult();
    }

    public void Seed(string epc, TagStatus status, DateTime? atUtc = null)
    {
        var t = atUtc ?? DateTime.UtcNow;
        lock (_sync)
            _tags[epc] = new TagState(epc, status, t, t);
    }

    public TagStatus? StatusOf(string epc)
    {
        lock (_sync)
            return _tags.TryGetValue(epc, out var s) ? s.Status : null;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        InitializeCalls++;
        return Task.CompletedTask;
    }

    public async Task<BatchResult> ApplyBatchAsync(OperationMode mode, IReadOnlyList<TagRead> reads, CancellationToken cancellationToken = default)
    {
        Task? gate;
        lock (_sync)
        {
            gate = _gate?.Task;
            _batchEntered.TrySetResult();
        }

        if (gate is not null)
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        if (reads.Count == 0)
            return BatchResult.Empty;

        lock (_sync)
        {
            var applied = new List<AppliedRead>(reads.Count);
            foreach (var r in reads)
            {
                TagStatus? previous = _tags.TryGetValue(r.Epc, out var cur) ? cur.Status : null;
                var d = InventoryRules.Decide(previous, mode);
                var statusAt = d.ChangesState || cur is null ? r.TimestampUtc : cur.UltimaAtualizacaoStatusUtc;
                _tags[r.Epc] = new TagState(r.Epc, d.NewStatus, r.TimestampUtc, statusAt, UltimaAntenaId: r.AntennaId, UltimaRssi: r.Rssi);
                if (d.Event is { } ev)
                    _events.Add(new InventoryEvent(++_nextEventId, r.Epc, mode, ev, r.TimestampUtc, r.AntennaId, r.Rssi));
                applied.Add(new AppliedRead(r, mode, previous, d.NewStatus, d.Event));
            }

            var result = new BatchResult(applied);
            _calls.Add(new BatchCall(mode, reads.ToArray(), result));
            return result;
        }
    }

    public Task<InventoryCounters> GetCountersAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            return Task.FromResult(new InventoryCounters(
                _tags.Values.Count(t => t.Status == TagStatus.Dentro),
                _tags.Values.Count(t => t.Status == TagStatus.Fora),
                _events.Count(e => e.IsAnomaly)));
        }
    }

    public Task<IReadOnlyList<TagState>> QueryTagsAsync(TagStatus? status, string? search, int limit = 500, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            IReadOnlyList<TagState> list = _tags.Values
                .Where(t => status is null || t.Status == status)
                .Where(t => search is null || t.Epc.Contains(search, StringComparison.OrdinalIgnoreCase))
                .Take(limit)
                .ToArray();
            return Task.FromResult(list);
        }
    }

    public Task<IReadOnlyList<InventoryEvent>> GetEventsAsync(string epc, int limit = 200, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            IReadOnlyList<InventoryEvent> list = _events
                .Where(e => e.Epc == epc)
                .OrderByDescending(e => e.TimestampUtc).ThenByDescending(e => e.Id)
                .Take(limit)
                .ToArray();
            return Task.FromResult(list);
        }
    }

    public Task<IReadOnlyList<StockMovement>> QueryMovementsAsync(MovementFilter filter, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            IReadOnlyList<StockMovement> list = _events
                .Where(e => filter.FromUtc is null || e.TimestampUtc >= filter.FromUtc)
                .Where(e => filter.ToUtc is null || e.TimestampUtc < filter.ToUtc)
                .Where(e => filter.Tipo is null || e.Mode == filter.Tipo)
                .Where(e => !filter.OnlyAnomalies || e.IsAnomaly)
                .Where(e => filter.Search is null || e.Epc.Contains(filter.Search, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.TimestampUtc).ThenByDescending(e => e.Id)
                .Take(filter.Limit)
                .Select(e => new StockMovement(e.Id, e.Epc, null, null, e.Mode, e.Type, e.TimestampUtc, e.AntennaId, e.Rssi, null))
                .ToArray();
            return Task.FromResult(list);
        }
    }

    public Task<long?> SavePassageAsync(PassageSnapshot passage, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (passage.Mode is null)
                return Task.FromResult<long?>(null);
            _passages.Add(passage);
            return Task.FromResult<long?>(_passages.Count);
        }
    }

    private static TaskCompletionSource NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
