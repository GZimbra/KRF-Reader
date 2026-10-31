using KrfReader.Application.Configuration;
using KrfReader.Domain;

namespace KrfReader.Application.Pipeline;

/// <summary>
/// Mapa porta → direção do modo POR ANTENA (imutável). Porta fora do mapa não movimenta estoque.
/// </summary>
public sealed class AntennaDirectionMap : IEquatable<AntennaDirectionMap>
{
    private readonly Dictionary<int, OperationMode> _byPort;

    private AntennaDirectionMap(int[] entrada, int[] saida, TimeSpan arbitration, TimeSpan zoneHold)
    {
        Entrada = entrada;
        Saida = saida;
        ArbitrationWindow = arbitration;
        ZoneHold = zoneHold;
        _byPort = entrada.Select(p => KeyValuePair.Create(p, OperationMode.Entrada))
            .Concat(saida.Select(p => KeyValuePair.Create(p, OperationMode.Saida)))
            .ToDictionary();
    }

    public IReadOnlyList<int> Entrada { get; }

    public IReadOnlyList<int> Saida { get; }

    /// <summary>Ver <see cref="DockOptions.ArbitrationMs"/>.</summary>
    public TimeSpan ArbitrationWindow { get; }

    /// <summary>Ver <see cref="DockOptions.ZoneHoldMs"/>.</summary>
    public TimeSpan ZoneHold { get; }

    public IEnumerable<int> Ports => _byPort.Keys;

    /// <summary>Direção da porta; null = porta sem doca.</summary>
    public OperationMode? Resolve(int antenna) => _byPort.TryGetValue(antenna, out var mode) ? mode : null;

    /// <summary>Ex.: "ENTRADA 1,2 · SAÍDA 3,4".</summary>
    public string Describe()
    {
        var parts = new List<string>(2);
        if (Entrada.Count > 0)
            parts.Add($"ENTRADA {string.Join(",", Entrada)}");
        if (Saida.Count > 0)
            parts.Add($"SAÍDA {string.Join(",", Saida)}");
        return string.Join(" · ", parts);
    }

    public override string ToString() => Describe();

    /// <summary>
    /// Valida as docas: portas 1–64, disjuntas, ao menos uma porta e, se <paramref name="activePorts"/> vier, todas ativas
    /// no leitor (porta de doca desligada nunca leria e a doca ficaria cega sem aviso).
    /// </summary>
    public static bool TryCreate(
        DockOptions? options, IReadOnlyCollection<int>? activePorts, out AntennaDirectionMap map, out string error)
    {
        map = null!;
        if (options is null || !options.IsConfigured)
        {
            error = "Docas por antena não configuradas: informe as antenas de ENTRADA e/ou SAÍDA em Configurações.";
            return false;
        }

        var entrada = Normalize(options.EntradaAntennas);
        var saida = Normalize(options.SaidaAntennas);
        if (First(entrada.Concat(saida), p => p is < 1 or > 64) is { } invalid)
        {
            error = $"Antena {invalid} inválida nas docas: use portas de 1 a 64.";
            return false;
        }

        if (First(entrada.Intersect(saida), _ => true) is { } both)
        {
            error = $"Antena {both} está nas duas docas: cada porta pertence a uma doca só.";
            return false;
        }

        if (activePorts is { Count: > 0 } && First(entrada.Concat(saida), p => !activePorts.Contains(p)) is { } off)
        {
            error = $"Antena {off} está numa doca mas não está nas antenas ativas do leitor.";
            return false;
        }

        if (options.ArbitrationMs is < 0 or > 5000)
        {
            error = "Janela de arbitragem deve estar entre 0 e 5000 ms.";
            return false;
        }

        if (options.ZoneHoldMs is < 0 or > 60_000)
        {
            error = "Retenção de zona deve estar entre 0 e 60000 ms.";
            return false;
        }

        map = new AntennaDirectionMap(
            entrada, saida, TimeSpan.FromMilliseconds(options.ArbitrationMs), TimeSpan.FromMilliseconds(options.ZoneHoldMs));
        error = "";
        return true;
    }

    public static AntennaDirectionMap Create(DockOptions options, IReadOnlyCollection<int>? activePorts = null) =>
        TryCreate(options, activePorts, out var map, out var error) ? map : throw new ArgumentException(error, nameof(options));

    public bool Equals(AntennaDirectionMap? other) =>
        other is not null
        && Entrada.SequenceEqual(other.Entrada)
        && Saida.SequenceEqual(other.Saida)
        && ArbitrationWindow == other.ArbitrationWindow
        && ZoneHold == other.ZoneHold;

    public override bool Equals(object? obj) => Equals(obj as AntennaDirectionMap);

    public override int GetHashCode() => HashCode.Combine(Entrada.Count, Saida.Count, ArbitrationWindow, ZoneHold);

    private static int? First(IEnumerable<int> ports, Func<int, bool> predicate)
    {
        foreach (var p in ports)
        {
            if (predicate(p))
                return p;
        }

        return null;
    }

    private static int[] Normalize(int[]? ports) => (ports ?? []).Distinct().Order().ToArray();
}

/// <summary>
/// Operação de inventário ativa: modo manual (direção fixa escolhida pelo operador, leitor de 1 porta) ou POR ANTENA
/// (direção pela porta que leu; entrada e saída simultâneas no mesmo leitor).
/// </summary>
public sealed record InventoryOperation
{
    private InventoryOperation(OperationMode? fixedMode, AntennaDirectionMap? antennas)
    {
        FixedMode = fixedMode;
        Antennas = antennas;
    }

    /// <summary>Direção fixa do modo manual; null no modo POR ANTENA.</summary>
    public OperationMode? FixedMode { get; }

    /// <summary>Mapa de docas do modo POR ANTENA; null no modo manual.</summary>
    public AntennaDirectionMap? Antennas { get; }

    public bool IsByAntenna => Antennas is not null;

    public static InventoryOperation Manual(OperationMode mode) =>
        Enum.IsDefined(mode) ? new(mode, null) : throw new ArgumentOutOfRangeException(nameof(mode), mode, "Modo inválido.");

    public static InventoryOperation ByAntenna(AntennaDirectionMap map) =>
        new(null, map ?? throw new ArgumentNullException(nameof(map)));

    /// <summary>Direção de uma leitura; null = porta sem doca (só no modo POR ANTENA).</summary>
    public OperationMode? Resolve(int antenna) => FixedMode ?? Antennas!.Resolve(antenna);

    public override string ToString() => FixedMode is { } m ? m.ToString() : $"POR ANTENA ({Antennas})";
}
