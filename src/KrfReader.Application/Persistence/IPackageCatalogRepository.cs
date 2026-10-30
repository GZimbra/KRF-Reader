using KrfReader.Domain;

namespace KrfReader.Application.Persistence;

/// <summary>
/// Cadastro de embalagens com várias etiquetas (tabelas <c>Embalagens</c> e <c>EmbalagemEtiquetas</c>).
/// Toda entrada passa por <see cref="PackageRules.Validate"/> (lança <see cref="ArgumentException"/>).
/// <para>
/// Um EPC pertence a no máximo uma embalagem e não pode estar, ao mesmo tempo, no cadastro de tag avulsa
/// (<c>TagCadastro</c>): o mesmo EPC com dois significados tornaria o item da movimentação ambíguo.
/// </para>
/// <para>
/// Ao vincular etiquetas a uma embalagem ainda sem estado, o estado é herdado da etiqueta que mudou de status mais
/// recentemente (caixa já lida como tag avulsa continua DENTRO/FORA, sem virar SAÍDA SEM ENTRADA).
/// </para>
/// </summary>
public interface IPackageCatalogRepository
{
    Task<PackageSaveOutcome> AddPackageAsync(PackageInput input, CancellationToken cancellationToken = default);

    /// <summary>Atualiza dados e SUBSTITUI a lista de etiquetas. Etiqueta removida volta a ser tag avulsa.</summary>
    Task<PackageSaveOutcome> UpdatePackageAsync(long id, PackageInput input, CancellationToken cancellationToken = default);

    /// <summary>Remove embalagem e vínculos. Estado das tags e histórico (Eventos.EmbalagemId) ficam.</summary>
    Task<bool> DeletePackageAsync(long id, CancellationToken cancellationToken = default);

    Task<RegisteredPackage?> GetPackageAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Embalagem que contém o EPC; null = EPC não vinculado.</summary>
    Task<RegisteredPackage?> FindPackageByEpcAsync(string epc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Busca parcial em código, descrição, tipo e EPC das etiquetas. Ordem: última detecção mais recente; nunca lidas
    /// no fim, por código.
    /// </summary>
    Task<IReadOnlyList<RegisteredPackage>> QueryPackagesAsync(
        string? term, RegisteredTagStatusFilter status = RegisteredTagStatusFilter.Todos, int limit = 500,
        CancellationToken cancellationToken = default);

    /// <summary>EPC → embalagem de todas as etiquetas vinculadas (agrupamento da passagem em memória).</summary>
    Task<IReadOnlyDictionary<string, PackageRef>> GetPackageEpcMapAsync(CancellationToken cancellationToken = default);
}

public enum PackageSaveStatus
{
    Ok,
    NaoEncontrada,
    CodigoDuplicado,
    EpcEmOutraEmbalagem,
    EpcCadastradoComoTag,
}

/// <summary>Resultado de gravar uma embalagem. <paramref name="Message"/> é legível para o operador.</summary>
public sealed record PackageSaveOutcome(PackageSaveStatus Status, long? Id, string Message)
{
    public bool Succeeded => Status == PackageSaveStatus.Ok;
}
