using KrfReader.Application.Configuration;
using KrfReader.Application.Pipeline;
using KrfReader.Domain;

namespace KrfReader.Tests.Passage;

/// <summary>Passagem com embalagens de várias etiquetas: veredito conta ITENS, não EPCs.</summary>
public class PackagePassageTests
{
    private static readonly DateTime T0 = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    private static readonly PackageRef Glt1 = new(1, "GLT-0001", 2);
    private static readonly PackageRef Glt2 = new(2, "GLT-0002", 2);

    private static PassageTracker Create(int stableRounds = 2)
    {
        var directory = new PackageDirectory();
        directory.Replace(new Dictionary<string, PackageRef>
        {
            ["A1"] = Glt1, ["B1"] = Glt1,
            ["A2"] = Glt2, ["B2"] = Glt2,
        });
        return new PassageTracker(
            new PassageOptions { StableRounds = stableRounds, SilenceMs = 2000, MaxEstimatedMissed = 0.5, MarginalRssiMarginDb = 6 },
            minRssi: -70, directory);
    }

    private static TagRead Read(string epc, double ms, double rssi = -50) => new(epc, rssi, 1, T0.AddMilliseconds(ms));

    [Fact]
    public void Embalagem_conta_uma_vez_e_etiqueta_nao_lida_marca_parcial()
    {
        var t = Create();
        for (var round = 0; round < 3; round++)
        {
            t.OnRead(Read("A1", round * 100), OperationMode.Entrada);
            t.OnRead(Read("B1", round * 100 + 1), OperationMode.Entrada);
            t.OnRead(Read("A2", round * 100 + 2), OperationMode.Entrada); // face B2 blindada: nunca responde
            t.OnRead(Read("FF01", round * 100 + 3), OperationMode.Entrada); // tag avulsa
            t.CompleteRound(T0.AddMilliseconds(round * 100 + 50));
        }

        var s = t.Snapshot()!;

        Assert.Equal(4, s.DistinctTags);
        Assert.Equal(3, s.DistinctItems);
        Assert.Equal(2, s.Packages);
        Assert.Equal(1, s.PartialPackages);
        Assert.Equal(PassageVerdict.Confiavel, s.Verdict);
        Assert.Equal("GLT-0001", s.Tags.Single(x => x.Epc == "B1").PackageCodigo);
        Assert.Null(s.Tags.Single(x => x.Epc == "FF01").PackageCodigo);
    }

    [Fact]
    public void Segunda_face_de_caixa_ja_vista_nao_e_item_novo()
    {
        var t = Create(stableRounds: 2);
        t.OnRead(Read("A1", 0), OperationMode.Entrada);
        t.CompleteRound(T0.AddMilliseconds(50));
        t.OnRead(Read("A1", 100), OperationMode.Entrada);
        t.OnRead(Read("B1", 101), OperationMode.Entrada); // outra face girou para a antena
        var live = t.CompleteRound(T0.AddMilliseconds(150)).Live!;
        t.OnRead(Read("B1", 200), OperationMode.Entrada);
        live = t.CompleteRound(T0.AddMilliseconds(250)).Live!;

        Assert.Equal(0, live.NewInLastRound);
        Assert.Equal(2, live.StableRounds);
        Assert.Equal(PassageVerdict.Confiavel, live.Verdict);
    }

    [Fact]
    public void Faces_diferentes_em_rodadas_diferentes_somam_incidencia_da_embalagem()
    {
        // A1 só na rodada 1, B1 só nas rodadas 2 e 3: por EPC, A1 seria "tag de uma rodada só" (marginal);
        // por item, a embalagem respondeu em 3 rodadas e não dispara o estimador.
        var t = Create(stableRounds: 2);
        t.OnRead(Read("A1", 0), OperationMode.Entrada);
        t.CompleteRound(T0.AddMilliseconds(50));
        t.OnRead(Read("B1", 100), OperationMode.Entrada);
        t.CompleteRound(T0.AddMilliseconds(150));
        t.OnRead(Read("B1", 200), OperationMode.Entrada);
        t.CompleteRound(T0.AddMilliseconds(250));

        var s = t.Snapshot()!;

        Assert.Equal(0, s.EstimatedMissed);
        Assert.Equal(PassageVerdict.Confiavel, s.Verdict);
        Assert.True(s.Tags.Single(x => x.Epc == "A1").IsMarginal);
    }

    [Fact]
    public void Sem_diretorio_cada_epc_e_um_item()
    {
        var t = new PassageTracker(new PassageOptions { StableRounds = 1, SilenceMs = 2000 }, minRssi: -70);
        t.OnRead(Read("A1", 0), OperationMode.Entrada);
        t.OnRead(Read("B1", 1), OperationMode.Entrada);

        var s = t.Snapshot()!;
        Assert.Equal(2, s.DistinctItems);
        Assert.Equal(0, s.Packages);
    }
}
