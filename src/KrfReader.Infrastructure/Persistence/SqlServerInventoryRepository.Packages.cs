using System.Data;
using Dapper;
using KrfReader.Application.Persistence;
using KrfReader.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace KrfReader.Infrastructure.Persistence;

/// <summary>
/// <see cref="IPackageCatalogRepository"/>: tabelas <c>dbo.Embalagens</c>/<c>dbo.EmbalagemEtiquetas</c> e view
/// <c>dbo.EmbalagensCadastradas</c>. Mesma semântica do SQLite (SqliteInventoryRepository.Packages.cs).
/// </summary>
public sealed partial class SqlServerInventoryRepository
{
    private const string SelectPackageColumns = """
        SELECT EmbalagemId, Codigo, Descricao, TipoGlt, ComprimentoMm, LarguraMm, AlturaMm, PesoKg, Observacao,
               CadastradoEm, AtualizadoEm, Status, UltimaDeteccao, UltimaEpcId, UltimaAntenaId, UltimaRssi
        FROM dbo.EmbalagensCadastradas
        """;

    private const string SeedAndSyncPackageSql = """
        IF EXISTS (SELECT 1 FROM dbo.Embalagens WHERE Id = @Id AND Status IS NULL)
        BEGIN
            DECLARE @SeedStatus NVARCHAR(8), @SeedAtualizacao DATETIME2(3);
            SELECT TOP (1) @SeedStatus = t.Status, @SeedAtualizacao = t.UltimaAtualizacaoStatus
            FROM dbo.Tags t JOIN dbo.EmbalagemEtiquetas ee ON ee.EpcId = t.EpcId
            WHERE ee.EmbalagemId = @Id
            ORDER BY t.UltimaAtualizacaoStatus DESC;

            IF @SeedStatus IS NOT NULL
                UPDATE em SET
                    Status = @SeedStatus,
                    UltimaAtualizacaoStatus = @SeedAtualizacao,
                    UltimaDeteccao = l.UltimaLeitura,
                    UltimaEpcId = l.EpcId,
                    UltimaAntenaId = l.UltimaAntenaId,
                    UltimaRssi = l.UltimaRssi
                FROM dbo.Embalagens em
                CROSS APPLY (
                    SELECT TOP (1) t.EpcId, t.UltimaLeitura, t.UltimaAntenaId, t.UltimaRssi
                    FROM dbo.Tags t JOIN dbo.EmbalagemEtiquetas ee ON ee.EpcId = t.EpcId
                    WHERE ee.EmbalagemId = @Id
                    ORDER BY t.UltimaLeitura DESC
                ) l
                WHERE em.Id = @Id;
        END;

        UPDATE t SET Status = em.Status, UltimaAtualizacaoStatus = em.UltimaAtualizacaoStatus
        FROM dbo.Tags t
        JOIN dbo.EmbalagemEtiquetas ee ON ee.EpcId = t.EpcId
        JOIN dbo.Embalagens em ON em.Id = ee.EmbalagemId
        WHERE ee.EmbalagemId = @Id AND em.Status IS NOT NULL AND t.Status <> em.Status;
        """;

    public async Task<PackageSaveOutcome> AddPackageAsync(PackageInput input, CancellationToken cancellationToken = default)
    {
        var valid = PackageRules.Validate(input);
        var now = new SqlDateTime2(DateTime.UtcNow);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        // SERIALIZABLE: a conferência de conflito trava as faixas lidas até o commit (cadastro é raro, sem disputa real).
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        try
        {
            var conflict = await FindPackageConflictAsync(connection, transaction, null, valid, cancellationToken).ConfigureAwait(false);
            if (conflict is not null)
                return conflict;

            var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO dbo.Embalagens (Codigo, Descricao, TipoGlt, ComprimentoMm, LarguraMm, AlturaMm, PesoKg, Observacao,
                                            CadastradoEm, AtualizadoEm)
                OUTPUT INSERTED.Id
                VALUES (@Codigo, @Descricao, @TipoGlt, @ComprimentoMm, @LarguraMm, @AlturaMm, @PesoKg, @Observacao, @Now, @Now);
                """, PackageParameters(valid, now), transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

            await UpsertPackageTagsAsync(connection, transaction, id, valid.Etiquetas, now, cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(SeedAndSyncPackageSql, new { Id = id }, transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Embalagem {Codigo} cadastrada com {Count} etiqueta(s).", valid.Codigo, valid.Etiquetas.Count);
            return new PackageSaveOutcome(PackageSaveStatus.Ok, id, $"Embalagem {valid.Codigo} cadastrada com {valid.Etiquetas.Count} etiqueta(s).");
        }
        catch (SqlException ex) when (ex.Number is DuplicateKeyError or DuplicateIndexError)
        {
            return ConcurrentConflict(valid);
        }
    }

    public async Task<PackageSaveOutcome> UpdatePackageAsync(long id, PackageInput input, CancellationToken cancellationToken = default)
    {
        var valid = PackageRules.Validate(input);
        var now = new SqlDateTime2(DateTime.UtcNow);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        try
        {
            var exists = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT Id FROM dbo.Embalagens WITH (UPDLOCK) WHERE Id = @Id", new { Id = id }, transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (exists is null)
                return new PackageSaveOutcome(PackageSaveStatus.NaoEncontrada, null, "Embalagem não está mais cadastrada (removida por outra estação?).");

            var conflict = await FindPackageConflictAsync(connection, transaction, id, valid, cancellationToken).ConfigureAwait(false);
            if (conflict is not null)
                return conflict;

            var parameters = PackageParameters(valid, now);
            parameters.Add("Id", id);
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE dbo.Embalagens
                SET Codigo = @Codigo, Descricao = @Descricao, TipoGlt = @TipoGlt, ComprimentoMm = @ComprimentoMm,
                    LarguraMm = @LarguraMm, AlturaMm = @AlturaMm, PesoKg = @PesoKg, Observacao = @Observacao, AtualizadoEm = @Now
                WHERE Id = @Id
                """, parameters, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM dbo.EmbalagemEtiquetas WHERE EmbalagemId = @Id AND EpcId NOT IN @Epcs",
                new { Id = id, Epcs = valid.Etiquetas.Select(e => e.Epc).ToArray() }, transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            await UpsertPackageTagsAsync(connection, transaction, id, valid.Etiquetas, now, cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(SeedAndSyncPackageSql, new { Id = id }, transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Embalagem {Codigo} atualizada ({Count} etiqueta(s)).", valid.Codigo, valid.Etiquetas.Count);
            return new PackageSaveOutcome(PackageSaveStatus.Ok, id, $"Embalagem {valid.Codigo} atualizada ({valid.Etiquetas.Count} etiqueta(s)).");
        }
        catch (SqlException ex) when (ex.Number is DuplicateKeyError or DuplicateIndexError)
        {
            return ConcurrentConflict(valid);
        }
    }

    public async Task<bool> DeletePackageAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM dbo.Embalagens WHERE Id = @Id", new { Id = id }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (rows == 1)
            _logger.LogInformation("Embalagem {Id} removida.", id);
        return rows == 1;
    }

    public async Task<RegisteredPackage?> GetPackageAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<PackageRow>(new CommandDefinition(
            SelectPackageColumns + " WHERE EmbalagemId = @Id", new { Id = id }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return (await ToPackagesAsync(connection, rows.ToList(), cancellationToken).ConfigureAwait(false)).FirstOrDefault();
    }

    public async Task<RegisteredPackage?> FindPackageByEpcAsync(string epc, CancellationToken cancellationToken = default)
    {
        var normalized = Epc.Normalize(epc);
        if (normalized is null)
            return null;

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<PackageRow>(new CommandDefinition(
            SelectPackageColumns + " WHERE EmbalagemId = (SELECT EmbalagemId FROM dbo.EmbalagemEtiquetas WHERE EpcId = @EpcId)",
            new { EpcId = normalized }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return (await ToPackagesAsync(connection, rows.ToList(), cancellationToken).ConfigureAwait(false)).FirstOrDefault();
    }

    public async Task<IReadOnlyList<RegisteredPackage>> QueryPackagesAsync(
        string? term, RegisteredTagStatusFilter status = RegisteredTagStatusFilter.Todos, int limit = 500,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
            return [];

        var statusCondition = status switch
        {
            RegisteredTagStatusFilter.Todos => "1 = 1",
            RegisteredTagStatusFilter.Dentro or RegisteredTagStatusFilter.Fora => "Status = @Status",
            RegisteredTagStatusFilter.NuncaLida => "Status IS NULL",
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };
        var trimmed = string.IsNullOrWhiteSpace(term) ? null : term.Trim();
        var sql = $$"""
            SELECT TOP (@Limit) EmbalagemId, Codigo, Descricao, TipoGlt, ComprimentoMm, LarguraMm, AlturaMm, PesoKg, Observacao,
                   CadastradoEm, AtualizadoEm, Status, UltimaDeteccao, UltimaEpcId, UltimaAntenaId, UltimaRssi
            FROM dbo.EmbalagensCadastradas v
            WHERE {{statusCondition}}
              AND (@Pattern IS NULL
                   OR Codigo LIKE @Pattern ESCAPE '\'
                   OR Descricao LIKE @Pattern ESCAPE '\'
                   OR TipoGlt LIKE @Pattern ESCAPE '\'
                   OR EXISTS (SELECT 1 FROM dbo.EmbalagemEtiquetas ee
                              WHERE ee.EmbalagemId = v.EmbalagemId AND ee.EpcId LIKE @Pattern ESCAPE '\'))
            ORDER BY CASE WHEN UltimaDeteccao IS NULL THEN 1 ELSE 0 END, UltimaDeteccao DESC, Codigo
            """;

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<PackageRow>(new CommandDefinition(sql, new
        {
            Status = status switch
            {
                RegisteredTagStatusFilter.Dentro => TagStatus.Dentro.ToDb(),
                RegisteredTagStatusFilter.Fora => TagStatus.Fora.ToDb(),
                _ => null,
            },
            Pattern = trimmed is null ? null : "%" + EscapeLike(trimmed) + "%",
            Limit = limit,
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return await ToPackagesAsync(connection, rows.ToList(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, PackageRef>> GetPackageEpcMapAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT ee.EpcId, em.Id, em.Codigo,
                   (SELECT COUNT(*) FROM dbo.EmbalagemEtiquetas x WHERE x.EmbalagemId = em.Id) AS Etiquetas
            FROM dbo.EmbalagemEtiquetas ee
            JOIN dbo.Embalagens em ON em.Id = ee.EmbalagemId
            """;

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<PackageLinkRow>(new CommandDefinition(sql, cancellationToken: cancellationToken)).ConfigureAwait(false);
        var refs = new Dictionary<long, PackageRef>();
        var map = new Dictionary<string, PackageRef>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!refs.TryGetValue(row.Id, out var reference))
                refs[row.Id] = reference = new PackageRef(row.Id, row.Codigo, row.Etiquetas);
            map[row.EpcId] = reference;
        }
        return map;
    }

    private static async Task EnsureNotPackageTagAsync(
        SqlConnection connection, SqlTransaction? transaction, string epc, CancellationToken cancellationToken)
    {
        var codigo = await connection.ExecuteScalarAsync<string?>(new CommandDefinition("""
            SELECT em.Codigo FROM dbo.EmbalagemEtiquetas ee JOIN dbo.Embalagens em ON em.Id = ee.EmbalagemId WHERE ee.EpcId = @EpcId
            """, new { EpcId = epc }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (codigo is not null)
            throw new InvalidOperationException($"EPC {epc} é etiqueta da embalagem {codigo}. Edite pela aba Embalagens.");
    }

    private static async Task<PackageSaveOutcome?> FindPackageConflictAsync(
        SqlConnection connection, SqlTransaction transaction, long? id, ValidPackage valid, CancellationToken cancellationToken)
    {
        var sameCode = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT Id FROM dbo.Embalagens WHERE Codigo = @Codigo AND (@Id IS NULL OR Id <> @Id)",
            new { valid.Codigo, Id = id }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (sameCode is not null)
            return new PackageSaveOutcome(PackageSaveStatus.CodigoDuplicado, null,
                $"Código {valid.Codigo} já pertence a outra embalagem. O código identifica a embalagem física; o modelo vai em Tipo.");

        var epcs = valid.Etiquetas.Select(e => e.Epc).ToArray();
        var other = await connection.QueryFirstOrDefaultAsync<EpcOwnerRow>(new CommandDefinition("""
            SELECT TOP (1) ee.EpcId, em.Codigo AS Owner FROM dbo.EmbalagemEtiquetas ee JOIN dbo.Embalagens em ON em.Id = ee.EmbalagemId
            WHERE ee.EpcId IN @Epcs AND (@Id IS NULL OR ee.EmbalagemId <> @Id)
            ORDER BY ee.EpcId
            """, new { Epcs = epcs, Id = id }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (other is { } o)
            return new PackageSaveOutcome(PackageSaveStatus.EpcEmOutraEmbalagem, null,
                $"EPC {o.EpcId} já é etiqueta da embalagem {o.Owner}. Remova-o de lá antes.");

        var loose = await connection.QueryFirstOrDefaultAsync<EpcOwnerRow>(new CommandDefinition(
            "SELECT TOP (1) EpcId, Descricao AS Owner FROM dbo.TagCadastro WHERE EpcId IN @Epcs ORDER BY EpcId",
            new { Epcs = epcs }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (loose is { } l)
            return new PackageSaveOutcome(PackageSaveStatus.EpcCadastradoComoTag, null,
                $"EPC {l.EpcId} está cadastrado como tag avulsa (\"{l.Owner}\"). Remova o cadastro na aba Cadastro antes de vinculá-lo à embalagem; estado e histórico da tag são mantidos.");

        return null;
    }

    private static PackageSaveOutcome ConcurrentConflict(ValidPackage valid) =>
        new(PackageSaveStatus.CodigoDuplicado, null,
            $"Conflito ao gravar {valid.Codigo}: código ou etiqueta cadastrado por outra estação neste instante. Recarregue a lista e tente de novo.");

    private static async Task UpsertPackageTagsAsync(
        SqlConnection connection, SqlTransaction transaction, long id, IReadOnlyList<PackageTag> tags, SqlDateTime2 now,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE dbo.EmbalagemEtiquetas SET Posicao = @Posicao WHERE EpcId = @EpcId AND EmbalagemId = @EmbalagemId;
            IF @@ROWCOUNT = 0
                INSERT INTO dbo.EmbalagemEtiquetas (EpcId, EmbalagemId, Posicao, VinculadoEm)
                VALUES (@EpcId, @EmbalagemId, @Posicao, @Now);
            """, tags.Select(t => new PackageTagParam { EpcId = t.Epc, EmbalagemId = id, Posicao = t.Posicao, Now = now }).ToList(),
            transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static DynamicParameters PackageParameters(ValidPackage valid, SqlDateTime2 now)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Codigo", valid.Codigo);
        parameters.Add("Descricao", valid.Descricao);
        parameters.Add("TipoGlt", valid.TipoGlt);
        parameters.Add("ComprimentoMm", valid.ComprimentoMm);
        parameters.Add("LarguraMm", valid.LarguraMm);
        parameters.Add("AlturaMm", valid.AlturaMm);
        parameters.Add("PesoKg", valid.PesoKg, DbType.Decimal, precision: 10, scale: 3);
        parameters.Add("Observacao", valid.Observacao);
        parameters.AddDynamicParams(new { Now = now });
        return parameters;
    }

    private static async Task<IReadOnlyList<RegisteredPackage>> ToPackagesAsync(
        SqlConnection connection, List<PackageRow> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
            return [];

        var tags = new Dictionary<long, List<PackageTag>>();
        foreach (var chunk in rows.Select(r => r.EmbalagemId).Chunk(InClauseChunkSize))
        {
            var links = await connection.QueryAsync<PackageTagRow>(new CommandDefinition(
                "SELECT EpcId, EmbalagemId, Posicao FROM dbo.EmbalagemEtiquetas WHERE EmbalagemId IN @Ids ORDER BY VinculadoEm, EpcId",
                new { Ids = chunk }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            foreach (var link in links)
            {
                if (!tags.TryGetValue(link.EmbalagemId, out var list))
                    tags[link.EmbalagemId] = list = [];
                list.Add(new PackageTag(link.EpcId, link.Posicao));
            }
        }

        return rows.Select(r => new RegisteredPackage(
            new PackageRegistration(
                r.EmbalagemId, r.Codigo, r.Descricao, r.TipoGlt, r.ComprimentoMm, r.LarguraMm, r.AlturaMm, r.PesoKg, r.Observacao,
                DbTimestamp.AsUtc(r.CadastradoEm),
                DbTimestamp.AsUtc(r.AtualizadoEm),
                tags.TryGetValue(r.EmbalagemId, out var list) ? list : []),
            r.Status is null ? null : DbLiterals.ParseStatus(r.Status),
            r.UltimaDeteccao is { } d ? DbTimestamp.AsUtc(d) : null,
            r.UltimaEpcId,
            r.UltimaAntenaId,
            r.UltimaRssi)).ToList();
    }

    private sealed record PackageTagParam
    {
        public string EpcId { get; init; } = "";
        public long EmbalagemId { get; init; }
        public string? Posicao { get; init; }
        public SqlDateTime2 Now { get; init; } = null!;
    }

    private sealed record EpcOwnerRow
    {
        public string EpcId { get; init; } = "";
        public string Owner { get; init; } = "";
    }

    private sealed record PackageTagRow
    {
        public string EpcId { get; init; } = "";
        public long EmbalagemId { get; init; }
        public string? Posicao { get; init; }
    }

    private sealed record PackageRow
    {
        public long EmbalagemId { get; init; }
        public string Codigo { get; init; } = "";
        public string? Descricao { get; init; }
        public string? TipoGlt { get; init; }
        public int? ComprimentoMm { get; init; }
        public int? LarguraMm { get; init; }
        public int? AlturaMm { get; init; }
        public decimal? PesoKg { get; init; }
        public string? Observacao { get; init; }
        public DateTime CadastradoEm { get; init; }
        public DateTime AtualizadoEm { get; init; }
        public string? Status { get; init; }
        public DateTime? UltimaDeteccao { get; init; }
        public string? UltimaEpcId { get; init; }
        public int? UltimaAntenaId { get; init; }
        public double? UltimaRssi { get; init; }
    }
}
