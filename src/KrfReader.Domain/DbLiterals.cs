namespace KrfReader.Domain;

/// <summary>Conversão enum ↔ texto gravado no banco. Única fonte da verdade para os literais.</summary>
public static class DbLiterals
{
    public static string ToDb(this TagStatus s) => s switch
    {
        TagStatus.Dentro => "DENTRO",
        TagStatus.Fora => "FORA",
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    public static string ToDb(this OperationMode m) => m switch
    {
        OperationMode.Entrada => "ENTRADA",
        OperationMode.Saida => "SAIDA",
        _ => throw new ArgumentOutOfRangeException(nameof(m)),
    };

    public static string ToDb(this EventType e) => e switch
    {
        EventType.Criacao => "CRIACAO",
        EventType.MudancaStatus => "MUDANCA_STATUS",
        EventType.SaidaSemEntrada => "SAIDA_SEM_ENTRADA",
        _ => throw new ArgumentOutOfRangeException(nameof(e)),
    };

    public static TagStatus ParseStatus(string s) => s switch
    {
        "DENTRO" => TagStatus.Dentro,
        "FORA" => TagStatus.Fora,
        _ => throw new FormatException($"Status inválido: '{s}'"),
    };

    public static OperationMode ParseMode(string s) => s switch
    {
        "ENTRADA" => OperationMode.Entrada,
        "SAIDA" => OperationMode.Saida,
        _ => throw new FormatException($"Modo inválido: '{s}'"),
    };

    public static EventType ParseEventType(string s) => s switch
    {
        "CRIACAO" => EventType.Criacao,
        "MUDANCA_STATUS" => EventType.MudancaStatus,
        "SAIDA_SEM_ENTRADA" => EventType.SaidaSemEntrada,
        _ => throw new FormatException($"Tipo de evento inválido: '{s}'"),
    };
}
