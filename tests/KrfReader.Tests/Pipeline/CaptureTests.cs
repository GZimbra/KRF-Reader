using KrfReader.Application.Configuration;
using KrfReader.Application.Pipeline;
using KrfReader.Domain;
using KrfReader.Tests.Fakes;

namespace KrfReader.Tests.Pipeline;

public sealed class CaptureTests : IAsyncLifetime
{
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(300);

    private readonly FakeRfidReader _reader = new();
    private readonly InMemoryInventoryRepository _repo = new();
    private InventoryPipeline _pipeline = null!;

    public async Task InitializeAsync()
    {
        _pipeline = new InventoryPipeline(_reader, _repo, new PipelineOptions
        {
            DedupWindowSeconds = 5,
            MinRssi = -70,
            AllowedAntennas = [1, 2],
            WorkerCount = 2,
            QueueCapacity = 1_000,
            BatchSize = 10,
            FlushIntervalMs = 20,
            ShutdownTimeoutMs = 5_000,
        });
        await _pipeline.StartAsync().WithTimeout();
    }

    public async Task DisposeAsync() =>
        await _pipeline.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

    private static TagRead Read(string epc, double rssi, int antenna = 1) => new(epc, rssi, antenna, DateTime.UtcNow);

    /// <summary>Emite assim que a captura ligar o leitor.</summary>
    private Task EmitWhenRunning(params TagRead[] reads) => Task.Run(async () =>
    {
        await TestUtil.Eventually(() => _reader.IsInventoryRunning, "captura ligar o leitor");
        _reader.Emit(reads);
    });

    [Fact]
    public async Task Captura_agrega_por_EPC_ordena_por_RSSI_e_aplica_filtro()
    {
        var emit = EmitWhenRunning(
            Read("aaaa0001", -60),
            Read("AAAA0001", -48),          // mesmo EPC, normalizado: atualiza RSSI máx.
            Read("BBBB0002", -40, antenna: 2),
            Read("CCCC0003", -80),          // abaixo do RSSI mínimo
            Read("DDDD0004", -30, antenna: 3), // antena fora do filtro
            Read("zz", -30));               // EPC inválido

        var result = await _pipeline.CaptureAsync(Window).WaitAsync(TestUtil.DefaultTimeout);
        await emit;

        Assert.Equal(["BBBB0002", "AAAA0001"], result.Tags.Select(t => t.Epc));
        Assert.True(result.IsAmbiguous);
        Assert.Equal("BBBB0002", result.Strongest!.Epc);
        var a = result.Tags[1];
        Assert.Equal(2, a.Reads);
        Assert.Equal(-48, a.MaxRssi);
        Assert.Equal(2, result.Tags[0].AntennaId);
    }

    [Fact]
    public async Task Captura_nao_grava_nada_e_desliga_o_leitor()
    {
        var emit = EmitWhenRunning(Read("AAAA0001", -50));

        var result = await _pipeline.CaptureAsync(Window).WaitAsync(TestUtil.DefaultTimeout);
        await emit;
        await Task.Delay(100); // daria tempo de um flush, se algo tivesse ido para a fila

        Assert.Single(result.Tags);
        Assert.False(result.IsAmbiguous);
        Assert.False(_reader.IsInventoryRunning);
        Assert.Equal(1, _reader.StopCalls);
        Assert.Empty(_repo.Calls);
        Assert.Null(_pipeline.CurrentMode);
        Assert.Equal(0, _pipeline.GetStats().Persisted);
    }

    [Fact]
    public async Task Captura_sem_tags_retorna_vazio()
    {
        var result = await _pipeline.CaptureAsync(Window).WaitAsync(TestUtil.DefaultTimeout);

        Assert.Empty(result.Tags);
        Assert.Null(result.Strongest);
    }

    [Fact]
    public async Task Captura_com_modo_ativo_e_recusada()
    {
        await _pipeline.SetModeAsync(OperationMode.Entrada).WithTimeout();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _pipeline.CaptureAsync(Window));

        Assert.True(_reader.IsInventoryRunning); // a recusa não mexe no inventário em andamento
        Assert.Equal(OperationMode.Entrada, _pipeline.CurrentMode);
    }

    [Fact]
    public async Task Leitura_apos_a_captura_volta_a_ser_descartada()
    {
        await _pipeline.CaptureAsync(TimeSpan.FromMilliseconds(50)).WithTimeout();
        var stale = _pipeline.GetStats().StaleDiscarded;

        _reader.Emit(Read("AAAA0001", -50));

        Assert.Equal(stale + 1, _pipeline.GetStats().StaleDiscarded);
    }

    [Fact]
    public async Task Troca_de_modo_espera_a_captura_terminar()
    {
        var capture = _pipeline.CaptureAsync(Window);
        await TestUtil.Eventually(() => _reader.IsInventoryRunning, "captura ligar o leitor");

        var setMode = _pipeline.SetModeAsync(OperationMode.Entrada);
        await Task.Delay(50);
        Assert.False(setMode.IsCompleted);

        await capture.WithTimeout();
        await setMode.WithTimeout();
        Assert.Equal(OperationMode.Entrada, _pipeline.CurrentMode);
        Assert.True(_reader.IsInventoryRunning);
    }

    [Fact]
    public async Task Janela_invalida_e_recusada()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _pipeline.CaptureAsync(TimeSpan.Zero));
    }
}
