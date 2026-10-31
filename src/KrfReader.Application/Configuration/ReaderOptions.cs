namespace KrfReader.Application.Configuration;

public enum ReaderType
{
    /// <summary>Porta serial/USB com protocolo plugável (<see cref="ReaderOptions.Protocol"/>).</summary>
    Serial,

    /// <summary>Socket TCP com protocolo plugável (<see cref="ReaderOptions.Protocol"/>).</summary>
    Tcp,

    /// <summary>LLRP 1.0.1 (EPCglobal) sobre TCP: Impinj Speedway/R700, Zebra FX7500/FX9600 e similares.</summary>
    Llrp,

    /// <summary>Impinj IoT Device Interface (REST/HTTP) do Impinj R700.</summary>
    ImpinjIot,
}

/// <summary>Seção <c>Reader</c> do appsettings.json.</summary>
public sealed class ReaderOptions
{
    public const string SectionName = "Reader";

    /// <summary>Id do perfil em <see cref="Readers.IReaderModelCatalog"/> (informativo; vazio = deduzido de Type/Protocol).</summary>
    public string Model { get; set; } = "";

    public ReaderType Type { get; set; } = ReaderType.Serial;

    /// <summary>Nome do protocolo registrado em <see cref="Readers.IReaderProtocolFactory"/> (Serial/Tcp).</summary>
    public string Protocol { get; set; } = "R200";

    public SerialReaderOptions Serial { get; set; } = new();

    public TcpReaderOptions Tcp { get; set; } = new();

    /// <summary>Potência de transmissão em dBm (UHF típico: 0–33 dBm). Controla o alcance.</summary>
    public double PowerDbm { get; set; } = 20;

    /// <summary>Antenas ativas no leitor (portas físicas).</summary>
    public int[] Antennas { get; set; } = [1];

    /// <summary>
    /// Potência por porta (dBm), sobrepondo <see cref="PowerDbm"/> nas portas listadas. Chave = número da porta.
    /// Só vale em leitores multiporta que aceitam potência individual (LLRP, Impinj IoT, R2000 com firmware ≥ 1.x).
    /// </summary>
    public Dictionary<int, double> AntennaPowerDbm { get; set; } = [];

    /// <summary>
    /// true = só opera em 915–928 MHz (Anatel, Res. 680/2017). Leitores que o app consegue configurar (R200/M100,
    /// R2000, Impinj IoT) recebem a faixa; nos LLRP o app só confere a tabela anunciada e recusa conectar se
    /// houver canal fora dela.
    /// </summary>
    public bool EnforceAnatelBand { get; set; } = true;

    public LlrpReaderOptions Llrp { get; set; } = new();

    public ImpinjIotReaderOptions ImpinjIot { get; set; } = new();

    /// <summary>Espera entre tentativas de reconexão automática.</summary>
    public int ReconnectDelayMs { get; set; } = 2000;

    /// <summary>Anticolisão Gen2 para leitura de muitas tags simultâneas.</summary>
    public InventoryOptions Inventory { get; set; } = new();

    /// <summary>
    /// Docas por antena: em leitor multiporta, a direção (ENTRADA/SAÍDA) vem da porta que leu, e as duas docas operam
    /// ao mesmo tempo no modo POR ANTENA. Vazio = só modo manual.
    /// </summary>
    public DockOptions Docks { get; set; } = new();

    /// <summary>Potência efetiva de uma porta: a individual, se houver, senão <see cref="PowerDbm"/>.</summary>
    public double PowerFor(int antenna) =>
        AntennaPowerDbm is not null && AntennaPowerDbm.TryGetValue(antenna, out var dbm) ? dbm : PowerDbm;

    /// <summary>Portas ativas (sem repetição, em ordem) com a potência efetiva de cada uma.</summary>
    public IReadOnlyList<Readers.AntennaPort> ActivePorts() =>
        (Antennas ?? []).Where(a => a >= 1).Distinct().Order().Select(a => new Readers.AntennaPort(a, PowerFor(a))).ToArray();
}

/// <summary>
/// Seção <c>Reader:Docks</c>. Antenas de cada doca (portas físicas, disjuntas) e a arbitragem de leitura cruzada:
/// tag na doca de entrada pode ser vista, mais fraca, pelas antenas da saída (lóbulo lateral, reflexão em metal).
/// </summary>
public sealed class DockOptions
{
    /// <summary>Portas da doca de ENTRADA: leitura nelas leva o item a DENTRO.</summary>
    public int[] EntradaAntennas { get; set; } = [];

    /// <summary>Portas da doca de SAÍDA: leitura nelas leva o item a FORA.</summary>
    public int[] SaidaAntennas { get; set; } = [];

    /// <summary>
    /// Janela de arbitragem: a primeira leitura de um EPC espera este tempo; se as duas docas o virem, vence a de maior
    /// RSSI de pico. 0 = decide na primeira leitura (sem proteção contra leitura cruzada no início da passagem).
    /// </summary>
    public int ArbitrationMs { get; set; } = 300;

    /// <summary>
    /// Retenção de zona: com o EPC decidido numa doca, leituras dele na outra doca são descartadas como cruzadas até
    /// ele ficar este tempo sem ser lido na doca decidida.
    /// </summary>
    public int ZoneHoldMs { get; set; } = 3000;

    public bool IsConfigured => (EntradaAntennas?.Length ?? 0) + (SaidaAntennas?.Length ?? 0) > 0;
}

/// <summary>Alternância de target entre rodadas.</summary>
public enum TargetStrategy
{
    /// <summary>
    /// Só target A: cada tag responde uma vez e fica quieta enquanto o flag da session persistir. Com S2/S3 e carga
    /// parada no campo, as leituras cessam e a passagem fecha por silêncio com a carga ainda presente.
    /// </summary>
    SingleA,

    /// <summary>
    /// Alterna A→B→A a cada <see cref="InventoryOptions.TargetDwellMs"/> (dual target). Cada meia-volta lê de novo
    /// toda a população, e dentro dela as tags já lidas saem do ar, liberando slots para as fracas.
    /// </summary>
    DualAB,
}

/// <summary>
/// Seção <c>Reader:Inventory</c>. Padrões pensados para passagem de dezenas de tags por uma antena:
/// Session S2 (tag lida fica quieta ≥ 2 s), alternância A/B por rodada e Q ajustado à população vista.
/// Q dinâmico e troca A/B em tempo real só em protocolos <see cref="Readers.IInventoryParameterProtocol"/> (R200/M100).
/// LLRP e Impinj IoT usam Session, população estimada (2^<see cref="InitialQ"/>) e o modo de busca (single/dual target)
/// fixos ao conectar; o próprio leitor ajusta o Q.
/// </summary>
public sealed class InventoryOptions
{
    public Readers.GenSession Session { get; set; } = Readers.GenSession.S2;

    public TargetStrategy Target { get; set; } = TargetStrategy.DualAB;

    /// <summary>Duração de cada rodada (meia-volta de target). Também é a unidade de "rodada" da métrica de confiança.</summary>
    public int TargetDwellMs { get; set; } = 500;

    /// <summary>
    /// Q da primeira rodada de uma carga (2^Q slots), antes de haver população medida. 5 = 32 slots: no simulador,
    /// com cargas de 50–100 tags, convergiu mais cedo que 4 (colisão demais) e que 6 (slot vazio demais).
    /// </summary>
    public int InitialQ { get; set; } = 5;

    /// <summary>Recalcula Q a cada rodada: 2^Q ≈ tags que responderam na rodada anterior.</summary>
    public bool AdaptiveQ { get; set; } = true;

    public int MinQ { get; set; } = 2;

    public int MaxQ { get; set; } = 8;

    /// <summary>Prefixo de EPC (hex) para Select em RF: tags fora do prefixo não respondem. Vazio = todas.</summary>
    public string SelectEpcPrefix { get; set; } = "";

    /// <summary>Pausa entre comandos da sequência parar → configurar → iniciar.</summary>
    public int CommandGapMs { get; set; } = 15;
}

public sealed class SerialReaderOptions
{
    public string PortName { get; set; } = OperatingSystem.IsWindows() ? "COM3" : "/dev/ttyUSB0";
    public int BaudRate { get; set; } = 115200;
    public int DataBits { get; set; } = 8;
    /// <summary>None | Odd | Even | Mark | Space</summary>
    public string Parity { get; set; } = "None";
    /// <summary>One | OnePointFive | Two</summary>
    public string StopBits { get; set; } = "One";
}

/// <summary>Endereço de rede. Vale para <see cref="ReaderType.Tcp"/>, <see cref="ReaderType.Llrp"/> e <see cref="ReaderType.ImpinjIot"/>.</summary>
public sealed class TcpReaderOptions
{
    public string Host { get; set; } = "192.168.1.100";
    public int Port { get; set; } = 4001;
    public int ConnectTimeoutMs { get; set; } = 5000;
}

/// <summary>Seção <c>Reader:Llrp</c>.</summary>
public sealed class LlrpReaderOptions
{
    /// <summary>Porta padrão IANA do LLRP sem TLS.</summary>
    public const int DefaultPort = 5084;

    /// <summary>Intervalo de KEEPALIVE pedido ao leitor. Sem nenhuma mensagem por 3× este tempo = link caído. 0 = sem watchdog.</summary>
    public int KeepaliveMs { get; set; } = 5000;

    /// <summary>Espera máxima por resposta a cada comando LLRP.</summary>
    public int ResponseTimeoutMs { get; set; } = 5000;

    /// <summary>true = SET_READER_CONFIG com ResetToFactoryDefault ao conectar (apaga ROSpecs/config LLRP de outros clientes; não mexe em rede nem região).</summary>
    public bool ResetToFactoryDefault { get; set; } = true;
}

/// <summary>Seção <c>Reader:ImpinjIot</c> (Impinj IoT Device Interface, R700).</summary>
public sealed class ImpinjIotReaderOptions
{
    public bool UseHttps { get; set; } = true;

    public string Username { get; set; } = "root";

    /// <summary>Senha da API REST. Fica em texto puro no appsettings: troque a senha padrão do leitor.</summary>
    public string Password { get; set; } = "impinj";

    /// <summary>
    /// true = aceita o certificado autoassinado de fábrica do R700. Expõe a credencial a man-in-the-middle na rede;
    /// em produção, instale um certificado confiável no leitor e desligue.
    /// </summary>
    public bool AllowSelfSignedCertificate { get; set; } = true;

    /// <summary>Nome do preset de inventário gravado no leitor.</summary>
    public string PresetId { get; set; } = "krf-reader";
}
