using Dapper;
using KrfReader.Application.Persistence;
using KrfReader.Domain;
using KrfReader.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace KrfReader.Tests.Persistence;

/// <summary>Comportamento só do SQL Server: migrador, precisão de data, LIKE com colchete, views para o Power BI.</summary>
public sealed class SqlServerSpecificTests : PersistenceContract<SqlServerInventoryRepository>
{
    private static readonly DateTime T0 = new(2026, 3, 10, 8, 0, 0, DateTimeKind.Utc);

    private protected override ITestDatabase<SqlServerInventoryRepository> CreateDatabase() => new SqlServerTestDatabase();

    private string ConnectionString => ((SqlServerTestDatabase)Database).ConnectionString;

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        return await connection.ExecuteScalarAsync<T>(sql) ?? throw new InvalidOperationException("null");
    }

    [SkippableFact]
    public async Task Initialize_cria_o_banco_grava_versao_e_liga_RCSI()
    {
        _ = Repo;

        Assert.Equal(SqlServerSchemaMigrator.Migrations[^1].Version, await ScalarAsync<int>("SELECT Versao FROM dbo.SchemaVersao"));
        Assert.True(await ScalarAsync<bool>("SELECT is_read_committed_snapshot_on FROM sys.databases WHERE database_id = DB_ID()"));
    }

    [SkippableFact]
    public async Task Initialize_repetido_e_em_paralelo_e_noop()
    {
        var a = await OpenAnotherAsync();
        await Task.WhenAll(a.InitializeAsync(), Repo.InitializeAsync(), Database.NewRepository().InitializeAsync());

        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SchemaVersao"));
        Assert.Equal(new InventoryCounters(0, 0, 0), await Repo.GetCountersAsync());
    }

    [SkippableFact]
    public async Task Banco_com_versao_maior_e_recusado()
    {
        _ = Repo;
        await ScalarAsync<int>("UPDATE dbo.SchemaVersao SET Versao = 999; SELECT 1");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Database.NewRepository().InitializeAsync());
        Assert.Contains("999", ex.Message);
    }

    [SkippableFact]
    public async Task Leituras_no_mesmo_intervalo_de_DATETIME_mantem_a_ordem()
    {
        // DATETIME arredonda para 0/3/7 ms: .001 e .002 virariam o mesmo valor e a antena da mais recente se perderia.
        await Repo.ApplyBatchAsync(OperationMode.Entrada, [new TagRead("E2801160AA01", -50, 1, T0.AddMilliseconds(2))]);
        await Repo.ApplyBatchAsync(OperationMode.Entrada, [new TagRead("E2801160AA01", -40, 2, T0.AddMilliseconds(1))]);

        var tag = Assert.Single(await Repo.QueryTagsAsync(null, null));
        Assert.Equal(T0.AddMilliseconds(2), tag.UltimaLeituraUtc);
        Assert.Equal(DateTimeKind.Utc, tag.UltimaLeituraUtc.Kind);
        Assert.Equal(1, tag.UltimaAntenaId);
    }

    [SkippableFact]
    public async Task Busca_trata_colchete_como_literal()
    {
        Assert.True(await Repo.AddRegistrationAsync(new TagRegistrationInput("E2801160AA01", "Caixa [A]", null, null)));
        Assert.True(await Repo.AddRegistrationAsync(new TagRegistrationInput("E2801160AA02", "Caixa A", null, null)));

        var found = await Repo.QueryRegistrationsAsync("[A]");

        Assert.Equal("E2801160AA01", Assert.Single(found).Epc);
    }

    [SkippableFact]
    public async Task Views_expoem_colunas_do_BI_com_horario_de_Brasilia()
    {
        await Repo.AddRegistrationAsync(new TagRegistrationInput("E2801160AA01", "Palete", null, null));
        await Repo.ApplyBatchAsync(OperationMode.Saida, [new TagRead("E2801160AA01", -50, 1, T0)]);

        await using var connection = new SqlConnection(ConnectionString);
        var mov = await connection.QuerySingleAsync<(DateTime DataHora, DateTime DataHoraLocal, bool Anomalia)>(
            "SELECT DataHora, DataHoraLocal, Anomalia FROM dbo.Movimentacoes");
        var cad = await connection.QuerySingleAsync<DateTime>("SELECT UltimaDeteccaoLocal FROM dbo.TagsCadastradas");

        Assert.Equal(T0, DateTime.SpecifyKind(mov.DataHora, DateTimeKind.Utc));
        Assert.Equal(T0.AddHours(-3), DateTime.SpecifyKind(mov.DataHoraLocal, DateTimeKind.Utc));
        Assert.True(mov.Anomalia);
        Assert.Equal(T0.AddHours(-3), DateTime.SpecifyKind(cad, DateTimeKind.Utc));
    }

    [SkippableFact]
    public async Task TestConnection_informa_se_o_banco_existe()
    {
        _ = Repo;
        var existing = await SqlServerInventoryRepository.TestConnectionAsync(ConnectionString);
        var missing = await SqlServerInventoryRepository.TestConnectionAsync(
            new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "KrfNaoExiste_" + Guid.NewGuid().ToString("N") }.ConnectionString);

        Assert.Contains("existe", existing);
        Assert.Contains("será criado", missing);
    }
}

public sealed class SqlServerMigrationScriptTests
{
    [Fact]
    public void Migracoes_SQL_Server_embutidas_sao_contiguas()
    {
        var migrations = SqlServerSchemaMigrator.Migrations;

        Assert.NotEmpty(migrations);
        Assert.Equal(Enumerable.Range(1, migrations.Count), migrations.Select(m => m.Version));
    }

    [Fact]
    public void Migracoes_SQLite_nao_incluem_as_do_SQL_Server()
    {
        Assert.DoesNotContain(SchemaMigrator.Migrations, m => m.Sql.Contains("NVARCHAR", StringComparison.Ordinal));
    }

    [Fact]
    public void SplitBatches_separa_em_GO_sozinho_na_linha_e_ignora_lote_vazio()
    {
        var batches = SqlServerSchemaMigrator.SplitBatches("""
            -- cabeçalho
            CREATE TABLE A (Id INT);
            go
            CREATE VIEW V AS SELECT Id AS GOAL FROM A; -- GO no meio não separa
              GO  -- comentário
            -- só comentário
            GO
            """);

        Assert.Equal(2, batches.Count);
        Assert.Contains("CREATE TABLE A", batches[0]);
        Assert.StartsWith("CREATE VIEW V", batches[1]);
    }

    [Fact]
    public void EscapeLike_escapa_colchete_alem_dos_curingas()
    {
        Assert.Equal(@"a\%b\_c\[d]\\e", SqlServerInventoryRepository.EscapeLike(@"a%b_c[d]\e"));
    }

    [Theory]
    [InlineData("Server=.;Integrated Security=True")]
    [InlineData("Server=.;Database=master;Integrated Security=True")]
    public void Connection_string_sem_banco_proprio_e_recusada(string connectionString)
    {
        Assert.Throws<ArgumentException>(() => new SqlServerInventoryRepository(
            new Application.Configuration.DatabaseOptions { Provider = "SqlServer", ConnectionString = connectionString }));
    }
}
