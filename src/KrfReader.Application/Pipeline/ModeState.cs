using KrfReader.Domain;

namespace KrfReader.Application.Pipeline;

/// <summary>
/// Operação ativa + geração. Thread-safe: leitura sem lock de um snapshot imutável (operação e geração sempre
/// consistentes entre si); escrita serializada por lock para a geração ser estritamente crescente.
/// </summary>
public sealed class ModeState
{
    private readonly object _gate = new();
    private State _state = new(null, 0);

    /// <summary>Direção fixa do modo manual; null = parado ou POR ANTENA.</summary>
    public OperationMode? Mode => Volatile.Read(ref _state).Operation?.FixedMode;

    /// <summary>Operação ativa; null = parado.</summary>
    public InventoryOperation? Operation => Volatile.Read(ref _state).Operation;

    public long Generation => Volatile.Read(ref _state).Generation;

    public (OperationMode? Mode, long Generation) Snapshot()
    {
        var s = Volatile.Read(ref _state);
        return (s.Operation?.FixedMode, s.Generation);
    }

    public (InventoryOperation? Operation, long Generation) SnapshotOperation()
    {
        var s = Volatile.Read(ref _state);
        return (s.Operation, s.Generation);
    }

    /// <summary>Define o modo manual (null = parado), incrementa a geração e retorna a nova geração.</summary>
    public long Set(OperationMode? mode) => SetOperation(mode is { } m ? InventoryOperation.Manual(m) : null);

    /// <summary>Define a operação (null = parado), incrementa a geração e retorna a nova geração.</summary>
    public long SetOperation(InventoryOperation? operation)
    {
        lock (_gate)
        {
            var next = new State(operation, _state.Generation + 1);
            Volatile.Write(ref _state, next);
            return next.Generation;
        }
    }

    private sealed record State(InventoryOperation? Operation, long Generation);
}
