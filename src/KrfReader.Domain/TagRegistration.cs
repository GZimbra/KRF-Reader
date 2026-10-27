namespace KrfReader.Domain;

/// <summary>
/// Cadastro (dado mestre) de uma tag: o que o EPC identifica. Tabela <c>TagCadastro</c>.
/// <para>
/// Independe do estado de estoque (<see cref="TagState"/>): uma tag pode estar cadastrada e nunca ter sido lida,
/// ou ter sido lida (e ter estado em <c>Tags</c>) sem estar cadastrada. O cadastro NÃO altera a regra de
/// <see cref="InventoryRules"/>.
/// </para>
/// </summary>
public sealed record TagRegistration(
    string Epc,
    string Descricao,
    string? Sku,
    string? Observacao,
    DateTime CadastradoEmUtc,
    DateTime AtualizadoEmUtc);

/// <summary>Dados informados pelo operador. Validados e normalizados por <see cref="TagRegistrationRules.Validate"/>.</summary>
public sealed record TagRegistrationInput(string? Epc, string? Descricao, string? Sku, string? Observacao);

/// <summary>Entrada já validada: EPC normalizado, textos aparados, opcionais vazios viram null.</summary>
public sealed record ValidTagRegistration(string Epc, string Descricao, string? Sku, string? Observacao);

/// <summary>Validação do cadastro. Pura, sem I/O.</summary>
public static class TagRegistrationRules
{
    /// <summary>EPC mínimo aceito no cadastro: 1 palavra Gen2 (16 bits).</summary>
    public const int MinEpcHexLength = 4;

    /// <summary>EPC máximo Gen2: 496 bits = 124 dígitos hex.</summary>
    public const int MaxEpcHexLength = 124;

    public const int MaxDescricaoLength = 200;
    public const int MaxSkuLength = 64;
    public const int MaxObservacaoLength = 500;

    /// <summary>
    /// Valida e normaliza. O EPC aceita espaços, '-' e ':' como separadores (<see cref="KrfReader.Domain.Epc.Normalize"/>)
    /// e precisa ser múltiplo de 16 bits (4 hex): o PC word do Gen2 declara o tamanho do EPC em palavras, então
    /// um EPC com tamanho quebrado não existe numa tag real e é erro de digitação.
    /// </summary>
    public static bool TryValidate(TagRegistrationInput input, out ValidTagRegistration? result, out string error)
    {
        ArgumentNullException.ThrowIfNull(input);
        result = null;

        var epc = KrfReader.Domain.Epc.Normalize(input.Epc);
        if (string.IsNullOrWhiteSpace(input.Epc))
        {
            error = "Informe o EPC.";
            return false;
        }
        if (epc is null)
        {
            error = "EPC inválido: use apenas dígitos hexadecimais (0-9, A-F).";
            return false;
        }
        if (epc.Length < MinEpcHexLength || epc.Length > MaxEpcHexLength || epc.Length % 4 != 0)
        {
            error = $"EPC com {epc.Length} dígitos: o tamanho deve ser múltiplo de 4, entre {MinEpcHexLength} e {MaxEpcHexLength} (EPC-96 = 24 dígitos).";
            return false;
        }

        var descricao = input.Descricao?.Trim();
        if (string.IsNullOrEmpty(descricao))
        {
            error = "Informe a descrição.";
            return false;
        }
        if (descricao.Length > MaxDescricaoLength)
        {
            error = $"Descrição com mais de {MaxDescricaoLength} caracteres.";
            return false;
        }

        var sku = NullIfEmpty(input.Sku);
        if (sku is { Length: > MaxSkuLength })
        {
            error = $"SKU com mais de {MaxSkuLength} caracteres.";
            return false;
        }

        var observacao = NullIfEmpty(input.Observacao);
        if (observacao is { Length: > MaxObservacaoLength })
        {
            error = $"Observação com mais de {MaxObservacaoLength} caracteres.";
            return false;
        }

        result = new ValidTagRegistration(epc, descricao, sku, observacao);
        error = "";
        return true;
    }

    /// <summary>Como <see cref="TryValidate"/>, mas lança <see cref="ArgumentException"/>.</summary>
    public static ValidTagRegistration Validate(TagRegistrationInput input) =>
        TryValidate(input, out var result, out var error) ? result! : throw new ArgumentException(error, nameof(input));

    private static string? NullIfEmpty(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
