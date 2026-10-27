using KrfReader.Domain;

namespace KrfReader.Application.Persistence;

/// <summary>
/// Acesso a dados do estoque. Trocar SQLite por SQL Server/PostgreSQL = nova implementação desta interface.
/// Toda regra de transição vem de <see cref="InventoryRules"/>; o repositório só aplica.
/// </summary>
public interface IInventoryRepository
{
    /// <summary>Cria schema/índices se não existirem. Idempotente.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Aplica um lote de leituras (todas do mesmo <paramref name="mode"/>) em UMA transação:
    /// carrega estado atual dos EPCs, decide via <see cref="InventoryRules.Decide"/> processando as leituras
    /// em ordem (o mesmo EPC pode aparecer mais de uma vez; o estado em memória é atualizado a cada uma),
    /// faz upsert em Tags (UltimaLeitura sempre; Status/UltimaAtualizacaoStatus só se mudou) e insere em
    /// Eventos apenas as leituras que geraram evento (Criacao, MudancaStatus, SaidaSemEntrada).
    /// Lote vazio = no-op.
    /// </summary>
    Task<BatchResult> ApplyBatchAsync(OperationMode mode, IReadOnlyList<TagRead> reads, CancellationToken cancellationToken = default);

    Task<InventoryCounters> GetCountersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Consulta tags, com descrição/SKU do cadastro quando houver. <paramref name="search"/> = busca parcial
    /// case-insensitive em EPC, descrição e SKU; null = sem filtro.
    /// </summary>
    Task<IReadOnlyList<TagState>> QueryTagsAsync(TagStatus? status, string? search, int limit = 500, CancellationToken cancellationToken = default);

    /// <summary>Histórico de uma tag, mais recente primeiro.</summary>
    Task<IReadOnlyList<InventoryEvent>> GetEventsAsync(string epc, int limit = 200, CancellationToken cancellationToken = default);
}

/// <summary>Resultado de uma leitura aplicada.</summary>
public sealed record AppliedRead(TagRead Read, OperationMode Mode, TagStatus? PreviousStatus, TagStatus NewStatus, EventType? Event)
{
    public bool ChangedState => Event is not null;
    public bool IsAnomaly => Event == EventType.SaidaSemEntrada;
}

public sealed record BatchResult(IReadOnlyList<AppliedRead> Applied)
{
    public static readonly BatchResult Empty = new([]);
}

/// <summary>Contadores globais. Anomalias = total de eventos SAIDA_SEM_ENTRADA.</summary>
public sealed record InventoryCounters(int Dentro, int Fora, int Anomalias);
