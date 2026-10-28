using System.Text;
using Dapper;
using KrfReader.Application.Persistence;
using KrfReader.Application.Pipeline;
using KrfReader.Domain;
using Microsoft.Extensions.Logging;

namespace KrfReader.Infrastructure.Persistence;

/// <summary>Movimentações (view <c>Movimentacoes</c>) e passagens (tabela <c>Passagens</c>).</summary>
public sealed partial class SqliteInventoryRepository
{
    public async Task<IReadOnlyList<StockMovement>> QueryMovementsAsync(MovementFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.Limit <= 0)
            return [];

        // WHERE montado só com os filtros presentes: "@P IS NULL OR col >= @P" impede o SQLite de usar o índice.
        var where = new List<string>();
        var parameters = new DynamicParameters();
        if (filter.FromUtc is { } from)
        {
            where.Add("DataHora >= @From");
            parameters.Add("From", DbTimestamp.ToDb(from));
        }
        if (filter.ToUtc is { } to)
        {
            where.Add("DataHora < @To");
            parameters.Add("To", DbTimestamp.ToDb(to));
        }
        if (filter.Tipo is { } tipo)
        {
            where.Add("Tipo = @Tipo");
            parameters.Add("Tipo", tipo.ToDb());
        }
        if (filter.OnlyAnomalies)
        {
            where.Add("Evento = @Anomalia");
            parameters.Add("Anomalia", EventType.SaidaSemEntrada.ToDb());
        }
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            where.Add(@"(EpcId LIKE @Pattern ESCAPE '\' OR Descricao LIKE @Pattern ESCAPE '\' OR Sku LIKE @Pattern ESCAPE '\')");
            parameters.Add("Pattern", "%" + EscapeLike(filter.Search.Trim()) + "%");
        }
        parameters.Add("Limit", filter.Limit);

        var sql = new StringBuilder("SELECT Id, EpcId, Descricao, Sku, Tipo, Evento, DataHora, AntenaId, Rssi, PassagemId FROM Movimentacoes");
        if (where.Count > 0)
            sql.Append(" WHERE ").AppendJoin(" AND ", where);
        sql.Append(" ORDER BY DataHora DESC, Id DESC LIMIT @Limit");

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<MovementRow>(new CommandDefinition(
            sql.ToString(), parameters, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.Select(r => new StockMovement(
            r.Id,
            r.EpcId,
            r.Descricao,
            r.Sku,
            DbLiterals.ParseMode(r.Tipo),
            DbLiterals.ParseEventType(r.Evento),
            DbTimestamp.FromDb(r.DataHora),
            checked((int)r.AntenaId),
            r.Rssi,
            r.PassagemId)).ToList();
    }

    public async Task<long?> SavePassageAsync(PassageSnapshot passage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(passage);
        if (passage.Mode is not { } mode)
            return null;

        var inicio = DbTimestamp.ToDb(passage.StartedUtc);
        var fim = DbTimestamp.ToDb(passage.EndedUtc ?? passage.StartedUtc + passage.Duration);
        if (string.CompareOrdinal(fim, inicio) < 0)
            fim = inicio;

        const string sql = """
            INSERT INTO Passagens (Modo, InicioUtc, FimUtc, Tags, Leituras, Rodadas, EstimativaPerdidas,
                                   Veredito, MotivoFechamento, Problemas)
            VALUES (@Modo, @Inicio, @Fim, @Tags, @Leituras, @Rodadas, @Estimativa, @Veredito, @Motivo, @Problemas);
            SELECT last_insert_rowid();
            """;

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(sql, new
        {
            Modo = mode.ToDb(),
            Inicio = inicio,
            Fim = fim,
            Tags = passage.DistinctTags,
            Leituras = passage.TotalReads,
            Rodadas = passage.Rounds,
            Estimativa = passage.EstimatedMissed,
            Veredito = VerdictToDb(passage.Verdict),
            Motivo = passage.CloseReason,
            Problemas = passage.Issues.Count == 0 ? null : string.Join(" | ", passage.Issues),
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        _logger.LogInformation("Passagem gravada: #{Id} {Mode} {Tags} tags, {Verdict}.", id, mode.ToDb(), passage.DistinctTags, passage.Verdict);
        return id;
    }

    internal static string VerdictToDb(PassageVerdict verdict) => verdict switch
    {
        PassageVerdict.Confiavel => "CONFIAVEL",
        PassageVerdict.Atencao => "ATENCAO",
        PassageVerdict.NaoConvergiu => "NAO_CONVERGIU",
        PassageVerdict.DadosIncompletos => "DADOS_INCOMPLETOS",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict)),
    };

    private sealed record MovementRow
    {
        public long Id { get; init; }
        public string EpcId { get; init; } = "";
        public string? Descricao { get; init; }
        public string? Sku { get; init; }
        public string Tipo { get; init; } = "";
        public string Evento { get; init; } = "";
        public string DataHora { get; init; } = "";
        public long AntenaId { get; init; }
        public double Rssi { get; init; }
        public long? PassagemId { get; init; }
    }
}
