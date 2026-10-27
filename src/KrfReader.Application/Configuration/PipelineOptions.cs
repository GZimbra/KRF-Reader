namespace KrfReader.Application.Configuration;

/// <summary>Seção <c>Pipeline</c> do appsettings.json.</summary>
public sealed class PipelineOptions
{
    public const string SectionName = "Pipeline";

    /// <summary>Janela de deduplicação: mesma tag + mesmo modo (mesma sessão de modo) dentro dela é ignorada.</summary>
    public double DedupWindowSeconds { get; set; } = 5;

    /// <summary>RSSI mínimo (dBm). Leituras abaixo são descartadas (filtro de leitura fantasma).</summary>
    public double MinRssi { get; set; } = -70;

    /// <summary>Antenas aceitas pelo filtro. Vazio = todas.</summary>
    public int[] AllowedAntennas { get; set; } = [];

    /// <summary>Workers paralelos de filtro/deduplicação (particionados por hash do EPC).</summary>
    public int WorkerCount { get; set; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    /// <summary>Capacidade da fila de entrada. Cheia = leitura descartada e contabilizada (TryWrite falha → QueueDropped).</summary>
    public int QueueCapacity { get; set; } = 10_000;

    /// <summary>Gravação dispara ao atingir este tamanho de lote...</summary>
    public int BatchSize { get; set; } = 200;

    /// <summary>...ou ao passar este intervalo desde o primeiro item pendente.</summary>
    public int FlushIntervalMs { get; set; } = 500;

    /// <summary>Tempo máximo para drenar filas e persistir no shutdown.</summary>
    public int ShutdownTimeoutMs { get; set; } = 10_000;
}
