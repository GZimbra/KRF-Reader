using KrfReader.Application.Configuration;
using KrfReader.Application.Readers;
using KrfReader.Infrastructure.Readers;
using KrfReader.Infrastructure.Readers.Protocols;

namespace KrfReader.Tests.Readers;

/// <summary>Transporte + protocolo R200: perfil de anticolisão na conexão e troca de parâmetros em tempo real.</summary>
public sealed class InventoryTuningTransportTests
{
    private static ReaderOptions Options(string prefix = "") => new()
    {
        Protocol = "R200",
        PowerDbm = 20,
        Inventory = new InventoryOptions { Session = GenSession.S2, InitialQ = 5, SelectEpcPrefix = prefix, CommandGapMs = 0 },
    };

    [Fact]
    public async Task Conexao_envia_perfil_e_troca_de_parametros_para_configura_e_reinicia()
    {
        var stream = new RecordingStream();
        await using var reader = new StreamReaderUnderTest(Options(), stream);

        await reader.ConnectAsync().WithTimeout();
        var connectCommands = stream.Commands();
        Assert.Equal([0x28, 0x07, 0xA9, 0xAD, 0xB6, 0x12, 0x0E], connectCommands.Select(c => c[2]).ToArray());
        Assert.Equal(new InventoryParameters(GenSession.S2, InventoryTarget.A, 5, false), reader.CurrentParameters);
        Assert.True(reader.IsTuningSupported);

        await reader.StartInventoryAsync().WithTimeout();
        stream.Clear();
        var next = new InventoryParameters(GenSession.S2, InventoryTarget.B, 6, false);
        await reader.ApplyInventoryParametersAsync(next).WithTimeout();

        Assert.Equal([0x28, 0x0E, 0x27], stream.Commands().Select(c => c[2]).ToArray());
        Assert.Equal(next, reader.CurrentParameters);

        await reader.StopInventoryAsync().WithTimeout();
        stream.Clear();
        await reader.ApplyInventoryParametersAsync(next with { Q = 7 }).WithTimeout();
        Assert.Equal([0x0E], stream.Commands().Select(c => c[2]).ToArray());
    }

    [Fact]
    public async Task Prefixo_de_select_e_mantido_nas_trocas_de_parametro()
    {
        var stream = new RecordingStream();
        await using var reader = new StreamReaderUnderTest(Options("E280"), stream);

        await reader.ConnectAsync().WithTimeout();
        Assert.Contains(stream.Commands(), c => c[2] == 0x0C);
        Assert.True(reader.CurrentParameters.UseSelect);

        await reader.ApplyInventoryParametersAsync(new InventoryParameters(GenSession.S2, InventoryTarget.B, 6, UseSelect: false)).WithTimeout();

        Assert.True(reader.CurrentParameters.UseSelect);
        var query = stream.Commands().Last(c => c[2] == 0x0E);
        Assert.Equal(0b11, (query[5] >> 2) & 0b11);
    }

    private sealed class StreamReaderUnderTest(ReaderOptions options, Stream stream)
        : StreamRfidReaderBase(options, M100Protocol.CreateR200())
    {
        public override string Name => "teste";

        protected override Task<Stream> OpenStreamAsync(CancellationToken cancellationToken) => Task.FromResult(stream);
    }

    /// <summary>Grava cada Write como um comando; Read bloqueia até o stream ser descartado.</summary>
    private sealed class RecordingStream : Stream
    {
        private readonly object _gate = new();
        private readonly List<byte[]> _writes = [];
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<byte[]> Commands()
        {
            lock (_gate)
                return [.. _writes];
        }

        public void Clear()
        {
            lock (_gate)
                _writes.Clear();
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _closed.Task.WaitAsync(cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            lock (_gate)
                _writes.Add(buffer.ToArray());
            return ValueTask.CompletedTask;
        }

        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().Wait();
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _closed.TrySetResult();
            base.Dispose(disposing);
        }
    }
}
