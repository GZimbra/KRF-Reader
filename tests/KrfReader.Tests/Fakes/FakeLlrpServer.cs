using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using KrfReader.Application.Readers;
using KrfReader.Infrastructure.Readers.Llrp;

namespace KrfReader.Tests.Fakes;

/// <summary>
/// Leitor LLRP falso em loopback (só testes): envia ConnectionAttemptEvent, responde a todos os comandos com
/// sucesso (ou com o erro configurado), anuncia capacidades configuráveis e injeta relatórios de tag.
/// </summary>
internal sealed class FakeLlrpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly SemaphoreSlim _write = new(1, 1);
    private TcpClient? _client;
    private int _connections;

    public FakeLlrpServer()
    {
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public uint ManufacturerPen { get; set; } = LlrpProtocol.ZebraPen;

    public int MaxAntennas { get; set; } = 4;

    /// <summary>Tabelas de salto anunciadas (Id, kHz). Padrão: Brasil 915,25–927,25.</summary>
    public List<LlrpHopTable> HopTables { get; set; } = [new(1, AnatelBand.FccGridChannelsKhz.Select(f => (uint)f).ToArray())];

    /// <summary>Tabela de potência: índice n = 10 + 0,25·(n−1) dBm, até 33 dBm.</summary>
    public List<LlrpPowerLevel> PowerLevels { get; set; } =
        Enumerable.Range(1, 93).Select(i => new LlrpPowerLevel((ushort)i, 10 + 0.25 * (i - 1))).ToList();

    public ushort ConnectionStatus { get; set; }

    /// <summary>Tipos de mensagem que recebem LLRPStatus de erro (código 100).</summary>
    public HashSet<ushort> FailingTypes { get; } = [];

    /// <summary>true = ADD_ROSPEC com parâmetro custom Impinj é recusado (firmware sem a extensão).</summary>
    public bool RejectCustomParameters { get; set; }

    public ConcurrentQueue<LlrpMessage> Received { get; } = new();

    public static bool HasImpinjCustom(LlrpMessage m)
    {
        Span<byte> pen = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(pen, LlrpProtocol.ImpinjPen);
        return m.Body.AsSpan().IndexOf(pen) >= 0;
    }

    public int Connections => Volatile.Read(ref _connections);

    public IReadOnlyList<ushort> ReceivedTypes() => Received.Select(m => m.Type).ToArray();

    public LlrpMessage Last(ushort type) => Received.Last(m => m.Type == type);

    public void Clear() => Received.Clear();

    public static byte[] Status(ushort code = 0, string text = "") => Tlv(LlrpParamType.LlrpStatus, w =>
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        w.U16(code).U16((ushort)bytes.Length).Bytes(bytes);
    });

    public static byte[] Tlv(ushort type, Action<LlrpWriter> body)
    {
        var w = new LlrpWriter();
        w.Tlv(type, body);
        return w.ToArray();
    }

    public static byte[] CapabilitiesBody(uint pen, int maxAntennas, IReadOnlyList<LlrpPowerLevel> power, IReadOnlyList<LlrpHopTable> hops,
        IReadOnlyList<uint>? fixedFrequencies = null)
    {
        var w = new LlrpWriter();
        w.Bytes(Status());
        w.Tlv(LlrpParamType.GeneralDeviceCapabilities, g =>
        {
            var fw = Encoding.UTF8.GetBytes("9.9.9");
            g.U16((ushort)maxAntennas).U16(0x8000).U32(pen).U32(2001).U16((ushort)fw.Length).Bytes(fw);
        });
        w.Tlv(LlrpParamType.RegulatoryCapabilities, r =>
        {
            r.U16(76).U16(1); // Brasil, FCC-like
            r.Tlv(LlrpParamType.UhfBandCapabilities, b =>
            {
                foreach (var p in power)
                    b.Tlv(LlrpParamType.TransmitPowerLevelTableEntry, e => e.U16(p.Index).U16((ushort)(short)Math.Round(p.Dbm * 100)));
                b.Tlv(LlrpParamType.FrequencyInformation, f =>
                {
                    f.U8(fixedFrequencies is null ? (byte)0x80 : (byte)0);
                    if (fixedFrequencies is null)
                    {
                        foreach (var t in hops)
                        {
                            f.Tlv(LlrpParamType.FrequencyHopTable, h =>
                            {
                                h.U8(t.Id).U8(0).U16((ushort)t.FrequenciesKhz.Count);
                                foreach (var khz in t.FrequenciesKhz)
                                    h.U32(khz);
                            });
                        }
                    }
                    else
                    {
                        f.Tlv(LlrpParamType.FixedFrequencyTable, h =>
                        {
                            h.U16((ushort)fixedFrequencies.Count);
                            foreach (var khz in fixedFrequencies)
                                h.U32(khz);
                        });
                    }
                });
            });
        });
        return w.ToArray();
    }

    /// <summary>TagReportData com EPCData, AntennaID e PeakRSSI.</summary>
    public static byte[] TagReport(string epcHex, ushort antenna, sbyte rssi) => Tlv(LlrpParamType.TagReportData, t =>
    {
        var epc = Convert.FromHexString(epcHex);
        t.Tlv(LlrpParamType.EpcData, e => e.U16((ushort)(epc.Length * 8)).Bytes(epc));
        t.U8(0x80 | LlrpParamType.AntennaId).U16(antenna);
        t.U8(0x80 | LlrpParamType.PeakRssi).U8((byte)rssi);
    });

    public Task SendReportAsync(params byte[][] tagReports) =>
        SendAsync(LlrpWriter.Message(LlrpMessageType.RoAccessReport, 0, w =>
        {
            foreach (var r in tagReports)
                w.Bytes(r);
        }));

    public Task SendKeepaliveAsync(uint id) => SendAsync(LlrpWriter.Message(LlrpMessageType.Keepalive, id));

    /// <summary>Derruba a conexão atual (simula cabo/PoE caindo).</summary>
    public void DropClient()
    {
        var client = Interlocked.Exchange(ref _client, null);
        try
        {
            client?.Client?.Close(0);
        }
        catch (ObjectDisposedException)
        {
        }

        client?.Dispose();
    }

    private async Task SendAsync(byte[] frame)
    {
        if (_client is not { } client)
            throw new InvalidOperationException("Sem cliente conectado.");
        await _write.WaitAsync();
        try
        {
            await client.GetStream().WriteAsync(frame);
        }
        finally
        {
            _write.Release();
        }
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                Interlocked.Increment(ref _connections);
                _client = client;
                _ = Task.Run(() => ServeAsync(client));
            }
        }
        catch (Exception) when (_cts.IsCancellationRequested)
        {
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            await SendAsync(LlrpWriter.Message(LlrpMessageType.ReaderEventNotification, 0, w =>
                w.Tlv(LlrpParamType.ReaderEventNotificationData, d =>
                {
                    d.Tlv(128, t => t.U32(0).U32(0)); // UTCTimestamp
                    d.Tlv(LlrpParamType.ConnectionAttemptEvent, e => e.U16(ConnectionStatus));
                })));

            var header = new byte[LlrpWriter.HeaderLength];
            while (!_cts.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(header, _cts.Token);
                var type = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(header) & 0x3FF);
                var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(2));
                var id = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(6));
                var body = new byte[length - LlrpWriter.HeaderLength];
                await stream.ReadExactlyAsync(body, _cts.Token);
                var message = new LlrpMessage(type, id, body);
                Received.Enqueue(message);
                await RespondAsync(message);
                if (type == LlrpMessageType.CloseConnection)
                    break;
            }
        }
        catch
        {
            // Cliente fechou ou o teste derrubou a conexão.
        }
        finally
        {
            client.Dispose();
        }
    }

    private Task RespondAsync(LlrpMessage m)
    {
        var fail = FailingTypes.Contains(m.Type)
            || (RejectCustomParameters && m.Type == LlrpMessageType.AddRoSpec && HasImpinjCustom(m));
        var status = fail ? Status(100, "recusado pelo fake") : Status();
        ushort? response = m.Type switch
        {
            LlrpMessageType.SetReaderConfig => LlrpMessageType.SetReaderConfigResponse,
            LlrpMessageType.DeleteRoSpec => LlrpMessageType.DeleteRoSpecResponse,
            LlrpMessageType.AddRoSpec => LlrpMessageType.AddRoSpecResponse,
            LlrpMessageType.EnableRoSpec => LlrpMessageType.EnableRoSpecResponse,
            LlrpMessageType.StartRoSpec => LlrpMessageType.StartRoSpecResponse,
            LlrpMessageType.StopRoSpec => LlrpMessageType.StopRoSpecResponse,
            LlrpMessageType.CloseConnection => LlrpMessageType.CloseConnectionResponse,
            _ => null,
        };

        return m.Type switch
        {
            LlrpMessageType.GetReaderCapabilities => SendAsync(LlrpWriter.Message(LlrpMessageType.GetReaderCapabilitiesResponse, m.Id,
                w => w.Bytes(CapabilitiesBody(ManufacturerPen, MaxAntennas, PowerLevels, HopTables)))),
            LlrpMessageType.CustomMessage => SendAsync(LlrpWriter.Message(LlrpMessageType.CustomMessage, m.Id,
                w => w.U32(LlrpProtocol.ImpinjPen).U8(22).Bytes(status))),
            _ when response is { } r => SendAsync(LlrpWriter.Message(r, m.Id, w => w.Bytes(status))),
            _ => Task.CompletedTask,
        };
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        DropClient();
        try
        {
            await _acceptLoop;
        }
        catch
        {
        }

        _cts.Dispose();
    }
}
