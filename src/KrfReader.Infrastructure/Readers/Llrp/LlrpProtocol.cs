using System.Buffers.Binary;
using System.Text;
using KrfReader.Application.Readers;
using KrfReader.Domain;

namespace KrfReader.Infrastructure.Readers.Llrp;

/// <summary>Entrada da tabela de potência do leitor: índice usado no RFTransmitter e o valor em dBm.</summary>
public readonly record struct LlrpPowerLevel(ushort Index, double Dbm);

/// <summary>Tabela de salto de frequência anunciada pelo leitor (kHz).</summary>
public sealed record LlrpHopTable(byte Id, IReadOnlyList<uint> FrequenciesKhz);

/// <summary>Resumo do GET_READER_CAPABILITIES_RESPONSE.</summary>
public sealed record LlrpCapabilities
{
    public int MaxAntennas { get; init; }

    /// <summary>IANA Private Enterprise Number do fabricante (Impinj = 25882, Zebra/Motorola = 161).</summary>
    public uint ManufacturerPen { get; init; }

    public uint ModelName { get; init; }

    public string Firmware { get; init; } = "";

    public ushort CountryCode { get; init; }

    public IReadOnlyList<LlrpPowerLevel> PowerLevels { get; init; } = [];

    public bool Hopping { get; init; }

    public IReadOnlyList<LlrpHopTable> HopTables { get; init; } = [];

    public IReadOnlyList<uint> FixedFrequenciesKhz { get; init; } = [];

    public IEnumerable<uint> AllFrequenciesKhz => Hopping ? HopTables.SelectMany(t => t.FrequenciesKhz) : FixedFrequenciesKhz;
}

/// <summary>Escolha de RF para o RFTransmitter: tabela de salto (ou canal fixo) dentro da faixa exigida.</summary>
public readonly record struct LlrpRfPlan(ushort HopTableId, ushort ChannelIndex, string Description);

/// <summary>Porta com o índice de potência escolhido na tabela do leitor.</summary>
public readonly record struct LlrpAntennaSetting(ushort AntennaId, ushort PowerIndex, double PowerDbm);

/// <summary>Parâmetros da ROSpec única que o app instala no leitor.</summary>
public sealed record LlrpRoSpecSettings(
    uint RoSpecId,
    IReadOnlyList<LlrpAntennaSetting> Antennas,
    LlrpRfPlan RfPlan,
    GenSession Session,
    ushort TagPopulation,
    ushort? ImpinjSearchMode);

/// <summary>
/// Construção e interpretação das mensagens LLRP 1.0.1 usadas pelo app (subconjunto mínimo para inventário).
/// Referência: EPCglobal "Low Level Reader Protocol (LLRP) 1.0.1".
/// </summary>
public static class LlrpProtocol
{
    public const uint ImpinjPen = 25882;
    public const uint ZebraPen = 161;

    /// <summary>Extensões Impinj: IMPINJ_ENABLE_EXTENSIONS (21/22) e ImpinjInventorySearchMode (23).</summary>
    internal const byte ImpinjEnableExtensions = 21;
    internal const uint ImpinjInventorySearchModeSubtype = 23;
    public const ushort ImpinjSingleTarget = 1;
    public const ushort ImpinjDualTarget = 2;

    private const byte ProtocolEpcGlobalC1G2 = 1;
    private const byte ReportUponNTagsOrEndOfRoSpec = 2;

    // TagReportContentSelector: AntennaID (bit 12) | PeakRSSI (bit 10) | FirstSeenTimestamp (bit 9).
    private const ushort ReportContent = 0x1000 | 0x0400 | 0x0200;

    // ───────────── Mensagens do cliente ─────────────

    public static byte[] GetReaderCapabilities(uint id) =>
        LlrpWriter.Message(LlrpMessageType.GetReaderCapabilities, id, w => w.U8(0)); // RequestedData = All

    public static byte[] SetReaderConfig(uint id, bool resetToFactoryDefault, int keepaliveMs) =>
        LlrpWriter.Message(LlrpMessageType.SetReaderConfig, id, w =>
        {
            w.U8(resetToFactoryDefault ? (byte)0x80 : (byte)0);
            w.Tlv(LlrpParamType.KeepaliveSpec, k => k
                .U8(keepaliveMs > 0 ? (byte)1 : (byte)0)
                .U32(keepaliveMs > 0 ? (uint)keepaliveMs : 0));
        });

    public static byte[] RoSpecCommand(ushort type, uint id, uint roSpecId) =>
        LlrpWriter.Message(type, id, w => w.U32(roSpecId));

    public static byte[] CloseConnection(uint id) => LlrpWriter.Message(LlrpMessageType.CloseConnection, id);

    public static byte[] KeepaliveAck(uint id) => LlrpWriter.Message(LlrpMessageType.KeepaliveAck, id);

    public static byte[] ImpinjEnableExtensionsMessage(uint id) =>
        LlrpWriter.Message(LlrpMessageType.CustomMessage, id, w => w.U32(ImpinjPen).U8(ImpinjEnableExtensions).U32(0));

    /// <summary>
    /// ROSpec sem gatilhos (só START_ROSPEC/STOP_ROSPEC), um AISpec com todas as portas ativas, inventário Gen2 com
    /// Session e população estimada, e relatório a cada tag (N = 1) com antena, RSSI de pico e horário.
    /// </summary>
    public static byte[] AddRoSpec(uint id, LlrpRoSpecSettings s) =>
        LlrpWriter.Message(LlrpMessageType.AddRoSpec, id, w => w.Tlv(LlrpParamType.RoSpec, r =>
        {
            r.U32(s.RoSpecId).U8(0).U8(0); // Priority 0, CurrentState Disabled
            r.Tlv(LlrpParamType.RoBoundarySpec, b =>
            {
                b.Tlv(LlrpParamType.RoSpecStartTrigger, t => t.U8(0));     // Null
                b.Tlv(LlrpParamType.RoSpecStopTrigger, t => t.U8(0).U32(0)); // Null
            });
            r.Tlv(LlrpParamType.AiSpec, a =>
            {
                a.U16((ushort)s.Antennas.Count);
                foreach (var antenna in s.Antennas)
                    a.U16(antenna.AntennaId);
                a.Tlv(LlrpParamType.AiSpecStopTrigger, t => t.U8(0).U32(0)); // Null
                a.Tlv(LlrpParamType.InventoryParameterSpec, ips =>
                {
                    ips.U16(1).U8(ProtocolEpcGlobalC1G2);
                    foreach (var antenna in s.Antennas)
                        ips.Tlv(LlrpParamType.AntennaConfiguration, ac => WriteAntennaConfiguration(ac, antenna, s));
                });
            });
            r.Tlv(LlrpParamType.RoReportSpec, rs =>
            {
                rs.U8(ReportUponNTagsOrEndOfRoSpec).U16(1);
                rs.Tlv(LlrpParamType.TagReportContentSelector, c => c.U16(ReportContent));
            });
        }));

    private static void WriteAntennaConfiguration(LlrpWriter ac, LlrpAntennaSetting antenna, LlrpRoSpecSettings s)
    {
        ac.U16(antenna.AntennaId);
        ac.Tlv(LlrpParamType.RfTransmitter, t => t.U16(s.RfPlan.HopTableId).U16(s.RfPlan.ChannelIndex).U16(antenna.PowerIndex));
        ac.Tlv(LlrpParamType.C1G2InventoryCommand, c =>
        {
            c.U8(0); // TagInventoryStateAware = 0
            c.Tlv(LlrpParamType.C1G2SingulationControl, sc => sc
                .U8((byte)(((int)s.Session & 0x03) << 6))
                .U16(s.TagPopulation)
                .U32(0));
            if (s.ImpinjSearchMode is { } mode)
                c.Tlv(LlrpParamType.Custom, x => x.U32(ImpinjPen).U32(ImpinjInventorySearchModeSubtype).U16(mode));
        });
    }

    // ───────────── Mensagens do leitor ─────────────

    /// <summary>Status do ConnectionAttemptEvent numa READER_EVENT_NOTIFICATION, ou null se não houver.</summary>
    public static ushort? ParseConnectionAttempt(ReadOnlySpan<byte> body)
    {
        var top = new LlrpParamReader(body);
        while (top.TryRead(out var data))
        {
            if (data.Type != LlrpParamType.ReaderEventNotificationData)
                continue;
            var inner = new LlrpParamReader(data.Value);
            while (inner.TryRead(out var ev))
            {
                if (ev.Type == LlrpParamType.ConnectionAttemptEvent && ev.Value.Length >= 2)
                    return BinaryPrimitives.ReadUInt16BigEndian(ev.Value);
            }
        }

        return null;
    }

    public static string DescribeConnectionAttempt(ushort status) => status switch
    {
        0 => "conexão aceita",
        1 => "o leitor já tem uma conexão iniciada por ele mesmo",
        2 => "já há outro cliente LLRP conectado (feche o outro software ou a interface web de inventário)",
        3 => "o leitor recusou a conexão",
        4 => "outro cliente tentou conectar",
        _ => $"status {status}",
    };

    public static LlrpCapabilities ParseCapabilities(ReadOnlySpan<byte> body)
    {
        var caps = new LlrpCapabilities();
        var top = new LlrpParamReader(body);
        while (top.TryRead(out var p))
        {
            if (p.Type == LlrpParamType.GeneralDeviceCapabilities && p.Value.Length >= 14)
            {
                var v = p.Value;
                var fwLength = BinaryPrimitives.ReadUInt16BigEndian(v[12..]);
                caps = caps with
                {
                    MaxAntennas = BinaryPrimitives.ReadUInt16BigEndian(v),
                    ManufacturerPen = BinaryPrimitives.ReadUInt32BigEndian(v[4..]),
                    ModelName = BinaryPrimitives.ReadUInt32BigEndian(v[8..]),
                    Firmware = fwLength <= v.Length - 14 ? Encoding.UTF8.GetString(v.Slice(14, fwLength)) : "",
                };
            }
            else if (p.Type == LlrpParamType.RegulatoryCapabilities && p.Value.Length >= 4)
            {
                caps = ParseRegulatory(p.Value, caps with { CountryCode = BinaryPrimitives.ReadUInt16BigEndian(p.Value) });
            }
        }

        return caps;
    }

    private static LlrpCapabilities ParseRegulatory(ReadOnlySpan<byte> value, LlrpCapabilities caps)
    {
        var powers = new List<LlrpPowerLevel>();
        var hops = new List<LlrpHopTable>();
        var fixedFreqs = new List<uint>();
        var hopping = false;

        var reader = new LlrpParamReader(value[4..]);
        while (reader.TryRead(out var band))
        {
            if (band.Type != LlrpParamType.UhfBandCapabilities)
                continue;
            var inner = new LlrpParamReader(band.Value);
            while (inner.TryRead(out var p))
            {
                if (p.Type == LlrpParamType.TransmitPowerLevelTableEntry && p.Value.Length >= 4)
                {
                    powers.Add(new LlrpPowerLevel(
                        BinaryPrimitives.ReadUInt16BigEndian(p.Value),
                        BinaryPrimitives.ReadInt16BigEndian(p.Value[2..]) / 100.0));
                }
                else if (p.Type == LlrpParamType.FrequencyInformation && p.Value.Length >= 1)
                {
                    hopping = (p.Value[0] & 0x80) != 0;
                    var tables = new LlrpParamReader(p.Value[1..]);
                    while (tables.TryRead(out var t))
                    {
                        if (t.Type == LlrpParamType.FrequencyHopTable && t.Value.Length >= 4)
                        {
                            var count = BinaryPrimitives.ReadUInt16BigEndian(t.Value[2..]);
                            hops.Add(new LlrpHopTable(t.Value[0], ReadFrequencies(t.Value[4..], count)));
                        }
                        else if (t.Type == LlrpParamType.FixedFrequencyTable && t.Value.Length >= 2)
                        {
                            var count = BinaryPrimitives.ReadUInt16BigEndian(t.Value);
                            fixedFreqs.AddRange(ReadFrequencies(t.Value[2..], count));
                        }
                    }
                }
            }
        }

        return caps with { PowerLevels = powers, Hopping = hopping, HopTables = hops, FixedFrequenciesKhz = fixedFreqs };
    }

    private static uint[] ReadFrequencies(ReadOnlySpan<byte> data, int count)
    {
        count = Math.Min(count, data.Length / 4);
        var result = new uint[count];
        for (var i = 0; i < count; i++)
            result[i] = BinaryPrimitives.ReadUInt32BigEndian(data[(4 * i)..]);
        return result;
    }

    /// <summary>
    /// Escolhe tabela de salto/canal. Com <paramref name="enforceAnatel"/>, só aceita tabela cujos canais estejam
    /// TODOS em 915–928 MHz (salto) ou o primeiro canal fixo dentro dela; se não houver, lança
    /// <see cref="LlrpException"/>: LLRP padrão não muda a região do leitor.
    /// </summary>
    public static LlrpRfPlan SelectRfPlan(LlrpCapabilities caps, bool enforceAnatel)
    {
        ArgumentNullException.ThrowIfNull(caps);
        var announced = AnatelBand.FormatRange(caps.AllFrequenciesKhz.Select(f => (long)f));

        if (caps.Hopping && caps.HopTables.Count > 0)
        {
            var table = enforceAnatel
                ? caps.HopTables.FirstOrDefault(t => t.FrequenciesKhz.Count > 0 && t.FrequenciesKhz.All(f => AnatelBand.Contains(f)))
                : caps.HopTables[0];
            if (table is null)
                throw OutOfBand(caps, announced);
            return new LlrpRfPlan(table.Id, 1,
                $"salto, tabela {table.Id}: {AnatelBand.FormatRange(table.FrequenciesKhz.Select(f => (long)f))}");
        }

        if (!caps.Hopping && caps.FixedFrequenciesKhz.Count > 0)
        {
            var index = enforceAnatel
                ? caps.FixedFrequenciesKhz.ToList().FindIndex(f => AnatelBand.Contains(f))
                : 0;
            if (index < 0)
                throw OutOfBand(caps, announced);
            return new LlrpRfPlan(1, (ushort)(index + 1), $"canal fixo {caps.FixedFrequenciesKhz[index] / 1000.0:0.00} MHz");
        }

        if (enforceAnatel)
            throw new LlrpException("O leitor não anunciou as frequências (RegulatoryCapabilities); não dá para garantir 915–928 MHz. Confira a região no leitor e desative a exigência Anatel só depois disso.");
        return new LlrpRfPlan(1, 1, "frequências não anunciadas");
    }

    private static LlrpException OutOfBand(LlrpCapabilities caps, string announced) => new(
        $"Leitor anuncia {announced} (país {caps.CountryCode}), fora de {AnatelBand.Description}. "
        + "LLRP não troca a região: configure a região Brasil/915–928 MHz na interface web do leitor"
        + (caps.ManufacturerPen == ImpinjPen ? " ou use o R700 pela interface Impinj IoT (REST), que restringe os canais" : "")
        + ".");

    /// <summary>Maior potência da tabela que não passa do pedido (ou a menor, se o pedido estiver abaixo da tabela).</summary>
    public static LlrpPowerLevel SelectPower(IReadOnlyList<LlrpPowerLevel> table, double requestedDbm)
    {
        if (table.Count == 0)
            throw new LlrpException("O leitor não anunciou a tabela de potência (TransmitPowerLevelTable).");
        var below = table.Where(p => p.Dbm <= requestedDbm + 0.001).ToArray();
        return below.Length > 0 ? below.MaxBy(p => p.Dbm) : table.MinBy(p => p.Dbm);
    }

    /// <summary>
    /// Leituras de um RO_ACCESS_REPORT. <paramref name="malformed"/> conta TagReportData sem EPC ou truncados.
    /// Sem PeakRSSI no relatório, RSSI = −127 dBm (o filtro de RSSI mínimo descarta).
    /// </summary>
    public static IReadOnlyList<TagRead> ParseReport(ReadOnlySpan<byte> body, DateTime receivedUtc, out int malformed)
    {
        malformed = 0;
        List<TagRead>? reads = null;
        var top = new LlrpParamReader(body);
        while (top.TryRead(out var p))
        {
            if (p.Type != LlrpParamType.TagReportData || p.IsTv)
                continue;

            string? epc = null;
            var antenna = 1;
            double rssi = -127;
            var inner = new LlrpParamReader(p.Value);
            while (inner.TryRead(out var f))
            {
                switch (f.Type)
                {
                    case LlrpParamType.Epc96 when f.IsTv:
                        epc = Epc.Normalize(Convert.ToHexString(f.Value));
                        break;
                    case LlrpParamType.EpcData when !f.IsTv && f.Value.Length >= 2:
                        var bits = BinaryPrimitives.ReadUInt16BigEndian(f.Value);
                        var bytes = Math.Min((bits + 7) / 8, f.Value.Length - 2);
                        epc = Epc.Normalize(Convert.ToHexString(f.Value.Slice(2, bytes)));
                        break;
                    case LlrpParamType.AntennaId when f.IsTv:
                        antenna = BinaryPrimitives.ReadUInt16BigEndian(f.Value);
                        break;
                    case LlrpParamType.PeakRssi when f.IsTv:
                        rssi = (sbyte)f.Value[0];
                        break;
                }
            }

            if (epc is null || inner.Truncated)
            {
                malformed++;
                if (epc is null)
                    continue;
            }

            (reads ??= []).Add(new TagRead(epc, rssi, antenna, receivedUtc));
        }

        if (top.Truncated)
            malformed++;
        return reads ?? (IReadOnlyList<TagRead>)Array.Empty<TagRead>();
    }

    public static string DescribeManufacturer(uint pen) => pen switch
    {
        ImpinjPen => "Impinj",
        ZebraPen => "Zebra",
        _ => $"PEN {pen}",
    };
}
