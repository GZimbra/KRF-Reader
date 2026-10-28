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
/// <item><description>Leitores de rede com API própria (LLRP, REST) usam <see cref="ReaderType.Llrp"/> /
/// <see cref="ReaderType.ImpinjIot"/>; <see cref="ReaderModel.Protocol"/> é só informativo.</description></item>
/// </list>
/// </summary>
public sealed class ReaderModelCatalog : IReaderModelCatalog
{
    public const string R200Id = "r200";
    public const string M100Id = "m100-bb";
    public const string R2000SerialId = "r2000-serial";
    public const string R2000TcpId = "r2000-tcp";
    public const string ChafonRu5106Id = "chafon-cf-ru5106";
    public const string ImpinjR700LlrpId = "impinj-r700-llrp";
    public const string ImpinjR700IotId = "impinj-r700-iot";
    public const string ZebraFx9600Id = "zebra-fx9600";
    public const string LlrpGenericId = "llrp-generic";
    public const string AsciiSerialId = "ascii-serial";
    public const string AsciiTcpId = "ascii-tcp";
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

        var match = Models.FirstOrDefault(m => !m.IsCustom && m.Type == options.Type
            && string.Equals(m.Protocol, options.Protocol, StringComparison.OrdinalIgnoreCase));
        return match ?? Models.First(m => m.IsCustom);
    }

    private const string R2000Band = "915,5–927,5 MHz · região FCC restrita (Anatel), enviada ao conectar";

    private const string R2000Notes =
        "Uma porta ativa: inventário em tempo real (0x89); duas ou mais: troca rápida de antena (0x8A), rearmado a cada 300 ms. "
        + "Potência individual por porta exige firmware que aceite 0x76 com 4 bytes. Não validado em hardware.";

    private const string LlrpBand = "A da região do leitor; o app só aceita tabela inteira em 915–928 MHz (Anatel)";

    private const string LlrpFrame = "LLRP 1.0.1: Ver/Tipo | Comprimento | MessageID | parâmetros TLV/TV";

    private const string LlrpNotes =
        "Ao conectar o app lê portas, tabela de potência e frequências do leitor, apaga as ROSpecs existentes (reset LLRP de fábrica) e instala a sua; "
        + "só um cliente LLRP por vez. Não validado em hardware.";

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
            FrequencyBand = R2000Band,
            Notes = R2000Notes,
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
            FrequencyBand = R2000Band,
            Notes = "Mesmo protocolo do modelo serial, transportado por TCP. " + R2000Notes,
        };
        yield return new ReaderModel
        {
            Id = ChafonRu5106Id,
            DisplayName = "Chafon CF-RU5106 · 4 portas (TCP/IP)",
            Family = "Chafon CF-RU5106 e leitores fixos R2000 de 4 portas (rede ou RS-232)",
            Type = ReaderType.Tcp,
            Protocol = R2000Protocol.ProtocolName,
            DefaultTcpPort = 4001,
            MinPowerDbm = R2000Protocol.MinPowerDbm,
            MaxPowerDbm = R2000Protocol.MaxPowerDbm,
            DefaultPowerDbm = 30,
            AntennaPorts = 4,
            Chip = "Impinj R2000",
            Interface = "Ethernet (cliente TCP) ou RS-232/RS-485; conectores TNC/SMA de 4 portas",
            FrameFormat = "A0 | Len | Endereço | Cmd | Dados | Check (compl. de 2)",
            FrequencyBand = R2000Band,
            Notes = "O app fala o protocolo R2000 \"A0\". A Chafon também entrega firmware com protocolo próprio (quadro Len|Adr|Cmd|Dados|CRC-16, demo UHFReader288): "
                  + "se o leitor não responder, confirme com o fornecedor qual protocolo está gravado e peça o R2000/A0. Para RS-232 use o perfil \"Impinj R2000 · serial/USB\". "
                  + R2000Notes,
        };
        yield return new ReaderModel
        {
            Id = ImpinjR700IotId,
            DisplayName = "Impinj R700 · IoT Device Interface (REST)",
            Family = "Impinj R700 / R720 com a interface Impinj IoT ativa",
            Type = ReaderType.ImpinjIot,
            Protocol = "ImpinjIoT",
            DefaultTcpPort = 443,
            MinPowerDbm = 10,
            MaxPowerDbm = 33,
            DefaultPowerDbm = 30,
            AntennaPorts = 4,
            Chip = "Impinj E710",
            Interface = "Ethernet/PoE, HTTPS REST (/api/v1) + fluxo NDJSON; usuário/senha da API",
            FrequencyBand = "915,25–927,25 MHz · 25 canais (o app envia a lista ao leitor)",
            FrameFormat = "JSON: preset de inventário + eventos tagInventoryEvent",
            Notes = "Escolha IoT Device Interface na interface web do R700 (o leitor roda LLRP OU REST, não os dois). "
                  + "É o caminho recomendado para o R700 no Brasil: restringe os canais a 915–928 MHz mesmo em região FCC. "
                  + "Certificado de fábrica é autoassinado (aceito por padrão; troque por um confiável em produção) e a senha fica no appsettings.Local.json: troque a senha padrão. Não validado em hardware.",
        };
        yield return new ReaderModel
        {
            Id = ImpinjR700LlrpId,
            DisplayName = "Impinj R700 / Speedway · LLRP",
            Family = "Impinj R700, R720, Speedway R420/R220, xPortal, xArray (LLRP 1.0.1)",
            Type = ReaderType.Llrp,
            Protocol = "LLRP",
            DefaultTcpPort = LlrpReaderOptions.DefaultPort,
            MinPowerDbm = 10,
            MaxPowerDbm = 33,
            DefaultPowerDbm = 30,
            AntennaPorts = 4,
            Chip = "Impinj E710 (R700); Speedway conforme a geração",
            Interface = "Ethernet/PoE, LLRP em TCP 5084 (sem TLS)",
            FrequencyBand = LlrpBand,
            FrameFormat = LlrpFrame,
            Notes = "No R700, ative LLRP na interface web. Potência por porta vai pela tabela do leitor (passo de 0,25 dB). Modo dual/single target via extensão Impinj. "
                  + LlrpNotes,
        };
        yield return new ReaderModel
        {
            Id = ZebraFx9600Id,
            DisplayName = "Zebra FX9600 / FX7500 · LLRP",
            Family = "Zebra FX9600 (4/8 portas) e FX7500 (2/4 portas)",
            Type = ReaderType.Llrp,
            Protocol = "LLRP",
            DefaultTcpPort = LlrpReaderOptions.DefaultPort,
            MinPowerDbm = 10,
            MaxPowerDbm = 33,
            DefaultPowerDbm = 30,
            AntennaPorts = 8,
            Interface = "Ethernet/PoE, LLRP em TCP 5084 (sem TLS)",
            FrequencyBand = LlrpBand,
            FrameFormat = LlrpFrame,
            Notes = "Defina o país (Brasil) na interface web do leitor: o app confere a tabela anunciada e recusa conectar se houver canal fora de 915–928 MHz. "
                  + "A versão de 4 portas aceita portas 1–4; a de 8, 1–8. " + LlrpNotes,
        };
        yield return new ReaderModel
        {
            Id = LlrpGenericId,
            DisplayName = "LLRP genérico (EPCglobal)",
            Family = "Qualquer leitor fixo com LLRP 1.0.1: ThingMagic, Alien, Keonn, Invengo, Feig e outros",
            Type = ReaderType.Llrp,
            Protocol = "LLRP",
            DefaultTcpPort = LlrpReaderOptions.DefaultPort,
            MinPowerDbm = 0,
            MaxPowerDbm = 33,
            DefaultPowerDbm = 27,
            AntennaPorts = 4,
            Interface = "Ethernet, LLRP em TCP 5084 (sem TLS)",
            FrequencyBand = LlrpBand,
            FrameFormat = LlrpFrame,
            Notes = LlrpNotes,
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
            Family = "Gateways de rede que enviam uma linha por leitura",
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
