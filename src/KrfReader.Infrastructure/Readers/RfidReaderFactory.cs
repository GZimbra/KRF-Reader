using KrfReader.Application.Configuration;
using KrfReader.Application.Readers;
using Microsoft.Extensions.Logging;

namespace KrfReader.Infrastructure.Readers;

/// <summary>
/// Cria o <see cref="IRfidReader"/> a partir de <see cref="ReaderOptions.Type"/>.
/// <para><b>PONTO DE EXTENSÃO — como plugar um leitor real:</b></para>
/// <list type="number">
/// <item><description>
/// <b>Protocolo proprietário sobre serial ou TCP</b> (caso mais comum: o leitor fala bytes num COM/socket):
/// escreva uma classe <see cref="IReaderProtocol"/> (enquadramento, CRC, comandos de potência/antena/start/stop;
/// use <see cref="Protocols.AsciiLineProtocol"/> como modelo) e registre-a com
/// <see cref="ReaderProtocolFactory.Register"/> na composição. Configure <c>Reader:Type = Serial|Tcp</c> e
/// <c>Reader:Protocol = &lt;nome&gt;</c>. Transporte, reconexão e threading já vêm prontos; nada muda aqui.
/// </description></item>
/// <item><description>
/// <b>SDK do fabricante (DLL própria) ou LLRP</b>: o SDK/LLRP já cuida do transporte, então implemente
/// <see cref="IRfidReader"/> diretamente numa nova classe em <c>Infrastructure/Readers</c> (respeitando o contrato
/// de threading e "nenhum TagRead após StopInventoryAsync"), acrescente um valor em <see cref="ReaderType"/>
/// (ex.: <c>Llrp</c>) com suas opções em <see cref="ReaderOptions"/> e adicione o caso no <c>switch</c> abaixo.
/// </description></item>
/// </list>
/// </summary>
public sealed class RfidReaderFactory(IReaderProtocolFactory protocols, ILoggerFactory? loggerFactory = null) : IRfidReaderFactory
{
    private readonly IReaderProtocolFactory _protocols = protocols ?? throw new ArgumentNullException(nameof(protocols));

    public IRfidReader Create(ReaderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.Type switch
        {
            ReaderType.Serial => new GenericSerialReader(
                options, _protocols.Create(options.Protocol), loggerFactory?.CreateLogger<GenericSerialReader>()),
            ReaderType.Tcp => new GenericTcpReader(
                options, _protocols.Create(options.Protocol), loggerFactory?.CreateLogger<GenericTcpReader>()),
            // Novo driver (SDK/LLRP): novo ReaderType + novo case aqui.
            _ => throw new ArgumentOutOfRangeException(nameof(options), options.Type, "Tipo de leitor não suportado."),
        };
    }
}
