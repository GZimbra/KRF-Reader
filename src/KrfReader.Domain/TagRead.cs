namespace KrfReader.Domain;

/// <summary>
/// Leitura bruta vinda do leitor. A tag carrega só o EPC; todo o resto é contexto da leitura.
/// <paramref name="TimestampUtc"/> é sempre UTC (<see cref="DateTimeKind.Utc"/>).
/// </summary>
public readonly record struct TagRead(string Epc, double Rssi, int AntennaId, DateTime TimestampUtc);
