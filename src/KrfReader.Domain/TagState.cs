namespace KrfReader.Domain;

/// <summary>Projeção do estado atual de uma tag (tabela <c>Tags</c>).</summary>
public sealed record TagState(
    string Epc,
    TagStatus Status,
    DateTime UltimaLeituraUtc,
    DateTime UltimaAtualizacaoStatusUtc);
