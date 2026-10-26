using System.Globalization;

namespace KrfReader.Infrastructure.Persistence;

/// <summary>
/// Datas no banco: texto ISO-8601 UTC de largura fixa ('yyyy-MM-ddTHH:mm:ss.fffZ'),
/// ordenável lexicograficamente. Não usa TypeHandlers globais do Dapper.
/// </summary>
internal static class DbTimestamp
{
    public const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>Formata em UTC. <see cref="DateTimeKind.Unspecified"/> é tratado como UTC; Local é convertido.</summary>
    public static string ToDb(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
        return utc.ToString(Format, CultureInfo.InvariantCulture);
    }

    /// <summary>Lê de volta garantindo <see cref="DateTimeKind.Utc"/>.</summary>
    public static DateTime FromDb(string value) =>
        DateTime.ParseExact(
            value,
            Format,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
