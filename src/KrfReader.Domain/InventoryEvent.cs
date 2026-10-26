namespace KrfReader.Domain;

/// <summary>Linha da tabela <c>Eventos</c> (histórico de auditoria).</summary>
public sealed record InventoryEvent(
    long Id,
    string Epc,
    OperationMode Mode,
    EventType Type,
    DateTime TimestampUtc,
    int AntennaId,
    double Rssi)
{
    public bool IsAnomaly => Type == EventType.SaidaSemEntrada;
}
