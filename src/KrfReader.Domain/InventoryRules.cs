namespace KrfReader.Domain;

/// <summary>Resultado da aplicação de uma leitura sobre o estado atual da tag.</summary>
/// <param name="NewStatus">Status resultante.</param>
/// <param name="Event">Evento a gravar no histórico; <c>null</c> = releitura idempotente (nenhum evento).</param>
public readonly record struct TransitionDecision(TagStatus NewStatus, EventType? Event)
{
    public bool ChangesState => Event is not null;
    public bool IsAnomaly => Event == EventType.SaidaSemEntrada;
}

/// <summary>
/// Regra de negócio central. Pura, sem I/O: toda decisão de estoque passa por aqui.
/// </summary>
public static class InventoryRules
{
    /// <param name="current">Status atual da tag, ou <c>null</c> se a tag não existe no banco.</param>
    /// <param name="mode">Modo ativo no momento da leitura.</param>
    public static TransitionDecision Decide(TagStatus? current, OperationMode mode) => (current, mode) switch
    {
        (null, OperationMode.Entrada) => new(TagStatus.Dentro, EventType.Criacao),
        (null, OperationMode.Saida) => new(TagStatus.Fora, EventType.SaidaSemEntrada),
        (TagStatus.Dentro, OperationMode.Entrada) => new(TagStatus.Dentro, null),
        (TagStatus.Fora, OperationMode.Saida) => new(TagStatus.Fora, null),
        (TagStatus.Fora, OperationMode.Entrada) => new(TagStatus.Dentro, EventType.MudancaStatus),
        (TagStatus.Dentro, OperationMode.Saida) => new(TagStatus.Fora, EventType.MudancaStatus),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), $"Combinação inválida: {current}/{mode}"),
    };

    public static TagStatus TargetStatus(OperationMode mode) =>
        mode == OperationMode.Entrada ? TagStatus.Dentro : TagStatus.Fora;
}
