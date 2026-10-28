using System.Globalization;
using System.Net.Sockets;
using KrfReader.Application.Configuration;
using KrfReader.Application.Readers;
using KrfReader.Domain;
using Microsoft.Extensions.Logging;

namespace KrfReader.Infrastructure.Readers.Llrp;

/// <summary>
/// Leitor LLRP 1.0.1 (Impinj Speedway/R700 em modo LLRP, Zebra FX7500/FX9600 e outros leitores fixos EPCglobal).
/// <para>
/// Ao conectar: espera o ConnectionAttemptEvent, lê as capacidades (portas, tabela de potência, frequências),
/// confere a faixa Anatel, aplica SET_READER_CONFIG (reset opcional + KEEPALIVE), apaga ROSpecs antigas e instala
/// uma ROSpec própria (todas as portas ativas, potência por porta, Session e população Gen2). Início/parada =
/// START_ROSPEC/STOP_ROSPEC. Queda de link (erro de socket ou KEEPALIVE ausente) dispara reconexão automática que
/// refaz a configuração e, se o inventário estava ativo, reinicia a ROSpec.
/// </para>
/// <para>
/// Em leitores Impinj, se a extensão for aceita, o modo de busca (single/dual target) segue
/// <see cref="InventoryOptions.Target"/>. Sem Q dinâmico: o próprio leitor ajusta o Q.
/// NÃO validado em hardware neste projeto.
/// </para>
/// </summary>
public sealed class LlrpReader : IRfidReader, IReaderDiagnosticsSource, IReaderInfoSource
{
    public const uint RoSpecId = 0x4B52; // "KR"

    [ThreadStatic] private static LlrpReader? t_raisingReader;

    private readonly ReaderOptions _options;
    private readonly ILogger? _logger;
    private readonly Func<CancellationToken, Task<Stream>> _open;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly object _emitLock = new();

    private LlrpConnection? _conn;
    private CancellationTokenSource? _sessionCts;
    private Task? _supervisor;
    private volatile bool _session;
    private volatile bool _linkUp;
    private volatile bool _running;
    private long _linkDrops;
    private long _malformedReports;

    public LlrpReader(ReaderOptions options, ILogger? logger = null)
        : this(options, logger, null, null)
    {
    }

    /// <param name="open">Abre o transporte (testes injetam um servidor falso); null = TCP em <c>Tcp:Host</c>/<c>Tcp:Port</c>.</param>
    internal LlrpReader(ReaderOptions options, ILogger? logger, Func<CancellationToken, Task<Stream>>? open, TimeProvider? timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _logger = logger;
        _open = open ?? OpenTcpAsync;
        _time = timeProvider ?? TimeProvider.System;
    }

    private TcpReaderOptions Tcp => _options.Tcp ?? new TcpReaderOptions();

    private LlrpReaderOptions Llrp => _options.Llrp ?? new LlrpReaderOptions();

    private TimeSpan ResponseTimeout => TimeSpan.FromMilliseconds(Math.Max(100, Llrp.ResponseTimeoutMs));

    public string Name => $"LLRP {Tcp.Host}:{Tcp.Port}";

    public bool IsConnected => _session && _linkUp;

    public bool IsInventoryRunning => _running;

    public ReaderInfo? Info { get; private set; }

    public event EventHandler<TagReadEventArgs>? TagRead;

    public event EventHandler<ReaderErrorEventArgs>? Error;

    public ReaderDiagnostics GetDiagnostics() =>
        new(Interlocked.Read(ref _malformedReports), 0, Interlocked.Read(ref _linkDrops));

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session)
                return;

            var conn = await OpenAndConfigureAsync(cancellationToken).ConfigureAwait(false);
            var cts = new CancellationTokenSource();
            _sessionCts = cts;
            _conn = conn;
            _linkUp = true;
            _session = true;
            _supervisor = Task.Run(() => SuperviseAsync(conn, cts.Token), CancellationToken.None);
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

            var reentrant = ReferenceEquals(t_raisingReader, this);
            if (_running)
                await StopCoreAsync(sendStop: !reentrant).ConfigureAwait(false);

            _session = false;
            var cts = _sessionCts;
            var supervisor = _supervisor;
            _sessionCts = null;
            _supervisor = null;
            cts?.Cancel();

            if (supervisor is not null && !reentrant)
            {
                try
                {
                    await supervisor.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Supervisor de {Reader} terminou com exceção.", Name);
                }
            }

            LlrpConnection? conn;
            await _io.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                conn = _conn;
                _conn = null;
                _linkUp = false;
            }
            finally
            {
                _io.Release();
            }

            if (conn is not null)
            {
                // Dentro do handler o loop de recepção está ocupado: nenhuma resposta chegaria.
                if (!reentrant)
                    await CloseGracefullyAsync(conn).ConfigureAwait(false);
                await conn.DisposeAsync(waitForLoop: !reentrant).ConfigureAwait(false);
            }

            cts?.Dispose();
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
            if (!_session)
                throw new InvalidOperationException("Leitor não conectado. Chame ConnectAsync antes de iniciar o inventário.");
            if (_running)
                return;

            lock (_emitLock)
                _running = true;

            await SendRoSpecCommandIfLinkUpAsync(LlrpMessageType.StartRoSpec, LlrpMessageType.StartRoSpecResponse, "START_ROSPEC", cancellationToken)
                .ConfigureAwait(false);
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
            await StopCoreAsync(sendStop: !ReferenceEquals(t_raisingReader, this)).ConfigureAwait(false);
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

    private async Task StopCoreAsync(bool sendStop)
    {
        if (!_running)
            return;

        // Sob _emitLock: quando isto retorna, nenhum relatório em andamento dispara TagRead.
        lock (_emitLock)
            _running = false;

        if (sendStop)
        {
            await SendRoSpecCommandIfLinkUpAsync(LlrpMessageType.StopRoSpec, LlrpMessageType.StopRoSpecResponse, "STOP_ROSPEC", CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private async Task SendRoSpecCommandIfLinkUpAsync(ushort type, ushort responseType, string description, CancellationToken cancellationToken)
    {
        await _io.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Link caído: a reconexão reinicia a ROSpec se _running estiver true.
            if (!_linkUp || _conn is not { } conn)
                return;
            await conn.TransactAsync(description, id => LlrpProtocol.RoSpecCommand(type, id, RoSpecId), responseType, ResponseTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RaiseError($"Falha em {description}.", ex);
        }
        finally
        {
            _io.Release();
        }
    }

    private async Task<Stream> OpenTcpAsync(CancellationToken cancellationToken)
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

    /// <summary>Abre o transporte e configura o leitor do zero. Falha = conexão descartada e exceção.</summary>
    private async Task<LlrpConnection> OpenAndConfigureAsync(CancellationToken ct)
    {
        var stream = await _open(ct).ConfigureAwait(false);
        var keepaliveMs = Math.Max(0, Llrp.KeepaliveMs);
        var idle = keepaliveMs > 0 ? TimeSpan.FromMilliseconds(3.0 * keepaliveMs) : TimeSpan.Zero;
        var conn = new LlrpConnection(stream, OnUnsolicited, idle, _logger);
        try
        {
            await WaitConnectionAttemptAsync(conn, ct).ConfigureAwait(false);

            var capsResponse = await conn.TransactAsync("GET_READER_CAPABILITIES", LlrpProtocol.GetReaderCapabilities,
                LlrpMessageType.GetReaderCapabilitiesResponse, ResponseTimeout, ct).ConfigureAwait(false);
            var caps = LlrpProtocol.ParseCapabilities(capsResponse.Body);

            var ports = _options.ActivePorts();
            if (ports.Count == 0)
                throw new LlrpException("Nenhuma antena ativa configurada.");
            if (caps.MaxAntennas > 0 && ports.FirstOrDefault(p => p.Port > caps.MaxAntennas) is { Port: > 0 } missing)
                throw new LlrpException($"Porta {missing.Port} não existe: o leitor tem {caps.MaxAntennas} porta(s).");

            var plan = LlrpProtocol.SelectRfPlan(caps, _options.EnforceAnatelBand);
            var antennas = ports.Select(p =>
            {
                var level = LlrpProtocol.SelectPower(caps.PowerLevels, p.PowerDbm);
                return new LlrpAntennaSetting((ushort)p.Port, level.Index, level.Dbm);
            }).ToArray();

            await conn.TransactAsync("SET_READER_CONFIG",
                id => LlrpProtocol.SetReaderConfig(id, Llrp.ResetToFactoryDefault, keepaliveMs),
                LlrpMessageType.SetReaderConfigResponse, ResponseTimeout, ct).ConfigureAwait(false);

            // ROSpecID 0 = todas: limpa o que outro cliente (ou uma sessão anterior) deixou.
            await conn.TransactAsync("DELETE_ROSPEC", id => LlrpProtocol.RoSpecCommand(LlrpMessageType.DeleteRoSpec, id, 0),
                LlrpMessageType.DeleteRoSpecResponse, ResponseTimeout, ct).ConfigureAwait(false);

            var searchMode = await TryEnableImpinjSearchModeAsync(conn, caps, ct).ConfigureAwait(false);
            var inventory = _options.Inventory ?? new InventoryOptions();
            var settings = new LlrpRoSpecSettings(
                RoSpecId, antennas, plan, inventory.Session,
                (ushort)(1 << Math.Clamp(inventory.InitialQ, 0, 15)), searchMode);
            try
            {
                await AddRoSpecAsync(conn, settings, ct).ConfigureAwait(false);
            }
            catch (LlrpException ex) when (searchMode is not null)
            {
                _logger?.LogWarning(ex, "{Reader}: ROSpec com modo de busca Impinj recusada; repetindo sem a extensão.", Name);
                searchMode = null;
                await AddRoSpecAsync(conn, settings with { ImpinjSearchMode = null }, ct).ConfigureAwait(false);
            }

            await conn.TransactAsync("ENABLE_ROSPEC", id => LlrpProtocol.RoSpecCommand(LlrpMessageType.EnableRoSpec, id, RoSpecId),
                LlrpMessageType.EnableRoSpecResponse, ResponseTimeout, ct).ConfigureAwait(false);

            Info = new ReaderInfo(Describe(caps, plan, antennas, searchMode));
            return conn;
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task WaitConnectionAttemptAsync(LlrpConnection conn, CancellationToken ct)
    {
        var timeout = TimeSpan.FromMilliseconds(Math.Max(500, Tcp.ConnectTimeoutMs));
        ushort status;
        try
        {
            status = await conn.ConnectionAttempt.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Obrigatório pela norma, mas não vale travar por isso: os comandos seguintes dizem se a conexão serve.
            _logger?.LogWarning("{Reader}: ConnectionAttemptEvent não chegou em {Timeout} ms; seguindo.", Name, timeout.TotalMilliseconds);
            return;
        }

        if (status != 0)
            throw new LlrpException($"Leitor recusou a conexão LLRP: {LlrpProtocol.DescribeConnectionAttempt(status)}.");
    }

    private async Task<ushort?> TryEnableImpinjSearchModeAsync(LlrpConnection conn, LlrpCapabilities caps, CancellationToken ct)
    {
        if (caps.ManufacturerPen != LlrpProtocol.ImpinjPen)
            return null;
        try
        {
            await conn.TransactAsync("IMPINJ_ENABLE_EXTENSIONS", LlrpProtocol.ImpinjEnableExtensionsMessage,
                LlrpMessageType.CustomMessage, ResponseTimeout, ct, statusOffset: 5).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is LlrpException or TimeoutException)
        {
            _logger?.LogWarning(ex, "{Reader}: extensões Impinj indisponíveis; modo de busca fica a critério do leitor.", Name);
            return null;
        }

        return (_options.Inventory?.Target ?? TargetStrategy.DualAB) == TargetStrategy.DualAB
            ? LlrpProtocol.ImpinjDualTarget
            : LlrpProtocol.ImpinjSingleTarget;
    }

    private Task AddRoSpecAsync(LlrpConnection conn, LlrpRoSpecSettings settings, CancellationToken ct) =>
        conn.TransactAsync("ADD_ROSPEC", id => LlrpProtocol.AddRoSpec(id, settings),
            LlrpMessageType.AddRoSpecResponse, ResponseTimeout, ct);

    private static string Describe(LlrpCapabilities caps, LlrpRfPlan plan, IReadOnlyList<LlrpAntennaSetting> antennas, ushort? searchMode)
    {
        var ports = string.Join(", ", antennas.Select(a => string.Create(CultureInfo.InvariantCulture, $"{a.AntennaId}@{a.PowerDbm:0.##} dBm")));
        var mode = searchMode switch
        {
            LlrpProtocol.ImpinjDualTarget => " · dual target",
            LlrpProtocol.ImpinjSingleTarget => " · single target",
            _ => "",
        };
        return $"{LlrpProtocol.DescribeManufacturer(caps.ManufacturerPen)} modelo {caps.ModelName} fw {caps.Firmware} · "
             + $"{caps.MaxAntennas} porta(s) · portas {ports} · {plan.Description}{mode}";
    }

    private async Task CloseGracefullyAsync(LlrpConnection conn)
    {
        var timeout = TimeSpan.FromMilliseconds(Math.Min(1000, ResponseTimeout.TotalMilliseconds));
        try
        {
            await conn.TransactAsync("DELETE_ROSPEC", id => LlrpProtocol.RoSpecCommand(LlrpMessageType.DeleteRoSpec, id, RoSpecId),
                LlrpMessageType.DeleteRoSpecResponse, timeout, CancellationToken.None).ConfigureAwait(false);
            await conn.TransactAsync("CLOSE_CONNECTION", LlrpProtocol.CloseConnection,
                LlrpMessageType.CloseConnectionResponse, timeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "{Reader}: encerramento LLRP incompleto.", Name);
        }
    }

    private async Task SuperviseAsync(LlrpConnection conn, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Exception? cause = null;
            try
            {
                await conn.Completion.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                cause = ex;
            }

            if (ct.IsCancellationRequested)
                return;

            _linkUp = false;
            Interlocked.Increment(ref _linkDrops);
            RaiseError("Conexão LLRP perdida; tentando reconectar.", cause);
            await conn.DisposeAsync().ConfigureAwait(false);

            var reopened = await ReconnectAsync(ct).ConfigureAwait(false);
            if (reopened is null)
                return;
            conn = reopened;
        }
    }

    private async Task<LlrpConnection?> ReconnectAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromMilliseconds(Math.Max(0, _options.ReconnectDelayMs));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            LlrpConnection? conn = null;
            try
            {
                conn = await OpenAndConfigureAsync(ct).ConfigureAwait(false);
                await _io.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    _conn = conn;
                    _linkUp = true;
                    if (_running)
                    {
                        await conn.TransactAsync("START_ROSPEC", id => LlrpProtocol.RoSpecCommand(LlrpMessageType.StartRoSpec, id, RoSpecId),
                            LlrpMessageType.StartRoSpecResponse, ResponseTimeout, ct).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _io.Release();
                }

                _logger?.LogInformation("Leitor {Reader} reconectado.", Name);
                return conn;
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                if (conn is not null)
                    await conn.DisposeAsync().ConfigureAwait(false);
                return null;
            }
            catch (Exception ex)
            {
                _linkUp = false;
                if (conn is not null)
                    await conn.DisposeAsync().ConfigureAwait(false);
                RaiseError($"Falha ao reconectar; nova tentativa em {delay.TotalMilliseconds:0} ms.", ex);
            }
        }

        return null;
    }

    private void OnUnsolicited(LlrpMessage message)
    {
        if (message.Type != LlrpMessageType.RoAccessReport)
            return;

        var reads = LlrpProtocol.ParseReport(message.Body, _time.GetUtcNow().UtcDateTime, out var malformed);
        if (malformed > 0)
            Interlocked.Add(ref _malformedReports, malformed);
        if (reads.Count == 0)
            return;

        lock (_emitLock)
        {
            if (!_running)
                return;
            var handler = TagRead;
            if (handler is null)
                return;

            var previous = t_raisingReader;
            t_raisingReader = this;
            try
            {
                foreach (var read in reads)
                {
                    try
                    {
                        handler(this, new TagReadEventArgs(read));
                    }
                    catch (Exception ex)
                    {
                        RaiseError("Handler de TagRead lançou exceção.", ex);
                    }
                }
            }
            finally
            {
                t_raisingReader = previous;
            }
        }
    }

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
