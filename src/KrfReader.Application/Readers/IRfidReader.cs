using KrfReader.Domain;

namespace KrfReader.Application.Readers;

/// <summary>
/// Abstração de leitor RFID UHF. Nenhum código fora de Infrastructure/Readers conhece fabricante/protocolo.
/// <para>
/// Contrato de threading: <see cref="TagRead"/> é disparado na thread interna do leitor (NÃO na UI).
/// Handlers devem ser rápidos e não bloqueantes (ex.: <c>Channel.Writer.TryWrite</c>).
/// </para>
/// <para>
/// Ponto de extensão: um driver real (SDK do fabricante, LLRP, protocolo proprietário) implementa esta
/// interface diretamente, ou implementa <see cref="IReaderProtocol"/> e reaproveita os transportes genéricos.
/// </para>
/// </summary>
public interface IRfidReader : IAsyncDisposable
{
    /// <summary>Nome legível (ex.: "Serial COM3", "TCP 192.168.1.100:4001").</summary>
    string Name { get; }

    bool IsConnected { get; }

    bool IsInventoryRunning { get; }

    /// <summary>Uma leitura de tag (epc, rssi, antena, timestamp UTC). EPC já normalizado via <see cref="Epc.Normalize"/>.</summary>
    event EventHandler<TagReadEventArgs>? TagRead;

    /// <summary>Erro de comunicação não fatal (conexão caiu, frame inválido, etc.).</summary>
    event EventHandler<ReaderErrorEventArgs>? Error;

    /// <summary>Abre a conexão e aplica configuração (potência, antenas). Idempotente.</summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Para o inventário (se ativo) e fecha a conexão. Idempotente.</summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Inicia o inventário contínuo. Requer conexão. Idempotente.</summary>
    Task StartInventoryAsync(CancellationToken cancellationToken = default);

    /// <summary>Para o inventário. Idempotente. Após retornar, nenhum <see cref="TagRead"/> novo é disparado.</summary>
    Task StopInventoryAsync(CancellationToken cancellationToken = default);
}

public sealed class TagReadEventArgs(TagRead read) : EventArgs
{
    public TagRead Read { get; } = read;
}

public sealed class ReaderErrorEventArgs(string message, Exception? exception = null) : EventArgs
{
    public string Message { get; } = message;
    public Exception? Exception { get; } = exception;
}
