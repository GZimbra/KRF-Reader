using KrfReader.Application.Configuration;
using KrfReader.Application.Persistence;
using KrfReader.Application.Pipeline;
using KrfReader.Domain;
using KrfReader.Infrastructure.Persistence;

namespace KrfReader.Tests.Persistence;

public sealed class SqliteMovementsAndRegisteredTagsTests : MovementsAndRegisteredTagsContract<SqliteInventoryRepository>
{
    private protected override ITestDatabase<SqliteInventoryRepository> CreateDatabase() => new SqliteTestDatabase();
}

public sealed class SqlServerMovementsAndRegisteredTagsTests : MovementsAndRegisteredTagsContract<SqlServerInventoryRepository>
{
    private protected override ITestDatabase<SqlServerInventoryRepository> CreateDatabase() => new SqlServerTestDatabase();
}

/// <summary>Views TagsCadastradas/Movimentacoes e Passagens: roda igual em SQLite e SQL Server.</summary>
public abstract class MovementsAndRegisteredTagsContract<TRepo> : PersistenceContract<TRepo>
    where TRepo : class, IInventoryRepository, ITagCatalogRepository
{
    private const string Epc1 = "E28011606000020800000001";
    private const string Epc2 = "E28011606000020800000002";
    private const string Epc3 = "E28011606000020800000003";
    private static readonly DateTime T0 = new(2026, 3, 10, 8, 0, 0, DateTimeKind.Utc);

    private static TagRead Read(string epc, DateTime at, int antenna = 1, double rssi = -50) => new(epc, rssi, antenna, at);

    private static PassageSnapshot Passage(OperationMode? mode, DateTime start, DateTime end, int tags = 1,
        PassageVerdict verdict = PassageVerdict.Confiavel) =>
        new(7, mode, PassageState.Closed, start, end, "silêncio", tags, tags * 10, 4, 3, 0, tags, 0, 0, verdict,
            verdict == PassageVerdict.Confiavel ? [] : ["motivo 1", "motivo 2"], []);

    // ───────────── última detecção ─────────────

    [SkippableFact]
    public async Task Ultima_deteccao_guarda_antena_e_RSSI_da_leitura_mais_recente()
    {
        await Repo.ApplyBatchAsync(OperationMode.Entrada,
        [
            Read(Epc1, T0.AddSeconds(2), antenna: 2, rssi: -40),
            Read(Epc1, T0.AddSeconds(1), antenna: 1, rssi: -70), // fora de ordem: não sobrescreve
        ]);

        var tag = Assert.Single(await Repo.QueryTagsAsync(null, null));
        Assert.Equal(2, tag.UltimaAntenaId);
        Assert.Equal(-40, tag.UltimaRssi);

        // Releitura idempotente (sem evento) atualiza a última detecção.
        await Repo.ApplyBatchAsync(OperationMode.Entrada, [Read(Epc1, T0.AddMinutes(1), antenna: 3, rssi: -55)]);
        tag = Assert.Single(await Repo.QueryTagsAsync(null, null));
        Assert.Equal(3, tag.UltimaAntenaId);
        Assert.Equal(-55, tag.UltimaRssi);
        Assert.Equal(T0.AddMinutes(1), tag.UltimaLeituraUtc);

        // Lote atrasado com leitura mais antiga não retrocede nada.
        await Repo.ApplyBatchAsync(OperationMode.Entrada, [Read(Epc1, T0, antenna: 4, rssi: -80)]);
        tag = Assert.Single(await Repo.QueryTagsAsync(null, null));
        Assert.Equal(3, tag.UltimaAntenaId);
        Assert.Equal(T0.AddMinutes(1), tag.UltimaLeituraUtc);
    }

    // ───────────── tags cadastradas ─────────────

    [SkippableFact]
    public async Task Cadastro_grava_e_le_categoria_unidade_localizacao()
    {
        Assert.True(await Repo.AddRegistrationAsync(new(Epc1, "Palete", "SKU-1", null, " Embalagem ", "PL", "Rua A")));

        var reg = (await Repo.GetRegistrationAsync(Epc1))!;
        Assert.Equal("Embalagem", reg.Categoria);
        Assert.Equal("PL", reg.Unidade);
        Assert.Equal("Rua A", reg.Localizacao);

        Assert.True(await Repo.UpdateRegistrationAsync(new(Epc1, "Palete", "SKU-1", null, null, "UN", "")));
        reg = (await Repo.GetRegistrationAsync(Epc1))!;
        Assert.Null(reg.Categoria);
        Assert.Equal("UN", reg.Unidade);
        Assert.Null(reg.Localizacao);
    }

    [SkippableFact]
    public async Task TagsCadastradas_traz_status_e_ultima_deteccao_e_nunca_lidas_no_fim()
    {
        await Repo.AddRegistrationAsync(new(Epc1, "Palete azul", null, null, "Embalagem", null, "Doca 1"));
        await Repo.AddRegistrationAsync(new(Epc2, "Caixa", null, null));
        await Repo.AddRegistrationAsync(new(Epc3, "Nunca lida", null, null));
        await Repo.ApplyBatchAsync(OperationMode.Entrada, [Read(Epc1, T0, antenna: 2, rssi: -45), Read(Epc2, T0.AddMinutes(1))]);
        await Repo.ApplyBatchAsync(OperationMode.Saida, [Read(Epc2, T0.AddMinutes(2))]);
        // Tag lida sem cadastro não entra na lista de cadastradas.
        await Repo.ApplyBatchAsync(OperationMode.Entrada, [Read("AAAA0001", T0.AddMinutes(3))]);

        var all = await Repo.QueryRegisteredTagsAsync(null);
        Assert.Equal([Epc2, Epc1, Epc3], all.Select(t => t.Epc));

        var e1 = all.Single(t => t.Epc == Epc1);
        Assert.Equal(TagStatus.Dentro, e1.Status);
        Assert.Equal(T0, e1.UltimaDeteccaoUtc);
        Assert.Equal(2, e1.UltimaAntenaId);
        Assert.Equal(-45, e1.UltimaRssi);

        var e3 = all.Single(t => t.Epc == Epc3);
        Assert.True(e3.NeverDetected);
        Assert.Null(e3.UltimaDeteccaoUtc);

        Assert.Equal([Epc1], (await Repo.QueryRegisteredTagsAsync(null, RegisteredTagStatusFilter.Dentro)).Select(t => t.Epc));
        Assert.Equal([Epc2], (await Repo.QueryRegisteredTagsAsync(null, RegisteredTagStatusFilter.Fora)).Select(t => t.Epc));
        Assert.Equal([Epc3], (await Repo.QueryRegisteredTagsAsync(null, RegisteredTagStatusFilter.NuncaLida)).Select(t => t.Epc));
        Assert.Equal([Epc1], (await Repo.QueryRegisteredTagsAsync("embal")).Select(t => t.Epc));
        Assert.Equal([Epc1], (await Repo.QueryRegisteredTagsAsync("doca")).Select(t => t.Epc));
        Assert.Single(await Repo.QueryRegisteredTagsAsync(null, limit: 1));
    }

    // ───────────── movimentações ─────────────

    [SkippableFact]
    public async Task Movimentacoes_filtra_por_periodo_tipo_anomalia_e_item()
    {
        await Repo.AddRegistrationAsync(new(Epc1, "Palete azul", "SKU-AZ", null));
        await Repo.ApplyBatchAsync(OperationMode.Entrada, [Read(Epc1, T0), Read(Epc2, T0.AddSeconds(1))]);
        await Repo.ApplyBatchAsync(OperationMode.Saida, [Read(Epc1, T0.AddHours(1)), Read(Epc3, T0.AddHours(1).AddSeconds(1))]);
        await Repo.ApplyBatchAsync(OperationMode.Saida, [Read(Epc1, T0.AddHours(2))]); // idempotente: não é movimentação

        var all = await Repo.QueryMovementsAsync(new());
        Assert.Equal(4, all.Count);
        Assert.Equal([Epc3, Epc1, Epc2, Epc1], all.Select(m => m.Epc)); // mais recente primeiro

        var saidaEpc1 = all[1];
        Assert.Equal(OperationMode.Saida, saidaEpc1.Tipo);
        Assert.Equal(EventType.MudancaStatus, saidaEpc1.Evento);
        Assert.Equal("Palete azul", saidaEpc1.Descricao);
        Assert.Equal("SKU-AZ", saidaEpc1.Sku);
        Assert.Null(all[0].Descricao);

        Assert.Equal(2, (await Repo.QueryMovementsAsync(new(Tipo: OperationMode.Entrada))).Count);
        var anomalia = Assert.Single(await Repo.QueryMovementsAsync(new(OnlyAnomalies: true)));
        Assert.Equal(Epc3, anomalia.Epc);
        Assert.True(anomalia.IsAnomaly);

        // Período [from, to): T0 entra, T0+1h fica de fora.
        var periodo = await Repo.QueryMovementsAsync(new(FromUtc: T0, ToUtc: T0.AddHours(1)));
        Assert.Equal([Epc2, Epc1], periodo.Select(m => m.Epc));

        Assert.Equal(2, (await Repo.QueryMovementsAsync(new(Search: "azul"))).Count);
        Assert.Equal(2, (await Repo.QueryMovementsAsync(new(Search: "sku-az"))).Count);
        Assert.Single(await Repo.QueryMovementsAsync(new(Search: "00000003")));
        Assert.Single(await Repo.QueryMovementsAsync(new(Limit: 1)));
        Assert.Empty(await Repo.QueryMovementsAsync(new(Limit: 0)));
    }

    [SkippableFact]
    public async Task Passagem_gravada_vincula_movimentacoes_do_mesmo_modo_e_janela()
    {
        await Repo.ApplyBatchAsync(OperationMode.Entrada, [Read(Epc1, T0.AddSeconds(1)), Read(Epc2, T0.AddSeconds(2))]);
        await Repo.ApplyBatchAsync(OperationMode.Entrada, [Read(Epc3, T0.AddMinutes(5))]); // fora da janela
        await Repo.ApplyBatchAsync(OperationMode.Saida, [Read(Epc1, T0.AddSeconds(3))]);   // mesmo horário, outro modo

        var id = await Repo.SavePassageAsync(Passage(OperationMode.Entrada, T0, T0.AddSeconds(10), tags: 2, verdict: PassageVerdict.Atencao));
        Assert.NotNull(id);

        var byEpc = (await Repo.QueryMovementsAsync(new())).ToLookup(m => m.Epc);
        Assert.Equal(id, byEpc[Epc2].Single().PassagemId);
        Assert.Equal(id, byEpc[Epc1].Single(m => m.Tipo == OperationMode.Entrada).PassagemId);
        Assert.Null(byEpc[Epc1].Single(m => m.Tipo == OperationMode.Saida).PassagemId);
        Assert.Null(byEpc[Epc3].Single().PassagemId);
    }

    [SkippableFact]
    public async Task Passagem_sem_modo_nao_e_gravada()
    {
        Assert.Null(await Repo.SavePassageAsync(Passage(null, T0, T0.AddSeconds(1))));
    }

    [SkippableTheory]
    [InlineData(PassageVerdict.Confiavel, "CONFIAVEL")]
    [InlineData(PassageVerdict.Atencao, "ATENCAO")]
    [InlineData(PassageVerdict.NaoConvergiu, "NAO_CONVERGIU")]
    [InlineData(PassageVerdict.DadosIncompletos, "DADOS_INCOMPLETOS")]
    public async Task Todos_os_vereditos_passam_no_CHECK(PassageVerdict verdict, string literal)
    {
        Assert.Equal(literal, SqliteInventoryRepository.VerdictToDb(verdict));
        Assert.NotNull(await Repo.SavePassageAsync(Passage(OperationMode.Saida, T0, T0.AddSeconds(1), verdict: verdict)));
    }

    // ───────────── período local → UTC ─────────────

    [SkippableFact]
    public void LocalDaysToUtc_cobre_os_dias_inteiros_no_fuso_local()
    {
        var brt = TimeZoneInfo.CreateCustomTimeZone("BRT", TimeSpan.FromHours(-3), "BRT", "BRT");

        var (from, to) = MovementFilter.LocalDaysToUtc(new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 10), brt);

        Assert.Equal(new DateTime(2026, 3, 10, 3, 0, 0, DateTimeKind.Utc), from);
        Assert.Equal(new DateTime(2026, 3, 11, 3, 0, 0, DateTimeKind.Utc), to);
        Assert.Equal((null, null), MovementFilter.LocalDaysToUtc(null, null, brt));
    }

    [SkippableFact]
    public void LocalDaysToUtc_trata_meia_noite_inexistente_do_horario_de_verao()
    {
        // Horário de verão que começa à 00:00 (regra antiga do Brasil): 00:00 → 01:00.
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2018, 1, 1), new DateTime(2018, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 11, 4),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 2, 18));
        var zone = TimeZoneInfo.CreateCustomTimeZone("BRT-DST", TimeSpan.FromHours(-3), "BRT", "BRT", "BRST", [rule]);

        var (from, _) = MovementFilter.LocalDaysToUtc(new DateOnly(2018, 11, 4), null, zone);

        // 01:00 BRST (-2) = 03:00 UTC.
        Assert.Equal(new DateTime(2018, 11, 4, 3, 0, 0, DateTimeKind.Utc), from);
    }
}
