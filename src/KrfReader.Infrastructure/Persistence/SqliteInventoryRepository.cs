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
/// </summary>
public sealed class SqliteInventoryRepository : IInventoryRepository
{
    internal const string SchemaResourceName = "KrfReader.Infrastructure.schema.sql";

    /// <summary>Limite de parâmetros por SELECT ... IN (...).</summary>
    internal const int InClauseChunkSize = 500;

    private const int BusyTimeoutMs = 5000;

    private const string SelectStatusSql =
        "SELECT EpcId, Status FROM Tags WHERE EpcId IN @Epcs";

    // Status sempre recebe o estado final decidido em memória; UltimaAtualizacaoStatus só muda
    // quando alguma leitura do lote mudou o status; UltimaLeitura nunca retrocede.
    private const string UpsertTagSql = """
        INSERT INTO Tags (EpcId, Status, UltimaLeitura, UltimaAtualizacaoStatus)
        VALUES (@EpcId, @Status, @UltimaLeitura, @UltimaAtualizacaoStatus)
        ON CONFLICT (EpcId) DO UPDATE SET
            UltimaLeitura = MAX(Tags.UltimaLeitura, excluded.UltimaLeitura),
            Status = CASE WHEN @StatusChanged = 1 THEN excluded.Status ELSE Tags.Status END,
            UltimaAtualizacaoStatus = CASE WHEN @StatusChanged = 1
                THEN excluded.UltimaAtualizacaoStatus ELSE Tags.UltimaAtualizacaoStatus END
        """;

    private const string InsertEventSql = """
        INSERT INTO Eventos (EpcId, Modo, Tipo, Timestamp, AntenaId, Rssi)
        VALUES (@EpcId, @Modo, @Tipo, @Timestamp, @AntenaId, @Rssi)
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
        var schema = LoadSchema();

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var journalMode = await connection.ExecuteScalarAsync<string>(
            new CommandDefinition("PRAGMA journal_mode=WAL;", cancellationToken: cancellationToken)).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(schema, cancellationToken: cancellationToken)).ConfigureAwait(false);

        _logger.LogInformation("Banco SQLite inicializado ({DataSource}, journal_mode={JournalMode}).",
            connection.DataSource, journalMode);
    }

    public async Task<BatchResult> ApplyBatchAsync(OperationMode mode, IReadOnlyList<TagRead> reads, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reads);
        if (reads.Count == 0)
            return BatchResult.Empty;

        var modeDb = mode.ToDb(); // valida o enum antes de abrir conexão

        var epcs = new string[reads.Count];
        for (var i = 0; i < reads.Count; i++)
        {
            epcs[i] = Epc.Normalize(reads[i].Epc)
                ?? throw new ArgumentException($"EPC inválido na posição {i}: '{reads[i].Epc}'.", nameof(reads));
        }

        var sw = Stopwatch.StartNew();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadStatusAsync(connection, transaction, epcs, cancellationToken).ConfigureAwait(false);

            var applied = new AppliedRead[reads.Count];
            var pending = new Dictionary<string, PendingTag>(StringComparer.Ordinal);
            var events = new List<EventRow>();

            for (var i = 0; i < reads.Count; i++)
            {
                var read = reads[i];
                var epc = epcs[i];
                state.TryGetValue(epc, out var previous);
                var decision = InventoryRules.Decide(previous, mode);
                var timestamp = DbTimestamp.ToDb(read.TimestampUtc);

                applied[i] = new AppliedRead(read, mode, previous, decision.NewStatus, decision.Event);
                state[epc] = decision.NewStatus;

                if (!pending.TryGetValue(epc, out var tag))
                {
                    tag = new PendingTag(timestamp);
                    pending[epc] = tag;
                }
                tag.Status = decision.NewStatus;
                if (string.CompareOrdinal(timestamp, tag.UltimaLeitura) > 0)
                    tag.UltimaLeitura = timestamp;

                if (decision.Event is { } evt)
                {
                    tag.StatusChanged = true;
                    tag.UltimaAtualizacaoStatus = timestamp;
                    events.Add(new EventRow
                    {
                        EpcId = epc,
                        Modo = modeDb,
                        Tipo = evt.ToDb(),
                        Timestamp = timestamp,
                        AntenaId = read.AntennaId,
                        Rssi = read.Rssi,
                    });
                }
            }

            var upserts = new List<TagUpsertRow>(pending.Count);
            foreach (var (epc, tag) in pending)
            {
                upserts.Add(new TagUpsertRow
                {
                    EpcId = epc,
                    Status = tag.Status.ToDb(),
                    UltimaLeitura = tag.UltimaLeitura,
                    // Para tag já existente sem mudança, o valor é ignorado pelo CASE do upsert.
                    UltimaAtualizacaoStatus = tag.UltimaAtualizacaoStatus ?? tag.UltimaLeitura,
                    StatusChanged = tag.StatusChanged ? 1 : 0,
                });
            }

            // Tags antes de Eventos por causa da FK.
            await connection.ExecuteAsync(new CommandDefinition(UpsertTagSql, upserts, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (events.Count > 0)
                await connection.ExecuteAsync(new CommandDefinition(InsertEventSql, events, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogDebug("Lote {Mode} aplicado: {Reads} leituras, {Tags} tags, {Events} eventos em {Elapsed} ms.",
                modeDb, reads.Count, upserts.Count, events.Count, sw.ElapsedMilliseconds);
            return new BatchResult(applied);
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
                (SELECT COUNT(*) FROM Tags WHERE Status = @Dentro) AS Dentro,
                (SELECT COUNT(*) FROM Tags WHERE Status = @Fora) AS Fora,
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

    public async Task<IReadOnlyList<TagState>> QueryTagsAsync(TagStatus? status, string? epcContains, int limit = 500, CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
            return [];

        var term = string.IsNullOrWhiteSpace(epcContains) ? null : epcContains.Trim().ToUpperInvariant();
        var sql = """
            SELECT EpcId, Status, UltimaLeitura, UltimaAtualizacaoStatus
            FROM Tags
            WHERE (@Status IS NULL OR Status = @Status)
              AND (@Pattern IS NULL OR EpcId LIKE @Pattern ESCAPE '\')
            ORDER BY UltimaLeitura DESC
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
            DbTimestamp.FromDb(r.UltimaAtualizacaoStatus))).ToList();
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

    internal static string EscapeLike(string term) =>
        term.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);

    private static string LoadSchema()
    {
        using var stream = typeof(SqliteInventoryRepository).Assembly.GetManifestResourceStream(SchemaResourceName)
            ?? throw new InvalidOperationException($"Recurso embutido '{SchemaResourceName}' não encontrado.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

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

    private sealed class PendingTag(string firstTimestamp)
    {
        public TagStatus Status { get; set; }
        public string UltimaLeitura { get; set; } = firstTimestamp;
        public string? UltimaAtualizacaoStatus { get; set; }
        public bool StatusChanged { get; set; }
    }

    private sealed record StatusRow
    {
        public string EpcId { get; init; } = "";
        public string Status { get; init; } = "";
    }

    private sealed record TagRow
    {
        public string EpcId { get; init; } = "";
        public string Status { get; init; } = "";
        public string UltimaLeitura { get; init; } = "";
        public string UltimaAtualizacaoStatus { get; init; } = "";
    }

    private sealed record TagUpsertRow
    {
        public string EpcId { get; init; } = "";
        public string Status { get; init; } = "";
        public string UltimaLeitura { get; init; } = "";
        public string UltimaAtualizacaoStatus { get; init; } = "";
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
    }

    private sealed record CountersRow
    {
        public long Dentro { get; init; }
        public long Fora { get; init; }
        public long Anomalias { get; init; }
    }
}
