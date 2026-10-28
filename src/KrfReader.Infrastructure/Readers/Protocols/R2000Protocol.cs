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
/// Multiporta (<see cref="IMultiAntennaProtocol"/>): com uma porta ativa usa 0x74 (antena de trabalho) + 0x89; com
/// duas ou mais, 0x8A (troca rápida de antena, portas 1–4, 1 ciclo por porta). Potência igual em todas as portas
/// vai em 0x76 com 1 byte; potências diferentes vão em 0x76 com 4 bytes (uma por porta), aceito só por firmwares
/// que suportam potência individual. Com faixa Anatel exigida, envia 0x78 região FCC com pontos 915,5–927,5 MHz.
/// NÃO validado em hardware neste projeto.
/// </para>
/// </summary>
public sealed class R2000Protocol : IReaderProtocol, IInventoryRefreshProtocol, IMultiAntennaProtocol
{
    public const string ProtocolName = "R2000";
    public const byte Head = 0xA0;
    public const byte BroadcastAddress = 0xFF;
    public const double MinPowerDbm = 0;
    public const double MaxPowerDbm = 33;

    internal const byte CmdSetWorkAntenna = 0x74;
    internal const byte CmdSetOutputPower = 0x76;
    internal const byte CmdRealTimeInventory = 0x89;
    internal const byte CmdFastSwitchInventory = 0x8A;
    internal const byte CmdSetFrequencyRegion = 0x78;

    public const int MaxPorts = 4;

    /// <summary>Região FCC no 0x78; os pontos de frequência seguem 902,0 + 0,5·(p − 7) MHz.</summary>
    internal const byte RegionFcc = 0x01;
    internal const byte AnatelStartPoint = 0x22; // 915,5 MHz
    internal const byte AnatelEndPoint = 0x3A;   // 927,5 MHz

    /// <summary>Portas ativas (1..4) da última configuração. Mais de uma = inventário 0x8A.</summary>
    private int[] _ports = [1];

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
        var antenna = antennas is { Count: > 0 } ? Math.Clamp(antennas.Min(), 1, MaxPorts) : 1;
        return BuildRadioCommands(new RadioSetup([new AntennaPort(antenna, powerDbm)], EnforceAnatelBand: false));
    }

    public IReadOnlyList<byte[]> BuildRadioCommands(RadioSetup setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        var ports = setup.Ports.Where(p => p.Port is >= 1 and <= MaxPorts).DistinctBy(p => p.Port).OrderBy(p => p.Port).ToArray();
        if (ports.Length == 0)
            ports = [new AntennaPort(1, setup.Ports.Count > 0 ? setup.Ports[0].PowerDbm : 20)];
        _ports = ports.Select(p => p.Port).ToArray();

        var commands = new List<byte[]>();
        if (setup.EnforceAnatelBand)
            commands.Add(BuildFrame(CmdSetFrequencyRegion, [RegionFcc, AnatelStartPoint, AnatelEndPoint]));
        if (ports.Length == 1)
            commands.Add(BuildFrame(CmdSetWorkAntenna, [(byte)(ports[0].Port - 1)]));

        var powers = ports.Select(p => ToPowerByte(p.PowerDbm)).ToArray();
        if (powers.Distinct().Count() == 1)
        {
            commands.Add(BuildFrame(CmdSetOutputPower, [powers[0]]));
        }
        else
        {
            // Portas inativas recebem a menor potência configurada (não transmitem no 0x8A, mas o comando exige 4 valores).
            var perPort = Enumerable.Repeat(powers.Min(), MaxPorts).ToArray();
            foreach (var p in ports)
                perPort[p.Port - 1] = ToPowerByte(p.PowerDbm);
            commands.Add(BuildFrame(CmdSetOutputPower, perPort));
        }

        return commands;
    }

    public byte[] BuildStartInventoryCommand()
    {
        if (_ports.Length <= 1)
            return BuildFrame(CmdRealTimeInventory, [InventoryRepeat]);

        // A Aloop B Bloop C Cloop D Dloop Interval Repeat; antena > 3 = pulada.
        var data = new byte[2 * MaxPorts + 2];
        for (var i = 0; i < MaxPorts; i++)
        {
            data[2 * i] = i < _ports.Length ? (byte)(_ports[i] - 1) : (byte)0xFF;
            data[2 * i + 1] = 1;
        }
        data[^2] = 0;
        data[^1] = 1;
        return BuildFrame(CmdFastSwitchInventory, data);
    }

    private static byte ToPowerByte(double dbm) => (byte)Math.Round(Math.Clamp(dbm, MinPowerDbm, MaxPowerDbm));

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
        if (frame[3] is not (CmdRealTimeInventory or CmdFastSwitchInventory) || len == RoundSummaryLength || frame.Length < 2 + MinTagLength)
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
