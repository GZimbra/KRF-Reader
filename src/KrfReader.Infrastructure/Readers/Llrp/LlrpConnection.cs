using System.Buffers.Binary;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace KrfReader.Infrastructure.Readers.Llrp;

/// <summary>
/// Uma conexão LLRP: loop de recepção dedicado, correlação requisição/resposta por MessageID, resposta automática a
/// KEEPALIVE e watchdog de inatividade. Mensagens não solicitadas (relatórios, eventos) vão para o callback, na
/// thread do loop. <see cref="Completion"/> termina (com exceção) quando o link cai.
/// </summary>
internal sealed class LlrpConnection : IAsyncDisposable
{
    private const int MaxMessageLength = 16 * 1024 * 1024;

    private readonly Stream _stream;
    private readonly Action<LlrpMessage> _onUnsolicited;
    private readonly TimeSpan _idleTimeout;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<LlrpMessage>> _pending = new();
    private readonly TaskCompletionSource<ushort> _connectionAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private int _nextId;
    private int _disposed;

    public LlrpConnection(Stream stream, Action<LlrpMessage> onUnsolicited, TimeSpan idleTimeout, ILogger? logger)
    {
        _stream = stream;
        _onUnsolicited = onUnsolicited;
        _idleTimeout = idleTimeout;
        _logger = logger;
        Completion = Task.Run(ReceiveLoopAsync);
    }

    /// <summary>Termina quando o loop de recepção para: com exceção = link caído; sem exceção = descarte local.</summary>
    public Task Completion { get; }

    /// <summary>Status do ConnectionAttemptEvent enviado pelo leitor logo após o TCP abrir.</summary>
    public Task<ushort> ConnectionAttempt => _connectionAttempt.Task;

    public uint NextId() => (uint)Interlocked.Increment(ref _nextId);

    public async Task SendAsync(byte[] frame, CancellationToken cancellationToken)
    {
        await _write.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _write.Release();
        }
    }

    /// <summary>
    /// Envia a mensagem criada por <paramref name="build"/> (recebe o MessageID) e espera a resposta do tipo
    /// <paramref name="responseType"/>. ERROR_MESSAGE ou LLRPStatus ≠ 0 viram <see cref="LlrpException"/>.
    /// </summary>
    public async Task<LlrpMessage> TransactAsync(
        string description, Func<uint, byte[]> build, ushort responseType, TimeSpan timeout,
        CancellationToken cancellationToken, int statusOffset = 0)
    {
        var id = NextId();
        var tcs = new TaskCompletionSource<LlrpMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await SendAsync(build(id), cancellationToken).ConfigureAwait(false);
            LlrpMessage response;
            try
            {
                response = await tcs.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"{description}: o leitor não respondeu em {timeout.TotalMilliseconds:0} ms.");
            }

            if (response.Type == LlrpMessageType.ErrorMessage)
            {
                var error = LlrpStatus.Find(response.Body);
                throw new LlrpException($"{description}: leitor respondeu ERROR_MESSAGE {error?.Code} {error?.Description}".TrimEnd());
            }

            if (response.Type != responseType)
                throw new LlrpException($"{description}: resposta inesperada (tipo {response.Type}, esperado {responseType}).");

            if (LlrpStatus.Find(response.Body, statusOffset) is { IsSuccess: false } status)
                throw new LlrpException($"{description}: leitor recusou (código {status.Code}) {status.Description}".TrimEnd());

            return response;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var header = new byte[LlrpWriter.HeaderLength];
        try
        {
            while (true)
            {
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
                {
                    if (_idleTimeout > TimeSpan.Zero)
                        idle.CancelAfter(_idleTimeout);
                    try
                    {
                        await _stream.ReadExactlyAsync(header, idle.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
                    {
                        throw new TimeoutException($"Nenhuma mensagem do leitor em {_idleTimeout.TotalSeconds:0} s (KEEPALIVE ausente).");
                    }
                }

                var type = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(header) & 0x3FF);
                var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(2));
                var id = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(6));
                if (length < LlrpWriter.HeaderLength || length > MaxMessageLength)
                    throw new InvalidDataException($"Cabeçalho LLRP inválido (tipo {type}, comprimento {length}).");

                var body = new byte[length - LlrpWriter.HeaderLength];
                await _stream.ReadExactlyAsync(body, _cts.Token).ConfigureAwait(false);
                await DispatchAsync(new LlrpMessage(type, id, body)).ConfigureAwait(false);
            }
        }
        catch (Exception) when (_cts.IsCancellationRequested)
        {
            FailPending(new ObjectDisposedException(nameof(LlrpConnection)));
        }
        catch (Exception ex)
        {
            FailPending(ex);
            throw;
        }
    }

    private async Task DispatchAsync(LlrpMessage message)
    {
        switch (message.Type)
        {
            case LlrpMessageType.Keepalive:
                await SendAsync(LlrpProtocol.KeepaliveAck(message.Id), _cts.Token).ConfigureAwait(false);
                return;
            case LlrpMessageType.ReaderEventNotification:
                if (LlrpProtocol.ParseConnectionAttempt(message.Body) is { } status)
                    _connectionAttempt.TrySetResult(status);
                _onUnsolicited(message);
                return;
            case LlrpMessageType.RoAccessReport:
                _onUnsolicited(message);
                return;
        }

        // Mensagens iniciadas pelo leitor têm MessageID próprio: só respostas são correlacionadas.
        if (_pending.TryGetValue(message.Id, out var tcs))
            tcs.TrySetResult(message);
        else
            _logger?.LogDebug("Mensagem LLRP tipo {Type} id {Id} sem requisição pendente; ignorada.", message.Type, message.Id);
    }

    private void FailPending(Exception ex)
    {
        _connectionAttempt.TrySetException(ex);
        foreach (var pending in _pending.Values)
            pending.TrySetException(ex);
    }

    /// <param name="waitForLoop">false quando chamado de dentro do callback (thread do loop): esperar seria deadlock.</param>
    public async ValueTask DisposeAsync(bool waitForLoop)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _cts.Cancel();
        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Erro ao fechar stream LLRP.");
        }

        if (waitForLoop)
        {
            try
            {
                await Completion.ConfigureAwait(false);
            }
            catch
            {
                // O motivo da queda já foi reportado por quem observa Completion.
            }

            _cts.Dispose();
        }
    }

    public ValueTask DisposeAsync() => DisposeAsync(waitForLoop: true);
}
