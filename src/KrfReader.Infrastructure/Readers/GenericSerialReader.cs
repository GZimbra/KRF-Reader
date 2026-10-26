using System.IO.Ports;
using KrfReader.Application.Configuration;
using KrfReader.Application.Readers;
using Microsoft.Extensions.Logging;

namespace KrfReader.Infrastructure.Readers;

/// <summary>Leitor genérico em porta serial (RS-232/USB-serial) com protocolo plugável.</summary>
public sealed class GenericSerialReader(ReaderOptions options, IReaderProtocol protocol, ILogger? logger = null)
    : StreamRfidReaderBase(options, protocol, logger)
{
    private SerialReaderOptions Serial => Options.Serial ?? new SerialReaderOptions();

    public override string Name => $"Serial {Serial.PortName}@{Serial.BaudRate}";

    protected override Task<Stream> OpenStreamAsync(CancellationToken cancellationToken)
    {
        var settings = Serial;
        var parity = Enum.Parse<Parity>(settings.Parity, ignoreCase: true);
        var stopBits = Enum.Parse<StopBits>(settings.StopBits, ignoreCase: true);

        // SerialPort.Open é síncrono e pode demorar (driver USB); roda fora da thread chamadora.
        return Task.Run<Stream>(() =>
        {
            var port = new SerialPort(settings.PortName, settings.BaudRate, parity, settings.DataBits, stopBits)
            {
                ReadTimeout = SerialPort.InfiniteTimeout,
                WriteTimeout = 2000,
                Handshake = Handshake.None,
            };
            try
            {
                port.Open();
                return new OwnedTransportStream(port.BaseStream, port);
            }
            catch
            {
                port.Dispose();
                throw;
            }
        }, cancellationToken);
    }
}
