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
    /// <summary>Modo ativo; null = parado.</summary>
    OperationMode? CurrentMode { get; }

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

    /// <summary>Mudança de modo (inclusive para null).</summary>
    event EventHandler<OperationMode?>? ModeChanged;

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
    int DistinctEpcsSession);

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
