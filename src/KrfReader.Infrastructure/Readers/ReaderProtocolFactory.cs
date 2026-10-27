using KrfReader.Application.Readers;
using KrfReader.Infrastructure.Readers.Protocols;

namespace KrfReader.Infrastructure.Readers;

/// <summary>
/// Registro de protocolos por nome (case-insensitive). Thread-safe.
/// Para um protocolo proprietário sobre serial/TCP: implemente <see cref="IReaderProtocol"/> e chame
/// <see cref="Register"/> na composição (ex.: <c>factory.Register("Acme", () =&gt; new AcmeProtocol())</c>).
/// </summary>
public sealed class ReaderProtocolFactory : IReaderProtocolFactory
{
    private readonly Dictionary<string, Func<IReaderProtocol>> _factories = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public ReaderProtocolFactory()
    {
        Register(AsciiLineProtocol.ProtocolName, static () => new AsciiLineProtocol());
        Register(R200Protocol.ProtocolName, static () => new R200Protocol());
    }

    public IReadOnlyCollection<string> AvailableProtocols
    {
        get
        {
            lock (_sync)
                return _factories.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    /// <summary>Registra (ou substitui) um protocolo. A fábrica deve devolver uma instância NOVA a cada chamada.</summary>
    public void Register(string name, Func<IReaderProtocol> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);
        lock (_sync)
            _factories[name.Trim()] = factory;
    }

    public IReaderProtocol Create(string name)
    {
        Func<IReaderProtocol>? factory;
        lock (_sync)
            _factories.TryGetValue(name?.Trim() ?? string.Empty, out factory);

        if (factory is null)
        {
            throw new ArgumentException(
                $"Protocolo de leitor desconhecido: '{name}'. Disponíveis: {string.Join(", ", AvailableProtocols)}.",
                nameof(name));
        }

        return factory();
    }
}
