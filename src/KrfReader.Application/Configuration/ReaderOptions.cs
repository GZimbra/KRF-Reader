namespace KrfReader.Application.Configuration;

public enum ReaderType
{
    Mock,
    Serial,
    Tcp,
}

/// <summary>Seção <c>Reader</c> do appsettings.json.</summary>
public sealed class ReaderOptions
{
    public const string SectionName = "Reader";

    public ReaderType Type { get; set; } = ReaderType.Mock;

    /// <summary>Nome do protocolo registrado em <see cref="Readers.IReaderProtocolFactory"/> (Serial/Tcp).</summary>
    public string Protocol { get; set; } = "AsciiLine";

    public SerialReaderOptions Serial { get; set; } = new();

    public TcpReaderOptions Tcp { get; set; } = new();

    public MockReaderOptions Mock { get; set; } = new();

    /// <summary>Potência de transmissão em dBm (UHF típico: 0–33 dBm). Controla o alcance.</summary>
    public double PowerDbm { get; set; } = 30;

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

public sealed class MockReaderOptions
{
    /// <summary>Quantidade de EPCs distintos simulados.</summary>
    public int TagPopulation { get; set; } = 50;

    /// <summary>Taxa total de leituras/s (simula releitura em rajada do UHF).</summary>
    public int ReadsPerSecond { get; set; } = 200;

    /// <summary>Quantas tags estão "no campo" simultaneamente (as demais entram/saem com o tempo).</summary>
    public int TagsInFieldAtOnce { get; set; } = 8;

    /// <summary>Intervalo médio (ms) para trocar uma tag do campo por outra da população.</summary>
    public int FieldChurnMs { get; set; } = 1500;

    public double MinRssi { get; set; } = -85;
    public double MaxRssi { get; set; } = -35;
    public string EpcPrefix { get; set; } = "E28011606000";

    /// <summary>Seed fixa para reprodutibilidade em testes; null = aleatória.</summary>
    public int? Seed { get; set; }
}
