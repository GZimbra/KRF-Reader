using KrfReader.Application.Persistence;
using KrfReader.Domain;

namespace KrfReader.Infrastructure.Persistence;

/// <summary>
/// Parte de <see cref="IInventoryRepository.ApplyBatchAsync"/> que independe do banco: dado o estado atual dos EPCs,
/// decide (via <see cref="InventoryRules"/>) leitura a leitura, em ordem, e produz as linhas de upsert de Tags e de
/// insert de Eventos. Cada provider só carrega o estado e grava o plano, na mesma transação.
/// <para>
/// Item de estoque: EPC vinculado a embalagem decide sobre o status DA EMBALAGEM (a segunda etiqueta da caixa no mesmo
/// modo é releitura); EPC avulso decide sobre o próprio status. A linha de Tags de cada etiqueta lida recebe o status
/// do item; as etiquetas da embalagem que não foram lidas são alinhadas pelo provider (UPDATE por EmbalagemId).
/// </para>
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
    public static Plan Build(OperationMode mode, IReadOnlyList<TagRead> reads, string[] epcs, Dictionary<string, TagStatus?> state) =>
        Build(mode, reads, epcs, state, new Dictionary<string, PackageState>(StringComparer.Ordinal));

    /// <param name="state">Status atual por EPC (ausente = tag desconhecida). É atualizado com o estado final.</param>
    /// <param name="packages">Embalagem de cada EPC do lote que é etiqueta (ausente = tag avulsa), com o status atual
    /// da embalagem. Etiquetas da mesma embalagem compartilham a mesma instância de <see cref="PackageState"/>.</param>
    public static Plan Build(
        OperationMode mode, IReadOnlyList<TagRead> reads, string[] epcs, Dictionary<string, TagStatus?> state,
        IReadOnlyDictionary<string, PackageState> packages)
    {
        var applied = new AppliedRead[reads.Count];
        var pending = new Dictionary<string, PendingTag>(StringComparer.Ordinal);
        var pendingPackages = new Dictionary<long, PendingPackage>();
        var events = new List<EventInsert>();

        for (var i = 0; i < reads.Count; i++)
        {
            var read = reads[i];
            var epc = epcs[i];
            state.TryGetValue(epc, out var tagPrevious);
            packages.TryGetValue(epc, out var package);
            var previous = package is null ? tagPrevious : package.Status;
            var decision = InventoryRules.Decide(previous, mode);
            var timestamp = DbTimestamp.TruncateToMillisUtc(read.TimestampUtc);

            applied[i] = new AppliedRead(read, mode, previous, decision.NewStatus, decision.Event, package?.Ref);
            state[epc] = decision.NewStatus;
            if (package is not null)
                package.Status = decision.NewStatus;

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

            // Etiqueta cuja embalagem já mudou por outra etiqueta do lote também muda a própria linha (sem evento).
            if (tagPrevious != decision.NewStatus)
            {
                tag.StatusChanged = true;
                tag.UltimaAtualizacaoStatus = timestamp;
            }

            if (decision.Event is { } evt)
                events.Add(new EventInsert(epc, evt, timestamp, read.AntennaId, read.Rssi, package?.Ref.Id));

            if (package is not null)
            {
                if (!pendingPackages.TryGetValue(package.Ref.Id, out var pkg))
                {
                    pkg = new PendingPackage(timestamp, epc, read.AntennaId, read.Rssi);
                    pendingPackages[package.Ref.Id] = pkg;
                }
                pkg.Status = decision.NewStatus;
                if (timestamp >= pkg.UltimaDeteccao)
                {
                    pkg.UltimaDeteccao = timestamp;
                    pkg.UltimaEpcId = epc;
                    pkg.UltimaAntenaId = read.AntennaId;
                    pkg.UltimaRssi = read.Rssi;
                }
                if (decision.Event is not null)
                {
                    pkg.StatusChanged = true;
                    pkg.UltimaAtualizacaoStatus = timestamp;
                }
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

        var packageUpdates = new List<PackageUpdate>(pendingPackages.Count);
        foreach (var (id, pkg) in pendingPackages)
        {
            packageUpdates.Add(new PackageUpdate(
                id, pkg.Status, pkg.StatusChanged, pkg.UltimaAtualizacaoStatus ?? pkg.UltimaDeteccao,
                pkg.UltimaDeteccao, pkg.UltimaEpcId, pkg.UltimaAntenaId, pkg.UltimaRssi));
        }

        return new Plan(applied, upserts, events, packageUpdates);
    }

    internal sealed record Plan(
        AppliedRead[] Applied, IReadOnlyList<TagUpsert> Upserts, IReadOnlyList<EventInsert> Events,
        IReadOnlyList<PackageUpdate> PackageUpdates);

    /// <summary>Embalagem de um EPC do lote com o status atual (mutável durante o plano).</summary>
    internal sealed class PackageState(PackageRef reference, TagStatus? status)
    {
        public PackageRef Ref { get; } = reference;
        public TagStatus? Status { get; set; } = status;
    }

    /// <summary>
    /// Estado final da embalagem no lote. Última detecção nunca retrocede (o provider compara com o valor gravado);
    /// Status/UltimaAtualizacaoStatus só são gravados com <see cref="StatusChanged"/>.
    /// </summary>
    internal sealed record PackageUpdate(
        long Id, TagStatus Status, bool StatusChanged, DateTime UltimaAtualizacaoStatus,
        DateTime UltimaDeteccao, string UltimaEpcId, int UltimaAntenaId, double UltimaRssi);

    internal sealed record TagUpsert(
        string EpcId, TagStatus Status, DateTime UltimaLeitura, DateTime UltimaAtualizacaoStatus,
        int UltimaAntenaId, double UltimaRssi, bool StatusChanged);

    internal sealed record EventInsert(string EpcId, EventType Tipo, DateTime Timestamp, int AntenaId, double Rssi, long? EmbalagemId = null);

    private sealed class PendingPackage(DateTime firstTimestamp, string epc, int antennaId, double rssi)
    {
        public TagStatus Status { get; set; }
        public DateTime UltimaDeteccao { get; set; } = firstTimestamp;
        public string UltimaEpcId { get; set; } = epc;
        public int UltimaAntenaId { get; set; } = antennaId;
        public double UltimaRssi { get; set; } = rssi;
        public DateTime? UltimaAtualizacaoStatus { get; set; }
        public bool StatusChanged { get; set; }
    }

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
