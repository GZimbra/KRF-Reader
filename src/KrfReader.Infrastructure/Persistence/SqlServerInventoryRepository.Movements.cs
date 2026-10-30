using System.Text;
using Dapper;
using KrfReader.Application.Persistence;
using KrfReader.Application.Pipeline;
using KrfReader.Domain;
using Microsoft.Extensions.Logging;

namespace KrfReader.Infrastructure.Persistence;

/// <summary>Movimentações (view <c>dbo.Movimentacoes</c>) e passagens (tabela <c>dbo.Passagens</c>).</summary>
public sealed partial class SqlServerInventoryRepository
{
    public async Task<IReadOnlyList<StockMovement>> QueryMovementsAsync(MovementFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.Limit <= 0)
            return [];

        // WHERE montado só com os filtros presentes, para o otimizador usar os índices de Eventos.
        var where = new List<string>();
        var parameters = new DynamicParameters();
        if (filter.FromUtc is { } from)
        {
            where.Add("DataHora >= @From");
            parameters.AddDynamicParams(new { From = new SqlDateTime2(from) });
        }
        if (filter.ToUtc is { } to)
        {
            where.Add("DataHora < @To");
            parameters.AddDynamicParams(new { To = new SqlDateTime2(to) });
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
            where.Add(@"(EpcId LIKE @Pattern ESCAPE '\' OR Descricao LIKE @Pattern ESCAPE '\' OR Sku LIKE @Pattern ESCAPE '\' OR EmbalagemCodigo LIKE @Pattern ESCAPE '\')");
            parameters.Add("Pattern", "%" + EscapeLike(filter.Search.Trim()) + "%");
        }
        parameters.Add("Limit", filter.Limit);

        var sql = new StringBuilder("SELECT TOP (@Limit) Id, EpcId, Descricao, Sku, Tipo, Evento, DataHora, AntenaId, Rssi, PassagemId, EmbalagemId, EmbalagemCodigo, TipoGlt FROM dbo.Movimentacoes");
        if (where.Count > 0)
            sql.Append(" WHERE ").AppendJoin(" AND ", where);
        sql.Append(" ORDER BY DataHora DESC, Id DESC");

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
            DbTimestamp.AsUtc(r.DataHora),
            r.AntenaId,
            r.Rssi,
            r.PassagemId,
            r.EmbalagemId,
            r.EmbalagemCodigo,
            r.TipoGlt)).ToList();
    }

    public async Task<long?> SavePassageAsync(PassageSnapshot passage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(passage);
        if (passage.Mode is not { } mode)
            return null;

        var inicio = DbTimestamp.TruncateToMillisUtc(passage.StartedUtc);
        var fim = DbTimestamp.TruncateToMillisUtc(passage.EndedUtc ?? passage.StartedUtc + passage.Duration);
        if (fim < inicio)
            fim = inicio;

        const string sql = """
            INSERT INTO dbo.Passagens (Modo, InicioUtc, FimUtc, Tags, Leituras, Rodadas, EstimativaPerdidas,
                                       Veredito, MotivoFechamento, Problemas, Itens, Embalagens)
            OUTPUT INSERTED.Id
            VALUES (@Modo, @Inicio, @Fim, @Tags, @Leituras, @Rodadas, @Estimativa, @Veredito, @Motivo, @Problemas, @Itens, @Embalagens);
            """;

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(sql, new
        {
            Modo = mode.ToDb(),
            Inicio = new SqlDateTime2(inicio),
            Fim = new SqlDateTime2(fim),
            Tags = passage.DistinctTags,
            Leituras = passage.TotalReads,
            Rodadas = passage.Rounds,
            Estimativa = passage.EstimatedMissed,
            Veredito = SqliteInventoryRepository.VerdictToDb(passage.Verdict),
            Motivo = passage.CloseReason,
            Problemas = passage.Issues.Count == 0 ? null : string.Join(" | ", passage.Issues),
            Itens = passage.DistinctItems,
            Embalagens = passage.Packages,
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        _logger.LogInformation("Passagem gravada: #{Id} {Mode} {Tags} tags, {Verdict}.", id, mode.ToDb(), passage.DistinctTags, passage.Verdict);
        return id;
    }

    private sealed record MovementRow
    {
        public long Id { get; init; }
        public string EpcId { get; init; } = "";
        public string? Descricao { get; init; }
        public string? Sku { get; init; }
        public string Tipo { get; init; } = "";
        public string Evento { get; init; } = "";
        public DateTime DataHora { get; init; }
        public int AntenaId { get; init; }
        public double Rssi { get; init; }
        public long? PassagemId { get; init; }
        public long? EmbalagemId { get; init; }
        public string? EmbalagemCodigo { get; init; }
        public string? TipoGlt { get; init; }
    }
}
