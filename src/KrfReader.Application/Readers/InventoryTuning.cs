namespace KrfReader.Application.Readers;

/// <summary>Session Gen2 (flag "inventoried" usada no Query). S0 só persiste energizada; S1 ~0,5–5 s; S2/S3 ≥ 2 s após sair do campo.</summary>
public enum GenSession
{
    S0 = 0,
    S1 = 1,
    S2 = 2,
    S3 = 3,
}

/// <summary>Target do Query: só tags com o flag da session neste estado respondem (e passam para o oposto ao serem lidas).</summary>
public enum InventoryTarget
{
    A = 0,
    B = 1,
}

/// <summary>
/// Parâmetros de anticolisão de uma rodada de inventário Gen2.
/// <paramref name="Q"/> define o quadro de 2^Q slots do ALOHA: ótimo quando 2^Q ≈ número de tags respondendo.
/// <paramref name="UseSelect"/> = Query com Sel=SL (só tags que casaram com a máscara do Select respondem).
/// </summary>
public readonly record struct InventoryParameters(GenSession Session, InventoryTarget Target, int Q, bool UseSelect)
{
    public const int MinQ = 0;
    public const int MaxQ = 15;
}

/// <summary>Configuração estática de inventário aplicada ao conectar (inclusive após reconexão).</summary>
/// <param name="SelectEpcPrefix">Prefixo de EPC em hex para o Select (vazio = sem filtro em RF).</param>
public sealed record InventoryProfile(InventoryParameters Parameters, string SelectEpcPrefix);

/// <summary>
/// Opcional para <see cref="IReaderProtocol"/>: protocolos que aceitam parâmetros Gen2 (Query/Select).
/// O transporte guarda os últimos parâmetros e os reaplica em toda reconexão.
/// </summary>
public interface IInventoryParameterProtocol
{
    /// <summary>Comandos de configuração inicial (Select + Query), enviados após <see cref="IReaderProtocol.BuildConfigureCommands"/>.</summary>
    IReadOnlyList<byte[]> BuildProfileCommands(InventoryProfile profile);

    /// <summary>
    /// Comandos para trocar os parâmetros. Com <paramref name="inventoryRunning"/>, a sequência para o inventário,
    /// aplica e reinicia (módulos como o M100 ignoram configuração durante multi-poll).
    /// </summary>
    IReadOnlyList<byte[]> BuildApplyCommands(InventoryParameters parameters, bool inventoryRunning);
}

/// <summary>Opcional para <see cref="IReaderProtocol"/>: contadores de diagnóstico do enlace.</summary>
public interface IProtocolDiagnostics
{
    /// <summary>Quadros descartados por checksum/terminador/tamanho inválido.</summary>
    long InvalidFrames { get; }

    /// <summary>Rodadas de inventário sem nenhuma tag (ex.: erro 0x15 do M100).</summary>
    long EmptyRounds { get; }
}

/// <summary>Contadores do leitor desde a criação.</summary>
public readonly record struct ReaderDiagnostics(long InvalidFrames, long EmptyRounds, long LinkDrops);

/// <summary>
/// Opcional para <see cref="IRfidReader"/>: leitor que aceita ajuste de anticolisão em tempo real.
/// Usado pelo <see cref="Pipeline.PassageMonitor"/> para Q dinâmico e alternância de target.
/// </summary>
public interface IInventoryTuning
{
    /// <summary>False quando o protocolo não aceita parâmetros Gen2 (aplicar vira no-op).</summary>
    bool IsTuningSupported { get; }

    /// <summary>Parâmetros vigentes (os últimos aplicados ou os do perfil inicial).</summary>
    InventoryParameters CurrentParameters { get; }

    Task ApplyInventoryParametersAsync(InventoryParameters parameters, CancellationToken cancellationToken = default);
}

/// <summary>Opcional para <see cref="IRfidReader"/>: expõe <see cref="ReaderDiagnostics"/>.</summary>
public interface IReaderDiagnosticsSource
{
    ReaderDiagnostics GetDiagnostics();
}
