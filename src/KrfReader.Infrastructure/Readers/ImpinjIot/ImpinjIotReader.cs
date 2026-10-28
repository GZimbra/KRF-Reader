using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KrfReader.Application.Configuration;
using KrfReader.Application.Readers;
using KrfReader.Domain;
using Microsoft.Extensions.Logging;

namespace KrfReader.Infrastructure.Readers.ImpinjIot;

/// <summary>Falha da API REST do leitor (HTTP ≠ 2xx ou resposta inesperada).</summary>
public sealed class ImpinjIotException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Impinj R700 pela Impinj IoT Device Interface (REST, <c>/api/v1</c>). O R700 roda LLRP OU esta interface, nunca as
/// duas: escolha na interface web do leitor.
/// <para>
/// Conectar = confere acesso (<c>GET /status</c>), para qualquer perfil ativo e grava o preset
/// <see cref="ImpinjIotReaderOptions.PresetId"/> (portas, potência por porta em cdBm, Session, modo de busca,
/// população estimada e, com Anatel exigida, só os canais 915,25–927,25 MHz). Iniciar = <c>POST .../start</c> e
/// leitura do fluxo NDJSON <c>GET /data/stream</c>; parar = <c>POST /profiles/stop</c>. Fluxo caído durante o
/// inventário é reaberto, e o preset é regravado e reiniciado se o leitor tiver parado (ex.: reinício).
/// NÃO validado em hardware neste projeto.
/// </para>
/// </summary>
public sealed class ImpinjIotReader : IRfidReader, IReaderDiagnosticsSource, IReaderInfoSource
{
    public const double MinPowerDbm = 10;
    public const double MaxPowerDbm = 33;

    [ThreadStatic] private static ImpinjIotReader? t_raisingReader;

    private readonly ReaderOptions _options;
    private readonly ILogger? _logger;
    private readonly Func<HttpMessageHandler>? _handlerFactory;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _emitLock = new();

    private HttpClient? _http;
    private CancellationTokenSource? _streamCts;
    private Task? _streamTask;
    private volatile bool _session;
    private volatile bool _linkUp;
    private volatile bool _running;
    private long _linkDrops;
    private long _invalidEvents;

    public ImpinjIotReader(ReaderOptions options, ILogger? logger = null)
        : this(options, logger, null, null)
    {
    }

    /// <param name="handlerFactory">Testes injetam um handler HTTP falso; null = <see cref="SocketsHttpHandler"/>.</param>
    internal ImpinjIotReader(ReaderOptions options, ILogger? logger, Func<HttpMessageHandler>? handlerFactory, TimeProvider? timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _logger = logger;
        _handlerFactory = handlerFactory;
        _time = timeProvider ?? TimeProvider.System;
    }

    private TcpReaderOptions Tcp => _options.Tcp ?? new TcpReaderOptions();

    private ImpinjIotReaderOptions Iot => _options.ImpinjIot ?? new ImpinjIotReaderOptions();

    private TimeSpan RequestTimeout => TimeSpan.FromMilliseconds(Math.Max(500, Tcp.ConnectTimeoutMs));

    private string PresetPath => $"profiles/inventory/presets/{Uri.EscapeDataString(Iot.PresetId)}";

    public string Name => $"Impinj IoT {(Iot.UseHttps ? "https" : "http")}://{Tcp.Host}:{Tcp.Port}";

    public bool IsConnected => _session && _linkUp;

    public bool IsInventoryRunning => _running;

    public ReaderInfo? Info { get; private set; }

    public event EventHandler<TagReadEventArgs>? TagRead;

    public event EventHandler<ReaderErrorEventArgs>? Error;

    public ReaderDiagnostics GetDiagnostics() =>
        new(Interlocked.Read(ref _invalidEvents), 0, Interlocked.Read(ref _linkDrops));

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session)
                return;

            var http = CreateClient();
            try
            {
                await ConfigureAsync(http, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                http.Dispose();
                throw;
            }

            _http = http;
            _linkUp = true;
            _session = true;
            _logger?.LogInformation("Leitor {Reader} conectado: {Info}.", Name, Info?.Description);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_session)
                return;
            if (_running)
                await StopCoreAsync().ConfigureAwait(false);
            _session = false;
            _linkUp = false;
            _http?.Dispose();
            _http = null;
            _logger?.LogInformation("Leitor {Reader} desconectado.", Name);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StartInventoryAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_session || _http is not { } http)
                throw new InvalidOperationException("Leitor não conectado. Chame ConnectAsync antes de iniciar o inventário.");
            if (_running)
                return;

            lock (_emitLock)
                _running = true;

            try
            {
                await SendAsync(http, HttpMethod.Post, $"{PresetPath}/start", null, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // O fluxo tenta de novo (regrava o preset e reinicia) enquanto _running.
                _linkUp = false;
                RaiseError("Falha ao iniciar o inventário.", ex);
            }

            var cts = new CancellationTokenSource();
            _streamCts = cts;
            _streamTask = Task.Run(() => StreamLoopAsync(http, cts.Token), CancellationToken.None);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopInventoryAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async Task StopCoreAsync()
    {
        if (!_running)
            return;

        lock (_emitLock)
            _running = false;

        var cts = _streamCts;
        var task = _streamTask;
        _streamCts = null;
        _streamTask = null;
        cts?.Cancel();
        // Dentro do handler (thread do fluxo) esperar o próprio loop seria deadlock.
        if (task is not null && !ReferenceEquals(t_raisingReader, this))
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Fluxo de {Reader} terminou com exceção.", Name);
            }
        }

        cts?.Dispose();

        if (_http is { } http)
        {
            try
            {
                await SendAsync(http, HttpMethod.Post, "profiles/stop", null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RaiseError("Falha ao parar o inventário no leitor.", ex);
            }
        }
    }

    private HttpClient CreateClient()
    {
        HttpMessageHandler handler;
        if (_handlerFactory is not null)
        {
            handler = _handlerFactory();
        }
        else
        {
            var sockets = new SocketsHttpHandler { ConnectTimeout = RequestTimeout };
            if (Iot.AllowSelfSignedCertificate)
                sockets.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
            handler = sockets;
        }

        var scheme = Iot.UseHttps ? "https" : "http";
        var http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri($"{scheme}://{Tcp.Host}:{Tcp.Port}/api/v1/"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Iot.Username}:{Iot.Password}"));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        return http;
    }

    private async Task ConfigureAsync(HttpClient http, CancellationToken ct)
    {
        using var status = await GetJsonAsync(http, "status", ct).ConfigureAwait(false);
        var state = status.RootElement.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        if (!string.Equals(state, "idle", StringComparison.OrdinalIgnoreCase))
            await SendAsync(http, HttpMethod.Post, "profiles/stop", null, ct).ConfigureAwait(false);

        var preset = BuildPreset(_options);
        await SendAsync(http, HttpMethod.Put, PresetPath, preset, ct).ConfigureAwait(false);

        var system = "";
        try
        {
            using var info = await GetJsonAsync(http, "system", ct).ConfigureAwait(false);
            system = string.Join(" ", new[] { "productModel", "productDescription", "productVersion" }
                .Select(k => info.RootElement.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null)
                .Where(v => !string.IsNullOrWhiteSpace(v)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "{Reader}: GET /system indisponível.", Name);
        }

        var ports = _options.ActivePorts();
        var band = _options.EnforceAnatelBand
            ? $"canais {AnatelBand.FormatRange(AnatelBand.FccGridChannelsKhz.Select(f => (long)f))}"
            : "canais da região do leitor";
        Info = new ReaderInfo($"{(system.Length > 0 ? system : "Impinj R700")} · portas "
            + string.Join(", ", ports.Select(p => $"{p.Port}@{ToCdbm(p.PowerDbm) / 100.0:0.##} dBm")) + $" · {band}");
    }

    /// <summary>Preset de inventário da IoT Device Interface (JSON).</summary>
    public static string BuildPreset(ReaderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var inventory = options.Inventory ?? new InventoryOptions();
        var configs = new JsonArray();
        foreach (var port in options.ActivePorts())
        {
            var config = new JsonObject
            {
                ["antennaPort"] = port.Port,
                ["transmitPowerCdbm"] = ToCdbm(port.PowerDbm),
                ["inventorySession"] = (int)inventory.Session,
                ["inventorySearchMode"] = inventory.Target == TargetStrategy.DualAB ? "dual-target" : "single-target",
                ["estimatedTagPopulation"] = 1 << Math.Clamp(inventory.InitialQ, 0, 15),
            };
            if (options.EnforceAnatelBand)
                config["channelFrequenciesKhz"] = new JsonArray(AnatelBand.FccGridChannelsKhz.Select(f => (JsonNode)f).ToArray());
            configs.Add(config);
        }

        return new JsonObject { ["antennaConfigs"] = configs }.ToJsonString();
    }

    /// <summary>Potência em centésimos de dBm, limitada à faixa do R700 e arredondada ao passo de 0,25 dB.</summary>
    public static int ToCdbm(double dbm) =>
        (int)(Math.Round(Math.Clamp(dbm, MinPowerDbm, MaxPowerDbm) * 4, MidpointRounding.AwayFromZero) * 25);

    /// <summary>Uma linha do fluxo NDJSON. Null para eventos que não são de tag ou sem EPC.</summary>
    public static TagRead? ParseEvent(string line, DateTime receivedUtc)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        using var doc = JsonDocument.Parse(line);
        if (!doc.RootElement.TryGetProperty("tagInventoryEvent", out var ev) || ev.ValueKind != JsonValueKind.Object)
            return null;

        string? epc = null;
        if (ev.TryGetProperty("epcHex", out var hex) && hex.ValueKind == JsonValueKind.String)
            epc = Epc.Normalize(hex.GetString());
        else if (ev.TryGetProperty("epc", out var b64) && b64.ValueKind == JsonValueKind.String && b64.TryGetBytesFromBase64(out var bytes))
            epc = Epc.Normalize(Convert.ToHexString(bytes));
        if (epc is null)
            return null;

        var antenna = ev.TryGetProperty("antennaPort", out var a) && a.TryGetInt32(out var port) ? port : 1;
        var rssi = ev.TryGetProperty("peakRssiCdbm", out var r) && r.TryGetInt32(out var cdbm) ? cdbm / 100.0 : -127;
        return new TagRead(epc, rssi, antenna, receivedUtc);
    }

    private async Task StreamLoopAsync(HttpClient http, CancellationToken ct)
    {
        var delay = TimeSpan.FromMilliseconds(Math.Max(0, _options.ReconnectDelayMs));
        var needsRestart = !_linkUp;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (needsRestart)
                    await RestartIfIdleAsync(http, ct).ConfigureAwait(false);

                using var response = await http.GetAsync("data/stream", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                await EnsureSuccessAsync(response, "GET data/stream", ct).ConfigureAwait(false);
                _linkUp = true;
                needsRestart = false;

                await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(body, Encoding.UTF8);
                while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                    Process(line);
                throw new EndOfStreamException("O leitor encerrou o fluxo de eventos.");
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (_linkUp)
                    Interlocked.Increment(ref _linkDrops);
                _linkUp = false;
                needsRestart = true;
                RaiseError($"Fluxo de eventos do leitor caiu; nova tentativa em {delay.TotalMilliseconds:0} ms.", ex);
            }

            try
            {
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Após queda: se o leitor não está rodando (reinício, outro cliente parou), regrava o preset e inicia.</summary>
    private async Task RestartIfIdleAsync(HttpClient http, CancellationToken ct)
    {
        using var status = await GetJsonAsync(http, "status", ct).ConfigureAwait(false);
        var state = status.RootElement.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        if (string.Equals(state, "running", StringComparison.OrdinalIgnoreCase))
            return;
        await SendAsync(http, HttpMethod.Put, PresetPath, BuildPreset(_options), ct).ConfigureAwait(false);
        await SendAsync(http, HttpMethod.Post, $"{PresetPath}/start", null, ct).ConfigureAwait(false);
    }

    private void Process(string line)
    {
        TagRead? read;
        try
        {
            read = ParseEvent(line, _time.GetUtcNow().UtcDateTime);
        }
        catch (JsonException)
        {
            Interlocked.Increment(ref _invalidEvents);
            return;
        }

        if (read is not { } tag)
            return;

        lock (_emitLock)
        {
            if (!_running || TagRead is not { } handler)
                return;
            var previous = t_raisingReader;
            t_raisingReader = this;
            try
            {
                handler(this, new TagReadEventArgs(tag));
            }
            catch (Exception ex)
            {
                RaiseError("Handler de TagRead lançou exceção.", ex);
            }
            finally
            {
                t_raisingReader = previous;
            }
        }
    }

    private async Task<JsonDocument> GetJsonAsync(HttpClient http, string path, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        using var response = await http.GetAsync(path, timeout.Token).ConfigureAwait(false);
        await EnsureSuccessAsync(response, $"GET {path}", timeout.Token).ConfigureAwait(false);
        await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token).ConfigureAwait(false);
    }

    private async Task SendAsync(HttpClient http, HttpMethod method, string path, string? json, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        using var request = new HttpRequestMessage(method, path);
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        await EnsureSuccessAsync(response, $"{method} {path}", timeout.Token).ConfigureAwait(false);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        var body = "";
        try
        {
            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // Corpo é só diagnóstico.
        }

        var hint = response.StatusCode == System.Net.HttpStatusCode.Unauthorized ? " (usuário/senha da API)" : "";
        throw new ImpinjIotException($"{what}: HTTP {(int)response.StatusCode}{hint} {Truncate(body, 300)}".TrimEnd());
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private void RaiseError(string message, Exception? ex)
    {
        _logger?.LogWarning(ex, "{Reader}: {Message}", Name, message);
        try
        {
            Error?.Invoke(this, new ReaderErrorEventArgs(ex is null ? message : $"{message} {ex.Message}", ex));
        }
        catch
        {
            // Handler de erro com falha não pode derrubar o leitor.
        }
    }
}
