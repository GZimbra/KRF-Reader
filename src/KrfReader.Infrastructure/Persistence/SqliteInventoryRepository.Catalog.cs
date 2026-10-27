using Dapper;
using Microsoft.Extensions.Logging;
using KrfReader.Application.Persistence;
using KrfReader.Domain;

namespace KrfReader.Infrastructure.Persistence;

/// <summary><see cref="ITagCatalogRepository"/>: tabela <c>TagCadastro</c>.</summary>
public sealed partial class SqliteInventoryRepository
{
    private const string SelectRegistrationColumns =
        "SELECT EpcId, Descricao, Sku, Observacao, CadastradoEm, AtualizadoEm FROM TagCadastro";

    public async Task<bool> AddRegistrationAsync(TagRegistrationInput input, CancellationToken cancellationToken = default)
    {
        var valid = TagRegistrationRules.Validate(input);
        var now = DbTimestamp.ToDb(DateTime.UtcNow);

        // ON CONFLICT DO NOTHING: duplicado vira "0 linhas" em vez de exceção de PK, sem corrida entre SELECT e INSERT.
        const string sql = """
            INSERT INTO TagCadastro (EpcId, Descricao, Sku, Observacao, CadastradoEm, AtualizadoEm)
            VALUES (@EpcId, @Descricao, @Sku, @Observacao, @Now, @Now)
            ON CONFLICT (EpcId) DO NOTHING
            """;

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            EpcId = valid.Epc,
            valid.Descricao,
            valid.Sku,
            valid.Observacao,
            Now = now,
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (rows == 1)
            _logger.LogInformation("Tag {Epc} cadastrada.", valid.Epc);
        return rows == 1;
    }

    public async Task<bool> UpdateRegistrationAsync(TagRegistrationInput input, CancellationToken cancellationToken = default)
    {
        var valid = TagRegistrationRules.Validate(input);
        const string sql = """
            UPDATE TagCadastro
            SET Descricao = @Descricao, Sku = @Sku, Observacao = @Observacao, AtualizadoEm = @Now
            WHERE EpcId = @EpcId
            """;

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            EpcId = valid.Epc,
            valid.Descricao,
            valid.Sku,
            valid.Observacao,
            Now = DbTimestamp.ToDb(DateTime.UtcNow),
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
            "DELETE FROM TagCadastro WHERE EpcId = @EpcId", new { EpcId = normalized },
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
            SelectRegistrationColumns + " WHERE EpcId = @EpcId", new { EpcId = normalized },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<TagRegistration>> QueryRegistrationsAsync(string? term, int limit = 500, CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
            return [];

        // LIKE do SQLite só ignora caixa em ASCII: "ção" não casa com "ÇÃO".
        var trimmed = string.IsNullOrWhiteSpace(term) ? null : term.Trim();
        var sql = SelectRegistrationColumns + """

            WHERE @Pattern IS NULL
               OR EpcId LIKE @Pattern ESCAPE '\'
               OR Descricao LIKE @Pattern ESCAPE '\'
               OR Sku LIKE @Pattern ESCAPE '\'
            ORDER BY EpcId
            LIMIT @Limit
            """;

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<RegistrationRow>(new CommandDefinition(sql, new
        {
            Pattern = trimmed is null ? null : "%" + EscapeLike(trimmed) + "%",
            Limit = limit,
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.Select(r => r.ToDomain()).ToList();
    }

    private sealed record RegistrationRow
    {
        public string EpcId { get; init; } = "";
        public string Descricao { get; init; } = "";
        public string? Sku { get; init; }
        public string? Observacao { get; init; }
        public string CadastradoEm { get; init; } = "";
        public string AtualizadoEm { get; init; } = "";

        public TagRegistration ToDomain() => new(
            EpcId, Descricao, Sku, Observacao, DbTimestamp.FromDb(CadastradoEm), DbTimestamp.FromDb(AtualizadoEm));
    }
}
