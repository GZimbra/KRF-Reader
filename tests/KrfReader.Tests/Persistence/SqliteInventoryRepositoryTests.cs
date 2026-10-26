using KrfReader.Application.Configuration;
using KrfReader.Application.Persistence;
using KrfReader.Domain;
using KrfReader.Infrastructure.Persistence;

namespace KrfReader.Tests.Persistence;

public sealed class SqliteInventoryRepositoryTests : IAsyncLifetime
{
    private static readonly DateTime T0 = new(2026, 3, 10, 8, 0, 0, DateTimeKind.Utc);

    private readonly string _dbPath = TestUtil.TempDbPath();
    private SqliteInventoryRepository _repo = null!;

    private DatabaseOptions DbOptions => new() { Provider = "Sqlite", ConnectionString = $"Data Source={_dbPath}" };

    public async Task InitializeAsync()
    {
        _repo = new SqliteInventoryRepository(DbOptions);
        await _repo.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        ((object)_repo as IDisposable)?.Dispose();
        TestUtil.DeleteDb(_dbPath);
        return Task.CompletedTask;
    }

    private static TagRead Read(string epc, DateTime at, int antenna = 1, double rssi = -45.5) => new(epc, rssi, antenna, at);

    private static void AssertClose(DateTime expected, DateTime actual) =>
        Assert.True(Math.Abs((expected - actual.ToUniversalTime()).TotalMilliseconds) < 2,
            $"esperado {expected:O}, obtido {actual:O}");

    [Fact]
    public async Task InitializeAsync_e_idempotente()
    {
        await _repo.InitializeAsync();
        await _repo.InitializeAsync();

        var other = new SqliteInventoryRepository(DbOptions);
        await other.InitializeAsync();

        Assert.Equal(new InventoryCounters(0, 0, 0), await _repo.GetCountersAsync());
    }

    [Fact]
    public async Task Lote_vazio_e_noop()
    {
        var result = await _repo.ApplyBatchAsync(OperationMode.Entrada, []);

        Assert.Empty(result.Applied);
        Assert.Empty(await _repo.QueryTagsAsync(null, null));
    }

    [Fact]
    public async Task Tag_desconhecida_em_entrada_cria_com_status_dentro()
    {
        var result = await _repo.ApplyBatchAsync(OperationMode.Entrada, [Read("E2801160AA01", T0, antenna: 2, rssi: -51.25)]);

        var a = Assert.Single(result.Applied);
        Assert.Null(a.PreviousStatus);
        Assert.Equal(TagStatus.Dentro, a.NewStatus);
        Assert.Equal(EventType.Criacao, a.Event);
        Assert.Equal(OperationMode.Entrada, a.Mode);

        var tag = Assert.Single(await _repo.QueryTagsAsync(null, null));
        Assert.Equal("E2801160AA01", tag.Epc);
        Assert.Equal(TagStatus.Dentro, tag.Status);
        AssertClose(T0, tag.UltimaLeituraUtc);
        AssertClose(T0, tag.UltimaAtualizacaoStatusUtc);

        var ev = Assert.Single(await _repo.GetEventsAsync("E2801160AA01"));
        Assert.Equal(EventType.Criacao, ev.Type);
        Assert.Equal(OperationMode.Entrada, ev.Mode);
        Assert.Equal(2, ev.AntennaId);
        Assert.Equal(-51.25, ev.Rssi, 3);
        AssertClose(T0, ev.TimestampUtc);
    }

    [Fact]
    public async Task Releitura_idempotente_nao_gera_evento_mas_atualiza_UltimaLeitura()
    {
        const string epc = "E2801160AA02";
        await _repo.ApplyBatchAsync(OperationMode.Entrada, [Read(epc, T0)]);

        var t1 = T0.AddMinutes(30);
        var result = await _repo.ApplyBatchAsync(OperationMode.Entrada, [Read(epc, t1)]);

        var a = Assert.Single(result.Applied);
        Assert.Null(a.Event);
        Assert.Equal(TagStatus.Dentro, a.PreviousStatus);
        Assert.Equal(TagStatus.Dentro, a.NewStatus);

        var tag = Assert.Single(await _repo.QueryTagsAsync(null, epc));
        Assert.Equal(TagStatus.Dentro, tag.Status);
        AssertClose(t1, tag.UltimaLeituraUtc);
        AssertClose(T0, tag.UltimaAtualizacaoStatusUtc);

        Assert.Single(await _repo.GetEventsAsync(epc));
    }

    [Fact]
    public async Task Mudanca_de_status_gera_evento_MUDANCA_STATUS()
    {
        const string epc = "E2801160AA03";
        await _repo.ApplyBatchAsync(OperationMode.Entrada, [Read(epc, T0)]);

        var t1 = T0.AddHours(1);
        var result = await _repo.ApplyBatchAsync(OperationMode.Saida, [Read(epc, t1)]);

        var a = Assert.Single(result.Applied);
        Assert.Equal(TagStatus.Dentro, a.PreviousStatus);
        Assert.Equal(TagStatus.Fora, a.NewStatus);
        Assert.Equal(EventType.MudancaStatus, a.Event);

        var tag = Assert.Single(await _repo.QueryTagsAsync(null, epc));
        Assert.Equal(TagStatus.Fora, tag.Status);
        AssertClose(t1, tag.UltimaAtualizacaoStatusUtc);

        Assert.Equal(new InventoryCounters(0, 1, 0), await _repo.GetCountersAsync());
    }

    [Fact]
    public async Task Saida_sem_entrada_conta_como_anomalia()
    {
        var result = await _repo.ApplyBatchAsync(OperationMode.Saida,
            [Read("E2801160BB01", T0), Read("E2801160BB02", T0)]);

        Assert.All(result.Applied, a =>
        {
            Assert.True(a.IsAnomaly);
            Assert.Equal(TagStatus.Fora, a.NewStatus);
        });

        // Releitura em SAÍDA: idempotente, não é nova anomalia.
        await _repo.ApplyBatchAsync(OperationMode.Saida, [Read("E2801160BB01", T0.AddMinutes(1))]);
        // Volta para dentro e sai de novo: mudança normal.
        await _repo.ApplyBatchAsync(OperationMode.Entrada, [Read("E2801160BB02", T0.AddMinutes(2))]);

        Assert.Equal(new InventoryCounters(1, 1, 2), await _repo.GetCountersAsync());

        var ev = await _repo.GetEventsAsync("E2801160BB01");
        Assert.True(Assert.Single(ev).IsAnomaly);
    }

    [Fact]
    public async Task Mesmo_epc_repetido_no_lote_e_processado_em_ordem()
    {
        const string epc = "E2801160CC01";
        var reads = new[] { Read(epc, T0), Read(epc, T0.AddSeconds(1)), Read(epc, T0.AddSeconds(2)) };

        var result = await _repo.ApplyBatchAsync(OperationMode.Saida, reads);

        Assert.Equal(3, result.Applied.Count);
        Assert.Equal(EventType.SaidaSemEntrada, result.Applied[0].Event);
        Assert.Null(result.Applied[0].PreviousStatus);
        Assert.Null(result.Applied[1].Event);
        Assert.Equal(TagStatus.Fora, result.Applied[1].PreviousStatus);
        Assert.Null(result.Applied[2].Event);

        Assert.Single(await _repo.GetEventsAsync(epc));
        var tag = Assert.Single(await _repo.QueryTagsAsync(null, epc));
        AssertClose(T0.AddSeconds(2), tag.UltimaLeituraUtc);
        AssertClose(T0, tag.UltimaAtualizacaoStatusUtc);
        Assert.Equal(new InventoryCounters(0, 1, 1), await _repo.GetCountersAsync());
    }

    [Fact]
    public async Task Applied_preserva_ordem_das_leituras()
    {
        var reads = Enumerable.Range(0, 20).Select(i => Read($"E2801160D{i:X3}", T0.AddMilliseconds(i))).ToArray();

        var result = await _repo.ApplyBatchAsync(OperationMode.Entrada, reads);

        Assert.Equal(reads.Select(r => r.Epc), result.Applied.Select(a => a.Read.Epc));
    }

    [Fact]
    public async Task QueryTagsAsync_filtra_por_status_e_epc_parcial()
    {
        await _repo.ApplyBatchAsync(OperationMode.Entrada,
            [Read("E28011600001ABCD", T0), Read("E28011600002ABCD", T0), Read("E28011600003FFFF", T0)]);
        await _repo.ApplyBatchAsync(OperationMode.Saida,
            [Read("E28011600002ABCD", T0.AddMinutes(1)), Read("E28011600004ABCD", T0.AddMinutes(1))]);

        var dentro = await _repo.QueryTagsAsync(TagStatus.Dentro, null);
        Assert.Equal(["E28011600001ABCD", "E28011600003FFFF"], dentro.Select(t => t.Epc).Order());

        var fora = await _repo.QueryTagsAsync(TagStatus.Fora, null);
        Assert.Equal(["E28011600002ABCD", "E28011600004ABCD"], fora.Select(t => t.Epc).Order());

        var parcial = await _repo.QueryTagsAsync(null, "abcd");
        Assert.Equal(3, parcial.Count);

        var parcialFora = await _repo.QueryTagsAsync(TagStatus.Fora, "0002");
        Assert.Equal("E28011600002ABCD", Assert.Single(parcialFora).Epc);

        Assert.Empty(await _repo.QueryTagsAsync(null, "999999"));
        Assert.Equal(2, (await _repo.QueryTagsAsync(null, null, limit: 2)).Count);
    }

    [Fact]
    public async Task GetEventsAsync_ordena_do_mais_recente_para_o_mais_antigo()
    {
        const string epc = "E2801160EE01";
        await _repo.ApplyBatchAsync(OperationMode.Entrada, [Read(epc, T0)]);
        await _repo.ApplyBatchAsync(OperationMode.Saida, [Read(epc, T0.AddMinutes(1))]);
        await _repo.ApplyBatchAsync(OperationMode.Entrada, [Read(epc, T0.AddMinutes(2))]);
        await _repo.ApplyBatchAsync(OperationMode.Entrada, [Read(epc, T0.AddMinutes(3))]); // idempotente

        var events = await _repo.GetEventsAsync(epc);

        Assert.Equal([EventType.MudancaStatus, EventType.MudancaStatus, EventType.Criacao], events.Select(e => e.Type));
        Assert.Equal([OperationMode.Entrada, OperationMode.Saida, OperationMode.Entrada], events.Select(e => e.Mode));
        AssertClose(T0.AddMinutes(2), events[0].TimestampUtc);
        AssertClose(T0, events[2].TimestampUtc);
        Assert.All(events, e => Assert.Equal(epc, e.Epc));

        Assert.Single(await _repo.GetEventsAsync(epc, limit: 1));
        Assert.Empty(await _repo.GetEventsAsync("FFFF"));
    }

    [Fact]
    public async Task Lote_de_2000_leituras_em_uma_chamada()
    {
        var reads = Enumerable.Range(0, 2_000)
            .Select(i => Read($"E2801160{i:X8}", T0.AddMilliseconds(i), antenna: 1 + i % 4))
            .ToArray();

        var result = await _repo.ApplyBatchAsync(OperationMode.Entrada, reads);

        Assert.Equal(2_000, result.Applied.Count);
        Assert.All(result.Applied, a => Assert.Equal(EventType.Criacao, a.Event));
        Assert.Equal(new InventoryCounters(2_000, 0, 0), await _repo.GetCountersAsync());

        var saida = await _repo.ApplyBatchAsync(OperationMode.Saida, reads);
        Assert.All(saida.Applied, a => Assert.Equal(EventType.MudancaStatus, a.Event));
        Assert.Equal(new InventoryCounters(0, 2_000, 0), await _repo.GetCountersAsync());
    }

    [Fact]
    public async Task Dados_persistem_entre_instancias()
    {
        await _repo.ApplyBatchAsync(OperationMode.Entrada, [Read("E2801160FF01", T0)]);

        var other = new SqliteInventoryRepository(DbOptions);
        await other.InitializeAsync();

        Assert.Equal(new InventoryCounters(1, 0, 0), await other.GetCountersAsync());
        ((object)other as IDisposable)?.Dispose();
    }
}
