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
/// <para>
/// Anticolisão (<see cref="IInventoryParameterProtocol"/>): Set Query (<c>0E</c>) define Session, Target e Q do
/// multi-poll; Set Select (<c>0C</c>) + Select Mode (<c>12</c>) restringem a rodada às tags com o prefixo de EPC.
/// Durante o multi-poll o módulo só atende a parada, por isso a troca de parâmetros é parar → configurar → iniciar.
/// </para>
/// </summary>
public sealed class M100Protocol : IReaderProtocol, IInventoryRefreshProtocol, IInventoryParameterProtocol, IProtocolDiagnostics
{
    public const string R200Name = "R200";
    public const string M100Name = "M100";
    public const int AntennaId = 1;
    public const double MinPowerDbm = 15;
    public const double MaxPowerDbm = 26;

    /// <summary>Maior PL aceito; acima disso o "header" é tratado como lixo.</summary>
    public const int MaxParamLength = 512;

    internal const byte TypeCommand = 0x00;
    internal const byte TypeResponse = 0x01;
    internal const byte TypeNotice = 0x02;
    internal const byte CmdSinglePoll = 0x22;
    internal const byte CmdMultiPoll = 0x27;
    internal const byte CmdStopMultiPoll = 0x28;
    internal const byte CmdSetPower = 0xB6;
    internal const byte CmdSetRegion = 0x07;
    internal const byte CmdInsertChannels = 0xA9;
    internal const byte CmdSetAutoHopping = 0xAD;
    internal const byte CmdSetSelect = 0x0C;
    internal const byte CmdSetSelectMode = 0x12;
    internal const byte CmdSetQuery = 0x0E;
    internal const byte CmdError = 0xFF;

    /// <summary>Código de erro do M100 para rodada de inventário sem tag (ou só colisões).</summary>
    internal const byte ErrorInventoryFail = 0x15;

    /// <summary>Select Mode: 00 = Select antes de toda operação (inclusive polling), 01 = nunca.</summary>
    internal const byte SelectModeAlways = 0x00;
    internal const byte SelectModeNever = 0x01;

    /// <summary>SelParam: Target=100 (flag SL), Action=000 (casou → afirma SL, não casou → nega), MemBank=01 (EPC).</summary>
    internal const byte SelParamSlEpc = 0b100_000_01;

    /// <summary>EPC começa no bit 0x20 do banco EPC (depois de CRC-16 e PC).</summary>
    internal const uint EpcBitPointer = 0x20;

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

    /// <summary>Rodadas do multi-poll sem tag lida (erro 0x15) desde a criação.</summary>
    public long EmptyRounds { get; private set; }

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
            else if (IsInventoryFail(frame))
                EmptyRounds++;
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

    public IReadOnlyList<byte[]> BuildProfileCommands(InventoryProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var mask = ParseSelectMask(profile.SelectEpcPrefix);
        var parameters = profile.Parameters with { UseSelect = mask.Length > 0 };
        if (mask.Length == 0)
            return [BuildFrame(CmdSetSelectMode, [SelectModeNever]), BuildSetQuery(parameters)];

        return
        [
            BuildSetSelect(mask),
            BuildFrame(CmdSetSelectMode, [SelectModeAlways]),
            BuildSetQuery(parameters),
        ];
    }

    public IReadOnlyList<byte[]> BuildApplyCommands(InventoryParameters parameters, bool inventoryRunning) =>
        inventoryRunning
            ? [BuildStopInventoryCommand(), BuildSetQuery(parameters), BuildStartInventoryCommand()]
            : [BuildSetQuery(parameters)];

    /// <summary>
    /// Set Query, 2 bytes big-endian: DR(1) M(2) TRext(1) Sel(2) Session(2) Target(1) Q(4) reservado(3).
    /// DR=8, M=1 (FM0) e TRext=1 (pilot tone) como no padrão do módulo; Sel=00 (todas) ou 11 (só SL).
    /// </summary>
    internal byte[] BuildSetQuery(InventoryParameters parameters)
    {
        var q = Math.Clamp(parameters.Q, InventoryParameters.MinQ, InventoryParameters.MaxQ);
        var sel = parameters.UseSelect ? 0b11 : 0b00;
        var word = (1 << 12)
                   | (sel << 10)
                   | (((int)parameters.Session & 0b11) << 8)
                   | (((int)parameters.Target & 0b1) << 7)
                   | (q << 3);
        return BuildFrame(CmdSetQuery, [(byte)(word >> 8), (byte)word]);
    }

    /// <summary>Set Select: SelParam | Ptr(4, em bits) | MaskLen(bits) | Truncate | Mask.</summary>
    internal byte[] BuildSetSelect(ReadOnlySpan<byte> mask)
    {
        var parameters = new byte[7 + mask.Length];
        parameters[0] = SelParamSlEpc;
        parameters[1] = (byte)(EpcBitPointer >> 24);
        parameters[2] = (byte)(EpcBitPointer >> 16);
        parameters[3] = (byte)(EpcBitPointer >> 8);
        parameters[4] = (byte)EpcBitPointer;
        parameters[5] = (byte)(mask.Length * 8);
        parameters[6] = 0x00;
        mask.CopyTo(parameters.AsSpan(7));
        return BuildFrame(CmdSetSelect, parameters);
    }

    /// <summary>Prefixo hex → bytes da máscara. Exige bytes inteiros (número par de dígitos) e no máximo 31 bytes (MaskLen ≤ 255 bits).</summary>
    public static byte[] ParseSelectMask(string? prefix)
    {
        var hex = Epc.Normalize(prefix);
        if (string.IsNullOrWhiteSpace(prefix))
            return [];
        if (hex is null || hex.Length % 2 != 0 || hex.Length > 62)
            throw new ArgumentException(
                $"Prefixo de Select inválido: \"{prefix}\". Use hexadecimal com número par de dígitos (bytes inteiros), até 62 dígitos.",
                nameof(prefix));
        return Convert.FromHexString(hex);
    }

    private static bool IsInventoryFail(ReadOnlySpan<byte> frame) =>
        frame[1] == TypeResponse && frame[2] == CmdError && frame.Length > FrameOverhead && frame[5] == ErrorInventoryFail;

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
