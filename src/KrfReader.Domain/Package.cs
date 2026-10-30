using System.Globalization;

namespace KrfReader.Domain;

/// <summary>
/// Embalagem retornável (GLT, caixa, palete) identificada por várias etiquetas. Tabela <c>Embalagens</c> +
/// <c>EmbalagemEtiquetas</c>.
/// <para>
/// A embalagem é o item de estoque: ler QUALQUER uma das etiquetas detecta a embalagem, e status/última detecção ficam
/// nela (não em cada EPC). Duas etiquetas em faces opostas existem para que uma face blindada (metal, peça encostada
/// na antena) não esconda a caixa; ler as duas na mesma passagem gera UMA movimentação.
/// </para>
/// <para>
/// <see cref="Codigo"/> identifica a embalagem FÍSICA e é único. <see cref="TipoGlt"/> (ex.: GLT1165) é o modelo e se
/// repete entre embalagens, inclusive com medidas diferentes, por isso não é chave.
/// </para>
/// </summary>
public sealed record PackageRegistration(
    long Id,
    string Codigo,
    string? Descricao,
    string? TipoGlt,
    int? ComprimentoMm,
    int? LarguraMm,
    int? AlturaMm,
    decimal? PesoKg,
    string? Observacao,
    DateTime CadastradoEmUtc,
    DateTime AtualizadoEmUtc,
    IReadOnlyList<PackageTag> Etiquetas)
{
    /// <summary>Descrição para exibição: a informada ou, sem ela, o código.</summary>
    public string DisplayName => Descricao ?? Codigo;
}

/// <summary>Etiqueta vinculada a uma embalagem. <paramref name="Posicao"/> = face/local na caixa (opcional).</summary>
public sealed record PackageTag(string Epc, string? Posicao = null);

/// <summary>Embalagem com o estado de estoque (view <c>EmbalagensCadastradas</c>). Estado null = nunca lida.</summary>
/// <param name="UltimaEpc">Etiqueta que gerou a última detecção (diagnóstico: etiqueta que nunca aparece = face blindada ou tag ruim).</param>
public sealed record RegisteredPackage(
    PackageRegistration Cadastro,
    TagStatus? Status,
    DateTime? UltimaDeteccaoUtc,
    string? UltimaEpc,
    int? UltimaAntenaId,
    double? UltimaRssi)
{
    public long Id => Cadastro.Id;
    public bool NeverDetected => Status is null;
}

/// <summary>Referência leve à embalagem de um EPC (lote do pipeline, passagem, lista ao vivo).</summary>
/// <param name="Etiquetas">Quantas etiquetas a embalagem tem no cadastro.</param>
public sealed record PackageRef(long Id, string Codigo, int Etiquetas = 1);

/// <summary>Dados informados pelo operador. Validados por <see cref="PackageRules.TryValidate"/>.</summary>
public sealed record PackageInput(
    string? Codigo,
    string? Descricao,
    string? TipoGlt,
    string? ComprimentoMm,
    string? LarguraMm,
    string? AlturaMm,
    string? PesoKg,
    string? Observacao,
    IReadOnlyList<PackageTagInput> Etiquetas);

public sealed record PackageTagInput(string? Epc, string? Posicao = null);

/// <summary>Entrada validada: código/tipo em maiúsculas, opcionais vazios = null, EPCs normalizados e sem repetição.</summary>
public sealed record ValidPackage(
    string Codigo,
    string? Descricao,
    string? TipoGlt,
    int? ComprimentoMm,
    int? LarguraMm,
    int? AlturaMm,
    decimal? PesoKg,
    string? Observacao,
    IReadOnlyList<PackageTag> Etiquetas);

/// <summary>Validação do cadastro de embalagem. Pura, sem I/O.</summary>
public static class PackageRules
{
    public const int MaxCodigoLength = 64;
    public const int MaxDescricaoLength = 200;
    public const int MaxTipoGltLength = 32;
    public const int MaxObservacaoLength = 500;
    public const int MaxPosicaoLength = 32;

    /// <summary>Etiquetas por embalagem. 2 é o caso normal (faces opostas); acima de 8 é erro de cadastro.</summary>
    public const int MaxEtiquetas = 8;

    public const int MaxDimensaoMm = 100_000;
    public const decimal MaxPesoKg = 100_000m;

    public static bool TryValidate(PackageInput input, out ValidPackage? result, out string error)
    {
        ArgumentNullException.ThrowIfNull(input);
        result = null;

        var codigo = TagRegistrationRules.NullIfEmpty(input.Codigo)?.ToUpperInvariant();
        if (codigo is null)
        {
            error = "Informe o código da embalagem (identificação física, ex.: patrimônio).";
            return false;
        }
        if (codigo.Length > MaxCodigoLength)
        {
            error = $"Código com mais de {MaxCodigoLength} caracteres.";
            return false;
        }

        var descricao = TagRegistrationRules.NullIfEmpty(input.Descricao);
        if (descricao is { Length: > MaxDescricaoLength })
        {
            error = $"Descrição com mais de {MaxDescricaoLength} caracteres.";
            return false;
        }

        var tipo = TagRegistrationRules.NullIfEmpty(input.TipoGlt)?.ToUpperInvariant();
        if (tipo is { Length: > MaxTipoGltLength })
        {
            error = $"Tipo com mais de {MaxTipoGltLength} caracteres.";
            return false;
        }

        if (!TryParseDimension(input.ComprimentoMm, "Comprimento", out var comprimento, out error)
            || !TryParseDimension(input.LarguraMm, "Largura", out var largura, out error)
            || !TryParseDimension(input.AlturaMm, "Altura", out var altura, out error))
            return false;

        if (!TryParseWeight(input.PesoKg, out var peso, out error))
            return false;

        var observacao = TagRegistrationRules.NullIfEmpty(input.Observacao);
        if (observacao is { Length: > MaxObservacaoLength })
        {
            error = $"Observação com mais de {MaxObservacaoLength} caracteres.";
            return false;
        }

        var etiquetas = new List<PackageTag>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tag in input.Etiquetas ?? [])
        {
            if (!TagRegistrationRules.TryValidateEpc(tag.Epc, out var epc, out var epcError))
            {
                error = $"Etiqueta {etiquetas.Count + 1}: {epcError}";
                return false;
            }
            if (!seen.Add(epc))
            {
                error = $"EPC {epc} repetido na embalagem.";
                return false;
            }
            var posicao = TagRegistrationRules.NullIfEmpty(tag.Posicao)?.ToUpperInvariant();
            if (posicao is { Length: > MaxPosicaoLength })
            {
                error = $"Posição da etiqueta {epc} com mais de {MaxPosicaoLength} caracteres.";
                return false;
            }
            etiquetas.Add(new PackageTag(epc, posicao));
        }

        if (etiquetas.Count == 0)
        {
            error = "Vincule ao menos uma etiqueta (EPC). Embalagem sem etiqueta nunca é detectada.";
            return false;
        }
        if (etiquetas.Count > MaxEtiquetas)
        {
            error = $"{etiquetas.Count} etiquetas: o máximo por embalagem é {MaxEtiquetas}.";
            return false;
        }

        result = new ValidPackage(codigo, descricao, tipo, comprimento, largura, altura, peso, observacao, etiquetas);
        error = "";
        return true;
    }

    /// <summary>Como <see cref="TryValidate"/>, mas lança <see cref="ArgumentException"/>.</summary>
    public static ValidPackage Validate(PackageInput input) =>
        TryValidate(input, out var result, out var error) ? result! : throw new ArgumentException(error, nameof(input));

    private static bool TryParseDimension(string? text, string name, out int? value, out string error)
    {
        value = null;
        error = "";
        var trimmed = TagRegistrationRules.NullIfEmpty(text);
        if (trimmed is null)
            return true;
        if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mm) || mm <= 0 || mm > MaxDimensaoMm)
        {
            error = $"{name} inválido: informe milímetros inteiros entre 1 e {MaxDimensaoMm}.";
            return false;
        }
        value = mm;
        return true;
    }

    /// <summary>Aceita vírgula ou ponto como separador decimal (operador em pt-BR digita "1,85").</summary>
    private static bool TryParseWeight(string? text, out decimal? value, out string error)
    {
        value = null;
        error = "";
        var trimmed = TagRegistrationRules.NullIfEmpty(text);
        if (trimmed is null)
            return true;
        var ok = decimal.TryParse(trimmed.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed);
        var kg = Math.Round(parsed, 3);
        if (!ok || kg <= 0 || kg > MaxPesoKg)
        {
            error = $"Peso inválido: informe quilogramas entre 0,001 e {MaxPesoKg:N0} (ex.: 1,85).";
            return false;
        }
        value = kg;
        return true;
    }
}
