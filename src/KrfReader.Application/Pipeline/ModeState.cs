using KrfReader.Domain;

namespace KrfReader.Application.Pipeline;

/// <summary>
/// Modo ativo + geração. Thread-safe: leitura sem lock de um snapshot imutável (modo e geração sempre
/// consistentes entre si); escrita serializada por lock para a geração ser estritamente crescente.
/// </summary>
public sealed class ModeState
{
    private readonly object _gate = new();
    private State _state = new(null, 0);

    public OperationMode? Mode => Volatile.Read(ref _state).Mode;

    public long Generation => Volatile.Read(ref _state).Generation;

    public (OperationMode? Mode, long Generation) Snapshot()
    {
        var s = Volatile.Read(ref _state);
        return (s.Mode, s.Generation);
    }

    /// <summary>Define o modo (null = parado), incrementa a geração e retorna a nova geração.</summary>
    public long Set(OperationMode? mode)
    {
        lock (_gate)
        {
            var next = new State(mode, _state.Generation + 1);
            Volatile.Write(ref _state, next);
            return next.Generation;
        }
    }

    private sealed record State(OperationMode? Mode, long Generation);
}
