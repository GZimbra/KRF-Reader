using System.Net.Sockets;
using KrfReader.Application.Configuration;
using KrfReader.Application.Readers;
using Microsoft.Extensions.Logging;

namespace KrfReader.Infrastructure.Readers;

/// <summary>Leitor genérico em TCP/IP (leitores de rede, conversores serial-Ethernet) com protocolo plugável.</summary>
public sealed class GenericTcpReader(ReaderOptions options, IReaderProtocol protocol, ILogger? logger = null)
    : StreamRfidReaderBase(options, protocol, logger)
{
    private TcpReaderOptions Tcp => Options.Tcp ?? new TcpReaderOptions();

    public override string Name => $"TCP {Tcp.Host}:{Tcp.Port}";

    protected override async Task<Stream> OpenStreamAsync(CancellationToken cancellationToken)
    {
        var settings = Tcp;
        var client = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (settings.ConnectTimeoutMs > 0)
            timeout.CancelAfter(settings.ConnectTimeoutMs);

        try
        {
            await client.ConnectAsync(settings.Host, settings.Port, timeout.Token).ConfigureAwait(false);
            return new OwnedTransportStream(client.GetStream(), client);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException($"Tempo esgotado ({settings.ConnectTimeoutMs} ms) conectando a {settings.Host}:{settings.Port}.");
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
