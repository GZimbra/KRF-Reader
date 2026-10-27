using KrfReader.Application.Configuration;
using KrfReader.Application.Pipeline;
using KrfReader.Application.Readers;
using KrfReader.Domain;
using KrfReader.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;
using Xunit.Abstractions;

namespace KrfReader.Tests.Passage;

/// <summary>
/// Passagem de dezenas de tags pelo campo com o simulador Gen2 (colisões, tags fracas, flags de session),
/// pipeline real e <see cref="PassageMonitor"/> real. Rodadas são disparadas à mão (relógio falso).
/// </summary>
public sealed class HighDensityPassageTests(ITestOutputHelper output)
{
    private static InventoryOptions Inventory(Action<InventoryOptions>? configure = null)
    {
        var o = new InventoryOptions
        {
            Session = GenSession.S2,
            Target = TargetStrategy.DualAB,
            TargetDwellMs = 500,
            InitialQ = 5,
            AdaptiveQ = true,
            MinQ = 2,
            MaxQ = 8,
        };
        configure?.Invoke(o);
        return o;
    }

    private static PassageOptions Passage() => new()
    {
        SilenceMs = 2500,
        StableRounds = 3,
        MaxEstimatedMissed = 0.5,
        MarginalRssiMarginDb = 6,
    };

    [Fact]
    public async Task Cinquenta_tags_paradas_no_campo_sao_todas_lidas_e_a_passagem_e_confiavel()
    {
        await using var h = await Harness.CreateAsync(seed: 7, Inventory());
        var tags = h.AddTags(50);

        h.Sim.EnterField(tags);
        await h.RoundsAsync(6);
        h.Sim.LeaveField(tags);
        await h.RoundsAsync(5);

        var report = Assert.Single(h.Closed);
        Assert.Equal(50, report.DistinctTags);
        Assert.Equal(PassageVerdict.Confiavel, report.Verdict);
        Assert.Equal(OperationMode.Entrada, report.Mode);
        Assert.True(report.StableRounds >= 3);
        Assert.InRange(report.EstimatedMissed, 0, 0.5);

        // Q dinâmico: com ~50 tags respondendo por rodada o quadro sobe para 2^6 = 64 slots.
        Assert.Contains(h.Sim.AppliedParameters, p => p.Q == 6);
        // Alternância A/B a cada rodada.
        Assert.Contains(h.Sim.AppliedParameters, p => p.Target == InventoryTarget.B);
        Assert.Contains(h.Sim.AppliedParameters, p => p.Target == InventoryTarget.A);
    }

    [Fact]
    public async Task Leituras_da_passagem_chegam_ao_estoque_deduplicadas()
    {
        await using var h = await Harness.CreateAsync(seed: 11, Inventory());
        var tags = h.AddTags(60);

        h.Sim.EnterField(tags);
        await h.RoundsAsync(5);
        await h.Pipeline.StopInventoryAsync().WithTimeout();

        var report = Assert.Single(h.Closed);
        Assert.Equal("PARAR", report.CloseReason);
        Assert.Equal(60, report.DistinctTags);
        Assert.True(report.TotalReads > 60);
        Assert.Equal(60, h.Repository.Applied.Select(a => a.Read.Epc).Distinct().Count());
        Assert.All(tags, t => Assert.Contains(h.Repository.Applied, a => a.Read.Epc == t.Epc));
    }

    [Fact]
    public async Task Caminhao_com_tags_entrando_e_saindo_escalonadas_e_lido_por_completo()
    {
        await using var h = await Harness.CreateAsync(seed: 3, Inventory());
        var tags = h.AddTags(50, weakEvery: 5, weakProbability: 0.35);

        // Carga atravessa a antena em ~4,5 s: 10 tags entram por rodada e cada uma fica 4 rodadas no campo.
        for (var round = 0; round < 9; round++)
        {
            h.Sim.EnterField(tags.Skip(round * 10).Take(10));
            if (round >= 4)
                h.Sim.LeaveField(tags.Skip((round - 4) * 10).Take(10));
            await h.RoundsAsync(1);
        }

        h.Sim.LeaveField(tags);
        await h.RoundsAsync(5);

        var report = Assert.Single(h.Closed);
        output.WriteLine($"lidas {report.DistinctTags}, rodadas {report.Rounds}, estáveis {report.StableRounds}, Chao2 {report.EstimatedMissed:0.00}, {report.Verdict}");
        Assert.Equal(50, report.DistinctTags);
        Assert.NotEqual(PassageVerdict.DadosIncompletos, report.Verdict);
    }

    [Fact]
    public async Task Passagem_de_uma_rodada_so_nunca_sai_como_confiavel()
    {
        // Configuração ruim de propósito: Q fixo 3 (8 slots) para 50 tags = quase tudo colide.
        await using var h = await Harness.CreateAsync(seed: 5, Inventory(o =>
        {
            o.AdaptiveQ = false;
            o.InitialQ = 3;
            o.Target = TargetStrategy.SingleA;
        }));
        var tags = h.AddTags(50);

        h.Sim.EnterField(tags);
        await h.RoundsAsync(1);
        h.Sim.LeaveField(tags);
        await h.RoundsAsync(5);

        var report = Assert.Single(h.Closed);
        output.WriteLine($"lidas {report.DistinctTags}/50, {report.Verdict}: {string.Join(" | ", report.Issues)}");
        Assert.Equal(PassageVerdict.NaoConvergiu, report.Verdict);
        Assert.NotEmpty(report.Issues);
    }

    [Fact]
    public async Task Queda_do_leitor_durante_a_passagem_marca_dados_incompletos()
    {
        await using var h = await Harness.CreateAsync(seed: 9, Inventory());
        var tags = h.AddTags(50);

        h.Sim.EnterField(tags);
        await h.RoundsAsync(3);
        h.Sim.RaiseError("Conexão com o leitor perdida; tentando reconectar.");
        await h.RoundsAsync(3);
        h.Sim.LeaveField(tags);
        await h.RoundsAsync(5);

        var report = Assert.Single(h.Closed);
        Assert.Equal(PassageVerdict.DadosIncompletos, report.Verdict);
    }

    /// <summary>
    /// Monte Carlo adverso: 200 passagens com 50–80 tags, 10–30% delas quase invisíveis (0,1–5% de chance de leitura
    /// por slot: orientação ruim, metal, líquido) e tempo no campo de 1 a 4 rodadas. Quase todas perdem tag: isso é
    /// física, não software. Exigido: a perda é sinalizada (veredito diferente de CONFIÁVEL) em pelo menos 98% dos casos.
    /// O resíduo é o caso indetectável sem referência externa: um grupo de tags que nunca respondeu enquanto todas as
    /// lidas responderam em todas as rodadas (população lida perfeitamente homogênea).
    /// </summary>
    [Fact]
    public async Task Monte_Carlo_adverso_confiavel_nunca_esconde_tag_perdida()
    {
        var random = new Random(2026);
        var stats = new MonteCarloStats();
        for (var run = 0; run < 200; run++)
        {
            await using var h = await Harness.CreateAsync(seed: random.Next(), Inventory());
            var count = random.Next(50, 81);
            var tags = h.AddTags(count, weakEvery: random.Next(3, 11), weakProbability: 0.001 + random.NextDouble() * 0.05);
            await h.PassAsync(tags, dwellRounds: random.Next(1, 5));
            stats.Add(Assert.Single(h.Closed), count);
        }

        output.WriteLine(stats.ToString());
        Assert.True(stats.Missed > 100, "Cenário adverso deveria perder tags na maioria das passagens.");
        Assert.True(stats.MissedFlagged >= stats.Missed * 0.98, $"Só {stats.MissedFlagged} de {stats.Missed} perdas sinalizadas.");
        Assert.All(stats.FalseConfidenceReports, r => Assert.All(r.Tags, t => Assert.Equal(r.Rounds, t.Rounds)));
    }

    /// <summary>
    /// Monte Carlo em condição normal de portal: 200 passagens com 50–100 tags, 20% com leitura fraca (30–70% por slot)
    /// e 4 a 8 rodadas no campo (2–4 s). Exigido: todas as tags lidas em todas e CONFIÁVEL em pelo menos 97%
    /// (o resto pode sair ATENÇÃO/NÃO CONVERGIU: excesso de cautela, nunca falso positivo).
    /// </summary>
    [Fact]
    public async Task Monte_Carlo_portal_normal_le_todas_e_confirma()
    {
        var random = new Random(4242);
        var stats = new MonteCarloStats();
        for (var run = 0; run < 200; run++)
        {
            await using var h = await Harness.CreateAsync(seed: random.Next(), Inventory());
            var count = random.Next(50, 101);
            var tags = h.AddTags(count, weakEvery: 5, weakProbability: 0.3 + random.NextDouble() * 0.4);
            await h.PassAsync(tags, dwellRounds: random.Next(4, 9));
            stats.Add(Assert.Single(h.Closed), count);
        }

        output.WriteLine(stats.ToString());
        Assert.Equal(200, stats.Complete);
        Assert.True(stats.Confiavel >= 194, $"Só {stats.Confiavel} de 200 passagens completas confirmadas.");
    }

    private sealed class MonteCarloStats
    {
        public int Runs { get; private set; }
        public int Complete { get; private set; }
        public int Confiavel { get; private set; }
        public int Missed { get; private set; }
        public int MissedFlagged { get; private set; }
        public int FalseConfidence { get; private set; }
        public List<string> Details { get; } = [];
        public List<PassageSnapshot> FalseConfidenceReports { get; } = [];
        public Dictionary<PassageVerdict, int> Verdicts { get; } = [];

        public void Add(PassageSnapshot report, int count)
        {
            Runs++;
            var allRead = report.DistinctTags == count;
            Complete += allRead ? 1 : 0;
            Confiavel += report.Verdict == PassageVerdict.Confiavel ? 1 : 0;
            Verdicts[report.Verdict] = Verdicts.GetValueOrDefault(report.Verdict) + 1;
            if (allRead && report.Verdict != PassageVerdict.Confiavel && Details.Count < 5)
                Details.Add($"completa sem confirmação: {report.Verdict} rodadas {report.Rounds} estáveis {report.StableRounds} " + string.Join(" | ", report.Issues));
            if (allRead)
                return;
            Missed++;
            if (report.Verdict == PassageVerdict.Confiavel)
            {
                FalseConfidence++;
                FalseConfidenceReports.Add(report);
                Details.Add($"{report.DistinctTags}/{count} rodadas {report.Rounds} estáveis {report.StableRounds} Chao2 {report.EstimatedMissed:0.00} " +
                    string.Join(",", report.Tags.GroupBy(t => t.Rounds).OrderBy(g => g.Key).Select(g => $"{g.Key}r:{g.Count()}")));
            }
            else
                MissedFlagged++;
        }

        public override string ToString() =>
            $"{Runs} passagens: {Complete} completas, {Confiavel} CONFIÁVEL, {Missed} com tag perdida " +
            $"({MissedFlagged} sinalizadas), {FalseConfidence} falso CONFIÁVEL; " +
            string.Join(", ", Verdicts.Select(v => $"{v.Key} {v.Value}")) + string.Concat(Details.Select(d => "\n  " + d));
    }

    [Theory]
    [InlineData(0, 4, 4)]
    [InlineData(1, 4, 2)]
    [InlineData(16, 4, 4)]
    [InlineData(17, 4, 5)]
    [InlineData(50, 4, 6)]
    [InlineData(64, 4, 6)]
    [InlineData(500, 4, 8)]
    public void Q_dinamico_segue_log2_da_populacao(int responding, int initial, int expected)
    {
        Assert.Equal(expected, PassageMonitor.NextQ(responding, initial, minQ: 2, maxQ: 8));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(FakeTimeProvider time, Gen2PopulationSimulator sim, InMemoryInventoryRepository repository,
            InventoryPipeline pipeline, PassageMonitor monitor, TimeSpan dwell)
        {
            Time = time;
            Sim = sim;
            Repository = repository;
            Pipeline = pipeline;
            Monitor = monitor;
            Dwell = dwell;
            monitor.PassageClosed += (_, s) => Closed.Add(s);
        }

        public FakeTimeProvider Time { get; }
        public Gen2PopulationSimulator Sim { get; }
        public InMemoryInventoryRepository Repository { get; }
        public InventoryPipeline Pipeline { get; }
        public PassageMonitor Monitor { get; }
        public TimeSpan Dwell { get; }
        public List<PassageSnapshot> Closed { get; } = [];

        public static async Task<Harness> CreateAsync(int seed, InventoryOptions inventory)
        {
            var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
            var initial = new InventoryParameters(inventory.Session, InventoryTarget.A, inventory.InitialQ, false);
            var sim = new Gen2PopulationSimulator(time, seed, initial);
            var repository = new InMemoryInventoryRepository();
            var pipeline = new InventoryPipeline(sim, repository, new PipelineOptions
            {
                DedupWindowSeconds = 5,
                MinRssi = -75,
                WorkerCount = 2,
                QueueCapacity = 100_000,
                BatchSize = 200,
                FlushIntervalMs = 20,
                ShutdownTimeoutMs = 5_000,
            });
            var monitor = new PassageMonitor(pipeline, sim, inventory, Passage(), minRssi: -75, timeProvider: time);
            var h = new Harness(time, sim, repository, pipeline, monitor, TimeSpan.FromMilliseconds(inventory.TargetDwellMs));
            await pipeline.StartAsync().WithTimeout();
            await pipeline.SetModeAsync(OperationMode.Entrada).WithTimeout();
            return h;
        }

        public List<Gen2PopulationSimulator.SimTag> AddTags(int count, int weakEvery = 0, double weakProbability = 0.3) =>
            Enumerable.Range(0, count)
                .Select(i => weakEvery > 0 && i % weakEvery == 0
                    ? Sim.Add($"E2801170000002{i:X10}", weakProbability, rssi: -68)
                    : Sim.Add($"E2801170000002{i:X10}"))
                .ToList();

        /// <summary>Tags entram, ficam <paramref name="dwellRounds"/> rodadas, saem; mais 5 rodadas para o silêncio fechar.</summary>
        public async Task PassAsync(IReadOnlyCollection<Gen2PopulationSimulator.SimTag> tags, int dwellRounds)
        {
            Sim.EnterField(tags);
            await RoundsAsync(dwellRounds);
            Sim.LeaveField(tags);
            await RoundsAsync(5);
        }

        public async Task RoundsAsync(int rounds)
        {
            for (var i = 0; i < rounds; i++)
            {
                Sim.Run(Dwell);
                await Monitor.TickAsync().WithTimeout();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Monitor.DisposeAsync();
            await Pipeline.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
