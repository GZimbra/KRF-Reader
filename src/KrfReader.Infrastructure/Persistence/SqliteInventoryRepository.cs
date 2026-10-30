using System.Data;
using System.Diagnostics;
using Dapper;
using KrfReader.Application.Configuration;
using KrfReader.Application.Persistence;
using KrfReader.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrfReader.Infrastructure.Persistence;

/// <summary>
/// <see cref="IInventoryRepository"/> sobre SQLite (Microsoft.Data.Sqlite + Dapper).
/// Uma conexão nova por operação (o provider faz pooling). Thread-safe: não guarda estado mutável.
/// Também implementa <see cref="ITagCatalogRepository"/> (SqliteInventoryRepository.Catalog.cs): mesmo banco e schema.
/// </summary>
public sealed partial class SqliteInventoryRepository : IInventoryRepository, ITagCatalogRepository, IPackageCatalogRepository
{
    /// <summary>Limite de parâmetros por SELECT ... IN (...).</summary>
    internal const int InClauseChunkSize = 500;

    private const int BusyTimeoutMs = 5000;

    private const string SelectStatusSql =
        "SELECT EpcId, Status FROM Tags WHERE EpcId IN @Epcs";

    // Status sempre recebe o estado final decidido em memória; UltimaAtualizacaoStatus só muda
    // quando alguma leitura do lote mudou o status; UltimaLeitura nunca retrocede, e antena/RSSI
    // acompanham a leitura que define UltimaLeitura. No SET, Tags.* são os valores ANTES do update.
    private const string UpsertTagSql = """
        INSERT INTO Tags (EpcId, Status, UltimaLeitura, UltimaAtualizacaoStatus, UltimaAntenaId, UltimaRssi)
        VALUES (@EpcId, @Status, @UltimaLeitura, @UltimaAtualizacaoStatus, @UltimaAntenaId, @UltimaRssi)
        ON CONFLICT (EpcId) DO UPDATE SET
            UltimaLeitura = MAX(Tags.UltimaLeitura, excluded.UltimaLeitura),
            UltimaAntenaId = CASE WHEN excluded.UltimaLeitura >= Tags.UltimaLeitura
                THEN excluded.UltimaAntenaId ELSE Tags.UltimaAntenaId END,
            UltimaRssi = CASE WHEN excluded.UltimaLeitura >= Tags.UltimaLeitura
                THEN excluded.UltimaRssi ELSE Tags.UltimaRssi END,
            Status = CASE WHEN @StatusChanged = 1 THEN excluded.Status ELSE Tags.Status END,
            UltimaAtualizacaoStatus = CASE WHEN @StatusChanged = 1
                THEN excluded.UltimaAtualizacaoStatus ELSE Tags.UltimaAtualizacaoStatus END
        """;

    private const string InsertEventSql = """
        INSERT INTO Eventos (EpcId, Modo, Tipo, Timestamp, AntenaId, Rssi, EmbalagemId)
        VALUES (@EpcId, @Modo, @Tipo, @Timestamp, @AntenaId, @Rssi, @EmbalagemId)
        """;

    private const string SelectPackagesSql = """
        SELECT ee.EpcId, em.Id, em.Codigo, em.Status,
               (SELECT COUNT(*) FROM EmbalagemEtiquetas x WHERE x.EmbalagemId = em.Id) AS Etiquetas
        FROM EmbalagemEtiquetas ee
        JOIN Embalagens em ON em.Id = ee.EmbalagemId
        WHERE ee.EpcId IN @Epcs
        """;

    // Última detecção nunca retrocede; antena/RSSI/EPC acompanham a leitura que a define. No SET, colunas = valor ANTES.
    // O segundo comando alinha o status das etiquetas da embalagem que não foram lidas neste lote.
    internal const string UpdatePackageSql = """
        UPDATE Embalagens SET
            UltimaEpcId = CASE WHEN UltimaDeteccao IS NULL OR @UltimaDeteccao >= UltimaDeteccao THEN @UltimaEpcId ELSE UltimaEpcId END,
            UltimaAntenaId = CASE WHEN UltimaDeteccao IS NULL OR @UltimaDeteccao >= UltimaDeteccao THEN @UltimaAntenaId ELSE UltimaAntenaId END,
            UltimaRssi = CASE WHEN UltimaDeteccao IS NULL OR @UltimaDeteccao >= UltimaDeteccao THEN @UltimaRssi ELSE UltimaRssi END,
            UltimaDeteccao = CASE WHEN UltimaDeteccao IS NULL OR @UltimaDeteccao > UltimaDeteccao THEN @UltimaDeteccao ELSE UltimaDeteccao END,
            Status = CASE WHEN @StatusChanged = 1 THEN @Status ELSE Status END,
            UltimaAtualizacaoStatus = CASE WHEN @StatusChanged = 1 THEN @UltimaAtualizacaoStatus ELSE UltimaAtualizacaoStatus END
        WHERE Id = @Id;
        UPDATE Tags SET Status = @Status, UltimaAtualizacaoStatus = @UltimaAtualizacaoStatus
        WHERE @StatusChanged = 1 AND Status <> @Status
          AND EpcId IN (SELECT EpcId FROM EmbalagemEtiquetas WHERE EmbalagemId = @Id);
        """;

    private readonly string _connectionString;
    private readonly ILogger<SqliteInventoryRepository> _logger;

    public SqliteInventoryRepository(DatabaseOptions options, ILogger<SqliteInventoryRepository>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            throw new ArgumentException("Database.ConnectionString não configurada.", nameof(options));

        var builder = new SqliteConnectionStringBuilder(options.ConnectionString)
        {
            ForeignKeys = true,
        };
        _connectionString = builder.ToString();
        _logger = logger ?? NullLogger<SqliteInventoryRepository>.Instance;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        EnsureDirectoryExists();

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var journalMode = await connection.ExecuteScalarAsync<string>(
            new CommandDefinition("PRAGMA journal_mode=WAL;", cancellationToken: cancellationToken)).ConfigureAwait(false);
        var (from, to) = await SchemaMigrator.MigrateAsync(connection, _logger, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Banco SQLite inicializado ({DataSource}, journal_mode={JournalMode}, schema {From} → {To}).",
            connection.DataSource, journalMode, from, to);
    }

    public async Task<BatchResult> ApplyBatchAsync(OperationMode mode, IReadOnlyList<TagRead> reads, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reads);
        if (reads.Count == 0)
            return BatchResult.Empty;

        var modeDb = mode.ToDb(); // valida o enum antes de abrir conexão

        var epcs = BatchPlanner.NormalizeEpcs(reads);

        var sw = Stopwatch.StartNew();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadStatusAsync(connection, transaction, epcs, cancellationToken).ConfigureAwait(false);
            var packages = await LoadPackagesAsync(connection, transaction, epcs, cancellationToken).ConfigureAwait(false);
            var plan = BatchPlanner.Build(mode, reads, epcs, state, packages);

            var upserts = plan.Upserts.Select(u => new TagUpsertRow
            {
                EpcId = u.EpcId,
                Status = u.Status.ToDb(),
                UltimaLeitura = DbTimestamp.ToDb(u.UltimaLeitura),
                UltimaAtualizacaoStatus = DbTimestamp.ToDb(u.UltimaAtualizacaoStatus),
                UltimaAntenaId = u.UltimaAntenaId,
                UltimaRssi = u.UltimaRssi,
                StatusChanged = u.StatusChanged ? 1 : 0,
            }).ToList();
            var events = plan.Events.Select(e => new EventRow
            {
                EpcId = e.EpcId,
                Modo = modeDb,
                Tipo = e.Tipo.ToDb(),
                Timestamp = DbTimestamp.ToDb(e.Timestamp),
                AntenaId = e.AntenaId,
                Rssi = e.Rssi,
                EmbalagemId = e.EmbalagemId,
            }).ToList();
            var packageUpdates = plan.PackageUpdates.Select(u => new
            {
                u.Id,
                Status = u.Status.ToDb(),
                StatusChanged = u.StatusChanged ? 1 : 0,
                UltimaAtualizacaoStatus = DbTimestamp.ToDb(u.UltimaAtualizacaoStatus),
                UltimaDeteccao = DbTimestamp.ToDb(u.UltimaDeteccao),
                u.UltimaEpcId,
                u.UltimaAntenaId,
                u.UltimaRssi,
            }).ToList();

            // Tags antes de Eventos por causa da FK.
            await connection.ExecuteAsync(new CommandDefinition(UpsertTagSql, upserts, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (events.Count > 0)
                await connection.ExecuteAsync(new CommandDefinition(InsertEventSql, events, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (packageUpdates.Count > 0)
                await connection.ExecuteAsync(new CommandDefinition(UpdatePackageSql, packageUpdates, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogDebug("Lote {Mode} aplicado: {Reads} leituras, {Tags} tags, {Events} eventos em {Elapsed} ms.",
                modeDb, reads.Count, upserts.Count, events.Count, sw.ElapsedMilliseconds);
            return new BatchResult(plan.Applied);
        }
        catch (Exception ex)
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackEx)
            {
                _logger.LogWarning(rollbackEx, "Falha no rollback do lote.");
            }
            if (ex is not OperationCanceledException)
                _logger.LogError(ex, "Falha ao aplicar lote {Mode} com {Reads} leituras; transação desfeita.", modeDb, reads.Count);
            throw;
        }
    }

    public async Task<InventoryCounters> GetCountersAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                (SELECT COUNT(*) FROM ItensEstoque WHERE Status = @Dentro) AS Dentro,
                (SELECT COUNT(*) FROM ItensEstoque WHERE Status = @Fora) AS Fora,
                (SELECT COUNT(*) FROM Eventos WHERE Tipo = @Anomalia) AS Anomalias
            """;

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleAsync<CountersRow>(new CommandDefinition(sql, new
        {
            Dentro = TagStatus.Dentro.ToDb(),
            Fora = TagStatus.Fora.ToDb(),
            Anomalia = EventType.SaidaSemEntrada.ToDb(),
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return new InventoryCounters(checked((int)row.Dentro), checked((int)row.Fora), checked((int)row.Anomalias));
    }

    public async Task<IReadOnlyList<TagState>> QueryTagsAsync(TagStatus? status, string? search, int limit = 500, CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
            return [];

        var term = string.IsNullOrWhiteSpace(search) ? null : search.Trim().ToUpperInvariant();
        // LEFT JOIN: tag lida sem cadastro aparece com Descricao/Sku nulos. Etiqueta de embalagem traz a descrição
        // (ou o código) da embalagem; o status da linha já acompanha o da embalagem (ApplyBatchAsync).
        var sql = """
            SELECT t.EpcId, t.Status, t.UltimaLeitura, t.UltimaAtualizacaoStatus, t.UltimaAntenaId, t.UltimaRssi,
                   COALESCE(em.Descricao, em.Codigo, c.Descricao) AS Descricao, c.Sku, em.Codigo AS EmbalagemCodigo
            FROM Tags t
            LEFT JOIN TagCadastro c ON c.EpcId = t.EpcId
            LEFT JOIN EmbalagemEtiquetas ee ON ee.EpcId = t.EpcId
            LEFT JOIN Embalagens em ON em.Id = ee.EmbalagemId
            WHERE (@Status IS NULL OR t.Status = @Status)
              AND (@Pattern IS NULL
                   OR t.EpcId LIKE @Pattern ESCAPE '\'
                   OR c.Descricao LIKE @Pattern ESCAPE '\'
                   OR c.Sku LIKE @Pattern ESCAPE '\'
                   OR em.Codigo LIKE @Pattern ESCAPE '\'
                   OR em.Descricao LIKE @Pattern ESCAPE '\')
            ORDER BY t.UltimaLeitura DESC
            LIMIT @Limit
            """;

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<TagRow>(new CommandDefinition(sql, new
        {
            Status = status?.ToDb(),
            Pattern = term is null ? null : "%" + EscapeLike(term) + "%",
            Limit = limit,
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.Select(r => new TagState(
            r.EpcId,
            DbLiterals.ParseStatus(r.Status),
            DbTimestamp.FromDb(r.UltimaLeitura),
            DbTimestamp.FromDb(r.UltimaAtualizacaoStatus),
            r.Descricao,
            r.Sku,
            r.UltimaAntenaId is { } antena ? checked((int)antena) : null,
            r.UltimaRssi,
            r.EmbalagemCodigo)).ToList();
    }

    public async Task<IReadOnlyList<InventoryEvent>> GetEventsAsync(string epc, int limit = 200, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(epc);
        if (limit <= 0)
            return [];

        var normalized = Epc.Normalize(epc) ?? epc.Trim().ToUpperInvariant();
        const string sql = """
            SELECT Id, EpcId, Modo, Tipo, Timestamp, AntenaId, Rssi
            FROM Eventos
            WHERE EpcId = @EpcId
            ORDER BY Timestamp DESC, Id DESC
            LIMIT @Limit
            """;

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<EventRow>(new CommandDefinition(sql, new
        {
            EpcId = normalized,
            Limit = limit,
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.Select(r => new InventoryEvent(
            r.Id,
            r.EpcId,
            DbLiterals.ParseMode(r.Modo),
            DbLiterals.ParseEventType(r.Tipo),
            DbTimestamp.FromDb(r.Timestamp),
            checked((int)r.AntenaId),
            r.Rssi)).ToList();
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            // Por conexão (foreign_keys vem da connection string: Foreign Keys=True).
            await connection.ExecuteAsync(new CommandDefinition(
                $"PRAGMA synchronous=NORMAL; PRAGMA busy_timeout={BusyTimeoutMs};",
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<Dictionary<string, TagStatus?>> LoadStatusAsync(
        SqliteConnection connection, IDbTransaction transaction, string[] epcs, CancellationToken cancellationToken)
    {
        var distinct = epcs.Distinct(StringComparer.Ordinal).ToArray();
        var state = new Dictionary<string, TagStatus?>(distinct.Length, StringComparer.Ordinal);

        foreach (var chunk in distinct.Chunk(InClauseChunkSize))
        {
            var rows = await connection.QueryAsync<StatusRow>(new CommandDefinition(
                SelectStatusSql, new { Epcs = chunk }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            foreach (var row in rows)
                state[row.EpcId] = DbLiterals.ParseStatus(row.Status);
        }

        return state;
    }

    private static async Task<Dictionary<string, BatchPlanner.PackageState>> LoadPackagesAsync(
        SqliteConnection connection, IDbTransaction transaction, string[] epcs, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, BatchPlanner.PackageState>(StringComparer.Ordinal);
        var byId = new Dictionary<long, BatchPlanner.PackageState>();
        foreach (var chunk in epcs.Distinct(StringComparer.Ordinal).Chunk(InClauseChunkSize))
        {
            var rows = await connection.QueryAsync<PackageLinkRow>(new CommandDefinition(
                SelectPackagesSql, new { Epcs = chunk }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            foreach (var row in rows)
            {
                // Uma instância por embalagem: as etiquetas compartilham o status durante o plano.
                if (!byId.TryGetValue(row.Id, out var package))
                {
                    package = new BatchPlanner.PackageState(
                        new PackageRef(row.Id, row.Codigo, checked((int)row.Etiquetas)),
                        row.Status is null ? null : DbLiterals.ParseStatus(row.Status));
                    byId[row.Id] = package;
                }
                result[row.EpcId] = package;
            }
        }
        return result;
    }

    internal static string EscapeLike(string term) =>
        term.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);

    private void EnsureDirectoryExists()
    {
        var builder = new SqliteConnectionStringBuilder(_connectionString);
        var dataSource = builder.DataSource;
        if (string.IsNullOrWhiteSpace(dataSource)
            || builder.Mode == SqliteOpenMode.Memory
            || dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
            || dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return;

        var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
    }

    private sealed record StatusRow
    {
        public string EpcId { get; init; } = "";
        public string Status { get; init; } = "";
    }

    private sealed record PackageLinkRow
    {
        public string EpcId { get; init; } = "";
        public long Id { get; init; }
        public string Codigo { get; init; } = "";
        public string? Status { get; init; }
        public long Etiquetas { get; init; }
    }

    private sealed record TagRow
    {
        public string EpcId { get; init; } = "";
        public string Status { get; init; } = "";
        public string UltimaLeitura { get; init; } = "";
        public string UltimaAtualizacaoStatus { get; init; } = "";
        public long? UltimaAntenaId { get; init; }
        public double? UltimaRssi { get; init; }
        public string? Descricao { get; init; }
        public string? Sku { get; init; }
        public string? EmbalagemCodigo { get; init; }
    }

    private sealed record TagUpsertRow
    {
        public string EpcId { get; init; } = "";
        public string Status { get; init; } = "";
        public string UltimaLeitura { get; init; } = "";
        public string UltimaAtualizacaoStatus { get; init; } = "";
        public int UltimaAntenaId { get; init; }
        public double UltimaRssi { get; init; }
        public int StatusChanged { get; init; }
    }

    private sealed record EventRow
    {
        public long Id { get; init; }
        public string EpcId { get; init; } = "";
        public string Modo { get; init; } = "";
        public string Tipo { get; init; } = "";
        public string Timestamp { get; init; } = "";
        public long AntenaId { get; init; }
        public double Rssi { get; init; }
        public long? EmbalagemId { get; init; }
    }

    private sealed record CountersRow
    {
        public long Dentro { get; init; }
        public long Fora { get; init; }
        public long Anomalias { get; init; }
    }
}
