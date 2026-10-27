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
