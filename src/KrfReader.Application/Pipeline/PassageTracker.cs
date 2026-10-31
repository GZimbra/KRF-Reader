using KrfReader.Application.Configuration;
using KrfReader.Domain;

namespace KrfReader.Application.Pipeline;

/// <summary>
/// Sessão de passagem: agrupa as leituras de uma carga atravessando o campo da antena e mede se a descoberta
/// de EPCs convergiu.
/// <para>
/// O tempo é dividido em rodadas (uma meia-volta de target A/B, ver <see cref="PassageMonitor"/>). Em cada rodada
/// a população presente é inventariada de novo; uma tag que existe e está no campo tende a aparecer em quase
/// todas. Duas medidas saem disso:
/// <list type="bullet">
/// <item><description>Convergência: rodadas seguidas, com leitura, sem nenhum EPC novo. Enquanto aparecem EPCs
/// novos, ainda há tag disputando slot; se a carga sai nesse estado, provavelmente ficou tag para trás.</description></item>
/// <item><description>Estimador Chao2 (captura e recaptura por incidência): com Q1 tags vistas em uma só rodada e Q2 em
/// duas, as não vistas são ≈ ((m−1)/m)·Q1(Q1−1)/(2(Q2+1)). Muitas tags "de uma rodada só" indicam população
/// com leitura marginal, e portanto tags que nem chegaram a ser lidas.</description></item>
/// </list>
/// </para>
/// <para>
/// Unidade de contagem = ITEM: embalagem cadastrada com várias etiquetas (<see cref="PackageDirectory"/>) conta uma vez,
/// e uma rodada "viu" a embalagem se viu qualquer etiqueta dela. Convergência e estimador usam itens: a segunda face de
/// uma caixa já vista não é tag nova, e a face blindada que nunca respondeu não é item perdido. A taxa de resposta por
/// EPC (base do Q dinâmico) continua por etiqueta, porque é ela que disputa slot no ar.
/// </para>
/// <para>Thread-safe: <see cref="OnRead"/> vem da thread do leitor; o resto, do timer do monitor.</para>
/// </summary>
public sealed class PassageTracker
{
    private readonly object _gate = new();
    private readonly PassageOptions _options;
    private readonly TimeSpan _silence;
    private readonly PackageDirectory? _packages;

    private Passage? _current;
    private readonly Func<long> _nextId;
    private long _localId;
    private double _minRssi;

    /// <param name="nextId">Numeração compartilhada entre trackers (uma passagem por doca no modo POR ANTENA); null = própria.</param>
    public PassageTracker(PassageOptions options, double minRssi, PackageDirectory? packages = null, Func<long>? nextId = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.SilenceMs, 1, nameof(options.SilenceMs));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.StableRounds, 1, nameof(options.StableRounds));
        _options = options;
        _silence = TimeSpan.FromMilliseconds(options.SilenceMs);
        _minRssi = minRssi;
        _packages = packages;
        _nextId = nextId ?? (() => ++_localId);
    }

    public bool IsActive
    {
        get
        {
            lock (_gate)
                return _current is not null;
        }
    }

    /// <summary>RSSI mínimo do filtro, referência para marcar tag marginal.</summary>
    public void SetMinRssi(double minRssi)
    {
        lock (_gate)
            _minRssi = minRssi;
    }

    /// <summary>Leitura aceita (EPC normalizado, já passou no filtro). Abre uma passagem se não houver.</summary>
    public void OnRead(in TagRead read, OperationMode mode)
    {
        lock (_gate)
        {
            var p = _current ??= new Passage(_nextId(), mode, read.TimestampUtc);
            p.Add(read, _packages);
        }
    }

    /// <summary>Problema de integridade (leituras perdidas fora do RF). Só conta com passagem aberta.</summary>
    public void ReportDataLoss(string reason)
    {
        lock (_gate)
            _current?.DataLoss.Add(reason);
    }

    /// <summary>Quadros corrompidos no enlace durante a passagem (uma leitura pode ter se perdido e não reaparecido).</summary>
    public void ReportInvalidFrames(long count)
    {
        if (count <= 0)
            return;
        lock (_gate)
        {
            if (_current is { } p)
                p.InvalidFrames += count;
        }
    }

    /// <summary>
    /// Fecha a rodada corrente. Se não houve leitura por <see cref="PassageOptions.SilenceMs"/>, fecha a passagem.
    /// </summary>
    /// <returns>Retrato da passagem ao vivo (ou null sem passagem) e o relatório quando ela fechou.</returns>
    public (PassageSnapshot? Live, PassageSnapshot? Closed) CompleteRound(DateTime nowUtc)
    {
        lock (_gate)
        {
            if (_current is not { } p)
                return (null, null);

            p.CompleteRound();
            if (nowUtc - p.LastReadUtc >= _silence)
            {
                var closed = CloseCore(nowUtc, "silêncio no campo");
                return (null, closed);
            }

            return (Build(p, PassageState.Active, null, null), null);
        }
    }

    /// <summary>Fecha a passagem aberta (PARAR, troca de modo, troca de leitor). Null se não havia.</summary>
    public PassageSnapshot? Close(DateTime nowUtc, string reason)
    {
        lock (_gate)
        {
            if (_current is not { } p)
                return null;
            p.CompleteRound();
            return CloseCore(nowUtc, reason);
        }
    }

    public PassageSnapshot? Snapshot()
    {
        lock (_gate)
            return _current is { } p ? Build(p, PassageState.Active, null, null) : null;
    }

    private PassageSnapshot CloseCore(DateTime nowUtc, string reason)
    {
        var p = _current!;
        _current = null;
        var ended = p.LastReadUtc > nowUtc ? p.LastReadUtc : nowUtc;
        return Build(p, PassageState.Closed, ended, reason);
    }

    private PassageSnapshot Build(Passage p, PassageState state, DateTime? endedUtc, string? reason)
    {
        var marginalLimit = _minRssi + _options.MarginalRssiMarginDb;
        var tags = new List<PassageTag>(p.Tags.Count);
        var marginal = 0;
        foreach (var (epc, t) in p.Tags)
        {
            // A rodada corrente (ainda aberta) conta como incidência: a tag foi vista nela.
            var rounds = t.Rounds + (t.InOpenRound ? 1 : 0);
            var isMarginal = rounds <= 1 || t.MaxRssi < marginalLimit;
            if (isMarginal)
                marginal++;
            tags.Add(new PassageTag(epc, t.Reads, rounds, t.MaxRssi, t.SumRssi / t.Reads, t.FirstUtc, t.LastUtc, isMarginal,
                t.Package?.Codigo));
        }

        // Estimador por item: embalagem vista em 2 rodadas por faces diferentes tem incidência 2, não 1+1.
        var q1 = 0;
        var q2 = 0;
        var packages = 0;
        var partial = 0;
        foreach (var item in p.Items.Values)
        {
            var rounds = item.Rounds + (item.InOpenRound ? 1 : 0);
            if (rounds == 1)
                q1++;
            else if (rounds == 2)
                q2++;
            if (item.Package is { } pkg)
            {
                packages++;
                if (item.Epcs < pkg.Etiquetas)
                    partial++;
            }
        }

        tags.Sort(static (a, b) =>
        {
            var byMarginal = b.IsMarginal.CompareTo(a.IsMarginal);
            return byMarginal != 0 ? byMarginal : string.CompareOrdinal(a.Epc, b.Epc);
        });

        var sampled = p.Rounds + (p.OpenRound.Count > 0 ? 1 : 0);
        var estimatedMissed = EstimateMissed(q1, q2, sampled);
        var stable = p.StableRounds;

        var issues = new List<string>();
        foreach (var loss in p.DataLoss.Distinct())
            issues.Add(loss);

        PassageVerdict verdict;
        if (p.DataLoss.Count > 0)
        {
            verdict = PassageVerdict.DadosIncompletos;
        }
        else if (stable < _options.StableRounds)
        {
            verdict = PassageVerdict.NaoConvergiu;
            issues.Add($"Descoberta não convergiu: {stable} de {_options.StableRounds} rodadas sem item novo.");
        }
        else if (q1 > 0 || estimatedMissed > _options.MaxEstimatedMissed || p.InvalidFrames > 0)
        {
            // Tag vista em uma rodada só = população com leitura marginal: outra igual pode não ter respondido nenhuma vez.
            verdict = PassageVerdict.Atencao;
            if (q1 > 0)
                issues.Add($"{q1} item(ns) lido(s) em uma única rodada: há leitura marginal; estimativa de itens não lidos ~{estimatedMissed:0.#}.");
            if (p.InvalidFrames > 0)
                issues.Add($"{p.InvalidFrames} quadro(s) corrompido(s) no enlace serial durante a passagem.");
        }
        else
        {
            verdict = PassageVerdict.Confiavel;
        }

        return new PassageSnapshot(
            p.Id,
            p.Mode,
            state,
            p.StartedUtc,
            endedUtc,
            reason,
            p.Tags.Count,
            p.TotalReads,
            p.Rounds,
            stable,
            p.NewInLastRound,
            p.RespondingLastRound,
            estimatedMissed,
            marginal,
            verdict,
            issues,
            tags)
        {
            DistinctItems = p.Items.Count,
            Packages = packages,
            PartialPackages = partial,
        };
    }

    /// <summary>
    /// Chao2 com correção de viés (Chao, 1987; Colwell &amp; Coddington, 1994) sobre incidência em <paramref name="rounds"/>
    /// rodadas: f0 ≈ ((m−1)/m)·Q1(Q1−1)/(2(Q2+1)). Com uma rodada só não há recaptura: todo Q1 conta como incerto.
    /// </summary>
    public static double EstimateMissed(int q1, int q2, int rounds)
    {
        if (q1 <= 0 || rounds <= 0)
            return 0;
        if (rounds == 1)
            return q1;
        var m = (double)rounds;
        return (m - 1) / m * q1 * (q1 - 1) / (2.0 * (q2 + 1));
    }

    private sealed class Passage(long id, OperationMode mode, DateTime startedUtc)
    {
        public long Id { get; } = id;
        public OperationMode Mode { get; } = mode;
        public DateTime StartedUtc { get; } = startedUtc;
        public DateTime LastReadUtc { get; private set; } = startedUtc;
        public Dictionary<string, TagStats> Tags { get; } = new(StringComparer.Ordinal);

        /// <summary>Itens por chave: "P:{id}" para embalagem, o EPC para tag avulsa.</summary>
        public Dictionary<string, ItemStats> Items { get; } = new(StringComparer.Ordinal);
        public HashSet<string> OpenRound { get; } = new(StringComparer.Ordinal);
        public HashSet<ItemStats> OpenRoundItems { get; } = [];
        public List<string> DataLoss { get; } = [];
        public long TotalReads { get; private set; }
        public long InvalidFrames { get; set; }

        /// <summary>Rodadas fechadas com ao menos uma leitura.</summary>
        public int Rounds { get; private set; }

        public int NewInLastRound { get; private set; }
        public int RespondingLastRound { get; private set; }

        private int _newInOpenRound;
        private int _lastNewRound;

        /// <summary>Rodadas com leitura desde a última com item novo. Rodada aberta com item novo zera.</summary>
        public int StableRounds => _newInOpenRound > 0 ? 0 : Rounds - _lastNewRound;

        public void Add(in TagRead read, PackageDirectory? packages)
        {
            TotalReads++;
            if (read.TimestampUtc > LastReadUtc)
                LastReadUtc = read.TimestampUtc;

            if (!Tags.TryGetValue(read.Epc, out var t))
            {
                // Embalagem resolvida uma vez por EPC: mudança no cadastro durante a passagem não reagrupa o que já foi visto.
                var package = packages?.Find(read.Epc);
                var key = package is null ? read.Epc : "P:" + package.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!Items.TryGetValue(key, out var item))
                {
                    item = new ItemStats(package);
                    Items.Add(key, item);
                    _newInOpenRound++;
                }
                item.Epcs++;
                t = new TagStats(read.TimestampUtc, item);
                Tags.Add(read.Epc, t);
            }

            t.Add(read);
            if (OpenRound.Add(read.Epc))
                t.InOpenRound = true;
            if (OpenRoundItems.Add(t.Item))
                t.Item.InOpenRound = true;
        }

        public void CompleteRound()
        {
            if (OpenRound.Count == 0)
                return;

            Rounds++;
            RespondingLastRound = OpenRound.Count;
            NewInLastRound = _newInOpenRound;
            if (_newInOpenRound > 0)
                _lastNewRound = Rounds;
            _newInOpenRound = 0;

            foreach (var epc in OpenRound)
            {
                var t = Tags[epc];
                t.Rounds++;
                t.InOpenRound = false;
            }

            foreach (var item in OpenRoundItems)
            {
                item.Rounds++;
                item.InOpenRound = false;
            }

            OpenRound.Clear();
            OpenRoundItems.Clear();
        }
    }

    /// <summary>Item de estoque na passagem: embalagem (várias etiquetas) ou tag avulsa.</summary>
    private sealed class ItemStats(PackageRef? package)
    {
        public PackageRef? Package { get; } = package;

        /// <summary>Etiquetas distintas do item lidas na passagem.</summary>
        public int Epcs { get; set; }
        public int Rounds { get; set; }
        public bool InOpenRound { get; set; }
    }

    private sealed class TagStats(DateTime firstUtc, ItemStats item)
    {
        public DateTime FirstUtc { get; } = firstUtc;
        public ItemStats Item { get; } = item;
        public PackageRef? Package => Item.Package;
        public DateTime LastUtc { get; private set; } = firstUtc;
        public int Reads { get; private set; }
        public int Rounds { get; set; }
        public bool InOpenRound { get; set; }
        public double MaxRssi { get; private set; } = double.NegativeInfinity;
        public double SumRssi { get; private set; }

        public void Add(in TagRead read)
        {
            Reads++;
            SumRssi += read.Rssi;
            if (read.Rssi > MaxRssi)
                MaxRssi = read.Rssi;
            if (read.TimestampUtc > LastUtc)
                LastUtc = read.TimestampUtc;
        }
    }
}
