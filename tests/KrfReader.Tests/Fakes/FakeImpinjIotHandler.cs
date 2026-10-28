using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Threading.Channels;

namespace KrfReader.Tests.Fakes;

/// <summary>API REST falsa da Impinj IoT Device Interface (só testes). O fluxo NDJSON é alimentado pelo teste.</summary>
internal sealed class FakeImpinjIotHandler : HttpMessageHandler
{
    private Channel<byte[]> _stream = Channel.CreateUnbounded<byte[]>();

    public ConcurrentQueue<(string Method, string Path, string? Body, string? Auth)> Requests { get; } = new();

    public string Status { get; set; } = "idle";

    public HttpStatusCode PresetStatus { get; set; } = HttpStatusCode.NoContent;

    public int StreamsOpened;

    public IReadOnlyList<string> Calls() => Requests.Select(r => $"{r.Method} {r.Path}").ToArray();

    public void PushEvent(string json) => _stream.Writer.TryWrite(Encoding.UTF8.GetBytes(json + "\n"));

    /// <summary>Fecha o fluxo atual (o leitor encerrou a conexão) e prepara um novo.</summary>
    public void EndStream()
    {
        var old = _stream;
        _stream = Channel.CreateUnbounded<byte[]>();
        old.Writer.TryComplete();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Enqueue((request.Method.Method, path, body, request.Headers.Authorization?.ToString()));

        if (request.Method == HttpMethod.Get && path.EndsWith("/status"))
            return Json($$"""{"status":"{{Status}}","time":"2026-10-28T12:00:00Z"}""");
        if (request.Method == HttpMethod.Get && path.EndsWith("/system"))
            return Json("""{"productModel":"R700","productDescription":"Impinj R700","productVersion":"8.4.0"}""");
        if (request.Method == HttpMethod.Put && path.Contains("/presets/"))
            return new HttpResponseMessage(PresetStatus) { Content = new StringContent(PresetStatus == HttpStatusCode.NoContent ? "" : """{"message":"invalid preset"}""") };
        if (request.Method == HttpMethod.Post && path.EndsWith("/start"))
        {
            Status = "running";
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
        if (request.Method == HttpMethod.Post && path.EndsWith("/profiles/stop"))
        {
            Status = "idle";
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
        if (request.Method == HttpMethod.Get && path.EndsWith("/data/stream"))
        {
            Interlocked.Increment(ref StreamsOpened);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ChannelStream(_stream.Reader)) };
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class ChannelStream(ChannelReader<byte[]> reader) : Stream
    {
        private byte[] _current = [];
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_offset >= _current.Length)
            {
                if (!await reader.WaitToReadAsync(cancellationToken))
                    return 0;
                if (reader.TryRead(out var next))
                {
                    _current = next;
                    _offset = 0;
                }
            }

            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
