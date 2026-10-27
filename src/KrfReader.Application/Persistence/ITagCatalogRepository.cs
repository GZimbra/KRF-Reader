using KrfReader.Domain;

namespace KrfReader.Application.Persistence;

/// <summary>
/// Cadastro de tags (dado mestre). Separado de <see cref="IInventoryRepository"/>: cadastrar não lê nem altera
/// <c>Tags</c>/<c>Eventos</c>, e o pipeline de leitura não depende do cadastro.
/// Toda entrada passa por <see cref="TagRegistrationRules.Validate"/> (lança <see cref="ArgumentException"/>).
/// </summary>
public interface ITagCatalogRepository
{
    /// <summary>Insere. Retorna <c>false</c> se o EPC já está cadastrado (nada é alterado).</summary>
    Task<bool> AddRegistrationAsync(TagRegistrationInput input, CancellationToken cancellationToken = default);

    /// <summary>Atualiza descrição/SKU/observação. Retorna <c>false</c> se o EPC não está cadastrado.</summary>
    Task<bool> UpdateRegistrationAsync(TagRegistrationInput input, CancellationToken cancellationToken = default);

    /// <summary>Remove só o cadastro; estado e histórico da tag ficam. Retorna <c>false</c> se não existia.</summary>
    Task<bool> DeleteRegistrationAsync(string epc, CancellationToken cancellationToken = default);

    Task<TagRegistration?> GetRegistrationAsync(string epc, CancellationToken cancellationToken = default);

    /// <summary>Busca parcial case-insensitive em EPC, descrição e SKU; null = todos. Ordem: EPC.</summary>
    Task<IReadOnlyList<TagRegistration>> QueryRegistrationsAsync(string? term, int limit = 500, CancellationToken cancellationToken = default);
}
