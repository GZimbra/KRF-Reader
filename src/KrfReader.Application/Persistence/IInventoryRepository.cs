using KrfReader.Application.Pipeline;
using KrfReader.Domain;

namespace KrfReader.Application.Persistence;

/// <summary>
/// Acesso a dados do estoque. Trocar SQLite por SQL Server/PostgreSQL = nova implementação desta interface.
/// Toda regra de transição vem de <see cref="InventoryRules"/>; o repositório só aplica.
/// </summary>
public interface IInventoryRepository
{
    /// <summary>Aplica as migrações de schema pendentes. Idempotente; preserva os dados existentes.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Aplica um lote de leituras (todas do mesmo <paramref name="mode"/>) em UMA transação:
    /// carrega estado atual dos EPCs, decide via <see cref="InventoryRules.Decide"/> processando as leituras
    /// em ordem (o mesmo EPC pode aparecer mais de uma vez; o estado em memória é atualizado a cada uma),
    /// faz upsert em Tags (UltimaLeitura + antena/RSSI da leitura mais recente sempre; Status/UltimaAtualizacaoStatus
    /// só se mudou) e insere em
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

    /// <summary>Movimentações de estoque filtradas, mais recente primeiro.</summary>
    Task<IReadOnlyList<StockMovement>> QueryMovementsAsync(MovementFilter filter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Grava uma passagem fechada (veredito de alta densidade). Retorna o Id no banco, que difere do número
    /// em memória do <see cref="PassageTracker"/> (reinicia a cada execução). Passagem sem modo é ignorada (retorna null).
    /// </summary>
    Task<long?> SavePassageAsync(PassageSnapshot passage, CancellationToken cancellationToken = default);
}

/// <summary>Filtro da consulta de movimentações. Datas em UTC; intervalo [FromUtc, ToUtc).</summary>
/// <param name="Search">Busca parcial em EPC, descrição e SKU; null = sem filtro.</param>
/// <param name="OnlyAnomalies">Só SAÍDA SEM ENTRADA.</param>
public sealed record MovementFilter(
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    OperationMode? Tipo = null,
    string? Search = null,
    bool OnlyAnomalies = false,
    int Limit = 1000)
{
    /// <summary>
    /// Converte um período em dias do calendário local (inclusivo nas duas pontas) para o intervalo UTC
    /// [from 00:00, to+1 00:00). Meia-noite inexistente (início de horário de verão) vira a primeira hora válida.
    /// </summary>
    public static (DateTime? FromUtc, DateTime? ToUtc) LocalDaysToUtc(DateOnly? from, DateOnly? to, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return (from is { } f ? StartOfDayUtc(f, zone) : null,
                to is { } t ? StartOfDayUtc(t.AddDays(1), zone) : null);
    }

    private static DateTime StartOfDayUtc(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
            local = local.AddMinutes(30);
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
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
