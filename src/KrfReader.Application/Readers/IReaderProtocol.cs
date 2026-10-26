using KrfReader.Domain;

namespace KrfReader.Application.Readers;

/// <summary>
/// Protocolo de enquadramento/parsing isolado do transporte (serial/TCP).
/// Implementação é STATEFUL (mantém bytes parciais entre chamadas) e NÃO precisa ser thread-safe:
/// cada conexão usa sua própria instância (criada por <see cref="IReaderProtocolFactory"/>).
/// </summary>
public interface IReaderProtocol
{
    /// <summary>Nome registrado (ex.: "AsciiLine"). Usado em <c>Reader:Protocol</c> no appsettings.</summary>
    string Name { get; }

    /// <summary>
    /// Alimenta bytes recebidos do transporte e devolve as leituras completas contidas neles.
    /// Bytes de frames incompletos ficam retidos até a próxima chamada. Frames inválidos são descartados.
    /// </summary>
    IReadOnlyList<TagRead> Feed(ReadOnlySpan<byte> data, DateTime receivedAtUtc);

    /// <summary>Comandos a enviar após conectar (configurar potência/antenas). Pode ser vazio.</summary>
    IReadOnlyList<byte[]> BuildConfigureCommands(double powerDbm, IReadOnlyCollection<int> antennas);

    /// <summary>Comando de início de inventário. Vazio se o leitor já faz streaming contínuo.</summary>
    byte[] BuildStartInventoryCommand();

    /// <summary>Comando de parada de inventário. Vazio se não aplicável.</summary>
    byte[] BuildStopInventoryCommand();

    /// <summary>Descarta bytes parciais retidos (chamado em reconexão).</summary>
    void Reset();
}

/// <summary>Registro de protocolos por nome. Novo protocolo = nova classe + registro.</summary>
public interface IReaderProtocolFactory
{
    IReadOnlyCollection<string> AvailableProtocols { get; }

    IReaderProtocol Create(string name);
}
