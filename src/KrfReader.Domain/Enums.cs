namespace KrfReader.Domain;

/// <summary>Status atual de uma tag no estoque. Persistido como 'DENTRO' / 'FORA'.</summary>
public enum TagStatus
{
    Dentro = 1,
    Fora = 2,
}

/// <summary>Modo de operação selecionado pelo operador. Persistido como 'ENTRADA' / 'SAIDA'.</summary>
public enum OperationMode
{
    Entrada = 1,
    Saida = 2,
}

/// <summary>
/// Tipo do evento gravado em <c>Eventos</c>. Releituras idempotentes NÃO geram evento
/// (apenas atualizam <c>Tags.UltimaLeitura</c>).
/// Persistido como 'CRIACAO' / 'MUDANCA_STATUS' / 'SAIDA_SEM_ENTRADA'.
/// </summary>
public enum EventType
{
    /// <summary>Tag desconhecida lida em ENTRADA: registro criado com status DENTRO.</summary>
    Criacao = 1,

    /// <summary>Tag conhecida mudou de status (DENTRO→FORA ou FORA→DENTRO).</summary>
    MudancaStatus = 2,

    /// <summary>Anomalia: tag desconhecida lida em SAÍDA. Registro criado com status FORA.</summary>
    SaidaSemEntrada = 3,
}
