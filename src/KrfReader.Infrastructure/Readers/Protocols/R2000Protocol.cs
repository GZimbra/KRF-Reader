using KrfReader.Application.Readers;
using KrfReader.Domain;

namespace KrfReader.Infrastructure.Readers.Protocols;

/// <summary>
/// Protocolo serial dos leitores com chip Impinj R2000 ("A0", usado por Invelion/Yanzeo/Chafon R2000 e muitos
/// leitores de mesa/fixos chineses), padrão 115200 8N1 — também sobre TCP (porta típica 4001).
/// <para>
/// Quadro: <c>A0 | Len | Endereço | Cmd | Dados | Check</c>, onde Len conta de Endereço até Check e
/// Check = complemento de 2 da soma de A0 até o último dado. Endereço <see cref="BroadcastAddress"/> (público).
/// </para>
/// <para>
/// Inventário em tempo real (<c>0x89</c>) roda um número fixo de rodadas e termina sozinho; por isso o comando é
/// reenviado a cada <see cref="InventoryRefreshInterval"/> e a parada não envia nada. Resposta de tag:
/// <c>FreqAnt | PC(2) | EPC(n) | RSSI</c>; fim de rodada (Len 0x0A) e erros (Len 0x04) são descartados.
/// RSSI em dBm = (byte &amp; 0x7F) − 129. Antena = (FreqAnt &amp; 0x03) + 1.
/// </para>
/// <para>
/// Limitação: só a primeira antena da lista é configurada como antena de trabalho (multiantena exige o
/// comando 0x8A de troca rápida). NÃO validado em hardware neste projeto.
/// </para>
/// </summary>
public sealed class R2000Protocol : IReaderProtocol, IInventoryRefreshProtocol
{
    public const string ProtocolName = "R2000";
    public const byte Head = 0xA0;
    public const byte BroadcastAddress = 0xFF;
    public const double MinPowerDbm = 0;
    public const double MaxPowerDbm = 33;

    internal const byte CmdSetWorkAntenna = 0x74;
    internal const byte CmdSetOutputPower = 0x76;
    internal const byte CmdRealTimeInventory = 0x89;

    private const byte InventoryRepeat = 0x05;
    private const byte RoundSummaryLength = 0x0A;
    private const int MinTagLength = 3 + 1 + 2 + 1 + 1 + 1; // addr cmd freqAnt PC(2) EPC(≥1) RSSI check

    private byte[] _buffer = new byte[1024];
    private int _length;

    public string Name => ProtocolName;

    public TimeSpan InventoryRefreshInterval => TimeSpan.FromMilliseconds(300);

    /// <summary>Quadros descartados por checksum inválido desde a criação.</summary>
    public long InvalidFrames { get; private set; }

    public IReadOnlyList<TagRead> Feed(ReadOnlySpan<byte> data, DateTime receivedAtUtc)
    {
        var receivedUtc = receivedAtUtc.Kind switch
        {
            DateTimeKind.Local => receivedAtUtc.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(receivedAtUtc, DateTimeKind.Utc),
            _ => receivedAtUtc,
        };

        if (_length + data.Length > _buffer.Length)
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + data.Length));
        data.CopyTo(_buffer.AsSpan(_length));
        _length += data.Length;

        List<TagRead>? reads = null;
        var pos = 0;
        while (true)
        {
            var start = Array.IndexOf(_buffer, Head, pos, _length - pos);
            if (start < 0)
            {
                pos = _length;
                break;
            }

            pos = start;
            if (_length - start < 2)
                break;

            var len = _buffer[start + 1];
            if (len < 3)
            {
                InvalidFrames++;
                pos = start + 1;
                continue;
            }

            var frameLength = 2 + len;
            if (_length - start < frameLength)
                break;

            var frame = _buffer.AsSpan(start, frameLength);
            if (frame[^1] != Check(frame[..^1]))
            {
                InvalidFrames++;
                pos = start + 1;
                continue;
            }

            if (TryParseTag(frame, receivedUtc, out var read))
                (reads ??= []).Add(read);
            pos = start + frameLength;
        }

        if (pos > 0)
        {
            _length -= pos;
            if (_length > 0)
                Buffer.BlockCopy(_buffer, pos, _buffer, 0, _length);
        }

        return reads ?? (IReadOnlyList<TagRead>)Array.Empty<TagRead>();
    }

    public IReadOnlyList<byte[]> BuildConfigureCommands(double powerDbm, IReadOnlyCollection<int> antennas)
    {
        var antenna = antennas is { Count: > 0 } ? Math.Clamp(antennas.Min(), 1, 4) : 1;
        var power = (byte)Math.Round(Math.Clamp(powerDbm, MinPowerDbm, MaxPowerDbm));
        return
        [
            BuildFrame(CmdSetWorkAntenna, [(byte)(antenna - 1)]),
            BuildFrame(CmdSetOutputPower, [power]),
        ];
    }

    public byte[] BuildStartInventoryCommand() => BuildFrame(CmdRealTimeInventory, [InventoryRepeat]);

    /// <summary>O inventário em tempo real termina sozinho ao fim das rodadas; basta não rearmar.</summary>
    public byte[] BuildStopInventoryCommand() => [];

    public void Reset() => _length = 0;

    internal static byte[] BuildFrame(byte command, ReadOnlySpan<byte> data)
    {
        var frame = new byte[5 + data.Length];
        frame[0] = Head;
        frame[1] = (byte)(3 + data.Length);
        frame[2] = BroadcastAddress;
        frame[3] = command;
        data.CopyTo(frame.AsSpan(4));
        frame[^1] = Check(frame.AsSpan(0, frame.Length - 1));
        return frame;
    }

    internal static byte Check(ReadOnlySpan<byte> headToLastData)
    {
        var sum = 0;
        foreach (var b in headToLastData)
            sum += b;
        return (byte)(~sum + 1);
    }

    private static bool TryParseTag(ReadOnlySpan<byte> frame, DateTime receivedUtc, out TagRead read)
    {
        read = default;
        var len = frame[1];
        if (frame[3] != CmdRealTimeInventory || len == RoundSummaryLength || frame.Length < 2 + MinTagLength)
            return false;

        var freqAnt = frame[4];
        var epcBytes = frame[7..^2];
        var epc = Epc.Normalize(Convert.ToHexString(epcBytes));
        if (epc is null)
            return false;

        var rssi = (frame[^2] & 0x7F) - 129;
        read = new TagRead(epc, rssi, (freqAnt & 0x03) + 1, receivedUtc);
        return true;
    }
}
