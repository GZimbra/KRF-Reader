using KrfReader.Application.Configuration;
using KrfReader.Application.Readers;
using KrfReader.Domain;
using KrfReader.Infrastructure.Readers;

namespace KrfReader.Tests.Readers;

public sealed class MockReaderTests : IAsyncLifetime
{
    private readonly ReaderOptions _options = new()
    {
        Type = ReaderType.Mock,
        Antennas = [1, 3],
        Mock = new MockReaderOptions
        {
            Seed = 1234,
            TagPopulation = 30,
            ReadsPerSecond = 500,
            TagsInFieldAtOnce = 5,
            FieldChurnMs = 200,
        },
    };

    private MockReader _reader = null!;

    public Task InitializeAsync()
    {
        _reader = new MockReader(_options);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _reader.DisposeAsync().WithTimeout();

    [Fact]
    public async Task Start_produz_leituras_validas_em_menos_de_2s()
    {
        var reads = new List<TagRead>();
        var gotEnough = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _reader.TagRead += (_, e) =>
        {
            lock (reads)
            {
                reads.Add(e.Read);
                if (reads.Count >= 50)
                    gotEnough.TrySetResult();
            }
        };

        await _reader.ConnectAsync().WithTimeout();
        Assert.True(_reader.IsConnected);
        Assert.False(_reader.IsInventoryRunning);

        await _reader.StartInventoryAsync().WithTimeout();
        Assert.True(_reader.IsInventoryRunning);

        await gotEnough.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await _reader.StopInventoryAsync().WithTimeout();

        TagRead[] snapshot;
        lock (reads)
            snapshot = reads.ToArray();

        Assert.All(snapshot, r =>
        {
            Assert.Equal(r.Epc, Epc.Normalize(r.Epc));
            Assert.Contains(r.AntennaId, _options.Antennas);
            Assert.InRange(r.Rssi, _options.Mock.MinRssi - 0.5, _options.Mock.MaxRssi + 0.5);
            Assert.Equal(DateTimeKind.Utc, r.TimestampUtc.Kind);
            Assert.StartsWith(_options.Mock.EpcPrefix, r.Epc);
        });
        Assert.True(snapshot.Select(r => r.Epc).Distinct().Count() <= _options.Mock.TagPopulation);
    }

    [Fact]
    public async Task Apos_StopInventoryAsync_nenhuma_leitura_nova_chega()
    {
        var count = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _reader.TagRead += (_, _) =>
        {
            if (Interlocked.Increment(ref count) >= 10)
                started.TrySetResult();
        };

        await _reader.ConnectAsync().WithTimeout();
        await _reader.StartInventoryAsync().WithTimeout();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await _reader.StopInventoryAsync().WithTimeout();
        var afterStop = Volatile.Read(ref count);
        Assert.False(_reader.IsInventoryRunning);

        // Janela de observação: o mock emite a cada ~20 ms; 300 ms cobre vários ticks.
        await Task.Delay(300);
        Assert.Equal(afterStop, Volatile.Read(ref count));

        // Idempotente.
        await _reader.StopInventoryAsync().WithTimeout();
    }

    [Fact]
    public async Task Start_sem_conexao_falha()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _reader.StartInventoryAsync());
    }

    [Fact]
    public async Task Disconnect_para_inventario()
    {
        await _reader.ConnectAsync().WithTimeout();
        await _reader.StartInventoryAsync().WithTimeout();

        await _reader.DisconnectAsync().WithTimeout();

        Assert.False(_reader.IsInventoryRunning);
        Assert.False(_reader.IsConnected);
    }

    [Fact]
    public async Task Handler_que_para_o_inventario_de_dentro_do_evento_nao_trava()
    {
        var stopTask = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fired = 0;
        _reader.TagRead += (_, _) =>
        {
            if (Interlocked.Exchange(ref fired, 1) == 0)
                stopTask.TrySetResult(_reader.StopInventoryAsync());
        };

        await _reader.ConnectAsync().WithTimeout();
        await _reader.StartInventoryAsync().WithTimeout();

        var inner = await stopTask.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await inner.WithTimeout();
        Assert.False(_reader.IsInventoryRunning);
    }

    [Fact]
    public void Implementa_IRfidReader()
    {
        IRfidReader r = _reader;
        Assert.False(string.IsNullOrWhiteSpace(r.Name));
    }
}
