namespace KrfReader.Domain;

/// <summary>Normalização de EPC: hex maiúsculo, sem espaços/separadores.</summary>
public static class Epc
{
    /// <summary>Normaliza o EPC. Retorna <c>null</c> se vazio ou não-hexadecimal.</summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        Span<char> buffer = raw.Length <= 256 ? stackalloc char[raw.Length] : new char[raw.Length];
        var len = 0;
        foreach (var c in raw)
        {
            if (c is ' ' or '-' or ':' or '\t')
                continue;
            if (!char.IsAsciiHexDigit(c))
                return null;
            buffer[len++] = char.ToUpperInvariant(c);
        }

        return len == 0 ? null : new string(buffer[..len]);
    }

    public static bool IsValid(string? raw) => Normalize(raw) is not null;
}
