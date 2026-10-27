namespace KrfReader.Application.Configuration;

/// <summary>Seção <c>Passage</c> do appsettings.json: sessão de passagem e critério de confiança.</summary>
public sealed class PassageOptions
{
    public const string SectionName = "Passage";

    /// <summary>Sem nenhuma leitura por este tempo = a carga saiu do campo e a passagem fecha.</summary>
    public int SilenceMs { get; set; } = 2500;

    /// <summary>Rodadas com leitura e sem EPC novo exigidas para considerar a descoberta convergida.</summary>
    public int StableRounds { get; set; } = 3;

    /// <summary>Máximo de tags não lidas estimadas (Chao2) para a passagem ser CONFIÁVEL.</summary>
    public double MaxEstimatedMissed { get; set; } = 0.5;

    /// <summary>Tag com RSSI máximo até esta margem acima do RSSI mínimo do filtro é marcada como marginal.</summary>
    public double MarginalRssiMarginDb { get; set; } = 6;
}
