namespace KrfReader.Application.Readers;

/// <summary>Porta de antena ativa e a potência pedida para ela.</summary>
public readonly record struct AntennaPort(int Port, double PowerDbm);

/// <summary>Configuração de RF aplicada ao conectar: portas ativas (com potência individual) e faixa Anatel.</summary>
public sealed record RadioSetup(IReadOnlyList<AntennaPort> Ports, bool EnforceAnatelBand);

/// <summary>
/// Opcional para <see cref="IReaderProtocol"/>: leitores multiporta com potência por porta e/ou região configurável.
/// Quando implementado, o transporte chama <see cref="BuildRadioCommands"/> no lugar de
/// <see cref="IReaderProtocol.BuildConfigureCommands"/>.
/// </summary>
public interface IMultiAntennaProtocol
{
    IReadOnlyList<byte[]> BuildRadioCommands(RadioSetup setup);
}

/// <summary>
/// Faixa de RFID UHF usada pelo app no Brasil (Anatel, radiação restrita): 915–928 MHz. Um leitor em região FCC
/// pura (902–928 MHz) salta por 907,5–915 MHz, que não é permitido aqui.
/// </summary>
public static class AnatelBand
{
    public const int MinKhz = 915_000;
    public const int MaxKhz = 928_000;

    /// <summary>Meia largura de canal assumida (canais de 500 kHz).</summary>
    public const int HalfChannelKhz = 250;

    public const string Description = "915–928 MHz (Anatel)";

    /// <summary>true se o canal centrado em <paramref name="centerKhz"/> cabe inteiro na faixa.</summary>
    public static bool Contains(long centerKhz) =>
        centerKhz - HalfChannelKhz >= MinKhz && centerKhz + HalfChannelKhz <= MaxKhz;

    /// <summary>Canais de 500 kHz na grade FCC (902,75 + n·0,5 MHz) que cabem na faixa: 915,25–927,25 MHz.</summary>
    public static IReadOnlyList<int> FccGridChannelsKhz { get; } =
        Enumerable.Range(0, 50).Select(n => 902_750 + n * 500).Where(f => Contains(f)).ToArray();

    public static string FormatRange(IEnumerable<long> frequenciesKhz)
    {
        var list = frequenciesKhz.ToArray();
        return list.Length == 0
            ? "nenhuma frequência"
            : $"{list.Min() / 1000.0:0.00}–{list.Max() / 1000.0:0.00} MHz ({list.Length} canais)";
    }
}

/// <summary>Identificação lida do leitor após conectar (modelo, firmware, portas, faixa em uso).</summary>
public sealed record ReaderInfo(string Description);

/// <summary>Opcional para <see cref="IRfidReader"/>: leitores que se identificam na conexão (LLRP, Impinj IoT).</summary>
public interface IReaderInfoSource
{
    /// <summary>Null até a primeira conexão bem-sucedida.</summary>
    ReaderInfo? Info { get; }
}
