using System.Data;
using Dapper;
using KrfReader.Application.Persistence;
using KrfReader.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace KrfReader.Infrastructure.Persistence;

/// <summary><see cref="IPackageCatalogRepository"/>: tabelas <c>Embalagens</c>/<c>EmbalagemEtiquetas</c> e view <c>EmbalagensCadastradas</c>.</summary>
public sealed partial class SqliteInventoryRepository
{
    private const string SelectPackageColumns = """
        SELECT EmbalagemId, Codigo, Descricao, TipoGlt, ComprimentoMm, LarguraMm, AlturaMm, PesoKg, Observacao,
               CadastradoEm, AtualizadoEm, Status, UltimaDeteccao, UltimaEpcId, UltimaAntenaId, UltimaRssi
        FROM EmbalagensCadastradas
        """;

    // Embalagem ainda sem estado herda o da etiqueta que mudou de status por último (caixa lida antes como tag avulsa);
    // depois, as linhas de Tags das etiquetas passam a acompanhar o status da embalagem.
    private const string SeedAndSyncPackageSql = """
        UPDATE Embalagens SET
            Status = (SELECT t.Status FROM Tags t JOIN EmbalagemEtiquetas ee ON ee.EpcId = t.EpcId
                      WHERE ee.EmbalagemId = @Id ORDER BY t.UltimaAtualizacaoStatus DESC LIMIT 1),
            UltimaAtualizacaoStatus = (SELECT MAX(t.UltimaAtualizacaoStatus) FROM Tags t JOIN EmbalagemEtiquetas ee ON ee.EpcId = t.EpcId
                      WHERE ee.EmbalagemId = @Id),
            UltimaDeteccao = (SELECT MAX(t.UltimaLeitura) FROM Tags t JOIN EmbalagemEtiquetas ee ON ee.EpcId = t.EpcId
                      WHERE ee.EmbalagemId = @Id),
            UltimaEpcId = (SELECT t.EpcId FROM Tags t JOIN EmbalagemEtiquetas ee ON ee.EpcId = t.EpcId
                      WHERE ee.EmbalagemId = @Id ORDER BY t.UltimaLeitura DESC LIMIT 1),
            UltimaAntenaId = (SELECT t.UltimaAntenaId FROM Tags t JOIN EmbalagemEtiquetas ee ON ee.EpcId = t.EpcId
                      WHERE ee.EmbalagemId = @Id ORDER BY t.UltimaLeitura DESC LIMIT 1),
            UltimaRssi = (SELECT t.UltimaRssi FROM Tags t JOIN EmbalagemEtiquetas ee ON ee.EpcId = t.EpcId
                      WHERE ee.EmbalagemId = @Id ORDER BY t.UltimaLeitura DESC LIMIT 1)
        WHERE Id = @Id AND Status IS NULL
          AND EXISTS (SELECT 1 FROM Tags t JOIN EmbalagemEtiquetas ee ON ee.EpcId = t.EpcId WHERE ee.EmbalagemId = @Id);
        UPDATE Tags SET
            Status = (SELECT Status FROM Embalagens WHERE Id = @Id),
            UltimaAtualizacaoStatus = (SELECT UltimaAtualizacaoStatus FROM Embalagens WHERE Id = @Id)
        WHERE EpcId IN (SELECT EpcId FROM EmbalagemEtiquetas WHERE EmbalagemId = @Id)
          AND Status <> (SELECT Status FROM Embalagens WHERE Id = @Id);
        """;

    public async Task<PackageSaveOutcome> AddPackageAsync(PackageInput input, CancellationToken cancellationToken = default)
    {
        var valid = PackageRules.Validate(input);
        var now = DbTimestamp.ToDb(DateTime.UtcNow);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        // IMMEDIATE: conferência de conflito e gravação sob o mesmo lock de escrita.
        await using var transaction = connection.BeginTransaction(deferred: false);
        var conflict = await FindPackageConflictAsync(connection, transaction, null, valid, cancellationToken).ConfigureAwait(false);
        if (conflict is not null)
            return conflict;

        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO Embalagens (Codigo, Descricao, TipoGlt, ComprimentoMm, LarguraMm, AlturaMm, PesoKg, Observacao,
                                    CadastradoEm, AtualizadoEm)
            VALUES (@Codigo, @Descricao, @TipoGlt, @ComprimentoMm, @LarguraMm, @AlturaMm, @PesoKg, @Observacao, @Now, @Now);
            SELECT last_insert_rowid();
            """, PackageParameters(valid, now), transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        await UpsertPackageTagsAsync(connection, transaction, id, valid.Etiquetas, now, cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(SeedAndSyncPackageSql, new { Id = id }, transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Embalagem {Codigo} cadastrada com {Count} etiqueta(s).", valid.Codigo, valid.Etiquetas.Count);
        return new PackageSaveOutcome(PackageSaveStatus.Ok, id, $"Embalagem {valid.Codigo} cadastrada com {valid.Etiquetas.Count} etiqueta(s).");
    }

    public async Task<PackageSaveOutcome> UpdatePackageAsync(long id, PackageInput input, CancellationToken cancellationToken = default)
    {
        var valid = PackageRules.Validate(input);
        var now = DbTimestamp.ToDb(DateTime.UtcNow);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var exists = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT Id FROM Embalagens WHERE Id = @Id", new { Id = id }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (exists is null)
            return new PackageSaveOutcome(PackageSaveStatus.NaoEncontrada, null, "Embalagem não está mais cadastrada (removida por outra estação?).");

        var conflict = await FindPackageConflictAsync(connection, transaction, id, valid, cancellationToken).ConfigureAwait(false);
        if (conflict is not null)
            return conflict;

        var parameters = PackageParameters(valid, now);
        parameters.Add("Id", id);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE Embalagens
            SET Codigo = @Codigo, Descricao = @Descricao, TipoGlt = @TipoGlt, ComprimentoMm = @ComprimentoMm,
                LarguraMm = @LarguraMm, AlturaMm = @AlturaMm, PesoKg = @PesoKg, Observacao = @Observacao, AtualizadoEm = @Now
            WHERE Id = @Id
            """, parameters, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM EmbalagemEtiquetas WHERE EmbalagemId = @Id AND EpcId NOT IN @Epcs",
            new { Id = id, Epcs = valid.Etiquetas.Select(e => e.Epc).ToArray() }, transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        await UpsertPackageTagsAsync(connection, transaction, id, valid.Etiquetas, now, cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(SeedAndSyncPackageSql, new { Id = id }, transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Embalagem {Codigo} atualizada ({Count} etiqueta(s)).", valid.Codigo, valid.Etiquetas.Count);
        return new PackageSaveOutcome(PackageSaveStatus.Ok, id, $"Embalagem {valid.Codigo} atualizada ({valid.Etiquetas.Count} etiqueta(s)).");
    }

    public async Task<bool> DeletePackageAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        // Vínculos saem por ON DELETE CASCADE (foreign_keys=ON na connection string).
        var rows = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM Embalagens WHERE Id = @Id", new { Id = id }, cancellationToken: cancellationToken)).ConfigureAwait(false);
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
            SelectPackageColumns + " WHERE EmbalagemId = (SELECT EmbalagemId FROM EmbalagemEtiquetas WHERE EpcId = @EpcId)",
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
        var sql = SelectPackageColumns + $$"""

            WHERE {{statusCondition}}
              AND (@Pattern IS NULL
                   OR Codigo LIKE @Pattern ESCAPE '\'
                   OR Descricao LIKE @Pattern ESCAPE '\'
                   OR TipoGlt LIKE @Pattern ESCAPE '\'
                   OR EXISTS (SELECT 1 FROM EmbalagemEtiquetas ee
                              WHERE ee.EmbalagemId = EmbalagensCadastradas.EmbalagemId AND ee.EpcId LIKE @Pattern ESCAPE '\'))
            ORDER BY UltimaDeteccao IS NULL, UltimaDeteccao DESC, Codigo
            LIMIT @Limit
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
                   (SELECT COUNT(*) FROM EmbalagemEtiquetas x WHERE x.EmbalagemId = em.Id) AS Etiquetas
            FROM EmbalagemEtiquetas ee
            JOIN Embalagens em ON em.Id = ee.EmbalagemId
            """;

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<PackageLinkRow>(new CommandDefinition(sql, cancellationToken: cancellationToken)).ConfigureAwait(false);
        var refs = new Dictionary<long, PackageRef>();
        var map = new Dictionary<string, PackageRef>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!refs.TryGetValue(row.Id, out var reference))
                refs[row.Id] = reference = new PackageRef(row.Id, row.Codigo, checked((int)row.Etiquetas));
            map[row.EpcId] = reference;
        }
        return map;
    }

    /// <summary>Lança se o EPC for etiqueta de embalagem (cadastro de tag avulsa não pode reutilizá-lo).</summary>
    private static async Task EnsureNotPackageTagAsync(
        SqliteConnection connection, IDbTransaction? transaction, string epc, CancellationToken cancellationToken)
    {
        var codigo = await connection.ExecuteScalarAsync<string?>(new CommandDefinition("""
            SELECT em.Codigo FROM EmbalagemEtiquetas ee JOIN Embalagens em ON em.Id = ee.EmbalagemId WHERE ee.EpcId = @EpcId
            """, new { EpcId = epc }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (codigo is not null)
            throw new InvalidOperationException($"EPC {epc} é etiqueta da embalagem {codigo}. Edite pela aba Embalagens.");
    }

    private static async Task<PackageSaveOutcome?> FindPackageConflictAsync(
        SqliteConnection connection, IDbTransaction transaction, long? id, ValidPackage valid, CancellationToken cancellationToken)
    {
        var sameCode = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT Id FROM Embalagens WHERE Codigo = @Codigo AND (@Id IS NULL OR Id <> @Id)",
            new { valid.Codigo, Id = id }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (sameCode is not null)
            return new PackageSaveOutcome(PackageSaveStatus.CodigoDuplicado, null,
                $"Código {valid.Codigo} já pertence a outra embalagem. O código identifica a embalagem física; o modelo vai em Tipo.");

        var epcs = valid.Etiquetas.Select(e => e.Epc).ToArray();
        var other = await connection.QueryFirstOrDefaultAsync<EpcOwnerRow>(new CommandDefinition("""
            SELECT ee.EpcId, em.Codigo AS Owner FROM EmbalagemEtiquetas ee JOIN Embalagens em ON em.Id = ee.EmbalagemId
            WHERE ee.EpcId IN @Epcs AND (@Id IS NULL OR ee.EmbalagemId <> @Id)
            ORDER BY ee.EpcId LIMIT 1
            """, new { Epcs = epcs, Id = id }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (other is { } o)
            return new PackageSaveOutcome(PackageSaveStatus.EpcEmOutraEmbalagem, null,
                $"EPC {o.EpcId} já é etiqueta da embalagem {o.Owner}. Remova-o de lá antes.");

        var loose = await connection.QueryFirstOrDefaultAsync<EpcOwnerRow>(new CommandDefinition(
            "SELECT EpcId, Descricao AS Owner FROM TagCadastro WHERE EpcId IN @Epcs ORDER BY EpcId LIMIT 1",
            new { Epcs = epcs }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (loose is { } l)
            return new PackageSaveOutcome(PackageSaveStatus.EpcCadastradoComoTag, null,
                $"EPC {l.EpcId} está cadastrado como tag avulsa (\"{l.Owner}\"). Remova o cadastro na aba Cadastro antes de vinculá-lo à embalagem; estado e histórico da tag são mantidos.");

        return null;
    }

    private static async Task UpsertPackageTagsAsync(
        SqliteConnection connection, IDbTransaction transaction, long id, IReadOnlyList<PackageTag> tags, string now,
        CancellationToken cancellationToken)
    {
        // Conflito de PK só ocorre com a própria embalagem (FindPackageConflictAsync): atualiza a posição e mantém VinculadoEm.
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO EmbalagemEtiquetas (EpcId, EmbalagemId, Posicao, VinculadoEm)
            VALUES (@EpcId, @EmbalagemId, @Posicao, @Now)
            ON CONFLICT (EpcId) DO UPDATE SET Posicao = excluded.Posicao
            """, tags.Select(t => new { EpcId = t.Epc, EmbalagemId = id, t.Posicao, Now = now }).ToList(), transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static DynamicParameters PackageParameters(ValidPackage valid, string now)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Codigo", valid.Codigo);
        parameters.Add("Descricao", valid.Descricao);
        parameters.Add("TipoGlt", valid.TipoGlt);
        parameters.Add("ComprimentoMm", valid.ComprimentoMm);
        parameters.Add("LarguraMm", valid.LarguraMm);
        parameters.Add("AlturaMm", valid.AlturaMm);
        // REAL no SQLite: decimal vira double (3 casas cabem sem perda visível).
        parameters.Add("PesoKg", valid.PesoKg is { } kg ? (double)kg : null);
        parameters.Add("Observacao", valid.Observacao);
        parameters.Add("Now", now);
        return parameters;
    }

    private static async Task<IReadOnlyList<RegisteredPackage>> ToPackagesAsync(
        SqliteConnection connection, List<PackageRow> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
            return [];

        var tags = new Dictionary<long, List<PackageTag>>();
        foreach (var chunk in rows.Select(r => r.EmbalagemId).Chunk(InClauseChunkSize))
        {
            var links = await connection.QueryAsync<PackageTagRow>(new CommandDefinition(
                "SELECT EpcId, EmbalagemId, Posicao FROM EmbalagemEtiquetas WHERE EmbalagemId IN @Ids ORDER BY VinculadoEm, EpcId",
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
                r.EmbalagemId, r.Codigo, r.Descricao, r.TipoGlt,
                r.ComprimentoMm is { } c ? checked((int)c) : null,
                r.LarguraMm is { } w ? checked((int)w) : null,
                r.AlturaMm is { } h ? checked((int)h) : null,
                r.PesoKg is { } kg ? Math.Round((decimal)kg, 3) : null,
                r.Observacao,
                DbTimestamp.FromDb(r.CadastradoEm),
                DbTimestamp.FromDb(r.AtualizadoEm),
                tags.TryGetValue(r.EmbalagemId, out var list) ? list : []),
            r.Status is null ? null : DbLiterals.ParseStatus(r.Status),
            r.UltimaDeteccao is null ? null : DbTimestamp.FromDb(r.UltimaDeteccao),
            r.UltimaEpcId,
            r.UltimaAntenaId is { } a ? checked((int)a) : null,
            r.UltimaRssi)).ToList();
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
        public long? ComprimentoMm { get; init; }
        public long? LarguraMm { get; init; }
        public long? AlturaMm { get; init; }
        public double? PesoKg { get; init; }
        public string? Observacao { get; init; }
        public string CadastradoEm { get; init; } = "";
        public string AtualizadoEm { get; init; } = "";
        public string? Status { get; init; }
        public string? UltimaDeteccao { get; init; }
        public string? UltimaEpcId { get; init; }
        public long? UltimaAntenaId { get; init; }
        public double? UltimaRssi { get; init; }
    }
}
