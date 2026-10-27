namespace KrfReader.Domain;

/// <summary>Projeção do estado atual de uma tag (tabela <c>Tags</c>).</summary>
/// <param name="Descricao">Descrição do cadastro (<c>TagCadastro</c>); null = tag não cadastrada.</param>
/// <param name="Sku">SKU do cadastro; null = não cadastrada ou sem SKU.</param>
public sealed record TagState(
    string Epc,
    TagStatus Status,
    DateTime UltimaLeituraUtc,
    DateTime UltimaAtualizacaoStatusUtc,
    string? Descricao = null,
    string? Sku = null)
{
    public bool IsRegistered => Descricao is not null;
}
