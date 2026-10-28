using KrfReader.Application.Configuration;
using KrfReader.Application.Persistence;
using KrfReader.Domain;
using KrfReader.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace KrfReader.Tests.Persistence;

public sealed class SqliteTagCatalogTests : TagCatalogContract<SqliteInventoryRepository>
{
    private protected override ITestDatabase<SqliteInventoryRepository> CreateDatabase() => new SqliteTestDatabase();

    [Fact]
    public async Task InitializeAsync_cria_TagCadastro_em_banco_da_versao_anterior()
    {
        var path = TestUtil.TempDbPath();
        try
        {
            // Banco v1: só Tags/Eventos, com dados.
            await using (var conn = new SqliteConnection($"Data Source={path}"))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE Tags (EpcId TEXT NOT NULL PRIMARY KEY, Status TEXT NOT NULL CHECK (Status IN ('DENTRO','FORA')),
                        UltimaLeitura TEXT NOT NULL, UltimaAtualizacaoStatus TEXT NOT NULL);
                    INSERT INTO Tags VALUES ('E280', 'DENTRO', '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z');
                    """;
                await cmd.ExecuteNonQueryAsync();
            }

            var repo = new SqliteInventoryRepository(new DatabaseOptions { Provider = "Sqlite", ConnectionString = $"Data Source={path}" });
            await repo.InitializeAsync();

            Assert.True(await repo.AddRegistrationAsync(new TagRegistrationInput("E280", "Migrada", null, null)));
            var tag = Assert.Single(await repo.QueryTagsAsync(null, null));
            Assert.Equal("Migrada", tag.Descricao);
        }
        finally
        {
            TestUtil.DeleteDb(path);
        }
    }
}

public sealed class SqlServerTagCatalogTests : TagCatalogContract<SqlServerInventoryRepository>
{
    private protected override ITestDatabase<SqlServerInventoryRepository> CreateDatabase() => new SqlServerTestDatabase();
}

/// <summary>Contrato de <see cref="ITagCatalogRepository"/>: roda igual em SQLite e SQL Server.</summary>
public abstract class TagCatalogContract<TRepo> : PersistenceContract<TRepo>
    where TRepo : class, IInventoryRepository, ITagCatalogRepository
{
    private const string Epc1 = "E28011606000020812345678";
    private const string Epc2 = "E28011606000020812345679";
    private static readonly DateTime T0 = new(2026, 3, 10, 8, 0, 0, DateTimeKind.Utc);

    private static TagRegistrationInput Input(string epc, string descricao = "Palete A", string? sku = null, string? obs = null) =>
        new(epc, descricao, sku, obs);

    private static TagRead Read(string epc, DateTime at) => new(epc, -50, 1, at);

    [SkippableFact]
    public async Task Add_grava_normalizado_e_Get_aceita_EPC_com_separadores()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);

        Assert.True(await Catalog.AddRegistrationAsync(Input("e280 1160 6000 0208 1234 5678", " Palete A ", " SKU-9 ", "obs")));

        var reg = await Catalog.GetRegistrationAsync("E2-80-11-60-60-00-02-08-12-34-56-78");
        Assert.NotNull(reg);
        Assert.Equal(Epc1, reg.Epc);
        Assert.Equal("Palete A", reg.Descricao);
        Assert.Equal("SKU-9", reg.Sku);
        Assert.Equal("obs", reg.Observacao);
        Assert.Equal(DateTimeKind.Utc, reg.CadastradoEmUtc.Kind);
        Assert.InRange(reg.CadastradoEmUtc, before, DateTime.UtcNow.AddSeconds(1));
        Assert.Equal(reg.CadastradoEmUtc, reg.AtualizadoEmUtc);
    }

    [SkippableFact]
    public async Task Add_duplicado_retorna_false_e_nao_sobrescreve()
    {
        Assert.True(await Catalog.AddRegistrationAsync(Input(Epc1, "Original")));

        Assert.False(await Catalog.AddRegistrationAsync(Input(Epc1.ToLowerInvariant(), "Outra")));

        Assert.Equal("Original", (await Catalog.GetRegistrationAsync(Epc1))!.Descricao);
    }

    [SkippableFact]
    public async Task Add_invalido_lanca_e_nao_grava()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Catalog.AddRegistrationAsync(Input("E2801", "x")));
        await Assert.ThrowsAsync<ArgumentException>(() => Catalog.AddRegistrationAsync(Input(Epc1, "  ")));

        Assert.Empty(await Catalog.QueryRegistrationsAsync(null));
    }

    [SkippableFact]
    public async Task Update_altera_campos_e_AtualizadoEm_mas_nao_CadastradoEm()
    {
        await Catalog.AddRegistrationAsync(Input(Epc1, "Antes", "S1", "o1"));
        var original = (await Catalog.GetRegistrationAsync(Epc1))!;
        await Task.Delay(20);

        Assert.True(await Catalog.UpdateRegistrationAsync(Input(Epc1, "Depois", null, null)));

        var reg = (await Catalog.GetRegistrationAsync(Epc1))!;
        Assert.Equal("Depois", reg.Descricao);
        Assert.Null(reg.Sku);
        Assert.Null(reg.Observacao);
        Assert.Equal(original.CadastradoEmUtc, reg.CadastradoEmUtc);
        Assert.True(reg.AtualizadoEmUtc > original.AtualizadoEmUtc);
    }

    [SkippableFact]
    public async Task Update_de_EPC_nao_cadastrado_retorna_false_e_nao_cria()
    {
        Assert.False(await Catalog.UpdateRegistrationAsync(Input(Epc1)));
        Assert.Null(await Catalog.GetRegistrationAsync(Epc1));
    }

    [SkippableFact]
    public async Task Delete_remove_so_o_cadastro_e_mantem_estado_e_historico()
    {
        await Catalog.AddRegistrationAsync(Input(Epc1));
        await Repo.ApplyBatchAsync(OperationMode.Entrada, [Read(Epc1, T0)]);

        Assert.True(await Catalog.DeleteRegistrationAsync(Epc1.ToLowerInvariant()));
        Assert.False(await Catalog.DeleteRegistrationAsync(Epc1));
        Assert.False(await Catalog.DeleteRegistrationAsync("não é hex"));

        Assert.Null(await Catalog.GetRegistrationAsync(Epc1));
        var tag = Assert.Single(await Repo.QueryTagsAsync(null, null));
        Assert.Equal(TagStatus.Dentro, tag.Status);
        Assert.False(tag.IsRegistered);
        Assert.Single(await Repo.GetEventsAsync(Epc1));
    }

    [SkippableFact]
    public async Task Query_busca_em_EPC_descricao_e_SKU_e_ordena_por_EPC()
    {
        await Catalog.AddRegistrationAsync(Input(Epc2, "Caixa de parafusos", "PAR-100"));
        await Catalog.AddRegistrationAsync(Input(Epc1, "Palete 100% madeira", "PAL_1"));
        await Catalog.AddRegistrationAsync(Input("AAAA0000", "Outro item", "X"));

        Assert.Equal(["AAAA0000", Epc1, Epc2], (await Catalog.QueryRegistrationsAsync(null)).Select(r => r.Epc));
        Assert.Equal([Epc2], (await Catalog.QueryRegistrationsAsync("parafuso")).Select(r => r.Epc));
        Assert.Equal([Epc2], (await Catalog.QueryRegistrationsAsync("par-1")).Select(r => r.Epc));
        Assert.Equal([Epc1, Epc2], (await Catalog.QueryRegistrationsAsync("123456")).Select(r => r.Epc));
        // Curingas do LIKE são literais.
        Assert.Equal([Epc1], (await Catalog.QueryRegistrationsAsync("100%")).Select(r => r.Epc));
        Assert.Equal([Epc1], (await Catalog.QueryRegistrationsAsync("_1")).Select(r => r.Epc));
        Assert.Single(await Catalog.QueryRegistrationsAsync(null, limit: 1));
        Assert.Empty(await Catalog.QueryRegistrationsAsync(null, limit: 0));
    }

    [SkippableFact]
    public async Task Cadastro_de_tag_nunca_lida_nao_aparece_em_Tags_nem_nos_contadores()
    {
        await Catalog.AddRegistrationAsync(Input(Epc1));

        Assert.Empty(await Repo.QueryTagsAsync(null, null));
        Assert.Equal(new InventoryCounters(0, 0, 0), await Repo.GetCountersAsync());
    }

    [SkippableFact]
    public async Task QueryTags_traz_descricao_do_cadastro_e_busca_por_ela()
    {
        await Catalog.AddRegistrationAsync(Input(Epc1, "Palete azul", "SKU-AZ"));
        await Repo.ApplyBatchAsync(OperationMode.Entrada, [Read(Epc1, T0), Read(Epc2, T0.AddSeconds(1))]);

        var tags = await Repo.QueryTagsAsync(null, null);
        var registered = tags.Single(t => t.Epc == Epc1);
        var unregistered = tags.Single(t => t.Epc == Epc2);
        Assert.Equal("Palete azul", registered.Descricao);
        Assert.Equal("SKU-AZ", registered.Sku);
        Assert.True(registered.IsRegistered);
        Assert.Null(unregistered.Descricao);
        Assert.False(unregistered.IsRegistered);

        Assert.Equal([Epc1], (await Repo.QueryTagsAsync(null, "azul")).Select(t => t.Epc));
        Assert.Equal([Epc1], (await Repo.QueryTagsAsync(TagStatus.Dentro, "sku-az")).Select(t => t.Epc));
        Assert.Empty(await Repo.QueryTagsAsync(TagStatus.Fora, "azul"));
    }

    [SkippableFact]
    public async Task Cadastro_nao_altera_a_regra_de_anomalia()
    {
        // Cadastrada mas nunca lida em ENTRADA: lida em SAÍDA continua sendo SAÍDA SEM ENTRADA.
        await Catalog.AddRegistrationAsync(Input(Epc1));

        var result = await Repo.ApplyBatchAsync(OperationMode.Saida, [Read(Epc1, T0)]);

        Assert.Equal(EventType.SaidaSemEntrada, Assert.Single(result.Applied).Event);
        Assert.Equal(1, (await Repo.GetCountersAsync()).Anomalias);
        Assert.NotNull(await Catalog.GetRegistrationAsync(Epc1));
    }
}
