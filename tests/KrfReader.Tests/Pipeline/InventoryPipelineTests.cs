using KrfReader.Application.Configuration;
using KrfReader.Application.Persistence;
using KrfReader.Application.Pipeline;
using KrfReader.Domain;
using KrfReader.Tests.Fakes;

namespace KrfReader.Tests.Pipeline;

public sealed class InventoryPipelineTests : IAsyncLifetime
{
    private readonly FakeRfidReader _reader = new();
    private readonly InMemoryInventoryRepository _repo = new();
    private readonly DateTime _t0 = DateTime.UtcNow;
    private InventoryPipeline? _pipeline;

    private static PipelineOptions Options(Action<PipelineOptions>? configure = null)
    {
        var o = new PipelineOptions
        {
            DedupWindowSeconds = 5,
            MinRssi = -70,
            AllowedAntennas = [],
            WorkerCount = 2,
            QueueCapacity = 10_000,
            BatchSize = 10,
            FlushIntervalMs = 20,
            ShutdownTimeoutMs = 5_000,
        };
        configure?.Invoke(o);
        return o;
    }

    private InventoryPipeline Create(PipelineOptions? options = null) =>
        _pipeline = new InventoryPipeline(_reader, _repo, options ?? Options());

    private TagRead Read(string epc, double offsetMs = 0, double rssi = -50, int antenna = 1) =>
        new(epc, rssi, antenna, _t0.AddMilliseconds(offsetMs));

    private static string Epc(string prefix, int i) => $"{prefix}{i:X6}";

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _repo.Unblock();
        if (_pipeline is not null)
            await _pipeline.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task StartAsync_conecta_sem_iniciar_inventario()
    {
        var p = Create();

        await p.StartAsync().WithTimeout();

        Assert.True(p.IsRunning);
        Assert.Null(p.CurrentMode);
        Assert.True(_reader.ConnectCalls >= 1);
        Assert.False(_reader.IsInventoryRunning);
    }

    [Fact]
    public async Task SetModeAsync_inicia_inventario_e_mesmo_modo_e_noop()
    {
        var p = Create();
        var modes = new List<OperationMode?>();
        p.ModeChanged += (_, m) => { lock (modes) modes.Add(m); };
        await p.StartAsync().WithTimeout();

        await p.SetModeAsync(OperationMode.Entrada).WithTimeout();
        var gen = p.Generation;

        Assert.Equal(OperationMode.Entrada, p.CurrentMode);
        Assert.True(_reader.IsInventoryRunning);

        await p.SetModeAsync(OperationMode.Entrada).WithTimeout();

        Assert.Equal(gen, p.Generation);
        lock (modes)
            Assert.Equal([OperationMode.Entrada], modes);
    }

    [Fact]
    public async Task Deduplicacao_ponta_a_ponta_500_leituras_mesma_tag_em_1s_aplica_1()
    {
        var p = Create();
        await p.StartAsync().WithTimeout();
        await p.SetModeAsync(OperationMode.Entrada).WithTimeout();

        for (var i = 0; i < 500; i++)
            _reader.Emit(Read("E2801160AAAA", offsetMs: i * 2));

        await p.StopInventoryAsync().WithTimeout();

        var applied = _repo.Applied.Where(a => a.Read.Epc == "E2801160AAAA").ToArray();
        Assert.Single(applied);
        Assert.Equal(EventType.Criacao, applied[0].Event);
        Assert.Equal(OperationMode.Entrada, applied[0].Mode);

        var stats = p.GetStats();
        Assert.Equal(500, stats.RawReads);
        Assert.Equal(499, stats.Duplicates);
        Assert.Equal(1, stats.Persisted);
        Assert.Equal(1, stats.DistinctEpcsSession);
        Assert.Equal(0, stats.QueueDropped);
    }

    [Fact]
    public async Task Troca_de_modo_nunca_aplica_leitura_antiga_com_modo_novo()
    {
        var p = Create(Options(o => o.BatchSize = 1));
        await p.StartAsync().WithTimeout();
        await p.SetModeAsync(OperationMode.Entrada).WithTimeout();

        // Banco "lento": o primeiro lote fica preso no repositório.
        _repo.Block();
        _reader.Emit(Read(Epc("A", 0), antenna: 1));
        await _repo.BatchEntered.WithTimeout();

        // Pendências da geração ENTRADA enquanto o lote está preso.
        for (var i = 1; i <= 20; i++)
            _reader.Emit(Read(Epc("A", i), offsetMs: i, antenna: 1));

        var switchTask = p.SetModeAsync(OperationMode.Saida);
        _repo.Unblock();
        await switchTask.WithTimeout();
        Assert.Equal(OperationMode.Saida, p.CurrentMode);

        // Após a troca: EPCs novos e também EPCs já emitidos antes (nova geração ⇒ não deduplicados).
        var post = Enumerable.Range(1, 10).Select(i => Epc("B", i))
            .Concat(Enumerable.Range(1, 5).Select(i => Epc("A", i)))
            .ToArray();
        foreach (var epc in post)
            _reader.Emit(Read(epc, offsetMs: 100, antenna: 2));

        await TestUtil.Eventually(
            () => _repo.Applied.Count(a => a.Read.AntennaId == 2) == post.Length,
            "leituras pós-troca aplicadas");
        await p.StopInventoryAsync().WithTimeout();

        var applied = _repo.Applied;
        Assert.All(applied.Where(a => a.Read.AntennaId == 1), a => Assert.Equal(OperationMode.Entrada, a.Mode));
        Assert.All(applied.Where(a => a.Read.AntennaId == 2), a => Assert.Equal(OperationMode.Saida, a.Mode));
        Assert.All(_repo.Calls, c => Assert.All(c.Reads, r => Assert.Equal(c.Mode == OperationMode.Entrada ? 1 : 2, r.AntennaId)));
        Assert.Equal(post.OrderBy(x => x), applied.Where(a => a.Read.AntennaId == 2).Select(a => a.Read.Epc).OrderBy(x => x));

        // B* eram desconhecidas: SAÍDA sem entrada.
        Assert.All(applied.Where(a => a.Read.Epc.StartsWith('B')), a => Assert.True(a.IsAnomaly));

        Assert.True(p.GetStats().StaleDiscarded > 0, "pendências da geração anterior deveriam ser descartadas");
    }

    [Fact]
    public async Task Troca_rapida_entrada_saida_entrada_aplica_cada_sessao()
    {
        var p = Create();
        await p.StartAsync().WithTimeout();
        const string epc = "E280116060000001";

        await p.SetModeAsync(OperationMode.Entrada).WithTimeout();
        _reader.Emit(Read(epc, 0));
        await TestUtil.Eventually(() => _repo.Applied.Count == 1, "ENTRADA aplicada");

        await p.SetModeAsync(OperationMode.Saida).WithTimeout();
        _reader.Emit(Read(epc, 100));
        await TestUtil.Eventually(() => _repo.Applied.Count == 2, "SAÍDA aplicada");

        await p.SetModeAsync(OperationMode.Entrada).WithTimeout();
        _reader.Emit(Read(epc, 200));
        await p.StopInventoryAsync().WithTimeout();

        Assert.Equal(
            [EventType.Criacao, EventType.MudancaStatus, EventType.MudancaStatus],
            _repo.Applied.Select(a => a.Event!.Value));
        Assert.Equal(TagStatus.Dentro, _repo.StatusOf(epc));
    }

    [Fact]
    public async Task Tag_ja_dentro_relida_em_entrada_apos_janela_nao_gera_evento()
    {
        var p = Create(Options(o => o.DedupWindowSeconds = 1));
        await p.StartAsync().WithTimeout();
        await p.SetModeAsync(OperationMode.Entrada).WithTimeout();
        const string epc = "E28011606000BEEF";

        _reader.Emit(Read(epc, 0));
        await TestUtil.Eventually(() => _repo.Applied.Count == 1, "primeira leitura aplicada");

        _reader.Emit(Read(epc, 2_500));
        await TestUtil.Eventually(() => _repo.Applied.Count == 2, "releitura após a janela aplicada");

        var applied = _repo.Applied;
        Assert.Equal(EventType.Criacao, applied[0].Event);
        Assert.Null(applied[1].Event);
        Assert.Equal(TagStatus.Dentro, applied[1].PreviousStatus);
        Assert.Single(_repo.Events);
    }

    [Fact]
    public async Task Tag_pre_existente_dentro_relida_em_entrada_nao_gera_evento()
    {
        const string epc = "E28011606000CAFE";
        _repo.Seed(epc, TagStatus.Dentro);
        var p = Create();
        await p.StartAsync().WithTimeout();
        await p.SetModeAsync(OperationMode.Entrada).WithTimeout();

        _reader.Emit(Read(epc));
        await p.StopInventoryAsync().WithTimeout();

        var a = Assert.Single(_repo.Applied);
        Assert.Null(a.Event);
        Assert.False(a.ChangedState);
        Assert.Empty(_repo.Events);
    }

    [Fact]
    public async Task Tag_desconhecida_em_saida_e_anomalia()
    {
        var p = Create();
        var batches = new List<BatchResult>();
        p.BatchPersisted += (_, b) => { lock (batches) batches.Add(b); };
        await p.StartAsync().WithTimeout();
        await p.SetModeAsync(OperationMode.Saida).WithTimeout();

        _reader.Emit(Read("E28011606000DEAD"));
        await TestUtil.Eventually(() => { lock (batches) return batches.Count > 0; }, "BatchPersisted disparado");

        var a = Assert.Single(_repo.Applied);
        Assert.True(a.IsAnomaly);
        Assert.Equal(EventType.SaidaSemEntrada, a.Event);
        Assert.Null(a.PreviousStatus);
        Assert.Equal(TagStatus.Fora, a.NewStatus);
        lock (batches)
            Assert.Contains(batches.SelectMany(b => b.Applied), x => x.IsAnomaly && x.Read.Epc == "E28011606000DEAD");
    }

    [Fact]
    public async Task Parar_persiste_pendentes_com_o_modo_correto()
    {
        // Lote grande e intervalo longo: nada é gravado até o PARAR.
        var p = Create(Options(o => { o.BatchSize = 10_000; o.FlushIntervalMs = 60_000; }));
        var modes = new List<OperationMode?>();
        p.ModeChanged += (_, m) => { lock (modes) modes.Add(m); };
        await p.StartAsync().WithTimeout();
        await p.SetModeAsync(OperationMode.Entrada).WithTimeout();

        var epcs = Enumerable.Range(0, 50).Select(i => Epc("C", i)).ToArray();
        foreach (var e in epcs)
            _reader.Emit(Read(e));

        await p.StopInventoryAsync().WithTimeout();

        var applied = _repo.Applied;
        Assert.Equal(epcs.Order(), applied.Select(a => a.Read.Epc).Order());
        Assert.All(applied, a => Assert.Equal(OperationMode.Entrada, a.Mode));
        Assert.Null(p.CurrentMode);
        Assert.True(_reader.StopCalls >= 1);
        Assert.False(_reader.IsInventoryRunning);
        Assert.Equal(50, p.GetStats().Persisted);
        lock (modes)
            Assert.Equal([OperationMode.Entrada, null], modes);
    }

    [Fact]
    public async Task Leituras_apos_parar_sao_descartadas()
    {
        var p = Create();
        await p.StartAsync().WithTimeout();
        await p.SetModeAsync(OperationMode.Entrada).WithTimeout();
        await p.StopInventoryAsync().WithTimeout();

        for (var i = 0; i < 5; i++)
            _reader.Emit(Read(Epc("D", i)));

        await TestUtil.Eventually(() => p.GetStats().StaleDiscarded >= 5, "leituras sem modo descartadas");
        Assert.Empty(_repo.Applied);
    }

    [Fact]
    public async Task Sem_modo_ativo_leituras_sao_descartadas_e_nada_persiste()
    {
        var p = Create();
        await p.StartAsync().WithTimeout();

        for (var i = 0; i < 10; i++)
            _reader.Emit(Read(Epc("E", i)));

        await TestUtil.Eventually(() => p.GetStats().StaleDiscarded >= 10, "leituras sem modo descartadas");
        await p.StopAsync().WithTimeout();

        Assert.Empty(_repo.Applied);
        Assert.Equal(0, p.GetStats().Persisted);
    }

    [Fact]
    public async Task StopAsync_drena_persiste_e_encerra_sem_tasks_presas()
    {
        var p = Create(Options(o => { o.BatchSize = 7; o.FlushIntervalMs = 60_000; }));
        await p.StartAsync().WithTimeout();
        await p.SetModeAsync(OperationMode.Entrada).WithTimeout();

        var epcs = Enumerable.Range(0, 100).Select(i => Epc("F", i)).ToArray();
        foreach (var e in epcs)
            _reader.Emit(Read(e));

        await p.StopAsync().WithTimeout();

        Assert.False(p.IsRunning);
        Assert.False(_reader.IsInventoryRunning);
        Assert.Equal(epcs.Order(), _repo.Applied.Select(a => a.Read.Epc).Order());
        Assert.All(_repo.Applied, a => Assert.Equal(OperationMode.Entrada, a.Mode));

        await p.DisposeAsync().WithTimeout();
        // Idempotente.
        await p.StopAsync().WithTimeout();
    }

    [Fact]
    public async Task DisposeAsync_sem_StopAsync_encerra_rapido()
    {
        var p = Create();
        await p.StartAsync().WithTimeout();
        await p.SetModeAsync(OperationMode.Saida).WithTimeout();
        _reader.Emit(Read("E28011606000F00D"));

        await p.DisposeAsync().WithTimeout();

        Assert.False(p.IsRunning);
    }

    [Fact]
    public async Task Filtro_rssi_abaixo_do_minimo_nao_persiste()
    {
        var p = Create(Options(o => o.MinRssi = -60));
        await p.StartAsync().WithTimeout();
        await p.SetModeAsync(OperationMode.Entrada).WithTimeout();

        _reader.Emit(Read("E2800001", rssi: -75));
        _reader.Emit(Read("E2800002", rssi: -60.5));
        _reader.Emit(Read("E2800003", rssi: -60));
        _reader.Emit(Read("E2800004", rssi: -40));
        await p.StopInventoryAsync().WithTimeout();

        Assert.Equal(["E2800003", "E2800004"], _repo.Applied.Select(a => a.Read.Epc).Order());
        Assert.Equal(2, p.GetStats().FilteredOut);
    }

    [Fact]
    public async Task UpdateFilter_em_runtime_vale_para_novas_leituras()
    {
        var p = Create(Options(o => { o.MinRssi = -60; o.AllowedAntennas = []; }));
        await p.StartAsync().WithTimeout();
        await p.SetModeAsync(OperationMode.Entrada).WithTimeout();

        _reader.Emit(Read("E2810001", rssi: -70, antenna: 1));
        await TestUtil.Eventually(() => p.GetStats().FilteredOut == 1, "leitura fraca filtrada");

        p.UpdateFilter(-80, [2]);

        _reader.Emit(Read("E2810002", rssi: -70, antenna: 2));
        _reader.Emit(Read("E2810003", rssi: -70, antenna: 1));
        _reader.Emit(Read("E2810004", rssi: -85, antenna: 2));
        await p.StopInventoryAsync().WithTimeout();

        Assert.Equal(["E2810002"], _repo.Applied.Select(a => a.Read.Epc));
        Assert.Equal(3, p.GetStats().FilteredOut);
    }

    [Fact]
    public async Task Varios_epcs_sao_todos_persistidos_em_lotes()
    {
        var p = Create(Options(o => { o.BatchSize = 25; o.WorkerCount = 4; }));
        await p.StartAsync().WithTimeout();
        await p.SetModeAsync(OperationMode.Entrada).WithTimeout();

        var epcs = Enumerable.Range(0, 1_000).Select(i => Epc("9", i)).ToArray();
        // Cada EPC lido 3x dentro da janela: só 1 aplicado por EPC.
        for (var round = 0; round < 3; round++)
            foreach (var e in epcs)
                _reader.Emit(Read(e, offsetMs: round * 10));

        await p.StopInventoryAsync().WithTimeout(TimeSpan.FromSeconds(10));

        Assert.Equal(epcs.Order(), _repo.Applied.Select(a => a.Read.Epc).Order());
        Assert.All(_repo.Calls, c => Assert.True(c.Reads.Count <= 25));
        var stats = p.GetStats();
        Assert.Equal(3_000, stats.RawReads);
        Assert.Equal(2_000, stats.Duplicates);
        Assert.Equal(1_000, stats.Persisted);
        Assert.Equal(1_000, stats.DistinctEpcsSession);
    }
}
