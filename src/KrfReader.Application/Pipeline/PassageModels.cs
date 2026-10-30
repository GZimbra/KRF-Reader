using KrfReader.Application.Readers;
using KrfReader.Domain;

namespace KrfReader.Application.Pipeline;

public enum PassageState
{
    /// <summary>Tags no campo, rodadas em andamento.</summary>
    Active,

    /// <summary>Fechada por silêncio, PARAR ou troca de modo.</summary>
    Closed,
}

/// <summary>Veredito da passagem, do melhor para o pior.</summary>
public enum PassageVerdict
{
    /// <summary>Descoberta convergiu (N rodadas sem EPC novo), estimador sem faltas, enlace íntegro.</summary>
    Confiavel,

    /// <summary>Convergiu, mas há sinal de tag fraca: estimador acima do limite ou quadros corrompidos no enlace.</summary>
    Atencao,

    /// <summary>A carga saiu do campo enquanto EPCs novos ainda apareciam: provável tag não lida. Repassar.</summary>
    NaoConvergiu,

    /// <summary>Leituras perdidas fora do RF (queda do leitor, fila cheia, falha de gravação). Repassar.</summary>
    DadosIncompletos,
}

/// <summary>Estatística de uma tag dentro da passagem.</summary>
/// <param name="Rounds">Rodadas distintas em que a tag respondeu (base do estimador).</param>
/// <param name="IsMarginal">Lida em uma única rodada ou com RSSI perto do limite do filtro.</param>
/// <param name="PackageCodigo">Embalagem da etiqueta; null = tag avulsa.</param>
public sealed record PassageTag(
    string Epc,
    int Reads,
    int Rounds,
    double MaxRssi,
    double AvgRssi,
    DateTime FirstSeenUtc,
    DateTime LastSeenUtc,
    bool IsMarginal,
    string? PackageCodigo = null);

/// <summary>Retrato da passagem (ao vivo ou fechada).</summary>
/// <param name="Rounds">Rodadas com ao menos uma leitura.</param>
/// <param name="StableRounds">Rodadas com leitura desde o último item novo (embalagem ou tag avulsa).</param>
/// <param name="NewInLastRound">Itens novos na última rodada fechada (taxa de descoberta).</param>
/// <param name="RespondingLastRound">EPCs distintos que responderam na última rodada (base do Q dinâmico).</param>
/// <param name="EstimatedMissed">Itens provavelmente não lidos, estimador Chao2 por incidência em rodadas.</param>
/// <param name="Issues">Motivos legíveis do veredito (vazio quando CONFIÁVEL).</param>
public sealed record PassageSnapshot(
    long Id,
    OperationMode? Mode,
    PassageState State,
    DateTime StartedUtc,
    DateTime? EndedUtc,
    string? CloseReason,
    int DistinctTags,
    long TotalReads,
    int Rounds,
    int StableRounds,
    int NewInLastRound,
    int RespondingLastRound,
    double EstimatedMissed,
    int MarginalTags,
    PassageVerdict Verdict,
    IReadOnlyList<string> Issues,
    IReadOnlyList<PassageTag> Tags)
{
    /// <summary>Itens distintos: embalagens + tags avulsas. É a contagem que o veredito avalia.</summary>
    public int DistinctItems { get; init; }

    /// <summary>Embalagens cadastradas detectadas (por qualquer etiqueta).</summary>
    public int Packages { get; init; }

    /// <summary>Embalagens detectadas com alguma etiqueta não lida (face blindada ou etiqueta com defeito). Não muda o veredito.</summary>
    public int PartialPackages { get; init; }

    public TimeSpan Duration => (EndedUtc ?? Tags.Select(t => t.LastSeenUtc).DefaultIfEmpty(StartedUtc).Max()) - StartedUtc;
}

/// <summary>Estado do controlador de anticolisão, para a tela.</summary>
public sealed record InventoryControlState(bool TuningSupported, InventoryParameters Parameters, long EmptyRounds, long InvalidFrames);
