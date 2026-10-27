using KrfReader.Application.Configuration;
using KrfReader.Application.Pipeline;
using KrfReader.Domain;

namespace KrfReader.Tests.Passage;

public class PassageTrackerTests
{
    private static readonly DateTime T0 = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);

    private static PassageTracker Create(int stableRounds = 3, int silenceMs = 2000) =>
        new(new PassageOptions { StableRounds = stableRounds, SilenceMs = silenceMs, MaxEstimatedMissed = 0.5, MarginalRssiMarginDb = 6 }, minRssi: -70);

    private static TagRead Read(string epc, double ms, double rssi = -50) => new(epc, rssi, 1, T0.AddMilliseconds(ms));

    [Fact]
    public void Sem_leitura_nao_ha_passagem()
    {
        var t = Create();

        Assert.Equal((null, null), t.CompleteRound(T0));
        Assert.Null(t.Close(T0, "PARAR"));
        Assert.False(t.IsActive);
    }

    [Fact]
    public void Releituras_sao_deduplicadas_e_contadas_por_tag()
    {
        var t = Create();
        for (var i = 0; i < 10; i++)
            t.OnRead(Read("AA01", i, rssi: -40 - i), OperationMode.Entrada);
        t.OnRead(Read("AA02", 5), OperationMode.Entrada);

        var s = t.Snapshot()!;

        Assert.Equal(2, s.DistinctTags);
        Assert.Equal(11, s.TotalReads);
        var tag = s.Tags.Single(x => x.Epc == "AA01");
        Assert.Equal(10, tag.Reads);
        Assert.Equal(-40, tag.MaxRssi);
        Assert.Equal(-44.5, tag.AvgRssi, 3);
        Assert.Equal(OperationMode.Entrada, s.Mode);
    }

    [Fact]
    public void Converge_apos_N_rodadas_sem_epc_novo()
    {
        var t = Create(stableRounds: 3);
        string[] epcs = ["01", "02", "03"];

        // Rodada 1: só duas tags. Rodada 2: aparece a terceira (EPC novo zera a estabilidade).
        t.OnRead(Read("01", 0), OperationMode.Entrada);
        t.OnRead(Read("02", 1), OperationMode.Entrada);
        Assert.Equal(0, t.CompleteRound(T0.AddMilliseconds(100)).Live!.StableRounds);
        t.OnRead(Read("03", 150), OperationMode.Entrada);
        var r2 = t.CompleteRound(T0.AddMilliseconds(200)).Live!;
        Assert.Equal(0, r2.StableRounds);
        Assert.Equal(1, r2.NewInLastRound);
        Assert.Equal(PassageVerdict.NaoConvergiu, r2.Verdict);

        PassageSnapshot? live = null;
        for (var round = 3; round <= 5; round++)
        {
            foreach (var e in epcs)
                t.OnRead(Read(e, round * 100), OperationMode.Entrada);
            live = t.CompleteRound(T0.AddMilliseconds(round * 100 + 50)).Live;
        }

        Assert.Equal(3, live!.StableRounds);
        Assert.Equal(5, live.Rounds);
        Assert.Equal(0, live.NewInLastRound);
        Assert.Equal(3, live.RespondingLastRound);
        Assert.Equal(PassageVerdict.Confiavel, live.Verdict);
        Assert.Empty(live.Issues);
    }

    [Fact]
    public void Rodada_sem_leitura_nao_conta_como_estavel()
    {
        var t = Create(stableRounds: 2, silenceMs: 10_000);
        t.OnRead(Read("01", 0), OperationMode.Saida);
        t.CompleteRound(T0.AddMilliseconds(100));

        for (var i = 0; i < 5; i++)
            t.CompleteRound(T0.AddMilliseconds(200 + i * 100));

        var s = t.Snapshot()!;
        Assert.Equal(1, s.Rounds);
        Assert.Equal(0, s.StableRounds);
        Assert.Equal(PassageVerdict.NaoConvergiu, s.Verdict);
    }

    [Fact]
    public void Silencio_fecha_a_passagem_com_relatorio()
    {
        var t = Create(stableRounds: 1, silenceMs: 1000);
        t.OnRead(Read("01", 0), OperationMode.Entrada);
        t.CompleteRound(T0.AddMilliseconds(100));
        t.OnRead(Read("01", 150), OperationMode.Entrada);
        t.CompleteRound(T0.AddMilliseconds(200));

        Assert.Null(t.CompleteRound(T0.AddMilliseconds(900)).Closed);
        var (live, closed) = t.CompleteRound(T0.AddMilliseconds(1200));

        Assert.Null(live);
        Assert.NotNull(closed);
        Assert.Equal(PassageState.Closed, closed.State);
        Assert.Equal("silêncio no campo", closed.CloseReason);
        Assert.False(t.IsActive);

        // Leitura seguinte abre outra passagem.
        t.OnRead(Read("02", 2000), OperationMode.Entrada);
        Assert.Equal(closed.Id + 1, t.Snapshot()!.Id);
    }

    [Fact]
    public void Perda_de_dados_domina_o_veredito()
    {
        var t = Create(stableRounds: 1);
        t.OnRead(Read("01", 0), OperationMode.Entrada);
        t.CompleteRound(T0.AddMilliseconds(100));
        t.OnRead(Read("01", 120), OperationMode.Entrada);
        t.ReportDataLoss("Conexão com o leitor caiu durante a passagem.");

        var s = t.Close(T0.AddMilliseconds(200), "PARAR")!;

        Assert.Equal(PassageVerdict.DadosIncompletos, s.Verdict);
        Assert.Contains("Conexão com o leitor caiu durante a passagem.", s.Issues);
    }

    [Fact]
    public void Muitas_tags_de_uma_rodada_so_disparam_o_estimador()
    {
        var t = Create(stableRounds: 1);
        // 4 rodadas: 20 tags firmes em todas; 6 tags vistas uma única vez cada (rodadas 1 e 2).
        for (var round = 0; round < 4; round++)
        {
            for (var i = 0; i < 20; i++)
                t.OnRead(Read($"F{i:D3}", round * 100), OperationMode.Entrada);
            if (round < 2)
            {
                for (var i = 0; i < 3; i++)
                    t.OnRead(Read($"W{round}{i}", round * 100, rssi: -68), OperationMode.Entrada);
            }

            t.CompleteRound(T0.AddMilliseconds(round * 100 + 50));
        }

        var s = t.Snapshot()!;

        Assert.Equal(26, s.DistinctTags);
        Assert.Equal(2, s.StableRounds);
        // Q1 = 6, Q2 = 0, m = 4 → 3/4 · 6·5/2 = 11,25.
        Assert.Equal(11.25, s.EstimatedMissed, 3);
        Assert.Equal(PassageVerdict.Atencao, s.Verdict);
        Assert.Equal(6, s.MarginalTags);
        Assert.All(s.Tags.Take(6), x => Assert.True(x.IsMarginal));
    }

    [Theory]
    [InlineData(0, 0, 5, 0)]
    [InlineData(3, 0, 1, 3)]
    [InlineData(1, 0, 5, 0)]
    [InlineData(4, 2, 2, 1)]
    public void Chao2(int q1, int q2, int rounds, double expected)
    {
        Assert.Equal(expected, PassageTracker.EstimateMissed(q1, q2, rounds), 6);
    }
}
