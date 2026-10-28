namespace KrfReader.Domain;

/// <summary>Projeção do estado atual de uma tag (tabela <c>Tags</c>).</summary>
/// <param name="Descricao">Descrição do cadastro (<c>TagCadastro</c>); null = tag não cadastrada.</param>
/// <param name="Sku">SKU do cadastro; null = não cadastrada ou sem SKU.</param>
/// <param name="UltimaAntenaId">Antena da última detecção; null = banco anterior à v0.5 sem leitura nova.</param>
/// <param name="UltimaRssi">RSSI da última detecção (dBm); null idem.</param>
public sealed record TagState(
    string Epc,
    TagStatus Status,
    DateTime UltimaLeituraUtc,
    DateTime UltimaAtualizacaoStatusUtc,
    string? Descricao = null,
    string? Sku = null,
    int? UltimaAntenaId = null,
    double? UltimaRssi = null)
{
    public bool IsRegistered => Descricao is not null;
}
