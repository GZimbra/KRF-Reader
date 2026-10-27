namespace KrfReader.Application.Configuration;

public enum ReaderType
{
    Serial,
    Tcp,
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

    /// <summary>Espera entre tentativas de reconexão automática.</summary>
    public int ReconnectDelayMs { get; set; } = 2000;

    /// <summary>Anticolisão Gen2 para leitura de muitas tags simultâneas.</summary>
    public InventoryOptions Inventory { get; set; } = new();
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
/// Só tem efeito em protocolos que implementam <see cref="Readers.IInventoryParameterProtocol"/> (R200/M100).
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

public sealed class TcpReaderOptions
{
    public string Host { get; set; } = "192.168.1.100";
    public int Port { get; set; } = 4001;
    public int ConnectTimeoutMs { get; set; } = 5000;
}
