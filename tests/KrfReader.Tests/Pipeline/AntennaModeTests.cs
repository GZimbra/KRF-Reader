using KrfReader.Application.Configuration;
using KrfReader.Application.Persistence;
using KrfReader.Application.Pipeline;
using KrfReader.Domain;
using KrfReader.Tests.Fakes;

namespace KrfReader.Tests.Pipeline;

/// <summary>Modo POR ANTENA: entrada e saída simultâneas no mesmo leitor, direção pela porta.</summary>
public sealed class AntennaModeTests : IAsyncLifetime
{
    private readonly FakeRfidReader _reader = new();
    private readonly InMemoryInventoryRepository _repo = new();
    private readonly DateTime _t0 = DateTime.UtcNow;
    private InventoryPipeline? _pipeline;

    private static AntennaDirectionMap Map(int arbitrationMs = 0, int zoneHoldMs = 3000) => AntennaDirectionMap.Create(new DockOptions
    {
        EntradaAntennas = [1, 2],
        SaidaAntennas = [3, 4],
        ArbitrationMs = arbitrationMs,
        ZoneHoldMs = zoneHoldMs,
    });

    private InventoryPipeline Create(int batchSize = 10) =>
        _pipeline = new InventoryPipeline(_reader, _repo, new PipelineOptions
        {
            DedupWindowSeconds = 5,
            MinRssi = -80,
            AllowedAntennas = [],
            WorkerCount = 2,
            QueueCapacity = 10_000,
            BatchSize = batchSize,
            FlushIntervalMs = 20,
            ShutdownTimeoutMs = 5_000,
        });

    private TagRead Read(string epc, int antenna, double offsetMs = 0, double rssi = -50) =>
        new(epc, rssi, antenna, _t0.AddMilliseconds(offsetMs));

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _repo.Unblock();
        if (_pipeline is not null)
            await _pipeline.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    }

    // ───────────────────────── mapa ─────────────────────────

    [Fact]
    public void Mapa_resolve_direcao_por_porta_e_porta_sem_doca_e_null()
    {
        var map = Map();

        Assert.Equal(OperationMode.Entrada, map.Resolve(1));
        Assert.Equal(OperationMode.Entrada, map.Resolve(2));
        Assert.Equal(OperationMode.Saida, map.Resolve(3));
        Assert.Equal(OperationMode.Saida, map.Resolve(4));
        Assert.Null(map.Resolve(5));
        Assert.Equal("ENTRADA 1,2 · SAÍDA 3,4", map.Describe());
    }

    [Theory]
    [InlineData(new int[0], new int[0], "não configuradas")]
    [InlineData(new[] { 1, 2 }, new[] { 2, 3 }, "nas duas docas")]
    [InlineData(new[] { 0 }, new[] { 3 }, "inválida")]
    [InlineData(new[] { 1 }, new[] { 9 }, "antenas ativas")]
    public void Mapa_invalido_e_recusado(int[] entrada, int[] saida, string expected)
    {
        var ok = AntennaDirectionMap.TryCreate(
            new DockOptions { EntradaAntennas = entrada, SaidaAntennas = saida }, [1, 2, 3, 4], out _, out var error);

        Assert.False(ok);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void Mapa_com_uma_doca_so_e_valido()
    {
        Assert.True(AntennaDirectionMap.TryCreate(new DockOptions { SaidaAntennas = [2] }, [1, 2], out var map, out _));
        Assert.Null(map.Resolve(1));
        Assert.Equal(OperationMode.Saida, map.Resolve(2));
    }

    [Fact]
    public void Mapas_iguais_sao_iguais()
    {
        Assert.Equal(Map(), Map());
        Assert.NotEqual(Map(), Map(arbitrationMs: 100));
        Assert.Equal(InventoryOperation.ByAntenna(Map()), InventoryOperation.ByAntenna(Map()));
    }

    // ───────────────────────── arbitragem ─────────────────────────

    private StampedRead Stamp(string epc, int antenna, double offsetMs, double rssi, long generation = 1) =>
        new(Read(epc, antenna, offsetMs, rssi), Map().Resolve(antenna)!.Value, generation);

    [Fact]
    public void Arbitragem_escolhe_a_doca_de_maior_rssi_e_descarta_a_cruzada()
    {
        var map = Map(arbitrationMs: 300);
        var arbiter = new DirectionArbiter();
        var output = new List<StampedRead>();

        Assert.Equal(0, arbiter.Offer(Stamp("A", 3, 0, -68), map, _t0, output));
        Assert.Equal(0, arbiter.Offer(Stamp("A", 1, 10, -45), map, _t0, output));
        Assert.Equal(0, arbiter.Offer(Stamp("A", 2, 20, -50), map, _t0, output));
        Assert.Empty(output);
        Assert.Equal(_t0.AddMilliseconds(300), arbiter.NextDeadline);

        Assert.Equal(0, arbiter.Expire(_t0.AddMilliseconds(299), output));
        Assert.Empty(output);

        var cross = arbiter.Expire(_t0.AddMilliseconds(300), output);

        Assert.Equal(1, cross);
        Assert.Equal(2, output.Count);
        Assert.All(output, r => Assert.Equal(OperationMode.Entrada, r.Mode));
        Assert.False(arbiter.HasPending);
    }

    [Fact]
    public void Epc_decidido_ignora_a_outra_doca_ate_sair_da_zona()
    {
        var map = Map(arbitrationMs: 0, zoneHoldMs: 1000);
        var arbiter = new DirectionArbiter();
        var output = new List<StampedRead>();

        arbiter.Offer(Stamp("A", 1, 0, -45), map, _t0, output);
        arbiter.Offer(Stamp("A", 1, 800, -45), map, _t0, output);
        // Ainda lido na entrada há 200 ms: a saída é leitura cruzada.
        Assert.Equal(1, arbiter.Offer(Stamp("A", 3, 1000, -40), map, _t0, output));
        Assert.Equal(2, output.Count);

        // 1000 ms sem leitura na entrada: o item saiu de lá e agora está na doca de saída.
        Assert.Equal(0, arbiter.Offer(Stamp("A", 3, 1800, -40), map, _t0, output));
        Assert.Equal(3, output.Count);
        Assert.Equal(OperationMode.Saida, output[^1].Mode);
    }

    [Fact]
    public void FlushAll_decide_pendencias_e_geracao_nova_zera_o_estado()
    {
        var map = Map(arbitrationMs: 5000);
        var arbiter = new DirectionArbiter();
        var output = new List<StampedRead>();

        arbiter.Offer(Stamp("A", 3, 0, -50), map, _t0, output);
        Assert.Equal(1, arbiter.PendingCount);
        arbiter.Offer(Stamp("B", 1, 0, -50, generation: 2), map, _t0, output);
        Assert.Equal(1, arbiter.PendingCount);

        Assert.Equal(0, arbiter.FlushAll(output));
        var only = Assert.Single(output);
        Assert.Equal("B", only.Read.Epc);
    }

    [Fact]
    public void Leituras_retidas_por_doca_sao_limitadas()
    {
        var map = Map(arbitrationMs: 300);
        var arbiter = new DirectionArbiter();
        var output = new List<StampedRead>();
        for (var i = 0; i < 1000; i++)
            arbiter.Offer(Stamp("A", 1, i, -50), map, _t0, output);

        arbiter.FlushAll(output);

        Assert.Equal(DirectionArbiter.MaxHeldReadsPerDirection, output.Count);
    }

    // ───────────────────────── pipeline ─────────────────────────

    [Fact]
    public async Task Entrada_e_saida_simultaneas_no_mesmo_leitor()
    {
        var p = Create();
        var operations = new List<InventoryOperation?>();
        p.OperationChanged += (_, o) => { lock (operations) operations.Add(o); };
        await p.StartAsync().WithTimeout();

        await p.SetAntennaModeAsync(Map()).WithTimeout();

        Assert.Null(p.CurrentMode);
        Assert.True(p.CurrentOperation!.IsByAntenna);
        Assert.True(_reader.IsInventoryRunning);

        _repo.Seed("E2000000B002", TagStatus.Dentro);
        _reader.Emit(Read("E2000000A001", antenna: 1));
        _reader.Emit(Read("E2000000B002", antenna: 4));
        _reader.Emit(Read("E2000000F003", antenna: 5));

        await p.StopInventoryAsync().WithTimeout();

        Assert.Equal(TagStatus.Dentro, _repo.StatusOf("E2000000A001"));
        Assert.Equal(TagStatus.Fora, _repo.StatusOf("E2000000B002"));
        Assert.Null(_repo.StatusOf("E2000000F003"));
        var applied = _repo.Applied.ToDictionary(a => a.Read.Epc);
        Assert.Equal((OperationMode.Entrada, EventType.Criacao), (applied["E2000000A001"].Mode, applied["E2000000A001"].Event!.Value));
        Assert.Equal((OperationMode.Saida, EventType.MudancaStatus), (applied["E2000000B002"].Mode, applied["E2000000B002"].Event!.Value));
        Assert.Equal(1, p.GetStats().UnmappedAntenna);
        lock (operations)
        {
            Assert.Equal(2, operations.Count);
            Assert.True(operations[0]!.IsByAntenna);
            Assert.Null(operations[1]);
        }
    }

    [Fact]
    public async Task Mesmo_mapa_ja_ativo_e_noop_e_troca_de_manual_para_antena_abre_geracao_nova()
    {
        var p = Create();
        await p.StartAsync().WithTimeout();
        await p.SetModeAsync(OperationMode.Entrada).WithTimeout();
        var manual = p.Generation;

        await p.SetAntennaModeAsync(Map()).WithTimeout();
        var antenna = p.Generation;
        await p.SetAntennaModeAsync(Map()).WithTimeout();

        Assert.True(antenna > manual);
        Assert.Equal(antenna, p.Generation);
    }

    [Fact]
    public async Task Leitura_cruzada_da_outra_doca_nao_gera_movimento()
    {
        var p = Create();
        await p.StartAsync().WithTimeout();
        await p.SetAntennaModeAsync(Map(arbitrationMs: 200)).WithTimeout();

        // Palete na doca de entrada; antenas da saída o veem fraco por reflexão.
        for (var i = 0; i < 20; i++)
        {
            _reader.Emit(Read("E2000000A004", antenna: 1, offsetMs: i * 50, rssi: -48));
            _reader.Emit(Read("E2000000A004", antenna: 3, offsetMs: i * 50 + 5, rssi: -71));
        }

        await TestUtil.Eventually(() => _repo.Applied.Count >= 1, "arbitragem decidir e gravar");
        await p.StopInventoryAsync().WithTimeout();

        var applied = Assert.Single(_repo.Applied);
        Assert.Equal(OperationMode.Entrada, applied.Mode);
        Assert.Equal(TagStatus.Dentro, _repo.StatusOf("E2000000A004"));
        Assert.True(p.GetStats().CrossReads >= 1);
    }

    [Fact]
    public async Task Item_que_entra_e_depois_sai_gera_os_dois_movimentos_em_ordem()
    {
        var p = Create(batchSize: 1000);
        await p.StartAsync().WithTimeout();
        await p.SetAntennaModeAsync(Map(arbitrationMs: 0, zoneHoldMs: 500)).WithTimeout();

        _reader.Emit(Read("E20000006105", antenna: 2, offsetMs: 0));
        _reader.Emit(Read("E20000006105", antenna: 3, offsetMs: 2000));

        await p.StopInventoryAsync().WithTimeout();

        var applied = _repo.Applied.Where(a => a.Read.Epc == "E20000006105").ToArray();
        Assert.Equal(2, applied.Length);
        Assert.Equal((OperationMode.Entrada, TagStatus.Dentro), (applied[0].Mode, applied[0].NewStatus));
        Assert.Equal((OperationMode.Saida, TagStatus.Fora), (applied[1].Mode, applied[1].NewStatus));
        Assert.Equal(TagStatus.Fora, _repo.StatusOf("E20000006105"));
    }

    [Fact]
    public async Task Parar_grava_leituras_ainda_em_arbitragem()
    {
        var p = Create();
        await p.StartAsync().WithTimeout();
        await p.SetAntennaModeAsync(Map(arbitrationMs: 5000)).WithTimeout();

        _reader.Emit(Read("E2000000D006", antenna: 4));
        await p.StopInventoryAsync().WithTimeout();

        Assert.Equal(TagStatus.Fora, _repo.StatusOf("E2000000D006"));
        Assert.Equal(EventType.SaidaSemEntrada, Assert.Single(_repo.Applied).Event);
    }

    [Fact]
    public async Task Captura_exige_parado_tambem_no_modo_por_antena()
    {
        var p = Create();
        await p.StartAsync().WithTimeout();
        await p.SetAntennaModeAsync(Map()).WithTimeout();

        await Assert.ThrowsAsync<InvalidOperationException>(() => p.CaptureAsync(TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public async Task Duas_docas_abrem_passagens_simultaneas_e_independentes()
    {
        var p = Create();
        await using var monitor = new PassageMonitor(
            p, _reader, new InventoryOptions(), new PassageOptions { SilenceMs = 60_000, StableRounds = 1 }, -80);
        var closed = new List<PassageSnapshot>();
        monitor.PassageClosed += (_, s) => { lock (closed) closed.Add(s); };
        await p.StartAsync().WithTimeout();
        await p.SetAntennaModeAsync(Map()).WithTimeout();

        for (var i = 0; i < 5; i++)
            _reader.Emit(Read($"E2000000A1{i:X2}", antenna: 1));
        for (var i = 0; i < 3; i++)
            _reader.Emit(Read($"E2000000B1{i:X2}", antenna: 3));

        await TestUtil.Eventually(
            () => monitor.TrackerFor(OperationMode.Entrada).Snapshot()?.DistinctTags == 5
                  && monitor.TrackerFor(OperationMode.Saida).Snapshot()?.DistinctTags == 3,
            "as duas passagens receberem as leituras");
        await p.StopInventoryAsync().WithTimeout();

        lock (closed)
        {
            Assert.Equal(2, closed.Count);
            var entrada = Assert.Single(closed, s => s.Mode == OperationMode.Entrada);
            var saida = Assert.Single(closed, s => s.Mode == OperationMode.Saida);
            Assert.Equal(5, entrada.DistinctTags);
            Assert.Equal(3, saida.DistinctTags);
            Assert.NotEqual(entrada.Id, saida.Id);
        }
    }
}
