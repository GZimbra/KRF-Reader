using System.Globalization;

namespace KrfReader.Infrastructure.Persistence;

/// <summary>
/// Datas no banco: texto ISO-8601 UTC de largura fixa ('yyyy-MM-ddTHH:mm:ss.fffZ'),
/// ordenável lexicograficamente. Não usa TypeHandlers globais do Dapper.
/// SQL Server: DATETIME2(3) via <see cref="SqlDateTime2"/>.
/// </summary>
internal static class DbTimestamp
{
    public const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>Formata em UTC. <see cref="DateTimeKind.Unspecified"/> é tratado como UTC; Local é convertido.</summary>
    public static string ToDb(DateTime value) =>
        ToUtc(value).ToString(Format, CultureInfo.InvariantCulture);

    /// <summary>
    /// UTC truncado em milissegundos: mesma precisão do texto do SQLite e do DATETIME2(3) do SQL Server
    /// (que arredondaria; truncar aqui mantém os dois bancos iguais).
    /// </summary>
    public static DateTime TruncateToMillisUtc(DateTime value)
    {
        var utc = ToUtc(value);
        return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
    }

    /// <summary>Valor lido de DATETIME2 (Kind = Unspecified) marcado como UTC.</summary>
    public static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    /// <summary>Lê de volta garantindo <see cref="DateTimeKind.Utc"/>.</summary>
    public static DateTime FromDb(string value) =>
        DateTime.ParseExact(
            value,
            Format,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
