using KrfReader.Application.Configuration;

namespace KrfReader.Application.Readers;

/// <summary>
/// Perfil de um modelo de leitor: valores padrão de conexão/protocolo e informações técnicas exibidas na tela
/// de Configurações. Trocar de leitor = escolher outro perfil (ou "Personalizado") e aplicar.
/// </summary>
public sealed record ReaderModel
{
    /// <summary>Identificador estável, gravado em <c>Reader:Model</c>.</summary>
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>Fabricante, família ou exemplos de produtos compatíveis.</summary>
    public required string Family { get; init; }

    public required ReaderType Type { get; init; }

    /// <summary>Nome registrado em <see cref="IReaderProtocolFactory"/>.</summary>
    public string Protocol { get; init; } = "";

    public int DefaultBaudRate { get; init; } = 115200;

    public int DefaultTcpPort { get; init; } = 4001;

    public double MinPowerDbm { get; init; }

    public double MaxPowerDbm { get; init; } = 33;

    public double DefaultPowerDbm { get; init; } = 20;

    /// <summary>Quantidade de portas de antena físicas.</summary>
    public int AntennaPorts { get; init; } = 1;

    /// <summary>Chip/firmware de RF (ex.: "MagicRF M100", "Impinj R2000").</summary>
    public string Chip { get; init; } = "";

    /// <summary>Interface física (ex.: "USB (CH340) → UART").</summary>
    public string Interface { get; init; } = "";

    /// <summary>Faixa de frequência configurada pelo app (vazio = a do próprio leitor).</summary>
    public string FrequencyBand { get; init; } = "";

    /// <summary>Formato do quadro do protocolo, para referência técnica.</summary>
    public string FrameFormat { get; init; } = "";

    /// <summary>Observações (limitações, cuidados, como identificar o hardware).</summary>
    public string Notes { get; init; } = "";

    /// <summary>true = comunicação conferida com hardware real neste projeto.</summary>
    public bool Validated { get; init; }

    /// <summary>true = perfil livre: os campos não são sobrescritos ao selecioná-lo.</summary>
    public bool IsCustom { get; init; }
}

/// <summary>Catálogo de modelos conhecidos. Novo modelo = nova entrada (e, se preciso, novo protocolo registrado).</summary>
public interface IReaderModelCatalog
{
    IReadOnlyList<ReaderModel> Models { get; }

    /// <summary>Modelo pelo Id (case-insensitive) ou null.</summary>
    ReaderModel? Find(string? id);

    /// <summary>Melhor perfil para opções sem <c>Model</c> (deduz por tipo/protocolo; senão, o personalizado).</summary>
    ReaderModel Resolve(ReaderOptions options);
}
