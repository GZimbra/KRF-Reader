using Dapper;
using KrfReader.Application.Configuration;
using KrfReader.Domain;
using KrfReader.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrfReader.Tests.Persistence;

public sealed class SchemaMigratorTests : IDisposable
{
    private readonly string _dbPath = TestUtil.TempDbPath();

    private string ConnectionString => $"Data Source={_dbPath}";

    public void Dispose() => TestUtil.DeleteDb(_dbPath);

    private SqliteInventoryRepository NewRepo() =>
        new(new DatabaseOptions { Provider = "Sqlite", ConnectionString = ConnectionString });

    private async Task<SqliteConnection> OpenAsync()
    {
        var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private async Task<int> UserVersionAsync()
    {
        await using var conn = await OpenAsync();
        return await conn.ExecuteScalarAsync<int>("PRAGMA user_version;");
    }

    private async Task<bool> ObjectExistsAsync(string type, string name)
    {
        await using var conn = await OpenAsync();
        return await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = @type AND name = @name", new { type, name }) == 1;
    }

    [Fact]
    public void Migracoes_embutidas_sao_contiguas_a_partir_de_1()
    {
        var migrations = SchemaMigrator.Migrations;
        Assert.Equal(Enumerable.Range(1, migrations.Count), migrations.Select(m => m.Version));
        Assert.Equal("inicial", migrations[0].Name);
        Assert.True(SchemaMigrator.LatestVersion >= 2);
    }

    [Fact]
    public async Task Banco_novo_fica_na_ultima_versao_com_tabelas_views_e_indices()
    {
        await NewRepo().InitializeAsync();

        Assert.Equal(SchemaMigrator.LatestVersion, await UserVersionAsync());
        foreach (var table in new[] { "Tags", "Eventos", "TagCadastro", "Passagens", "Embalagens", "EmbalagemEtiquetas" })
            Assert.True(await ObjectExistsAsync("table", table), table);
        foreach (var view in new[] { "TagsCadastradas", "Movimentacoes", "EmbalagensCadastradas", "ItensEstoque" })
            Assert.True(await ObjectExistsAsync("view", view), view);
        foreach (var index in new[] { "IX_Eventos_EpcId_Timestamp", "IX_Eventos_Timestamp", "IX_Eventos_Modo_Timestamp", "IX_Passagens_Modo_Inicio" })
            Assert.True(await ObjectExistsAsync("index", index), index);
    }

    [Fact]
    public async Task Reexecutar_e_noop()
    {
        var repo = NewRepo();
        await repo.InitializeAsync();
        await repo.ApplyBatchAsync(OperationMode.Entrada, [new TagRead("E2801160AA01", -50, 1, DateTime.UtcNow)]);

        await repo.InitializeAsync();
        await NewRepo().InitializeAsync();

        Assert.Equal(SchemaMigrator.LatestVersion, await UserVersionAsync());
        Assert.Single(await repo.QueryTagsAsync(null, null));
    }

    [Fact]
    public async Task Banco_v04_sem_versao_migra_preservando_dados()
    {
        // Banco como a v0.4 deixava: schema 0001 aplicado, user_version = 0, com dados.
        await using (var conn = await OpenAsync())
        {
            await conn.ExecuteAsync(SchemaMigrator.Migrations[0].Sql);
            await conn.ExecuteAsync("""
                INSERT INTO Tags VALUES ('E280AA01', 'DENTRO', '2026-01-01T10:00:00.000Z', '2026-01-01T10:00:00.000Z');
                INSERT INTO Tags VALUES ('E280AA02', 'DENTRO', '2026-01-02T10:00:00.000Z', '2026-01-01T10:00:00.000Z');
                INSERT INTO Eventos (EpcId, Modo, Tipo, Timestamp, AntenaId, Rssi)
                    VALUES ('E280AA01', 'ENTRADA', 'CRIACAO', '2026-01-01T10:00:00.000Z', 2, -48.5);
                INSERT INTO Eventos (EpcId, Modo, Tipo, Timestamp, AntenaId, Rssi)
                    VALUES ('E280AA02', 'ENTRADA', 'CRIACAO', '2026-01-01T10:00:00.000Z', 1, -60);
                INSERT INTO TagCadastro VALUES ('E280AA01', 'Palete', 'SKU-1', NULL, '2026-01-01T09:00:00.000Z', '2026-01-01T09:00:00.000Z');
                """);
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>("PRAGMA user_version;"));
        }

        var repo = NewRepo();
        await repo.InitializeAsync();

        Assert.Equal(SchemaMigrator.LatestVersion, await UserVersionAsync());

        var tags = (await repo.QueryTagsAsync(null, null)).ToDictionary(t => t.Epc);
        Assert.Equal(2, tags.Count);
        // Backfill: último evento coincide com UltimaLeitura.
        Assert.Equal(2, tags["E280AA01"].UltimaAntenaId);
        Assert.Equal(-48.5, tags["E280AA01"].UltimaRssi);
        // UltimaLeitura veio de releitura sem evento: contexto desconhecido.
        Assert.Null(tags["E280AA02"].UltimaAntenaId);
        Assert.Null(tags["E280AA02"].UltimaRssi);

        var reg = Assert.Single(await repo.QueryRegisteredTagsAsync(null));
        Assert.Equal("Palete", reg.Cadastro.Descricao);
        Assert.Null(reg.Cadastro.Categoria);
        Assert.Equal(TagStatus.Dentro, reg.Status);

        Assert.Equal(2, (await repo.QueryMovementsAsync(new())).Count);
    }

    [Fact]
    public async Task Banco_v06_migra_para_embalagens_preservando_tags_eventos_e_passagens()
    {
        // Banco como a v0.5–v0.7 deixava: 0001 + 0002, user_version = 2.
        await using (var conn = await OpenAsync())
        {
            await conn.ExecuteAsync(SchemaMigrator.Migrations[0].Sql);
            await conn.ExecuteAsync(SchemaMigrator.Migrations[1].Sql);
            await conn.ExecuteAsync("""
                PRAGMA user_version = 2;
                INSERT INTO Tags VALUES ('E280AA01', 'DENTRO', '2026-01-01T10:00:00.000Z', '2026-01-01T10:00:00.000Z', 1, -50);
                INSERT INTO Tags VALUES ('E280AA02', 'FORA',   '2026-01-02T10:00:00.000Z', '2026-01-02T10:00:00.000Z', 2, -55);
                INSERT INTO Eventos (EpcId, Modo, Tipo, Timestamp, AntenaId, Rssi)
                    VALUES ('E280AA01', 'ENTRADA', 'CRIACAO', '2026-01-01T10:00:00.000Z', 1, -50);
                INSERT INTO Eventos (EpcId, Modo, Tipo, Timestamp, AntenaId, Rssi)
                    VALUES ('E280AA02', 'SAIDA', 'SAIDA_SEM_ENTRADA', '2026-01-02T10:00:00.000Z', 2, -55);
                INSERT INTO TagCadastro (EpcId, Descricao, Sku, Observacao, CadastradoEm, AtualizadoEm)
                    VALUES ('E280AA01', 'Palete', 'SKU-1', NULL, '2026-01-01T09:00:00.000Z', '2026-01-01T09:00:00.000Z');
                INSERT INTO Passagens (Modo, InicioUtc, FimUtc, Tags, Leituras, Rodadas, EstimativaPerdidas, Veredito)
                    VALUES ('ENTRADA', '2026-01-01T09:59:59.000Z', '2026-01-01T10:00:01.000Z', 1, 10, 4, 0, 'CONFIAVEL');
                """);
        }

        var repo = NewRepo();
        await repo.InitializeAsync();

        Assert.Equal(SchemaMigrator.LatestVersion, await UserVersionAsync());
        var c = await repo.GetCountersAsync();
        Assert.Equal((1, 1, 1), (c.Dentro, c.Fora, c.Anomalias));

        var movements = await repo.QueryMovementsAsync(new());
        Assert.Equal(2, movements.Count);
        Assert.All(movements, m => Assert.Null(m.EmbalagemId));
        Assert.Equal(1, movements.Single(m => m.Epc == "E280AA01").PassagemId);
        Assert.Equal("Palete", movements.Single(m => m.Epc == "E280AA01").Descricao);

        var reg = Assert.Single(await repo.QueryRegisteredTagsAsync(null));
        Assert.Equal(TagStatus.Dentro, reg.Status);
    }

    [Fact]
    public async Task Banco_mais_novo_que_o_executavel_e_recusado()
    {
        await using (var conn = await OpenAsync())
            await conn.ExecuteAsync($"PRAGMA user_version = {SchemaMigrator.LatestVersion + 1};");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => NewRepo().InitializeAsync());
        Assert.Contains("mais nova", ex.Message);
    }

    [Fact]
    public async Task Falha_no_meio_da_migracao_desfaz_tudo_e_mantem_a_versao()
    {
        Migration[] migrations =
        [
            new(1, "um", "CREATE TABLE A (x INTEGER);"),
            new(2, "dois", "CREATE TABLE B (x INTEGER); INSERT INTO A VALUES (1); SELECT * FROM TabelaQueNaoExiste;"),
        ];

        await using var conn = await OpenAsync();
        await Assert.ThrowsAsync<SqliteException>(() =>
            SchemaMigrator.MigrateAsync(conn, migrations, NullLogger.Instance, CancellationToken.None));

        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("PRAGMA user_version;"));
        Assert.False(await ObjectExistsAsync("table", "B"));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM A"));

        // Corrigida, a migração 2 roda na próxima inicialização.
        migrations[1] = new(2, "dois", "CREATE TABLE B (x INTEGER);");
        var (from, to) = await SchemaMigrator.MigrateAsync(conn, migrations, NullLogger.Instance, CancellationToken.None);
        Assert.Equal((1, 2), (from, to));
        Assert.True(await ObjectExistsAsync("table", "B"));
    }

    [Theory]
    [InlineData("KrfReader.Infrastructure.migrations.semnumero.sql")]
    [InlineData("KrfReader.Infrastructure.migrations.x1_nome.sql")]
    public void Parse_recusa_nome_invalido(string resource)
    {
        Assert.Throws<InvalidOperationException>(() => SchemaMigrator.Parse([(resource, "")]));
    }

    [Fact]
    public void Parse_recusa_buraco_na_sequencia()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SchemaMigrator.Parse(
        [
            ("KrfReader.Infrastructure.migrations.0001_a.sql", ""),
            ("KrfReader.Infrastructure.migrations.0003_c.sql", ""),
        ]));
        Assert.Contains("esperado 2", ex.Message);
    }
}
