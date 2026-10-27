using KrfReader.Application.Readers;
using KrfReader.Domain;
using Microsoft.Extensions.Time.Testing;

namespace KrfReader.Tests.Fakes;

/// <summary>
/// Leitor simulado com camada MAC Gen2 simplificada, para testar a passagem de dezenas de tags sem hardware:
/// <list type="bullet">
/// <item><description>cada quadro tem 2^Q slots; cada tag elegível (no campo, flag da session = target) sorteia um slot;</description></item>
/// <item><description>slot com 2+ tags = colisão (ninguém é lido); slot com 1 tag = leitura com probabilidade
/// <see cref="SimTag.LinkProbability"/> (tag fraca, orientação ruim, metal);</description></item>
/// <item><description>tag lida inverte o flag da session (A↔B) e, em S1/S2/S3, fica quieta para aquele target;</description></item>
/// <item><description>S2/S3: flag volta para A <see cref="PersistenceS2"/> depois de a tag sair do campo.</description></item>
/// </list>
/// Fica só no projeto de testes: o executável não tem simulação.
/// </summary>
public sealed class Gen2PopulationSimulator : IRfidReader, IInventoryTuning, IReaderDiagnosticsSource
{
    public static readonly TimeSpan PersistenceS2 = TimeSpan.FromSeconds(4);

    private readonly FakeTimeProvider _time;
    private readonly Random _random;
    private readonly List<SimTag> _tags = [];
    private InventoryParameters _parameters;
    private long _emptyRounds;
    private bool _connected;
    private bool _running;

    public Gen2PopulationSimulator(FakeTimeProvider time, int seed, InventoryParameters initial)
    {
        _time = time;
        _random = new Random(seed);
        _parameters = initial;
    }

    /// <summary>Custo de um slot vazio ou com colisão (QueryRep + espera pelo RN16).</summary>
    public TimeSpan EmptySlotCost { get; set; } = TimeSpan.FromMilliseconds(0.3);

    /// <summary>Custo de um slot com leitura (ACK + EPC + notificação na serial a 115200 bps).</summary>
    public TimeSpan ReadSlotCost { get; set; } = TimeSpan.FromMilliseconds(2.5);

    public List<InventoryParameters> AppliedParameters { get; } = [];

    public IReadOnlyList<SimTag> Tags => _tags;

    public string Name => "Simulador Gen2";
    public bool IsConnected => _connected;
    public bool IsInventoryRunning => _running;
    public bool IsTuningSupported => true;
    public InventoryParameters CurrentParameters => _parameters;

    public event EventHandler<TagReadEventArgs>? TagRead;
    public event EventHandler<ReaderErrorEventArgs>? Error;

    public SimTag Add(string epc, double linkProbability = 0.95, double rssi = -50)
    {
        var tag = new SimTag(epc, linkProbability, rssi);
        _tags.Add(tag);
        return tag;
    }

    public void EnterField(IEnumerable<SimTag> tags)
    {
        foreach (var t in tags)
            t.InField = true;
    }

    public void LeaveField(IEnumerable<SimTag> tags)
    {
        foreach (var t in tags)
        {
            t.InField = false;
            t.LeftAt = _time.GetUtcNow().UtcDateTime;
        }
    }

    /// <summary>Roda o inventário por <paramref name="duration"/>, avançando o relógio e emitindo as leituras.</summary>
    public void Run(TimeSpan duration)
    {
        var end = _time.GetUtcNow() + duration;
        while (_time.GetUtcNow() < end)
        {
            var cost = _running ? RunFrame() : duration;
            var remaining = end - _time.GetUtcNow();
            _time.Advance(cost < remaining ? cost : remaining);
        }
    }

    public void RaiseError(string message) => Error?.Invoke(this, new ReaderErrorEventArgs(message));

    public Task ApplyInventoryParametersAsync(InventoryParameters parameters, CancellationToken cancellationToken = default)
    {
        _parameters = parameters;
        AppliedParameters.Add(parameters);
        return Task.CompletedTask;
    }

    public ReaderDiagnostics GetDiagnostics() => new(0, _emptyRounds, 0);

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _connected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _running = false;
        _connected = false;
        return Task.CompletedTask;
    }

    public Task StartInventoryAsync(CancellationToken cancellationToken = default)
    {
        _running = true;
        return Task.CompletedTask;
    }

    public Task StopInventoryAsync(CancellationToken cancellationToken = default)
    {
        _running = false;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _running = false;
        _connected = false;
        return ValueTask.CompletedTask;
    }

    /// <summary>Executa um quadro e devolve quanto tempo de ar ele consumiu.</summary>
    private TimeSpan RunFrame()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var p = _parameters;
        var slots = 1 << Math.Clamp(p.Q, 0, 15);

        foreach (var t in _tags)
        {
            // S0: flag só persiste energizada. S2/S3: volta para A depois da persistência fora do campo.
            if (!t.InField && (p.Session == GenSession.S0 || now - t.LeftAt >= PersistenceS2))
                t.Flag = InventoryTarget.A;
        }

        var bySlot = new Dictionary<int, List<SimTag>>();
        foreach (var t in _tags)
        {
            if (!t.InField || t.Flag != p.Target)
                continue;
            var slot = _random.Next(slots);
            if (!bySlot.TryGetValue(slot, out var list))
                bySlot[slot] = list = [];
            list.Add(t);
        }

        var reads = 0;
        foreach (var (_, list) in bySlot)
        {
            if (list.Count != 1)
                continue;
            var t = list[0];
            if (_random.NextDouble() > t.LinkProbability)
                continue;

            reads++;
            t.TimesRead++;
            t.Flag = t.Flag == InventoryTarget.A ? InventoryTarget.B : InventoryTarget.A;
            var rssi = t.Rssi + (_random.NextDouble() - 0.5) * 4;
            TagRead?.Invoke(this, new TagReadEventArgs(new TagRead(t.Epc, rssi, 1, now)));
        }

        if (reads == 0)
            _emptyRounds++;
        return EmptySlotCost * (slots - reads) + ReadSlotCost * reads;
    }

    public sealed class SimTag(string epc, double linkProbability, double rssi)
    {
        public string Epc { get; } = epc;
        public double LinkProbability { get; set; } = linkProbability;
        public double Rssi { get; } = rssi;
        public bool InField { get; set; }
        public DateTime LeftAt { get; set; } = DateTime.MinValue;
        public InventoryTarget Flag { get; set; } = InventoryTarget.A;
        public int TimesRead { get; set; }
    }
}
