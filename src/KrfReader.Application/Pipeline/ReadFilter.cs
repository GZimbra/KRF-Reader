using System.Collections.Frozen;
using KrfReader.Domain;

namespace KrfReader.Application.Pipeline;

/// <summary>
/// Filtro de RSSI/antena. Thread-safe: as configurações ficam num snapshot imutável trocado atomicamente,
/// então <see cref="Accept"/> nunca vê o RSSI de uma versão com as antenas de outra.
/// </summary>
public sealed class ReadFilter
{
    private Settings _settings;

    public ReadFilter(double minRssi, IReadOnlyCollection<int> allowedAntennas)
    {
        _settings = Create(minRssi, allowedAntennas);
    }

    public double MinRssi => Volatile.Read(ref _settings).MinRssi;

    public IReadOnlyCollection<int> AllowedAntennas => Volatile.Read(ref _settings).Antennas;

    /// <summary>RSSI &gt;= mínimo e (lista de antenas vazia OU contém a antena).</summary>
    public bool Accept(in TagRead read)
    {
        var s = Volatile.Read(ref _settings);
        return read.Rssi >= s.MinRssi && (s.Antennas.Count == 0 || s.Antennas.Contains(read.AntennaId));
    }

    public void Update(double minRssi, IReadOnlyCollection<int> allowedAntennas) =>
        Volatile.Write(ref _settings, Create(minRssi, allowedAntennas));

    private static Settings Create(double minRssi, IReadOnlyCollection<int> allowedAntennas)
    {
        ArgumentNullException.ThrowIfNull(allowedAntennas);
        if (double.IsNaN(minRssi))
            throw new ArgumentException("RSSI mínimo não pode ser NaN.", nameof(minRssi));
        return new Settings(minRssi, allowedAntennas.ToFrozenSet());
    }

    private sealed record Settings(double MinRssi, FrozenSet<int> Antennas);
}
