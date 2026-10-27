using KrfReader.Application.Configuration;
using KrfReader.Application.Pipeline;
using KrfReader.Domain;
using KrfReader.Infrastructure.Persistence;
using KrfReader.Infrastructure.Readers;

namespace KrfReader.Tests.Integration;

[Trait("Category", "Integration")]
public sealed class EndToEndTests : IAsyncLifetime
{
    private readonly string _dbPath = TestUtil.TempDbPath();
    private SqliteInventoryRepository _repo = null!;
    private MockReader _reader = null!;
    private InventoryPipeline _pipeline = null!;

    public async Task InitializeAsync()
    {
        _repo = new SqliteInventoryRepository(new DatabaseOptions { ConnectionString = $"Data Source={_dbPath}" });
        await _repo.InitializeAsync();

        _reader = new MockReader(new ReaderOptions
        {
            Type = ReaderType.Mock,
            Antennas = [1, 2],
            Mock = new MockReaderOptions
            {
                Seed = 42,
                TagPopulation = 40,
                ReadsPerSecond = 300,
                TagsInFieldAtOnce = 8,
                FieldChurnMs = 300,
            },
        });

        _pipeline = new InventoryPipeline(_reader, _repo, new PipelineOptions
        {
            DedupWindowSeconds = 5,
            MinRssi = -70,
            WorkerCount = 2,
            BatchSize = 50,
            FlushIntervalMs = 100,
            ShutdownTimeoutMs = 5_000,
        });
    }

    public async Task DisposeAsync()
    {
        try
        {
            await _pipeline.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            await _reader.DisposeAsync();
            ((object)_repo as IDisposable)?.Dispose();
        }
        finally
        {
            TestUtil.DeleteDb(_dbPath);
        }
    }

    [Fact]
    public async Task Entrada_depois_saida_com_mock_e_sqlite()
    {
        var errors = new List<string>();
        _pipeline.Error += (_, e) => { lock (errors) errors.Add(e); };

        await _pipeline.StartAsync().WithTimeout();

        // ENTRADA
        await _pipeline.SetModeAsync(OperationMode.Entrada).WithTimeout();
        await Task.Delay(1500);
        await _pipeline.StopInventoryAsync().WithTimeout();

        Assert.Null(_pipeline.CurrentMode);
        var afterEntrada = await _repo.GetCountersAsync();
        Assert.True(afterEntrada.Dentro > 0, "deveria haver tags DENTRO após ENTRADA");
        Assert.Equal(0, afterEntrada.Fora);
        Assert.Equal(0, afterEntrada.Anomalias);

        var dentro = await _repo.QueryTagsAsync(TagStatus.Dentro, null);
        Assert.Equal(afterEntrada.Dentro, dentro.Count);
        foreach (var tag in dentro)
        {
            var ev = Assert.Single(await _repo.GetEventsAsync(tag.Epc));
            Assert.Equal(EventType.Criacao, ev.Type);
            Assert.Equal(OperationMode.Entrada, ev.Mode);
            Assert.True(ev.Rssi >= -70);
        }

        var statsEntrada = _pipeline.GetStats();
        Assert.True(statsEntrada.RawReads > statsEntrada.Persisted, "dedup/filtro deveriam reduzir leituras");
        Assert.True(statsEntrada.Persisted >= afterEntrada.Dentro);

        // SAÍDA
        await _pipeline.SetModeAsync(OperationMode.Saida).WithTimeout();
        await Task.Delay(1500);
        await _pipeline.StopInventoryAsync().WithTimeout();

        var afterSaida = await _repo.GetCountersAsync();
        Assert.True(afterSaida.Fora > 0, "deveria haver tags FORA após SAÍDA");
        Assert.Equal(afterEntrada.Dentro + afterEntrada.Fora + afterSaida.Anomalias, afterSaida.Dentro + afterSaida.Fora);

        Assert.Equal(afterSaida.Fora, (await _repo.QueryTagsAsync(TagStatus.Fora, null)).Count);

        await _pipeline.StopAsync().WithTimeout();
        Assert.False(_pipeline.IsRunning);
        lock (errors)
            Assert.Empty(errors);
    }
}
