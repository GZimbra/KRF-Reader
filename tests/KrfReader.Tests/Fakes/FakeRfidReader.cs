using KrfReader.Application.Readers;
using KrfReader.Domain;

namespace KrfReader.Tests.Fakes;

/// <summary>Leitor controlado pelo teste: <see cref="Emit"/> dispara <see cref="TagRead"/> de forma síncrona.</summary>
public sealed class FakeRfidReader : IRfidReader
{
    private int _connectCalls;
    private int _disconnectCalls;
    private int _startCalls;
    private int _stopCalls;
    private volatile bool _connected;
    private volatile bool _running;

    public string Name => "Fake";
    public bool IsConnected => _connected;
    public bool IsInventoryRunning => _running;

    public int ConnectCalls => Volatile.Read(ref _connectCalls);
    public int DisconnectCalls => Volatile.Read(ref _disconnectCalls);
    public int StartCalls => Volatile.Read(ref _startCalls);
    public int StopCalls => Volatile.Read(ref _stopCalls);
    public bool Disposed { get; private set; }

    public event EventHandler<TagReadEventArgs>? TagRead;
    public event EventHandler<ReaderErrorEventArgs>? Error;

    /// <summary>Dispara a leitura independentemente do estado (o pipeline é quem deve filtrar).</summary>
    public void Emit(TagRead read) => TagRead?.Invoke(this, new TagReadEventArgs(read));

    public void Emit(IEnumerable<TagRead> reads)
    {
        foreach (var r in reads)
            Emit(r);
    }

    public void RaiseError(string message) => Error?.Invoke(this, new ReaderErrorEventArgs(message));

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _connectCalls);
        _connected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _disconnectCalls);
        _running = false;
        _connected = false;
        return Task.CompletedTask;
    }

    public Task StartInventoryAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _startCalls);
        _running = true;
        return Task.CompletedTask;
    }

    public Task StopInventoryAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _stopCalls);
        _running = false;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _running = false;
        _connected = false;
        return ValueTask.CompletedTask;
    }
}
