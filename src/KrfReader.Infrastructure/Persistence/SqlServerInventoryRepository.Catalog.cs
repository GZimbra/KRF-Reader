using Dapper;
using KrfReader.Application.Persistence;
using KrfReader.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace KrfReader.Infrastructure.Persistence;

/// <summary><see cref="ITagCatalogRepository"/>: tabela <c>dbo.TagCadastro</c> e view <c>dbo.TagsCadastradas</c>.</summary>
public sealed partial class SqlServerInventoryRepository
{
    private const string SelectRegistrationColumns =
        "SELECT TOP (@Limit) EpcId, Descricao, Sku, Observacao, Categoria, Unidade, Localizacao, CadastradoEm, AtualizadoEm FROM dbo.TagCadastro";

    // Violação de PK / índice único.
    private const int DuplicateKeyError = 2627;
    private const int DuplicateIndexError = 2601;

    public async Task<bool> AddRegistrationAsync(TagRegistrationInput input, CancellationToken cancellationToken = default)
    {
        var valid = TagRegistrationRules.Validate(input);

        // UPDLOCK+HOLDLOCK fecha a corrida entre o NOT EXISTS e o INSERT; a PK cobre o resto (2627 = false).
        const string sql = """
            IF NOT EXISTS (SELECT 1 FROM dbo.TagCadastro WITH (UPDLOCK, HOLDLOCK) WHERE EpcId = @EpcId)
                INSERT INTO dbo.TagCadastro (EpcId, Descricao, Sku, Observacao, Categoria, Unidade, Localizacao, CadastradoEm, AtualizadoEm)
                VALUES (@EpcId, @Descricao, @Sku, @Observacao, @Categoria, @Unidade, @Localizacao, @Now, @Now);
            """;

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        int rows;
        try
        {
            rows = await connection.ExecuteAsync(new CommandDefinition(sql, new
            {
                EpcId = valid.Epc,
                valid.Descricao,
                valid.Sku,
                valid.Observacao,
                valid.Categoria,
                valid.Unidade,
                valid.Localizacao,
                Now = new SqlDateTime2(DateTime.UtcNow),
            }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException ex) when (ex.Number is DuplicateKeyError or DuplicateIndexError)
        {
            return false;
        }

        if (rows == 1)
            _logger.LogInformation("Tag {Epc} cadastrada.", valid.Epc);
        return rows == 1;
    }

    public async Task<bool> UpdateRegistrationAsync(TagRegistrationInput input, CancellationToken cancellationToken = default)
    {
        var valid = TagRegistrationRules.Validate(input);
        const string sql = """
            UPDATE dbo.TagCadastro
            SET Descricao = @Descricao, Sku = @Sku, Observacao = @Observacao,
                Categoria = @Categoria, Unidade = @Unidade, Localizacao = @Localizacao, AtualizadoEm = @Now
            WHERE EpcId = @EpcId
            """;

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            EpcId = valid.Epc,
            valid.Descricao,
            valid.Sku,
            valid.Observacao,
            valid.Categoria,
            valid.Unidade,
            valid.Localizacao,
            Now = new SqlDateTime2(DateTime.UtcNow),
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows == 1;
    }

    public async Task<bool> DeleteRegistrationAsync(string epc, CancellationToken cancellationToken = default)
    {
        var normalized = Epc.Normalize(epc);
        if (normalized is null)
            return false;

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM dbo.TagCadastro WHERE EpcId = @EpcId", new { EpcId = normalized },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (rows == 1)
            _logger.LogInformation("Cadastro da tag {Epc} removido.", normalized);
        return rows == 1;
    }

    public async Task<TagRegistration?> GetRegistrationAsync(string epc, CancellationToken cancellationToken = default)
    {
        var normalized = Epc.Normalize(epc);
        if (normalized is null)
            return null;

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<RegistrationRow>(new CommandDefinition(
            SelectRegistrationColumns + " WHERE EpcId = @EpcId", new { EpcId = normalized, Limit = 1 },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<TagRegistration>> QueryRegistrationsAsync(string? term, int limit = 500, CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
            return [];

        var trimmed = string.IsNullOrWhiteSpace(term) ? null : term.Trim();
        var sql = SelectRegistrationColumns + """

            WHERE @Pattern IS NULL
               OR EpcId LIKE @Pattern ESCAPE '\'
               OR Descricao LIKE @Pattern ESCAPE '\'
               OR Sku LIKE @Pattern ESCAPE '\'
            ORDER BY EpcId
            """;

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<RegistrationRow>(new CommandDefinition(sql, new
        {
            Pattern = trimmed is null ? null : "%" + EscapeLike(trimmed) + "%",
            Limit = limit,
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<IReadOnlyList<RegisteredTag>> QueryRegisteredTagsAsync(
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
        // SQL Server já ordena NULL primeiro em ASC/último em DESC; o CASE deixa explícito: nunca lidas no fim.
        var sql = $$"""
            SELECT TOP (@Limit) EpcId, Descricao, Sku, Observacao, Categoria, Unidade, Localizacao, CadastradoEm, AtualizadoEm,
                   Status, UltimaDeteccao, UltimaAntenaId, UltimaRssi
            FROM dbo.TagsCadastradas
            WHERE {{statusCondition}}
              AND (@Pattern IS NULL
                   OR EpcId LIKE @Pattern ESCAPE '\'
                   OR Descricao LIKE @Pattern ESCAPE '\'
                   OR Sku LIKE @Pattern ESCAPE '\'
                   OR Categoria LIKE @Pattern ESCAPE '\'
                   OR Localizacao LIKE @Pattern ESCAPE '\')
            ORDER BY CASE WHEN UltimaDeteccao IS NULL THEN 1 ELSE 0 END, UltimaDeteccao DESC, EpcId
            """;

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<RegisteredTagRow>(new CommandDefinition(sql, new
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

        return rows.Select(r => new RegisteredTag(
            r.ToDomain(),
            r.Status is null ? null : DbLiterals.ParseStatus(r.Status),
            r.UltimaDeteccao is { } detected ? DbTimestamp.AsUtc(detected) : null,
            r.UltimaAntenaId,
            r.UltimaRssi)).ToList();
    }

    private record RegistrationRow
    {
        public string EpcId { get; init; } = "";
        public string Descricao { get; init; } = "";
        public string? Sku { get; init; }
        public string? Observacao { get; init; }
        public string? Categoria { get; init; }
        public string? Unidade { get; init; }
        public string? Localizacao { get; init; }
        public DateTime CadastradoEm { get; init; }
        public DateTime AtualizadoEm { get; init; }

        public TagRegistration ToDomain() => new(
            EpcId, Descricao, Sku, Observacao, DbTimestamp.AsUtc(CadastradoEm), DbTimestamp.AsUtc(AtualizadoEm),
            Categoria, Unidade, Localizacao);
    }

    private sealed record RegisteredTagRow : RegistrationRow
    {
        public string? Status { get; init; }
        public DateTime? UltimaDeteccao { get; init; }
        public int? UltimaAntenaId { get; init; }
        public double? UltimaRssi { get; init; }
    }
}
