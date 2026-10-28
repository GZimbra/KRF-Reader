using System.Buffers.Binary;
using System.Text;

namespace KrfReader.Infrastructure.Readers.Llrp;

/// <summary>Tipos de mensagem LLRP 1.0.1 usados pelo app.</summary>
public static class LlrpMessageType
{
    public const ushort GetReaderCapabilities = 1;
    public const ushort SetReaderConfig = 3;
    public const ushort CloseConnectionResponse = 4;
    public const ushort GetReaderCapabilitiesResponse = 11;
    public const ushort SetReaderConfigResponse = 13;
    public const ushort CloseConnection = 14;
    public const ushort AddRoSpec = 20;
    public const ushort DeleteRoSpec = 21;
    public const ushort StartRoSpec = 22;
    public const ushort StopRoSpec = 23;
    public const ushort EnableRoSpec = 24;
    public const ushort AddRoSpecResponse = 30;
    public const ushort DeleteRoSpecResponse = 31;
    public const ushort StartRoSpecResponse = 32;
    public const ushort StopRoSpecResponse = 33;
    public const ushort EnableRoSpecResponse = 34;
    public const ushort RoAccessReport = 61;
    public const ushort Keepalive = 62;
    public const ushort ReaderEventNotification = 63;
    public const ushort KeepaliveAck = 72;
    public const ushort ErrorMessage = 100;
    public const ushort CustomMessage = 1023;
}

/// <summary>Tipos de parâmetro LLRP (TLV ≥ 128; TV 1–127).</summary>
public static class LlrpParamType
{
    // TV
    public const ushort AntennaId = 1;
    public const ushort FirstSeenTimestampUtc = 2;
    public const ushort PeakRssi = 6;
    public const ushort Epc96 = 13;

    // TLV
    public const ushort GeneralDeviceCapabilities = 137;
    public const ushort RegulatoryCapabilities = 143;
    public const ushort UhfBandCapabilities = 144;
    public const ushort TransmitPowerLevelTableEntry = 145;
    public const ushort FrequencyInformation = 146;
    public const ushort FrequencyHopTable = 147;
    public const ushort FixedFrequencyTable = 148;
    public const ushort RoSpec = 177;
    public const ushort RoBoundarySpec = 178;
    public const ushort RoSpecStartTrigger = 179;
    public const ushort RoSpecStopTrigger = 182;
    public const ushort AiSpec = 183;
    public const ushort AiSpecStopTrigger = 184;
    public const ushort InventoryParameterSpec = 186;
    public const ushort KeepaliveSpec = 220;
    public const ushort AntennaConfiguration = 222;
    public const ushort RfTransmitter = 224;
    public const ushort RoReportSpec = 237;
    public const ushort TagReportContentSelector = 238;
    public const ushort TagReportData = 240;
    public const ushort EpcData = 241;
    public const ushort ReaderEventNotificationData = 246;
    public const ushort ConnectionAttemptEvent = 256;
    public const ushort LlrpStatus = 287;
    public const ushort C1G2InventoryCommand = 330;
    public const ushort C1G2SingulationControl = 336;
    public const ushort Custom = 1023;
}

/// <summary>Mensagem LLRP recebida: tipo, id e corpo (sem o cabeçalho de 10 bytes).</summary>
public sealed record LlrpMessage(ushort Type, uint Id, byte[] Body);

/// <summary>Falha de protocolo LLRP (status de erro do leitor, resposta inesperada, faixa proibida).</summary>
public sealed class LlrpException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Codificação big-endian de mensagens e parâmetros LLRP 1.0.1.
/// Cabeçalho: <c>Rsvd(3) Ver(3)=1 Type(10) | Length(32) | MessageID(32)</c>; TLV: <c>Rsvd(6) Type(10) | Length(16)</c>.
/// </summary>
public sealed class LlrpWriter
{
    public const int HeaderLength = 10;
    public const byte Version = 1;

    private byte[] _buffer = new byte[256];
    private int _length;

    public int Length => _length;

    public static byte[] Message(ushort type, uint id, Action<LlrpWriter>? body = null)
    {
        var w = new LlrpWriter();
        w.U16((ushort)((Version << 10) | (type & 0x3FF)));
        w.U32(0);
        w.U32(id);
        body?.Invoke(w);
        BinaryPrimitives.WriteUInt32BigEndian(w._buffer.AsSpan(2), (uint)w._length);
        return w.ToArray();
    }

    public byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();

    public LlrpWriter U8(byte v)
    {
        Ensure(1)[0] = v;
        _length += 1;
        return this;
    }

    public LlrpWriter U16(ushort v)
    {
        BinaryPrimitives.WriteUInt16BigEndian(Ensure(2), v);
        _length += 2;
        return this;
    }

    public LlrpWriter U32(uint v)
    {
        BinaryPrimitives.WriteUInt32BigEndian(Ensure(4), v);
        _length += 4;
        return this;
    }

    public LlrpWriter Bytes(ReadOnlySpan<byte> v)
    {
        v.CopyTo(Ensure(v.Length));
        _length += v.Length;
        return this;
    }

    /// <summary>Escreve um parâmetro TLV; o comprimento é preenchido ao fim de <paramref name="body"/>.</summary>
    public LlrpWriter Tlv(ushort type, Action<LlrpWriter> body)
    {
        var start = _length;
        U16((ushort)(type & 0x3FF));
        U16(0);
        body(this);
        BinaryPrimitives.WriteUInt16BigEndian(_buffer.AsSpan(start + 2), (ushort)(_length - start));
        return this;
    }

    private Span<byte> Ensure(int count)
    {
        if (_length + count > _buffer.Length)
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + count));
        return _buffer.AsSpan(_length, count);
    }
}

/// <summary>Parâmetro lido: tipo e valor (sem cabeçalho). <see cref="IsTv"/> = parâmetro TV (tamanho fixo).</summary>
public readonly ref struct LlrpParam(ushort type, ReadOnlySpan<byte> value, bool isTv)
{
    public ushort Type { get; } = type;
    public ReadOnlySpan<byte> Value { get; } = value;
    public bool IsTv { get; } = isTv;
}

/// <summary>Itera parâmetros TLV/TV consecutivos. Para ao encontrar TV desconhecido ou TLV truncado.</summary>
public ref struct LlrpParamReader(ReadOnlySpan<byte> data)
{
    private ReadOnlySpan<byte> _rest = data;

    /// <summary>true se a leitura parou por dado malformado ou TV de tamanho desconhecido.</summary>
    public bool Truncated { get; private set; }

    public bool TryRead(out LlrpParam param)
    {
        param = default;
        if (_rest.IsEmpty)
            return false;

        if ((_rest[0] & 0x80) != 0)
        {
            var type = (ushort)(_rest[0] & 0x7F);
            var size = TvValueLength(type);
            if (size < 0 || _rest.Length < 1 + size)
            {
                Truncated = true;
                _rest = default;
                return false;
            }

            param = new LlrpParam(type, _rest.Slice(1, size), isTv: true);
            _rest = _rest[(1 + size)..];
            return true;
        }

        if (_rest.Length < 4)
        {
            Truncated = true;
            _rest = default;
            return false;
        }

        var tlvType = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(_rest) & 0x3FF);
        var length = BinaryPrimitives.ReadUInt16BigEndian(_rest[2..]);
        if (length < 4 || length > _rest.Length)
        {
            Truncated = true;
            _rest = default;
            return false;
        }

        param = new LlrpParam(tlvType, _rest[4..length], isTv: false);
        _rest = _rest[length..];
        return true;
    }

    /// <summary>Tamanho do valor dos parâmetros TV do LLRP 1.0.1 (-1 = desconhecido).</summary>
    public static int TvValueLength(ushort type) => type switch
    {
        1 => 2,             // AntennaID
        2 or 3 or 4 or 5 => 8, // First/LastSeenTimestamp UTC/Uptime
        6 => 1,             // PeakRSSI
        7 => 2,             // ChannelIndex
        8 => 2,             // TagSeenCount
        9 => 4,             // ROSpecID
        10 => 2,            // InventoryParameterSpecID
        11 => 2,            // C1G2-CRC
        12 => 2,            // C1G2-PC
        13 => 12,           // EPC-96
        14 => 2,            // SpecIndex
        15 => 2,            // ClientRequestOpSpecResult
        16 => 4,            // AccessSpecID
        17 => 2,            // OpSpecID
        18 => 4,            // C1G2SingulationDetails
        19 or 20 => 2,      // C1G2-XPCW1/W2
        _ => -1,
    };
}

/// <summary>Status de uma resposta (<c>LLRPStatus</c>): código 0 = sucesso.</summary>
public readonly record struct LlrpStatus(ushort Code, string Description)
{
    public bool IsSuccess => Code == 0;

    /// <summary>Procura o LLRPStatus entre os parâmetros de topo do corpo (a partir de <paramref name="offset"/>).</summary>
    public static LlrpStatus? Find(ReadOnlySpan<byte> body, int offset = 0)
    {
        if (offset > body.Length)
            return null;
        var reader = new LlrpParamReader(body[offset..]);
        while (reader.TryRead(out var p))
        {
            if (p.Type != LlrpParamType.LlrpStatus || p.IsTv || p.Value.Length < 4)
                continue;
            var code = BinaryPrimitives.ReadUInt16BigEndian(p.Value);
            var count = BinaryPrimitives.ReadUInt16BigEndian(p.Value[2..]);
            var text = count <= p.Value.Length - 4 ? Encoding.UTF8.GetString(p.Value.Slice(4, count)) : "";
            return new LlrpStatus(code, text);
        }

        return null;
    }
}
