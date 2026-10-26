using System.Globalization;
using System.Text;
using KrfReader.Application.Readers;
using KrfReader.Domain;

namespace KrfReader.Infrastructure.Readers.Protocols;

/// <summary>
/// Protocolo texto de referência, uma leitura por linha:
/// <c>EPC,RSSI,ANTENA[,TIMESTAMP_ISO8601]\n</c> (aceita <c>\r\n</c>).
/// <para>
/// Linhas vazias ou iniciadas por <c>#</c> são ignoradas; linhas inválidas são descartadas (contadas em
/// <see cref="InvalidLines"/>). Sem timestamp, usa o instante de recepção. Uma linha acima de
/// <see cref="MaxLineBytes"/> sem terminador é descartada inteira (proteção contra stream lixo).
/// </para>
/// <para>
/// Útil para testes com netcat (<c>nc -l 4001</c>) e como modelo para escrever o driver de um protocolo real:
/// implemente <see cref="IReaderProtocol"/> e registre em <see cref="ReaderProtocolFactory.Register"/>.
/// </para>
/// </summary>
public sealed class AsciiLineProtocol : IReaderProtocol
{
    public const string ProtocolName = "AsciiLine";
    public const int MaxLineBytes = 1024;

    private static readonly byte[] StartCommand = Encoding.ASCII.GetBytes("START\n");
    private static readonly byte[] StopCommand = Encoding.ASCII.GetBytes("STOP\n");

    private readonly byte[] _line = new byte[MaxLineBytes];
    private int _length;
    private bool _discardingOversizedLine;

    public string Name => ProtocolName;

    /// <summary>Linhas descartadas por formato inválido ou tamanho excessivo desde a criação.</summary>
    public long InvalidLines { get; private set; }

    public IReadOnlyList<TagRead> Feed(ReadOnlySpan<byte> data, DateTime receivedAtUtc)
    {
        List<TagRead>? reads = null;
        var receivedUtc = receivedAtUtc.Kind switch
        {
            DateTimeKind.Local => receivedAtUtc.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(receivedAtUtc, DateTimeKind.Utc),
            _ => receivedAtUtc,
        };

        foreach (var b in data)
        {
            if (b == (byte)'\n')
            {
                if (!_discardingOversizedLine)
                {
                    if (TryParseLine(_line.AsSpan(0, _length), receivedUtc, out var read, out var ignored))
                        (reads ??= []).Add(read);
                    else if (!ignored)
                        InvalidLines++;
                }

                _length = 0;
                _discardingOversizedLine = false;
                continue;
            }

            if (_discardingOversizedLine)
                continue;

            if (_length == MaxLineBytes)
            {
                _length = 0;
                _discardingOversizedLine = true;
                InvalidLines++;
                continue;
            }

            _line[_length++] = b;
        }

        return reads ?? (IReadOnlyList<TagRead>)Array.Empty<TagRead>();
    }

    public IReadOnlyList<byte[]> BuildConfigureCommands(double powerDbm, IReadOnlyCollection<int> antennas)
    {
        var commands = new List<byte[]>(2)
        {
            Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"POWER {powerDbm:0.#}\n")),
        };

        if (antennas is { Count: > 0 })
            commands.Add(Encoding.ASCII.GetBytes($"ANT {string.Join(',', antennas.Distinct())}\n"));

        return commands;
    }

    public byte[] BuildStartInventoryCommand() => (byte[])StartCommand.Clone();

    public byte[] BuildStopInventoryCommand() => (byte[])StopCommand.Clone();

    public void Reset()
    {
        _length = 0;
        _discardingOversizedLine = false;
    }

    /// <summary>Faz o parsing de uma linha (sem o <c>\n</c>). <paramref name="ignored"/> = comentário/vazia.</summary>
    internal static bool TryParseLine(ReadOnlySpan<byte> raw, DateTime receivedAtUtc, out TagRead read, out bool ignored)
    {
        read = default;
        ignored = false;

        var text = Encoding.UTF8.GetString(raw).Trim();
        if (text.Length == 0 || text[0] == '#')
        {
            ignored = true;
            return false;
        }

        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length is < 3 or > 4)
            return false;

        var epc = Epc.Normalize(parts[0]);
        if (epc is null)
            return false;

        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var rssi) || !double.IsFinite(rssi))
            return false;

        if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var antenna))
            return false;

        var timestamp = receivedAtUtc;
        if (parts.Length == 4 && parts[3].Length > 0)
        {
            if (!DateTimeOffset.TryParse(parts[3], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
                return false;
            timestamp = dto.UtcDateTime;
        }

        read = new TagRead(epc, rssi, antenna, timestamp);
        return true;
    }
}
