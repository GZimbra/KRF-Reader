using KrfReader.Application.Configuration;
using KrfReader.Application.Readers;
using KrfReader.Domain;
using Microsoft.Extensions.Logging;

namespace KrfReader.Infrastructure.Readers;

/// <summary>
/// Transporte genérico baseado em <see cref="Stream"/> (serial, TCP, ...) com protocolo plugável
/// (<see cref="IReaderProtocol"/>). A subclasse só sabe abrir o stream; enquadramento, comandos e parsing
/// ficam no protocolo.
/// <para>
/// Enquanto conectado há um loop de leitura dedicado (buffer de 4 KB) que alimenta o protocolo. Se o stream
/// cair, dispara <see cref="Error"/>, espera <see cref="ReaderOptions.ReconnectDelayMs"/>, reabre, reconfigura
/// e, se o inventário estava ativo, reenvia o comando de início — repetindo até reconectar ou até
/// <see cref="DisconnectAsync"/>. Durante a queda <see cref="IsConnected"/> é false, mas a sessão continua:
/// Start/Stop chamados nesse intervalo valem para quando o link voltar.
/// </para>
/// </summary>
public abstract class StreamRfidReaderBase : IRfidReader
{
    private const int ReadBufferSize = 4096;

    [ThreadStatic] private static StreamRfidReaderBase? t_raisingReader;

    private readonly IReaderProtocol _protocol;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly object _emitLock = new();

    private Stream? _stream;
    private CancellationTokenSource? _sessionCts;
    private Task? _readTask;
    private Task? _refreshTask;
    private volatile bool _session;
    private volatile bool _linkUp;
    private volatile bool _running;

    protected StreamRfidReaderBase(ReaderOptions options, IReaderProtocol protocol, ILogger? logger = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(protocol);
        Options = options;
        _protocol = protocol;
        Logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    protected ReaderOptions Options { get; }

    protected ILogger? Logger { get; }

    public abstract string Name { get; }

    public bool IsConnected => _session && _linkUp;

    public bool IsInventoryRunning => _running;

    public event EventHandler<TagReadEventArgs>? TagRead;

    public event EventHandler<ReaderErrorEventArgs>? Error;

    /// <summary>
    /// Abre o transporte. O stream devolvido deve ser dono do recurso subjacente (fechá-lo libera porta/socket);
    /// use <see cref="OwnedTransportStream"/> quando o stream não for dono do objeto de transporte.
    /// </summary>
    protected abstract Task<Stream> OpenStreamAsync(CancellationToken cancellationToken);

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session)
                return;

            var stream = await OpenStreamAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _protocol.Reset();
                await ConfigureAsync(stream, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await CloseQuietlyAsync(stream).ConfigureAwait(false);
                throw;
            }

            var cts = new CancellationTokenSource();
            _sessionCts = cts;
            _stream = stream;
            _linkUp = true;
            _session = true;
            _readTask = Task.Run(() => ReadLoopAsync(stream, cts.Token), CancellationToken.None);
            if (_protocol is IInventoryRefreshProtocol { InventoryRefreshInterval: var interval } && interval > TimeSpan.Zero)
                _refreshTask = Task.Run(() => RefreshLoopAsync(interval, cts.Token), CancellationToken.None);
            Logger?.LogInformation("Leitor {Reader} conectado.", Name);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_session)
                return;

            if (_running)
                await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);

            _session = false;
            var cts = _sessionCts;
            var readTask = _readTask;
            var refreshTask = _refreshTask;
            _sessionCts = null;
            _readTask = null;
            _refreshTask = null;
            cts?.Cancel();

            await _io.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (_stream is { } stream)
                    await CloseQuietlyAsync(stream).ConfigureAwait(false);
                _stream = null;
                _linkUp = false;
            }
            finally
            {
                _io.Release();
            }

            // De dentro de um handler (thread do loop) esperar o loop seria deadlock; o token já está cancelado.
            if (readTask is not null && !ReferenceEquals(t_raisingReader, this))
            {
                try
                {
                    await readTask.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger?.LogDebug(ex, "Loop de leitura de {Reader} terminou com exceção.", Name);
                }
            }

            if (refreshTask is not null)
            {
                try
                {
                    await refreshTask.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger?.LogDebug(ex, "Loop de rearme de {Reader} terminou com exceção.", Name);
                }
            }

            cts?.Dispose();
            Logger?.LogInformation("Leitor {Reader} desconectado.", Name);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StartInventoryAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_session)
                throw new InvalidOperationException("Leitor não conectado. Chame ConnectAsync antes de iniciar o inventário.");
            if (_running)
                return;

            lock (_emitLock)
                _running = true;

            await SendIfLinkUpAsync(_protocol.BuildStartInventoryCommand(), "início de inventário", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopInventoryAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        if (!_running)
            return;

        // Sob _emitLock: quando isto retorna, nenhum lote em andamento pode mais disparar TagRead.
        lock (_emitLock)
            _running = false;

        await SendIfLinkUpAsync(_protocol.BuildStopInventoryCommand(), "parada de inventário", cancellationToken).ConfigureAwait(false);
    }

    private async Task SendIfLinkUpAsync(byte[] command, string description, CancellationToken cancellationToken)
    {
        if (command is not { Length: > 0 })
            return;

        await _io.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Link caído: o loop de reconexão reenvia o início se _running estiver true.
            if (!_linkUp || _stream is not { } stream)
                return;
            await WriteCommandAsync(stream, command, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A queda real será detectada pelo loop de leitura, que reconecta.
            RaiseError($"Falha ao enviar comando de {description}.", ex);
        }
        finally
        {
            _io.Release();
        }
    }

    private async Task ConfigureAsync(Stream stream, CancellationToken cancellationToken)
    {
        var commands = _protocol.BuildConfigureCommands(Options.PowerDbm, Options.Antennas ?? []);
        foreach (var command in commands)
            await WriteCommandAsync(stream, command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteCommandAsync(Stream stream, byte[] command, CancellationToken cancellationToken)
    {
        if (command is not { Length: > 0 })
            return;
        await stream.WriteAsync(command, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(Stream initialStream, CancellationToken ct)
    {
        var buffer = new byte[ReadBufferSize];
        var stream = initialStream;

        while (!ct.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0)
                    throw new EndOfStreamException("Conexão encerrada pelo leitor.");
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _linkUp = false;
                RaiseError("Conexão com o leitor perdida; tentando reconectar.", ex);
                await CloseQuietlyAsync(stream).ConfigureAwait(false);

                var reopened = await ReconnectAsync(ct).ConfigureAwait(false);
                if (reopened is null)
                    break;
                stream = reopened;
                continue;
            }

            Process(buffer.AsSpan(0, read));
        }
    }

    /// <summary>Reenvia o início de inventário periodicamente (<see cref="IInventoryRefreshProtocol"/>).</summary>
    private async Task RefreshLoopAsync(TimeSpan interval, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(interval, _time);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await _io.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    // Checado sob _io: StopCoreAsync zera _running antes de enviar a parada, então um rearme
                    // nunca sai depois do comando de parada.
                    if (!_running || !_linkUp || _stream is not { } stream)
                        continue;
                    await WriteCommandAsync(stream, _protocol.BuildStartInventoryCommand(), ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    RaiseError("Falha ao rearmar o inventário.", ex);
                }
                finally
                {
                    _io.Release();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task<Stream?> ReconnectAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromMilliseconds(Math.Max(0, Options.ReconnectDelayMs));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            Stream? stream = null;
            try
            {
                stream = await OpenStreamAsync(ct).ConfigureAwait(false);
                _protocol.Reset();
                await ConfigureAsync(stream, ct).ConfigureAwait(false);

                await _io.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    _stream = stream;
                    _linkUp = true;
                    if (_running)
                        await WriteCommandAsync(stream, _protocol.BuildStartInventoryCommand(), ct).ConfigureAwait(false);
                }
                finally
                {
                    _io.Release();
                }

                Logger?.LogInformation("Leitor {Reader} reconectado.", Name);
                return stream;
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                if (stream is not null)
                    await CloseQuietlyAsync(stream).ConfigureAwait(false);
                return null;
            }
            catch (Exception ex)
            {
                _linkUp = false;
                if (stream is not null)
                    await CloseQuietlyAsync(stream).ConfigureAwait(false);
                RaiseError($"Falha ao reconectar; nova tentativa em {delay.TotalMilliseconds:0} ms.", ex);
            }
        }

        return null;
    }

    private void Process(ReadOnlySpan<byte> data)
    {
        IReadOnlyList<TagRead> reads;
        try
        {
            reads = _protocol.Feed(data, _time.GetUtcNow().UtcDateTime);
        }
        catch (Exception ex)
        {
            _protocol.Reset();
            RaiseError($"Protocolo {_protocol.Name} falhou ao interpretar dados; buffer descartado.", ex);
            return;
        }

        if (reads.Count == 0)
            return;

        lock (_emitLock)
        {
            // Fora de inventário o loop continua drenando o stream, mas descarta as leituras.
            if (!_running)
                return;

            var handler = TagRead;
            if (handler is null)
                return;

            var previous = t_raisingReader;
            t_raisingReader = this;
            try
            {
                foreach (var read in reads)
                {
                    try
                    {
                        handler(this, new TagReadEventArgs(read));
                    }
                    catch (Exception ex)
                    {
                        RaiseError("Handler de TagRead lançou exceção.", ex);
                    }
                }
            }
            finally
            {
                t_raisingReader = previous;
            }
        }
    }

    private void RaiseError(string message, Exception? ex)
    {
        Logger?.LogWarning(ex, "{Reader}: {Message}", Name, message);
        try
        {
            Error?.Invoke(this, new ReaderErrorEventArgs(message, ex));
        }
        catch
        {
            // Handler de erro com falha não pode derrubar o leitor.
        }
    }

    private async ValueTask CloseQuietlyAsync(Stream stream)
    {
        try
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger?.LogDebug(ex, "Erro ao fechar stream de {Reader}.", Name);
        }
    }
}

/// <summary>Stream que delega para outro e, ao ser descartado, descarta também o dono do transporte (porta, cliente TCP).</summary>
public sealed class OwnedTransportStream(Stream inner, IDisposable owner) : Stream
{
    private int _disposed;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => inner.Read(buffer);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        inner.ReadAsync(buffer, offset, count, cancellationToken);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        inner.ReadAsync(buffer, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        inner.WriteAsync(buffer, offset, count, cancellationToken);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        inner.WriteAsync(buffer, cancellationToken);

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                inner.Dispose();
            }
            finally
            {
                owner.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}
