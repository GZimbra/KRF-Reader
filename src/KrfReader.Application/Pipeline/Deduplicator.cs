namespace KrfReader.Application.Pipeline;

/// <summary>
/// Deduplicação por EPC dentro de uma janela de tempo, por sessão de modo (geração).
/// NÃO é thread-safe: cada worker tem a sua instância e o particionamento por hash do EPC
/// garante que todas as leituras de um EPC passam pelo mesmo worker.
/// </summary>
public sealed class Deduplicator
{
    private readonly Dictionary<string, Entry> _lastAccepted = new(StringComparer.Ordinal);
    private readonly TimeSpan _window;

    public Deduplicator(TimeSpan window)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(window, TimeSpan.Zero);
        _window = window;
    }

    public int Count => _lastAccepted.Count;

    /// <summary>
    /// Aceita se o EPC não foi aceito na MESMA geração dentro da janela. Geração diferente = nova sessão de modo.
    /// Leitura aceita passa a ser o "último aceito" do EPC.
    /// </summary>
    public bool ShouldAccept(string epc, long generation, DateTime timestampUtc)
    {
        ArgumentNullException.ThrowIfNull(epc);

        if (_lastAccepted.TryGetValue(epc, out var last)
            && last.Generation == generation
            && timestampUtc - last.TimestampUtc < _window)
        {
            return false;
        }

        _lastAccepted[epc] = new Entry(generation, timestampUtc);
        return true;
    }

    /// <summary>Remove entradas cujo último aceite já saiu da janela. Retorna quantas removeu.</summary>
    public int Purge(DateTime nowUtc)
    {
        List<string>? expired = null;
        foreach (var (epc, entry) in _lastAccepted)
        {
            if (nowUtc - entry.TimestampUtc >= _window)
                (expired ??= []).Add(epc);
        }

        if (expired is null)
            return 0;

        foreach (var epc in expired)
            _lastAccepted.Remove(epc);
        return expired.Count;
    }

    private readonly record struct Entry(long Generation, DateTime TimestampUtc);
}
