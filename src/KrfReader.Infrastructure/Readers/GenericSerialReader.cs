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
                ReadTimeout = SerialPortStream.PollTimeoutMs,
                WriteTimeout = 2000,
                Handshake = Handshake.None,
            };
            try
            {
                port.Open();
                return new SerialPortStream(port);
            }
            catch
            {
                port.Dispose();
                throw;
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Stream sobre <see cref="SerialPort"/> com leitura BLOQUEANTE (timeout curto, em loop) numa thread do pool.
    /// <para>
    /// O <c>BaseStream.ReadAsync</c> do SerialPort no Windows usa I/O sobreposto preso à thread que o emitiu; quando
    /// essa thread do pool é recolhida o Windows cancela a leitura (erro 995, "I/O operation has been aborted") e o
    /// leitor entrava em loop de reconexão. Fechar a porta (Dispose) interrompe a leitura pendente.
    /// </para>
    /// </summary>
    private sealed class SerialPortStream(SerialPort port) : Stream
    {
        public const int PollTimeoutMs = 250;

        private int _disposed;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            while (true)
            {
                if (Volatile.Read(ref _disposed) != 0)
                    throw new ObjectDisposedException(nameof(SerialPortStream));
                try
                {
                    return port.Read(buffer, offset, count);
                }
                catch (TimeoutException)
                {
                    // Sem dados no intervalo: continua esperando.
                }
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return new ValueTask<int>(Task.Run(() =>
            {
                var chunk = new byte[buffer.Length];
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Volatile.Read(ref _disposed) != 0)
                        throw new ObjectDisposedException(nameof(SerialPortStream));
                    try
                    {
                        var read = port.Read(chunk, 0, chunk.Length);
                        chunk.AsSpan(0, read).CopyTo(buffer.Span);
                        return read;
                    }
                    catch (TimeoutException)
                    {
                    }
                }
            }, cancellationToken));
        }

        public override void Write(byte[] buffer, int offset, int count) => port.Write(buffer, offset, count);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            port.Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var data = buffer.ToArray();
            port.Write(data, 0, data.Length);
            return ValueTask.CompletedTask;
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
                port.Dispose();
            base.Dispose(disposing);
        }
    }
}
