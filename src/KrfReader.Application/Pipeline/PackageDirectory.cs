using KrfReader.Domain;

namespace KrfReader.Application.Pipeline;

/// <summary>
/// Mapa EPC → embalagem em memória, usado pela passagem para contar ITENS (embalagem conta uma vez, qualquer que seja
/// a etiqueta lida). O banco continua sendo a fonte da verdade da movimentação; este mapa só agrupa o veredito.
/// Recarregado pela UI ao abrir o banco e depois de cada alteração no cadastro de embalagens. Thread-safe: troca
/// atômica de referência, leitura sem lock.
/// </summary>
public sealed class PackageDirectory
{
    private volatile IReadOnlyDictionary<string, PackageRef> _map = new Dictionary<string, PackageRef>(StringComparer.Ordinal);

    public int Count => _map.Count;

    public void Replace(IReadOnlyDictionary<string, PackageRef> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        _map = map;
    }

    /// <summary>Embalagem do EPC (normalizado); null = tag avulsa.</summary>
    public PackageRef? Find(string epc) => _map.TryGetValue(epc, out var package) ? package : null;
}
