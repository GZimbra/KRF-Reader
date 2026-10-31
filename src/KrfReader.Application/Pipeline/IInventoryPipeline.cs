using KrfReader.Application.Persistence;
using KrfReader.Domain;

namespace KrfReader.Application.Pipeline;

/// <summary>
/// Pipeline produtor/consumidor:
/// leitor (evento TagRead) → Channel de entrada (bounded) → dispatcher particiona por hash(EPC) →
/// N workers (filtro RSSI/antena + deduplicação) → Channel de persistência → 1 worker de lote (upsert).
/// <para>Todos os eventos deste tipo são disparados em threads de background: a UI deve fazer marshal.</para>
/// </summary>
public interface IInventoryPipeline : IAsyncDisposable
{
    /// <summary>Direção fixa do modo manual; null = parado OU modo POR ANTENA (ver <see cref="CurrentOperation"/>).</summary>
    OperationMode? CurrentMode { get; }

    /// <summary>Operação ativa (manual ou POR ANTENA); null = parado.</summary>
    InventoryOperation? CurrentOperation { get; }

    /// <summary>Incrementado a cada troca de modo/parada. Leituras carimbadas com geração antiga são descartadas.</summary>
    long Generation { get; }

    bool IsRunning { get; }

    /// <summary>Conecta o leitor e sobe as tasks (sem iniciar inventário).</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Troca de modo: incrementa geração, DESCARTA leituras pendentes da geração anterior (fila e buffer de lote),
    /// e inicia o inventário se não estiver ativo. Só leituras recebidas após esta chamada usam o modo novo.
    /// Chamar com o mesmo modo já ativo é no-op.
    /// </summary>
    Task SetModeAsync(OperationMode mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Modo POR ANTENA: entrada e saída simultâneas no mesmo leitor; a direção de cada leitura vem da porta
    /// (<paramref name="map"/>), com arbitragem de leitura cruzada entre as docas (<see cref="DirectionArbiter"/>).
    /// Mesma semântica de geração de <see cref="SetModeAsync"/>. Mesmo mapa já ativo = no-op.
    /// </summary>
    Task SetAntennaModeAsync(AntennaDirectionMap map, CancellationToken cancellationToken = default);

    /// <summary>
    /// PARAR: para o inventário, persiste as leituras pendentes da geração atual (que já tinham modo correto),
    /// e passa para CurrentMode = null.
    /// </summary>
    Task StopInventoryAsync(CancellationToken cancellationToken = default);

    /// <summary>Shutdown limpo: para leitor, drena filas, persiste o pendente e encerra as tasks.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Captura de tags para cadastro. Exige inventário PARADO (senão <see cref="InvalidOperationException"/>):
    /// liga o leitor por <paramref name="window"/>, coleta as leituras que passam no filtro de RSSI/antena e
    /// desliga o leitor. Nada vai para a fila nem para o banco: a captura não altera estoque.
    /// Serializada com troca de modo (ENTRADA/SAÍDA esperam a captura terminar).
    /// </summary>
    Task<TagCaptureResult> CaptureAsync(TimeSpan window, CancellationToken cancellationToken = default);

    /// <summary>Atualiza filtro em tempo real (thread-safe).</summary>
    void UpdateFilter(double minRssi, IReadOnlyCollection<int> allowedAntennas);

    PipelineStats GetStats();

    /// <summary>Disparado após cada lote persistido com sucesso.</summary>
    event EventHandler<BatchResult>? BatchPersisted;

    /// <summary>
    /// Leitura aceita com modo ativo (EPC normalizado, passou no filtro de RSSI/antena), ANTES da deduplicação:
    /// todas as releituras chegam aqui. Modo manual: disparado na thread do leitor. POR ANTENA: disparado no worker,
    /// depois da arbitragem de doca (leitura cruzada não chega). Handler deve ser rápido e thread-safe.
    /// </summary>
    event EventHandler<AcceptedRead>? ReadAccepted;

    /// <summary>Mudança de operação, com o <see cref="CurrentMode"/> novo (null = parado ou POR ANTENA).</summary>
    event EventHandler<OperationMode?>? ModeChanged;

    /// <summary>Mudança de operação (inclusive para null = parado). Disparado antes de <see cref="ModeChanged"/>.</summary>
    event EventHandler<InventoryOperation?>? OperationChanged;

    /// <summary>Erro do leitor ou de persistência (não derruba o pipeline).</summary>
    event EventHandler<string>? Error;
}

/// <param name="RawReads">Leituras recebidas do leitor.</param>
/// <param name="FilteredOut">Descartadas por RSSI/antena.</param>
/// <param name="Duplicates">Descartadas pela deduplicação.</param>
/// <param name="StaleDiscarded">Descartadas por pertencerem a geração (modo) anterior ou sem modo ativo.</param>
/// <param name="QueueDropped">Descartadas por fila cheia.</param>
/// <param name="Persisted">Leituras aplicadas no banco.</param>
/// <param name="DistinctEpcsSession">EPCs distintos aceitos (pós filtro) desde StartAsync.</param>
public sealed record PipelineStats(
    long RawReads,
    long FilteredOut,
    long Duplicates,
    long StaleDiscarded,
    long QueueDropped,
    long Persisted,
    int DistinctEpcsSession)
{
    /// <summary>POR ANTENA: leituras de porta sem doca (não movimentam estoque).</summary>
    public long UnmappedAntenna { get; init; }

    /// <summary>POR ANTENA: leituras descartadas como cruzadas (EPC atribuído à outra doca).</summary>
    public long CrossReads { get; init; }
}

/// <summary>Leitura aceita pelo filtro, com a direção vigente no recebimento (no POR ANTENA, a da doca que leu).</summary>
public sealed class AcceptedRead(TagRead read, OperationMode mode) : EventArgs
{
    public TagRead Read { get; } = read;
    public OperationMode Mode { get; } = mode;
}

/// <summary>Tag vista durante <see cref="IInventoryPipeline.CaptureAsync"/>.</summary>
/// <param name="AntennaId">Antena da leitura de maior RSSI.</param>
public sealed record CapturedTag(string Epc, double MaxRssi, int Reads, int AntennaId);

/// <summary>Tags capturadas, maior RSSI primeiro (a mais próxima da antena tende a ser a primeira).</summary>
public sealed record TagCaptureResult(IReadOnlyList<CapturedTag> Tags)
{
    public CapturedTag? Strongest => Tags.Count > 0 ? Tags[0] : null;

    /// <summary>Mais de uma tag no campo: o operador precisa confirmar qual é.</summary>
    public bool IsAmbiguous => Tags.Count > 1;
}
