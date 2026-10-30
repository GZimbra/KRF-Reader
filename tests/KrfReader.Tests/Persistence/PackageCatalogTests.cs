using KrfReader.Application.Persistence;
using KrfReader.Application.Pipeline;
using KrfReader.Domain;
using KrfReader.Infrastructure.Persistence;

namespace KrfReader.Tests.Persistence;

public sealed class SqlitePackageCatalogTests : PackageCatalogContract<SqliteInventoryRepository>
{
    private protected override ITestDatabase<SqliteInventoryRepository> CreateDatabase() => new SqliteTestDatabase();
}

public sealed class SqlServerPackageCatalogTests : PackageCatalogContract<SqlServerInventoryRepository>
{
    private protected override ITestDatabase<SqlServerInventoryRepository> CreateDatabase() => new SqlServerTestDatabase();
}

/// <summary>Embalagem com várias etiquetas: cadastro, movimentação por embalagem, views. Igual em SQLite e SQL Server.</summary>
public abstract class PackageCatalogContract<TRepo> : PersistenceContract<TRepo>
    where TRepo : class, IInventoryRepository, ITagCatalogRepository, IPackageCatalogRepository
{
    private const string FaceA = "E28011606000020800000A01";
    private const string FaceB = "E28011606000020800000B01";
    private const string Other = "E28011606000020800000C01";
    private const string Loose = "E28011606000020800000D01";
    private static readonly DateTime T0 = new(2026, 10, 30, 8, 0, 0, DateTimeKind.Utc);

    private IPackageCatalogRepository Packages => Repo;

    private static TagRead Read(string epc, DateTime at, int antenna = 1, double rssi = -50) => new(epc, rssi, antenna, at);

    private static PackageInput Glt(string codigo, string tipo = "GLT1165", params string[] epcs) =>
        new(codigo, "GLT peças estampadas", tipo, "1200", "1000", "975", "61,5", null,
            epcs.Select((e, i) => new PackageTagInput(e, i == 0 ? "frente" : "trás")).ToArray());

    private async Task<long> AddGltAsync(string codigo = "glt-0001", params string[] epcs)
    {
        var outcome = await Packages.AddPackageAsync(Glt(codigo, epcs: epcs.Length == 0 ? [FaceA, FaceB] : epcs));
        Assert.True(outcome.Succeeded, outcome.Message);
        return outcome.Id!.Value;
    }

    // ───────────── cadastro ─────────────

    [SkippableFact]
    public async Task Cadastra_embalagem_com_duas_etiquetas_e_dimensoes()
    {
        var id = await AddGltAsync();

        var p = (await Packages.GetPackageAsync(id))!;
        Assert.Equal("GLT-0001", p.Cadastro.Codigo);
        Assert.Equal("GLT1165", p.Cadastro.TipoGlt);
        Assert.Equal((1200, 1000, 975), (p.Cadastro.ComprimentoMm, p.Cadastro.LarguraMm, p.Cadastro.AlturaMm));
        Assert.Equal(61.5m, p.Cadastro.PesoKg);
        Assert.Equal([FaceA, FaceB], p.Cadastro.Etiquetas.Select(e => e.Epc).Order());
        Assert.Contains(p.Cadastro.Etiquetas, e => e.Epc == FaceA && e.Posicao == "FRENTE");
        Assert.True(p.NeverDetected);

        Assert.Equal(id, (await Packages.FindPackageByEpcAsync(FaceB))!.Id);
        Assert.Null(await Packages.FindPackageByEpcAsync(Other));
    }

    [SkippableFact]
    public async Task Tipo_repete_entre_embalagens_mas_codigo_e_unico_sem_distinguir_caixa()
    {
        await AddGltAsync("GLT-0001", FaceA);

        // Mesmo tipo, outra embalagem física (medidas podem diferir): permitido.
        var second = await Packages.AddPackageAsync(Glt("GLT-0002", epcs: [FaceB]) with { ComprimentoMm = "1400" });
        Assert.True(second.Succeeded, second.Message);

        var dup = await Packages.AddPackageAsync(Glt("glt-0001", epcs: [Other]));
        Assert.Equal(PackageSaveStatus.CodigoDuplicado, dup.Status);
        Assert.Null(await Packages.FindPackageByEpcAsync(Other));
    }

    [SkippableFact]
    public async Task Etiqueta_nao_pode_estar_em_duas_embalagens_nem_ser_tag_avulsa()
    {
        await AddGltAsync("GLT-0001", FaceA, FaceB);

        var inOther = await Packages.AddPackageAsync(Glt("GLT-0002", epcs: [Other, FaceB]));
        Assert.Equal(PackageSaveStatus.EpcEmOutraEmbalagem, inOther.Status);
        Assert.Contains(FaceB, inOther.Message);

        Assert.True(await Repo.AddRegistrationAsync(new(Loose, "Palete avulso", null, null)));
        var loose = await Packages.AddPackageAsync(Glt("GLT-0003", epcs: [Loose]));
        Assert.Equal(PackageSaveStatus.EpcCadastradoComoTag, loose.Status);

        // E o contrário: etiqueta de embalagem não vira tag avulsa.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Repo.AddRegistrationAsync(new(FaceA, "x", null, null)));
        Assert.Contains("GLT-0001", ex.Message);
    }

    [SkippableFact]
    public async Task Atualizar_substitui_etiquetas_e_etiqueta_removida_volta_a_ser_avulsa()
    {
        var id = await AddGltAsync("GLT-0001", FaceA, FaceB);

        var outcome = await Packages.UpdatePackageAsync(id, Glt("GLT-0001", epcs: [FaceA, Other]));
        Assert.True(outcome.Succeeded, outcome.Message);

        var p = (await Packages.GetPackageAsync(id))!;
        Assert.Equal([FaceA, Other], p.Cadastro.Etiquetas.Select(e => e.Epc).Order());
        Assert.Null(await Packages.FindPackageByEpcAsync(FaceB));

        Assert.Equal(PackageSaveStatus.NaoEncontrada, (await Packages.UpdatePackageAsync(id + 1000, Glt("X", epcs: [Loose]))).Status);
    }

    [SkippableFact]
    public async Task Remover_embalagem_mantem_historico()
    {
        var id = await AddGltAsync();
        await Repo.ApplyBatchAsync(OperationMode.Entrada, [Read(FaceA, T0)]);

        Assert.True(await Packages.DeletePackageAsync(id));
        Assert.False(await Packages.DeletePackageAsync(id));
        Assert.Null(await Packages.FindPackageByEpcAsync(FaceA));

        var m = Assert.Single(await Repo.QueryMovementsAsync(new MovementFilter()));
        Assert.Equal(id, m.EmbalagemId);
        Assert.Null(m.EmbalagemCodigo);
    }

    // ───────────── movimentação ─────────────

    [SkippableFact]
    public async Task Duas_etiquetas_na_mesma_passagem_geram_uma_movimentacao()
    {
        var id = await AddGltAsync();

        var batch = await Repo.ApplyBatchAsync(OperationMode.Entrada,
        [
            Read(FaceA, T0, antenna: 1, rssi: -60),
            Read(FaceB, T0.AddMilliseconds(40), antenna: 2, rssi: -48),
        ]);

        Assert.Equal([EventType.Criacao, null], batch.Applied.Select(a => a.Event));
        Assert.All(batch.Applied, a => Assert.Equal(id, a.Package!.Id));
        Assert.All(batch.Applied, a => Assert.Equal(TagStatus.Dentro, a.NewStatus));

        var m = Assert.Single(await Repo.QueryMovementsAsync(new MovementFilter()));
        Assert.Equal(FaceA, m.Epc);
        Assert.Equal(id, m.EmbalagemId);
        Assert.Equal("GLT-0001", m.EmbalagemCodigo);
        Assert.Equal("GLT1165", m.TipoGlt);
        Assert.Equal("GLT peças estampadas", m.Descricao);

        var p = (await Packages.GetPackageAsync(id))!;
        Assert.Equal(TagStatus.Dentro, p.Status);
        Assert.Equal(FaceB, p.UltimaEpc);
        Assert.Equal(2, p.UltimaAntenaId);
        Assert.Equal(T0.AddMilliseconds(40), p.UltimaDeteccaoUtc);

        // Contadores por item: a embalagem conta uma vez.
        var c = await Repo.GetCountersAsync();
        Assert.Equal((1, 0), (c.Dentro, c.Fora));
    }

    [SkippableFact]
    public async Task Saida_por_qualquer_face_movimenta_a_embalagem_e_alinha_a_outra_etiqueta()
    {
        var id = await AddGltAsync();
        await Repo.ApplyBatchAsync(OperationMode.Entrada, [Read(FaceA, T0), Read(FaceB, T0.AddMilliseconds(5))]);

        // Na saída só a face B passa perto da antena.
        var batch = await Repo.ApplyBatchAsync(OperationMode.Saida, [Read(FaceB, T0.AddMinutes(10))]);
        var applied = Assert.Single(batch.Applied);
        Assert.Equal((TagStatus.Dentro, TagStatus.Fora, EventType.MudancaStatus), (applied.PreviousStatus, applied.NewStatus, applied.Event));

        // Releitura da face A na mesma saída: sem evento.
        batch = await Repo.ApplyBatchAsync(OperationMode.Saida, [Read(FaceA, T0.AddMinutes(10).AddSeconds(1))]);
        Assert.Null(Assert.Single(batch.Applied).Event);

        Assert.Equal(TagStatus.Fora, (await Packages.GetPackageAsync(id))!.Status);
        var tags = await Repo.QueryTagsAsync(null, null);
        Assert.All(tags, t => Assert.Equal(TagStatus.Fora, t.Status));
        Assert.All(tags, t => Assert.Equal("GLT-0001", t.EmbalagemCodigo));

        var movements = await Repo.QueryMovementsAsync(new MovementFilter());
        Assert.Equal(2, movements.Count);
        Assert.Equal([OperationMode.Saida, OperationMode.Entrada], movements.Select(m => m.Tipo));
        var c = await Repo.GetCountersAsync();
        Assert.Equal((0, 1, 0), (c.Dentro, c.Fora, c.Anomalias));
    }

    [SkippableFact]
    public async Task Etiqueta_nao_lida_no_lote_acompanha_o_status_da_embalagem()
    {
        await AddGltAsync();
        await Repo.ApplyBatchAsync(OperationMode.Entrada, [Read(FaceA, T0), Read(FaceB, T0.AddMilliseconds(5))]);
        await Repo.ApplyBatchAsync(OperationMode.Saida, [Read(FaceA, T0.AddMinutes(5))]);

        var b = Assert.Single(await Repo.QueryTagsAsync(TagStatus.Fora, FaceB));
        Assert.Equal(TagStatus.Fora, b.Status);
        Assert.Equal(T0.AddMilliseconds(5), b.UltimaLeituraUtc); // última leitura continua sendo a dela
    }

    [SkippableFact]
    public async Task Saida_sem_entrada_de_embalagem_e_uma_anomalia_so()
    {
        await AddGltAsync();

        await Repo.ApplyBatchAsync(OperationMode.Saida, [Read(FaceA, T0), Read(FaceB, T0.AddMilliseconds(1))]);

        var m = Assert.Single(await Repo.QueryMovementsAsync(new MovementFilter(OnlyAnomalies: true)));
        Assert.True(m.IsAnomaly);
        Assert.Equal(1, (await Repo.GetCountersAsync()).Anomalias);
    }

    [SkippableFact]
    public async Task Tag_avulsa_continua_1_epc_1_item_no_mesmo_lote()
    {
        await AddGltAsync();

        var batch = await Repo.ApplyBatchAsync(OperationMode.Entrada,
            [Read(FaceA, T0), Read(Loose, T0.AddMilliseconds(1)), Read(FaceB, T0.AddMilliseconds(2))]);

        Assert.Equal([EventType.Criacao, EventType.Criacao, null], batch.Applied.Select(a => a.Event));
        Assert.Null(batch.Applied[1].Package);
        var c = await Repo.GetCountersAsync();
        Assert.Equal(2, c.Dentro);
        Assert.Equal(2, (await Repo.QueryMovementsAsync(new MovementFilter())).Count);
    }

    [SkippableFact]
    public async Task Embalagem_herda_estado_das_etiquetas_ja_lidas_como_tag_avulsa()
    {
        // Caixa passou na ENTRADA antes do cadastro (EPCs soltos).
        await Repo.ApplyBatchAsync(OperationMode.Entrada, [Read(FaceA, T0, antenna: 3), Read(FaceB, T0.AddSeconds(1), antenna: 4)]);
        Assert.Equal(2, (await Repo.GetCountersAsync()).Dentro);

        var id = await AddGltAsync();
        var p = (await Packages.GetPackageAsync(id))!;
        Assert.Equal(TagStatus.Dentro, p.Status);
        Assert.Equal(FaceB, p.UltimaEpc);
        Assert.Equal(4, p.UltimaAntenaId);
        Assert.Equal(1, (await Repo.GetCountersAsync()).Dentro);

        // SAÍDA agora é mudança de status, não SAÍDA SEM ENTRADA.
        var batch = await Repo.ApplyBatchAsync(OperationMode.Saida, [Read(FaceA, T0.AddHours(1))]);
        Assert.Equal(EventType.MudancaStatus, Assert.Single(batch.Applied).Event);
    }

    // ───────────── consultas ─────────────

    [SkippableFact]
    public async Task Consulta_filtra_por_status_e_busca_codigo_tipo_e_epc()
    {
        var a = await AddGltAsync("GLT-0001", FaceA, FaceB);
        var b = (await Packages.AddPackageAsync(Glt("CX-9", "KLT6429", Other))).Id!.Value;
        await Repo.ApplyBatchAsync(OperationMode.Entrada, [Read(FaceB, T0)]);

        Assert.Equal([a, b], (await Packages.QueryPackagesAsync(null)).Select(p => p.Id));
        Assert.Equal([a], (await Packages.QueryPackagesAsync(null, RegisteredTagStatusFilter.Dentro)).Select(p => p.Id));
        Assert.Equal([b], (await Packages.QueryPackagesAsync(null, RegisteredTagStatusFilter.NuncaLida)).Select(p => p.Id));
        Assert.Equal([b], (await Packages.QueryPackagesAsync("klt")).Select(p => p.Id));
        Assert.Equal([a], (await Packages.QueryPackagesAsync("0B01")).Select(p => p.Id));
        Assert.Equal(2, (await Packages.QueryPackagesAsync(null)).First().Cadastro.Etiquetas.Count);

        var map = await Packages.GetPackageEpcMapAsync();
        Assert.Equal(3, map.Count);
        Assert.Same(map[FaceA], map[FaceB]);
        Assert.Equal(2, map[FaceA].Etiquetas);
        Assert.Equal("CX-9", map[Other].Codigo);
    }

    [SkippableFact]
    public async Task Cadastro_de_tags_avulsas_nao_lista_etiquetas_de_embalagem()
    {
        await AddGltAsync();
        Assert.True(await Repo.AddRegistrationAsync(new(Loose, "Palete", null, null)));

        var tags = await Repo.QueryRegisteredTagsAsync(null);
        Assert.Equal([Loose], tags.Select(t => t.Epc));
    }

    [SkippableFact]
    public async Task Passagem_grava_itens_e_embalagens()
    {
        var snapshot = new PassageSnapshot(1, OperationMode.Entrada, PassageState.Closed, T0, T0.AddSeconds(5), "silêncio",
            4, 40, 5, 3, 0, 4, 0, 0, PassageVerdict.Confiavel, [], [])
        {
            DistinctItems = 3,
            Packages = 1,
        };

        Assert.NotNull(await Repo.SavePassageAsync(snapshot));
    }
}
