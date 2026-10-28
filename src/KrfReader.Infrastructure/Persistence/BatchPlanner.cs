using KrfReader.Application.Persistence;
using KrfReader.Domain;

namespace KrfReader.Infrastructure.Persistence;

/// <summary>
/// Parte de <see cref="IInventoryRepository.ApplyBatchAsync"/> que independe do banco: dado o estado atual dos EPCs,
/// decide (via <see cref="InventoryRules"/>) leitura a leitura, em ordem, e produz as linhas de upsert de Tags e de
/// insert de Eventos. Cada provider só carrega o estado e grava o plano, na mesma transação.
/// Datas truncadas em milissegundos (UTC): é a precisão gravada nos dois bancos, então a comparação
/// "leitura mais recente" aqui é a mesma que o banco faria.
/// </summary>
internal static class BatchPlanner
{
    /// <summary>EPCs normalizados na ordem das leituras. Lança se algum for inválido (antes de abrir conexão).</summary>
    public static string[] NormalizeEpcs(IReadOnlyList<TagRead> reads)
    {
        var epcs = new string[reads.Count];
        for (var i = 0; i < reads.Count; i++)
        {
            epcs[i] = Epc.Normalize(reads[i].Epc)
                ?? throw new ArgumentException($"EPC inválido na posição {i}: '{reads[i].Epc}'.", nameof(reads));
        }
        return epcs;
    }

    /// <param name="state">Status atual por EPC (ausente = tag desconhecida). É atualizado com o estado final.</param>
    public static Plan Build(OperationMode mode, IReadOnlyList<TagRead> reads, string[] epcs, Dictionary<string, TagStatus?> state)
    {
        var applied = new AppliedRead[reads.Count];
        var pending = new Dictionary<string, PendingTag>(StringComparer.Ordinal);
        var events = new List<EventInsert>();

        for (var i = 0; i < reads.Count; i++)
        {
            var read = reads[i];
            var epc = epcs[i];
            state.TryGetValue(epc, out var previous);
            var decision = InventoryRules.Decide(previous, mode);
            var timestamp = DbTimestamp.TruncateToMillisUtc(read.TimestampUtc);

            applied[i] = new AppliedRead(read, mode, previous, decision.NewStatus, decision.Event);
            state[epc] = decision.NewStatus;

            if (!pending.TryGetValue(epc, out var tag))
            {
                tag = new PendingTag(timestamp, read.AntennaId, read.Rssi);
                pending[epc] = tag;
            }
            tag.Status = decision.NewStatus;
            if (timestamp >= tag.UltimaLeitura)
            {
                tag.UltimaLeitura = timestamp;
                tag.UltimaAntenaId = read.AntennaId;
                tag.UltimaRssi = read.Rssi;
            }

            if (decision.Event is { } evt)
            {
                tag.StatusChanged = true;
                tag.UltimaAtualizacaoStatus = timestamp;
                events.Add(new EventInsert(epc, evt, timestamp, read.AntennaId, read.Rssi));
            }
        }

        var upserts = new List<TagUpsert>(pending.Count);
        foreach (var (epc, tag) in pending)
        {
            upserts.Add(new TagUpsert(
                epc,
                tag.Status,
                tag.UltimaLeitura,
                // Para tag já existente sem mudança, o valor é ignorado pelo upsert (StatusChanged = false).
                tag.UltimaAtualizacaoStatus ?? tag.UltimaLeitura,
                tag.UltimaAntenaId,
                tag.UltimaRssi,
                tag.StatusChanged));
        }

        return new Plan(applied, upserts, events);
    }

    internal sealed record Plan(AppliedRead[] Applied, IReadOnlyList<TagUpsert> Upserts, IReadOnlyList<EventInsert> Events);

    internal sealed record TagUpsert(
        string EpcId, TagStatus Status, DateTime UltimaLeitura, DateTime UltimaAtualizacaoStatus,
        int UltimaAntenaId, double UltimaRssi, bool StatusChanged);

    internal sealed record EventInsert(string EpcId, EventType Tipo, DateTime Timestamp, int AntenaId, double Rssi);

    private sealed class PendingTag(DateTime firstTimestamp, int antennaId, double rssi)
    {
        public TagStatus Status { get; set; }
        public DateTime UltimaLeitura { get; set; } = firstTimestamp;
        public int UltimaAntenaId { get; set; } = antennaId;
        public double UltimaRssi { get; set; } = rssi;
        public DateTime? UltimaAtualizacaoStatus { get; set; }
        public bool StatusChanged { get; set; }
    }
}
