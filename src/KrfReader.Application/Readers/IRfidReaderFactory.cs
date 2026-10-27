using KrfReader.Application.Configuration;

namespace KrfReader.Application.Readers;

/// <summary>Cria o leitor a partir da configuração (Serial / Tcp).</summary>
public interface IRfidReaderFactory
{
    IRfidReader Create(ReaderOptions options);
}
