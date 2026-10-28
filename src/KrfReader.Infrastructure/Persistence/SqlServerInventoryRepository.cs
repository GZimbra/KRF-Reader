using System.Diagnostics;
using Dapper;
using KrfReader.Application.Configuration;
using KrfReader.Application.Persistence;
using KrfReader.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrfReader.Infrastructure.Persistence;

/// <summary>
/// <see cref="IInventoryRepository"/> e <see cref="ITagCatalogRepository"/> sobre SQL Server (2016+, Express incluso),
/// com Microsoft.Data.SqlClient + Dapper. Mesmo modelo e mesmas views do SQLite; existe para o Power BI ler
/// o banco ao vivo por DirectQuery. Uma conexão por operação (pool do SqlClient). Thread-safe.
/// <para>
/// <see cref="InitializeAsync"/> cria o banco do <c>Initial Catalog</c> se não existir (exige permissão CREATE DATABASE),
/// liga READ_COMMITTED_SNAPSHOT (consulta do BI não bloqueia gravação do lote e vice-versa) e aplica as migrações.
/// </para>
/// </summary>
public sealed partial class SqlServerInventoryRepository : IInventoryRepository, ITagCatalogRepository
{
    /// <summary>Limite de parâmetros por SELECT ... IN (...). O SQL Server aceita até 2100 por comando.</summary>
    internal const int InClauseChunkSize = 500;

    private const string SelectStatusSql =
        "SELECT EpcId, Status FROM dbo.Tags WHERE EpcId IN @Epcs";

    // Mesma semântica do upsert do SQLite. No SET do UPDATE toda referência a coluna é o valor ANTES do update.
    // Escritor único (fila de persistência do pipeline): UPDATE + INSERT basta, sem MERGE/HOLDLOCK.
    private const string UpsertTagSql = """
        UPDATE dbo.Tags SET
            UltimaLeitura = CASE WHEN @UltimaLeitura > UltimaLeitura THEN @UltimaLeitura ELSE UltimaLeitura END,
            UltimaAntenaId = CASE WHEN @UltimaLeitura >= UltimaLeitura THEN @UltimaAntenaId ELSE UltimaAntenaId END,
            UltimaRssi = CASE WHEN @UltimaLeitura >= UltimaLeitura THEN @UltimaRssi ELSE UltimaRssi END,
            Status = CASE WHEN @StatusChanged = 1 THEN @Status ELSE Status END,
            UltimaAtualizacaoStatus = CASE WHEN @StatusChanged = 1 THEN @UltimaAtualizacaoStatus ELSE UltimaAtualizacaoStatus END
        WHERE EpcId = @EpcId;
        IF @@ROWCOUNT = 0
            INSERT INTO dbo.Tags (EpcId, Status, UltimaLeitura, UltimaAtualizacaoStatus, UltimaAntenaId, UltimaRssi)
            VALUES (@EpcId, @Status, @UltimaLeitura, @UltimaAtualizacaoStatus, @UltimaAntenaId, @UltimaRssi);
        """;

    private const string InsertEventSql = """
        INSERT INTO dbo.Eventos (EpcId, Modo, Tipo, [Timestamp], AntenaId, Rssi)
        VALUES (@EpcId, @Modo, @Tipo, @Timestamp, @AntenaId, @Rssi)
        """;

    private readonly string _connectionString;
    private readonly ILogger<SqlServerInventoryRepository> _logger;

    public SqlServerInventoryRepository(DatabaseOptions options, ILogger<SqlServerInventoryRepository>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            throw new ArgumentException("Database.ConnectionString não configurada.", nameof(options));

        var builder = new SqlConnectionStringBuilder(options.ConnectionString);
        if (string.IsNullOrWhiteSpace(builder.InitialCatalog))
            throw new ArgumentException("Connection string do SQL Server sem Database/Initial Catalog (ex.: Database=KrfReader).", nameof(options));
        if (string.Equals(builder.InitialCatalog, "master", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Não use o banco master: informe um banco próprio (ex.: Database=KrfReader).", nameof(options));
        if (string.IsNullOrWhiteSpace(builder.ApplicationName) || builder.ApplicationName == new SqlConnectionStringBuilder().ApplicationName)
            builder.ApplicationName = "KRF Reader";

        _connectionString = builder.ConnectionString;
        DatabaseName = builder.InitialCatalog;
        DataSource = builder.DataSource;
        _logger = logger ?? NullLogger<SqlServerInventoryRepository>.Instance;
    }

    public string DatabaseName { get; }

    public string DataSource { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await EnsureDatabaseExistsAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureReadCommittedSnapshotAsync(connection, cancellationToken).ConfigureAwait(false);
        var (from, to) = await SqlServerSchemaMigrator.MigrateAsync(connection, _logger, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Banco SQL Server inicializado ({DataSource}, banco {Database}, versão {ServerVersion}, schema {From} → {To}).",
            connection.DataSource, connection.Database, connection.ServerVersion, from, to);
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
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadStatusAsync(connection, transaction, epcs, cancellationToken).ConfigureAwait(false);
            var plan = BatchPlanner.Build(mode, reads, epcs, state);

            var upserts = plan.Upserts.Select(u => new TagUpsertRow
            {
                EpcId = u.EpcId,
                Status = u.Status.ToDb(),
                UltimaLeitura = new SqlDateTime2(u.UltimaLeitura),
                UltimaAtualizacaoStatus = new SqlDateTime2(u.UltimaAtualizacaoStatus),
                UltimaAntenaId = u.UltimaAntenaId,
                UltimaRssi = u.UltimaRssi,
                StatusChanged = u.StatusChanged ? 1 : 0,
            }).ToList();
            var events = plan.Events.Select(e => new EventInsertRow
            {
                EpcId = e.EpcId,
                Modo = modeDb,
                Tipo = e.Tipo.ToDb(),
                Timestamp = new SqlDateTime2(e.Timestamp),
                AntenaId = e.AntenaId,
                Rssi = e.Rssi,
            }).ToList();

            // Tags antes de Eventos por causa da FK.
            await connection.ExecuteAsync(new CommandDefinition(UpsertTagSql, upserts, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (events.Count > 0)
                await connection.ExecuteAsync(new CommandDefinition(InsertEventSql, events, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

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
                (SELECT COUNT(*) FROM dbo.Tags WHERE Status = @Dentro) AS Dentro,
                (SELECT COUNT(*) FROM dbo.Tags WHERE Status = @Fora) AS Fora,
                (SELECT COUNT(*) FROM dbo.Eventos WHERE Tipo = @Anomalia) AS Anomalias
            """;

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleAsync<CountersRow>(new CommandDefinition(sql, new
        {
            Dentro = TagStatus.Dentro.ToDb(),
            Fora = TagStatus.Fora.ToDb(),
            Anomalia = EventType.SaidaSemEntrada.ToDb(),
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return new InventoryCounters(row.Dentro, row.Fora, row.Anomalias);
    }

    public async Task<IReadOnlyList<TagState>> QueryTagsAsync(TagStatus? status, string? search, int limit = 500, CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
            return [];

        var term = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        const string sql = """
            SELECT TOP (@Limit) t.EpcId, t.Status, t.UltimaLeitura, t.UltimaAtualizacaoStatus, t.UltimaAntenaId, t.UltimaRssi,
                   c.Descricao, c.Sku
            FROM dbo.Tags t
            LEFT JOIN dbo.TagCadastro c ON c.EpcId = t.EpcId
            WHERE (@Status IS NULL OR t.Status = @Status)
              AND (@Pattern IS NULL
                   OR t.EpcId LIKE @Pattern ESCAPE '\'
                   OR c.Descricao LIKE @Pattern ESCAPE '\'
                   OR c.Sku LIKE @Pattern ESCAPE '\')
            ORDER BY t.UltimaLeitura DESC
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
            DbTimestamp.AsUtc(r.UltimaLeitura),
            DbTimestamp.AsUtc(r.UltimaAtualizacaoStatus),
            r.Descricao,
            r.Sku,
            r.UltimaAntenaId,
            r.UltimaRssi)).ToList();
    }

    public async Task<IReadOnlyList<InventoryEvent>> GetEventsAsync(string epc, int limit = 200, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(epc);
        if (limit <= 0)
            return [];

        var normalized = Epc.Normalize(epc) ?? epc.Trim().ToUpperInvariant();
        const string sql = """
            SELECT TOP (@Limit) Id, EpcId, Modo, Tipo, [Timestamp], AntenaId, Rssi
            FROM dbo.Eventos
            WHERE EpcId = @EpcId
            ORDER BY [Timestamp] DESC, Id DESC
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
            DbTimestamp.AsUtc(r.Timestamp),
            r.AntenaId,
            r.Rssi)).ToList();
    }

    /// <summary>
    /// Abre uma conexão no servidor (no <c>master</c> se o banco ainda não existe) e devolve a versão.
    /// Usado pelo botão "Testar conexão" da tela Configurações; não cria nada.
    /// </summary>
    public static async Task<string> TestConnectionAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var database = builder.InitialCatalog;
        if (string.IsNullOrWhiteSpace(database))
            throw new ArgumentException("Informe Database=<nome> na connection string.");
        builder.InitialCatalog = "master";

        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var exists = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT CASE WHEN DB_ID(@Name) IS NULL THEN 0 ELSE 1 END", new { Name = database },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        var edition = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT CAST(SERVERPROPERTY('Edition') AS NVARCHAR(128))", cancellationToken: cancellationToken)).ConfigureAwait(false);

        return exists == 1
            ? $"OK · {connection.DataSource} · SQL Server {connection.ServerVersion} {edition} · banco {database} existe."
            : $"OK · {connection.DataSource} · SQL Server {connection.ServerVersion} {edition} · banco {database} será criado ao abrir.";
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task EnsureDatabaseExistsAsync(CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = "master" };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // QUOTENAME: nome vem da connection string, não entra cru no SQL dinâmico.
        var created = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            IF DB_ID(@Name) IS NOT NULL
                SELECT 0;
            ELSE
            BEGIN
                DECLARE @sql NVARCHAR(400) = N'CREATE DATABASE ' + QUOTENAME(@Name) + N';';
                EXEC (@sql);
                SELECT 1;
            END
            """,
            new { Name = DatabaseName }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (created == 1)
            _logger.LogInformation("Banco {Database} criado em {DataSource}.", DatabaseName, connection.DataSource);
    }

    /// <summary>
    /// READ_COMMITTED_SNAPSHOT: leitores (Power BI em DirectQuery, abas de consulta) leem a última versão confirmada
    /// sem bloquear o lote do pipeline. Ligar exige ficar sozinho no banco; com outra sessão aberta, só avisa.
    /// </summary>
    private async Task EnsureReadCommittedSnapshotAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var on = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE database_id = DB_ID()",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (on)
            return;

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH NO_WAIT;",
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            _logger.LogInformation("READ_COMMITTED_SNAPSHOT ligado em {Database}.", DatabaseName);
        }
        catch (SqlException ex)
        {
            _logger.LogWarning(ex,
                "Não foi possível ligar READ_COMMITTED_SNAPSHOT em {Database} (outra sessão aberta ou sem permissão). " +
                "Consultas do BI podem esperar pelo lote em gravação.", DatabaseName);
        }
    }

    private static async Task<Dictionary<string, TagStatus?>> LoadStatusAsync(
        SqlConnection connection, SqlTransaction transaction, string[] epcs, CancellationToken cancellationToken)
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

    /// <summary>Como no SQLite, mais <c>[</c>: no LIKE do SQL Server <c>[...]</c> é classe de caracteres.</summary>
    internal static string EscapeLike(string term) =>
        SqliteInventoryRepository.EscapeLike(term).Replace("[", @"\[", StringComparison.Ordinal);

    private sealed record StatusRow
    {
        public string EpcId { get; init; } = "";
        public string Status { get; init; } = "";
    }

    private sealed record TagRow
    {
        public string EpcId { get; init; } = "";
        public string Status { get; init; } = "";
        public DateTime UltimaLeitura { get; init; }
        public DateTime UltimaAtualizacaoStatus { get; init; }
        public int? UltimaAntenaId { get; init; }
        public double? UltimaRssi { get; init; }
        public string? Descricao { get; init; }
        public string? Sku { get; init; }
    }

    private sealed record TagUpsertRow
    {
        public string EpcId { get; init; } = "";
        public string Status { get; init; } = "";
        public SqlDateTime2 UltimaLeitura { get; init; } = null!;
        public SqlDateTime2 UltimaAtualizacaoStatus { get; init; } = null!;
        public int UltimaAntenaId { get; init; }
        public double UltimaRssi { get; init; }
        public int StatusChanged { get; init; }
    }

    private sealed record EventInsertRow
    {
        public string EpcId { get; init; } = "";
        public string Modo { get; init; } = "";
        public string Tipo { get; init; } = "";
        public SqlDateTime2 Timestamp { get; init; } = null!;
        public int AntenaId { get; init; }
        public double Rssi { get; init; }
    }

    private sealed record EventRow
    {
        public long Id { get; init; }
        public string EpcId { get; init; } = "";
        public string Modo { get; init; } = "";
        public string Tipo { get; init; } = "";
        public DateTime Timestamp { get; init; }
        public int AntenaId { get; init; }
        public double Rssi { get; init; }
    }

    private sealed record CountersRow
    {
        public int Dentro { get; init; }
        public int Fora { get; init; }
        public int Anomalias { get; init; }
    }
}
