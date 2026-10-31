using KrfReader.Domain;

namespace KrfReader.Application.Pipeline;

/// <summary>
/// Arbitragem de doca do modo POR ANTENA. Com as duas docas no mesmo leitor, uma tag numa doca costuma ser vista
/// também, mais fraca, pelas antenas da outra (lóbulo lateral, reflexão em metal/piso). Sem arbitragem cada leitura
/// cruzada viraria uma movimentação falsa (DENTRO → FORA → DENTRO).
/// <list type="number">
/// <item><description>Primeira leitura de um EPC: segura por <see cref="AntennaDirectionMap.ArbitrationWindow"/>. Ao fim,
/// vence a doca de maior RSSI de pico (empate: mais leituras); as leituras da outra doca são descartadas.</description></item>
/// <item><description>EPC decidido: leituras da mesma doca passam direto; da outra doca são cruzadas e descartadas enquanto o
/// EPC continuar sendo lido na doca decidida há menos de <see cref="AntennaDirectionMap.ZoneHold"/>. Passado esse tempo
/// sem leitura na doca decidida, o item saiu dela: a próxima leitura na outra doca reabre a arbitragem.</description></item>
/// </list>
/// <para>NÃO é thread-safe: uma instância por worker (o particionamento por EPC garante o mesmo worker por EPC).</para>
/// </summary>
internal sealed class DirectionArbiter
{
    /// <summary>Leituras retidas por doca durante a arbitragem (as demais só entram na estatística).</summary>
    internal const int MaxHeldReadsPerDirection = 32;

    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Zone> _zones = new(StringComparer.Ordinal);
    private AntennaDirectionMap? _map;
    private long _generation = -1;

    public int PendingCount => _pending.Count;

    public int ZoneCount => _zones.Count;

    public bool HasPending => _pending.Count > 0;

    /// <summary>Menor prazo de arbitragem pendente (relógio do pipeline); null sem pendências.</summary>
    public DateTime? NextDeadline
    {
        get
        {
            DateTime? next = null;
            foreach (var p in _pending.Values)
            {
                if (next is null || p.DeadlineUtc < next)
                    next = p.DeadlineUtc;
            }

            return next;
        }
    }

    /// <summary>Nova sessão (geração): zera pendências e zonas.</summary>
    public void Reset(long generation, AntennaDirectionMap map)
    {
        _generation = generation;
        _map = map;
        _pending.Clear();
        _zones.Clear();
    }

    /// <summary>
    /// Entrega uma leitura já carimbada com a direção da porta. Leituras liberadas vão para <paramref name="output"/>.
    /// Retorna quantas leituras foram descartadas como cruzadas.
    /// </summary>
    public int Offer(in StampedRead read, AntennaDirectionMap map, DateTime nowUtc, List<StampedRead> output)
    {
        if (read.Generation != _generation || !ReferenceEquals(map, _map))
            Reset(read.Generation, map);

        var epc = read.Read.Epc;
        var ts = read.Read.TimestampUtc;
        if (_zones.TryGetValue(epc, out var zone))
        {
            if (read.Mode == zone.Direction)
            {
                if (ts > zone.LastSeenUtc)
                    _zones[epc] = zone with { LastSeenUtc = ts };
                output.Add(read);
                return 0;
            }

            if (ts - zone.LastSeenUtc < map.ZoneHold)
                return 1;

            _zones.Remove(epc);
        }

        if (map.ArbitrationWindow <= TimeSpan.Zero)
        {
            _zones[epc] = new Zone(read.Mode, ts);
            output.Add(read);
            return 0;
        }

        if (!_pending.TryGetValue(epc, out var pending))
        {
            pending = new Pending(nowUtc + map.ArbitrationWindow);
            _pending[epc] = pending;
        }

        pending.Add(read);
        return 0;
    }

    /// <summary>Decide as arbitragens vencidas até <paramref name="nowUtc"/>. Retorna as leituras cruzadas descartadas.</summary>
    public int Expire(DateTime nowUtc, List<StampedRead> output)
    {
        if (_pending.Count == 0)
            return 0;

        List<string>? due = null;
        foreach (var (epc, p) in _pending)
        {
            if (p.DeadlineUtc <= nowUtc)
                (due ??= []).Add(epc);
        }

        if (due is null)
            return 0;

        var discarded = 0;
        foreach (var epc in due)
            discarded += Decide(epc, output);
        return discarded;
    }

    /// <summary>Decide todas as pendências agora (PARAR / encerramento: nada fica para trás sem gravar).</summary>
    public int FlushAll(List<StampedRead> output)
    {
        var discarded = 0;
        foreach (var epc in _pending.Keys.ToArray())
            discarded += Decide(epc, output);
        return discarded;
    }

    /// <summary>Remove zonas sem leitura há mais que a retenção (libera memória). Retorna quantas removeu.</summary>
    public int Purge(DateTime nowUtc)
    {
        if (_map is null || _zones.Count == 0)
            return 0;

        List<string>? expired = null;
        foreach (var (epc, z) in _zones)
        {
            if (nowUtc - z.LastSeenUtc >= _map.ZoneHold)
                (expired ??= []).Add(epc);
        }

        if (expired is null)
            return 0;
        foreach (var epc in expired)
            _zones.Remove(epc);
        return expired.Count;
    }

    private int Decide(string epc, List<StampedRead> output)
    {
        var p = _pending[epc];
        _pending.Remove(epc);

        var winner = p.Winner();
        var won = p.For(winner);
        _zones[epc] = new Zone(winner, won.LastSeenUtc);
        output.AddRange(won.Held);
        return p.For(Other(winner)).Count;
    }

    private static OperationMode Other(OperationMode mode) =>
        mode == OperationMode.Entrada ? OperationMode.Saida : OperationMode.Entrada;

    private readonly record struct Zone(OperationMode Direction, DateTime LastSeenUtc);

    private sealed class Pending(DateTime deadlineUtc)
    {
        private readonly DirectionStats _entrada = new();
        private readonly DirectionStats _saida = new();
        private OperationMode _first;
        private bool _hasFirst;

        public DateTime DeadlineUtc { get; } = deadlineUtc;

        public DirectionStats For(OperationMode mode) => mode == OperationMode.Entrada ? _entrada : _saida;

        public void Add(in StampedRead read)
        {
            if (!_hasFirst)
            {
                _first = read.Mode;
                _hasFirst = true;
            }

            For(read.Mode).Add(read);
        }

        /// <summary>Maior RSSI de pico; empate: mais leituras; empate: a doca que leu primeiro.</summary>
        public OperationMode Winner()
        {
            if (_entrada.Count == 0)
                return OperationMode.Saida;
            if (_saida.Count == 0)
                return OperationMode.Entrada;
            var byRssi = _entrada.PeakRssi.CompareTo(_saida.PeakRssi);
            if (byRssi != 0)
                return byRssi > 0 ? OperationMode.Entrada : OperationMode.Saida;
            if (_entrada.Count != _saida.Count)
                return _entrada.Count > _saida.Count ? OperationMode.Entrada : OperationMode.Saida;
            return _first;
        }
    }

    private sealed class DirectionStats
    {
        public List<StampedRead> Held { get; } = [];
        public int Count { get; private set; }
        public double PeakRssi { get; private set; } = double.NegativeInfinity;
        public DateTime LastSeenUtc { get; private set; } = DateTime.MinValue;

        public void Add(in StampedRead read)
        {
            Count++;
            if (read.Read.Rssi > PeakRssi)
                PeakRssi = read.Read.Rssi;
            if (read.Read.TimestampUtc > LastSeenUtc)
                LastSeenUtc = read.Read.TimestampUtc;
            if (Held.Count < MaxHeldReadsPerDirection)
                Held.Add(read);
        }
    }
}
