namespace KrfReader.Domain;

/// <summary>
/// Tag cadastrada com o estado atual (view <c>TagsCadastradas</c>): cadastro + status + última detecção.
/// Campos de estado são null quando a tag foi cadastrada e nunca lida.
/// </summary>
public sealed record RegisteredTag(
    TagRegistration Cadastro,
    TagStatus? Status,
    DateTime? UltimaDeteccaoUtc,
    int? UltimaAntenaId,
    double? UltimaRssi)
{
    public string Epc => Cadastro.Epc;
    public bool NeverDetected => Status is null;
}

/// <summary>
/// Movimentação de estoque (view <c>Movimentacoes</c>): um evento de <c>Eventos</c> com o item do cadastro.
/// <paramref name="Tipo"/> = modo em que a tag foi lida (ENTRADA/SAIDA).
/// </summary>
/// <param name="PassagemId">Passagem de alta densidade que contém a leitura; null = fora de passagem registrada.</param>
/// <param name="EmbalagemId">Embalagem movimentada (o EPC é a etiqueta que foi lida); null = tag avulsa.</param>
public sealed record StockMovement(
    long Id,
    string Epc,
    string? Descricao,
    string? Sku,
    OperationMode Tipo,
    EventType Evento,
    DateTime DataHoraUtc,
    int AntennaId,
    double Rssi,
    long? PassagemId,
    long? EmbalagemId = null,
    string? EmbalagemCodigo = null,
    string? TipoGlt = null)
{
    /// <summary>Saída sem entrada registrada.</summary>
    public bool IsAnomaly => Evento == EventType.SaidaSemEntrada;
}
