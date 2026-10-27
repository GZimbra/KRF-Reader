using KrfReader.Application.Configuration;
using KrfReader.Application.Readers;
using KrfReader.Infrastructure.Readers.Protocols;

namespace KrfReader.Infrastructure.Readers;

/// <summary>
/// Modelos de leitor conhecidos. Para suportar um leitor novo:
/// <list type="number">
/// <item><description>Se ele fala um protocolo já registrado (R200, M100, R2000, AsciiLine): só acrescente uma entrada aqui.</description></item>
/// <item><description>Senão: implemente <see cref="IReaderProtocol"/> em <c>Readers/Protocols</c>, registre em
/// <see cref="ReaderProtocolFactory"/> e acrescente a entrada aqui.</description></item>
/// </list>
/// </summary>
public sealed class ReaderModelCatalog : IReaderModelCatalog
{
    public const string R200Id = "r200";
    public const string M100Id = "m100-bb";
    public const string R2000SerialId = "r2000-serial";
    public const string R2000TcpId = "r2000-tcp";
    public const string AsciiSerialId = "ascii-serial";
    public const string AsciiTcpId = "ascii-tcp";
    public const string MockId = "mock";
    public const string CustomId = "custom";

    public static ReaderModelCatalog Default { get; } = new(BuiltIn());

    public ReaderModelCatalog(IEnumerable<ReaderModel> models)
    {
        Models = models.ToArray();
        if (Models.Select(m => m.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Models.Count)
            throw new ArgumentException("Ids de modelo duplicados.", nameof(models));
        if (!Models.Any(m => m.IsCustom))
            throw new ArgumentException("O catálogo precisa de um perfil personalizado.", nameof(models));
    }

    public IReadOnlyList<ReaderModel> Models { get; }

    public ReaderModel? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : Models.FirstOrDefault(m => string.Equals(m.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    public ReaderModel Resolve(ReaderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (Find(options.Model) is { } byId)
            return byId;

        var match = options.Type == ReaderType.Mock
            ? Models.FirstOrDefault(m => m.Type == ReaderType.Mock && !m.IsCustom)
            : Models.FirstOrDefault(m => !m.IsCustom && m.Type == options.Type
                && string.Equals(m.Protocol, options.Protocol, StringComparison.OrdinalIgnoreCase));
        return match ?? Models.First(m => m.IsCustom);
    }

    private static IEnumerable<ReaderModel> BuiltIn()
    {
        yield return new ReaderModel
        {
            Id = R200Id,
            DisplayName = "R200 · módulo UHF (USB/UART)",
            Family = "Placa genérica com módulo R200 (AliExpress), firmware \"M100 26dBm V1.0\"",
            Type = ReaderType.Serial,
            Protocol = M100Protocol.R200Name,
            DefaultBaudRate = 115200,
            MinPowerDbm = M100Protocol.MinPowerDbm,
            MaxPowerDbm = M100Protocol.MaxPowerDbm,
            DefaultPowerDbm = 20,
            AntennaPorts = 1,
            Chip = "MagicRF M100 (ISO 18000-6C / EPC C1G2, 840–960 MHz)",
            Interface = "USB → CH340 (VID 1A86, PID 7523) → UART 115200 8N1, DTR/RTS desligados",
            FrequencyBand = M100Protocol.FrequencyBand,
            FrameFormat = "AA | Tipo | Cmd | PL(2) | Params | Soma | DD",
            Notes = "Validado: identificação, potência, região/canais, inventário e parada. Ao conectar, o app define região US com só os canais 915–928 MHz (Anatel). Potência abaixo de 15 dBm é fixada em 15 pelo módulo. "
                  + "Nunca opere sem antena UHF no conector SMA: a potência refletida pode danificar o amplificador.",
            Validated = true,
        };
        yield return new ReaderModel
        {
            Id = M100Id,
            DisplayName = "M100 · JRD-100 / YRM100 (BB…7E)",
            Family = "Módulos MagicRF M100 com o protocolo original do fabricante",
            Type = ReaderType.Serial,
            Protocol = M100Protocol.M100Name,
            DefaultBaudRate = 115200,
            MinPowerDbm = M100Protocol.MinPowerDbm,
            MaxPowerDbm = M100Protocol.MaxPowerDbm,
            DefaultPowerDbm = 20,
            AntennaPorts = 1,
            Chip = "MagicRF M100",
            Interface = "UART 3,3 V ou USB-serial, 115200 8N1",
            FrequencyBand = M100Protocol.FrequencyBand,
            FrameFormat = "BB | Tipo | Cmd | PL(2) | Params | Soma | 7E",
            Notes = "Mesmos comandos do R200 com delimitadores BB/7E. Se o módulo não responder, teste o perfil R200.",
        };
        yield return new ReaderModel
        {
            Id = R2000SerialId,
            DisplayName = "Impinj R2000 · serial/USB",
            Family = "Leitores de mesa/fixos com chip R2000 (Invelion, Yanzeo e similares)",
            Type = ReaderType.Serial,
            Protocol = R2000Protocol.ProtocolName,
            DefaultBaudRate = 115200,
            MinPowerDbm = R2000Protocol.MinPowerDbm,
            MaxPowerDbm = R2000Protocol.MaxPowerDbm,
            DefaultPowerDbm = 26,
            AntennaPorts = 4,
            Chip = "Impinj R2000",
            Interface = "RS-232 / USB-serial, 115200 8N1 (alguns modelos 38400)",
            FrameFormat = "A0 | Len | Endereço | Cmd | Dados | Check (compl. de 2)",
            Notes = "Faixa de frequência: a configurada no próprio leitor (o app não altera); no Brasil use 915–928 MHz. Inventário em tempo real (0x89) rearmado a cada 300 ms. Só a primeira antena da lista é usada. Não validado em hardware.",
        };
        yield return new ReaderModel
        {
            Id = R2000TcpId,
            DisplayName = "Impinj R2000 · rede (TCP/IP)",
            Family = "Leitores fixos R2000 com porta Ethernet",
            Type = ReaderType.Tcp,
            Protocol = R2000Protocol.ProtocolName,
            DefaultTcpPort = 4001,
            MinPowerDbm = R2000Protocol.MinPowerDbm,
            MaxPowerDbm = R2000Protocol.MaxPowerDbm,
            DefaultPowerDbm = 26,
            AntennaPorts = 4,
            Chip = "Impinj R2000",
            Interface = "Ethernet, cliente TCP (porta típica 4001)",
            FrameFormat = "A0 | Len | Endereço | Cmd | Dados | Check (compl. de 2)",
            Notes = "Mesmo protocolo do modelo serial, transportado por TCP. Não validado em hardware.",
        };
        yield return new ReaderModel
        {
            Id = AsciiSerialId,
            DisplayName = "Genérico texto · serial",
            Family = "Leitores/gateways que enviam uma linha por leitura",
            Type = ReaderType.Serial,
            Protocol = AsciiLineProtocol.ProtocolName,
            DefaultBaudRate = 115200,
            DefaultPowerDbm = 30,
            AntennaPorts = 4,
            Interface = "Serial, texto ASCII",
            FrameFormat = "EPC,RSSI,ANTENA[,TIMESTAMP]\\n · comandos START/STOP/POWER/ANT",
            Notes = "Útil para integrar via firmware próprio (ex.: ESP32 + módulo) ou testar com terminal serial.",
        };
        yield return new ReaderModel
        {
            Id = AsciiTcpId,
            DisplayName = "Genérico texto · TCP/IP",
            Family = "Gateways de rede ou simuladores (netcat)",
            Type = ReaderType.Tcp,
            Protocol = AsciiLineProtocol.ProtocolName,
            DefaultTcpPort = 4001,
            DefaultPowerDbm = 30,
            AntennaPorts = 4,
            Interface = "Ethernet, cliente TCP, texto ASCII",
            FrameFormat = "EPC,RSSI,ANTENA[,TIMESTAMP]\\n · comandos START/STOP/POWER/ANT",
            Notes = "Teste rápido: nc -l 4001 e digite linhas no formato acima.",
        };
        yield return new ReaderModel
        {
            Id = MockId,
            DisplayName = "Simulador (sem hardware)",
            Family = "Leitor simulado do KRF Reader",
            Type = ReaderType.Mock,
            DefaultPowerDbm = 30,
            AntennaPorts = 2,
            Interface = "Nenhuma",
            Notes = "Gera leituras aleatórias (inclusive fantasmas de RSSI baixo) para treinar e testar sem leitor.",
            Validated = true,
        };
        yield return new ReaderModel
        {
            Id = CustomId,
            DisplayName = "Personalizado",
            Family = "Configuração manual",
            Type = ReaderType.Serial,
            Protocol = AsciiLineProtocol.ProtocolName,
            DefaultPowerDbm = 20,
            AntennaPorts = 4,
            Notes = "Escolha tipo, protocolo, conexão e potência à mão. Use quando o modelo não estiver na lista mas falar um protocolo suportado.",
            IsCustom = true,
        };
    }
}
