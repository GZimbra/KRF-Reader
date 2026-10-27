using KrfReader.Application.Readers;
using KrfReader.Domain;

namespace KrfReader.Infrastructure.Readers.Protocols;

/// <summary>
/// Família MagicRF M100 (módulos R200, JRD-100, YRM100 e similares) via UART, padrão 115200 8N1.
/// <para>
/// Quadro: <c>HEADER | Tipo | Cmd | PL(2, big-endian) | Params | Checksum | END</c>, onde Tipo = 00 comando,
/// 01 resposta, 02 notificação, e Checksum = byte baixo da soma de Tipo até o último parâmetro.
/// Leitura de tag = notificação <c>02 22</c> com params <c>RSSI(int8 dBm) | PC(2) | EPC(n) | CRC(2)</c>.
/// Respostas a comandos e erros (<c>Cmd FF</c>, ex.: 0x15 = nenhuma tag na rodada) são descartados.
/// </para>
/// <para>
/// Variantes (mesmos comandos, delimitadores diferentes):
/// <list type="bullet">
/// <item><description><see cref="R200Name"/>: <c>AA … DD</c> (módulo R200, firmware "M100 26dBm V1.0"; validado em hardware).</description></item>
/// <item><description><see cref="M100Name"/>: <c>BB … 7E</c> (manual MagicRF M100 original, JRD-100/YRM100).</description></item>
/// </list>
/// </para>
/// <para>
/// Módulo de antena única: toda leitura sai com <see cref="AntennaId"/>. Potência aceita:
/// <see cref="MinPowerDbm"/>–<see cref="MaxPowerDbm"/> dBm (o R200 fixa 15 dBm para valores menores; aqui é
/// limitado antes de enviar). O multi-poll tem número finito de rodadas, então o inventário é rearmado
/// periodicamente (<see cref="IInventoryRefreshProtocol"/>).
/// </para>
/// </summary>
public sealed class M100Protocol : IReaderProtocol, IInventoryRefreshProtocol
{
    public const string R200Name = "R200";
    public const string M100Name = "M100";
    public const int AntennaId = 1;
    public const double MinPowerDbm = 15;
    public const double MaxPowerDbm = 26;

    /// <summary>Maior PL aceito; acima disso o "header" é tratado como lixo.</summary>
    public const int MaxParamLength = 512;

    internal const byte TypeCommand = 0x00;
    internal const byte TypeNotice = 0x02;
    internal const byte CmdSinglePoll = 0x22;
    internal const byte CmdMultiPoll = 0x27;
    internal const byte CmdStopMultiPoll = 0x28;
    internal const byte CmdSetPower = 0xB6;
    internal const byte CmdSetRegion = 0x07;
    internal const byte CmdInsertChannels = 0xA9;
    internal const byte CmdSetAutoHopping = 0xAD;

    /// <summary>Região "US" do M100: canal n = 902,25 + 0,5·n MHz (n = 0..51).</summary>
    internal const byte RegionUs = 0x02;

    /// <summary>
    /// Plano de canais Anatel (Brasil): 915–928 MHz. Canais US 26..51 = 915,25–927,75 MHz (26 canais, salto
    /// automático). A região US inteira (902–928) não serve: 907,5–915 MHz é proibido para RFID no Brasil.
    /// </summary>
    public const int FirstChannel = 26;
    public const int LastChannel = 51;
    public const string FrequencyBand = "915,25–927,75 MHz · 26 canais com salto (Anatel)";

    public static double ChannelMhz(int channel) => 902.25 + 0.5 * channel;

    private const int FrameOverhead = 7; // HEADER tipo cmd PLh PLl cs END
    private const ushort MultiPollRounds = 0xFFFF;

    private readonly byte _header;
    private readonly byte _end;
    private byte[] _buffer = new byte[1024];
    private int _length;

    private M100Protocol(string name, byte header, byte end)
    {
        Name = name;
        _header = header;
        _end = end;
    }

    /// <summary>Variante <c>AA … DD</c> (módulo R200).</summary>
    public static M100Protocol CreateR200() => new(R200Name, 0xAA, 0xDD);

    /// <summary>Variante <c>BB … 7E</c> (MagicRF M100, JRD-100, YRM100).</summary>
    public static M100Protocol CreateM100() => new(M100Name, 0xBB, 0x7E);

    public string Name { get; }

    public byte Header => _header;

    public byte End => _end;

    public TimeSpan InventoryRefreshInterval => TimeSpan.FromSeconds(30);

    /// <summary>Quadros descartados por checksum/terminador inválido desde a criação.</summary>
    public long InvalidFrames { get; private set; }

    public IReadOnlyList<TagRead> Feed(ReadOnlySpan<byte> data, DateTime receivedAtUtc)
    {
        var receivedUtc = receivedAtUtc.Kind switch
        {
            DateTimeKind.Local => receivedAtUtc.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(receivedAtUtc, DateTimeKind.Utc),
            _ => receivedAtUtc,
        };

        Append(data);

        List<TagRead>? reads = null;
        var pos = 0;
        while (true)
        {
            var start = Array.IndexOf(_buffer, _header, pos, _length - pos);
            if (start < 0)
            {
                pos = _length;
                break;
            }

            pos = start;
            if (_length - start < 5)
                break;

            var paramLength = (_buffer[start + 3] << 8) | _buffer[start + 4];
            if (paramLength > MaxParamLength)
            {
                InvalidFrames++;
                pos = start + 1;
                continue;
            }

            var frameLength = FrameOverhead + paramLength;
            if (_length - start < frameLength)
                break;

            var frame = _buffer.AsSpan(start, frameLength);
            if (frame[^1] != _end || frame[^2] != Checksum(frame[1..^2]))
            {
                InvalidFrames++;
                pos = start + 1;
                continue;
            }

            if (TryParseTagNotice(frame, receivedUtc, out var read))
                (reads ??= []).Add(read);
            pos = start + frameLength;
        }

        Compact(pos);
        return reads ?? (IReadOnlyList<TagRead>)Array.Empty<TagRead>();
    }

    public IReadOnlyList<byte[]> BuildConfigureCommands(double powerDbm, IReadOnlyCollection<int> antennas)
    {
        var centiDbm = (int)Math.Round(Math.Clamp(powerDbm, MinPowerDbm, MaxPowerDbm) * 100);
        var channels = new byte[1 + LastChannel - FirstChannel + 1];
        channels[0] = (byte)(channels.Length - 1);
        for (var i = 1; i < channels.Length; i++)
            channels[i] = (byte)(FirstChannel + i - 1);

        return
        [
            // Um multi-poll de sessão anterior pode continuar rodando no módulo.
            BuildFrame(CmdStopMultiPoll, []),
            BuildFrame(CmdSetRegion, [RegionUs]),
            BuildFrame(CmdInsertChannels, channels),
            BuildFrame(CmdSetAutoHopping, [0xFF]),
            BuildFrame(CmdSetPower, [(byte)(centiDbm >> 8), (byte)centiDbm]),
        ];
    }

    public byte[] BuildStartInventoryCommand() =>
        BuildFrame(CmdMultiPoll, [0x22, MultiPollRounds >> 8, MultiPollRounds & 0xFF]);

    public byte[] BuildStopInventoryCommand() => BuildFrame(CmdStopMultiPoll, []);

    public void Reset() => _length = 0;

    internal byte[] BuildFrame(byte command, ReadOnlySpan<byte> parameters)
    {
        var frame = new byte[FrameOverhead + parameters.Length];
        frame[0] = _header;
        frame[1] = TypeCommand;
        frame[2] = command;
        frame[3] = (byte)(parameters.Length >> 8);
        frame[4] = (byte)parameters.Length;
        parameters.CopyTo(frame.AsSpan(5));
        frame[^2] = Checksum(frame.AsSpan(1, 4 + parameters.Length));
        frame[^1] = _end;
        return frame;
    }

    internal static byte Checksum(ReadOnlySpan<byte> typeToLastParam)
    {
        var sum = 0;
        foreach (var b in typeToLastParam)
            sum += b;
        return (byte)sum;
    }

    private static bool TryParseTagNotice(ReadOnlySpan<byte> frame, DateTime receivedUtc, out TagRead read)
    {
        read = default;
        if (frame[1] != TypeNotice || frame[2] != CmdSinglePoll)
            return false;

        var parameters = frame[5..^2];
        // RSSI(1) + PC(2) + CRC(2) + ao menos 1 byte de EPC.
        if (parameters.Length < 6)
            return false;

        var epc = Epc.Normalize(Convert.ToHexString(parameters[3..^2]));
        if (epc is null)
            return false;

        read = new TagRead(epc, (sbyte)parameters[0], AntennaId, receivedUtc);
        return true;
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        if (_length + data.Length > _buffer.Length)
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + data.Length));
        data.CopyTo(_buffer.AsSpan(_length));
        _length += data.Length;
    }

    private void Compact(int consumed)
    {
        if (consumed <= 0)
            return;
        _length -= consumed;
        if (_length > 0)
            Buffer.BlockCopy(_buffer, consumed, _buffer, 0, _length);
    }
}
