namespace KrfReader.Application.Configuration;

/// <summary>Seção <c>Database</c> do appsettings.json.</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary><see cref="DatabaseProviders.Sqlite"/> (padrão, arquivo local) ou <see cref="DatabaseProviders.SqlServer"/>.</summary>
    public string Provider { get; set; } = DatabaseProviders.Sqlite;

    public string ConnectionString { get; set; } = DatabaseProviders.DefaultSqliteConnectionString;
}

public static class DatabaseProviders
{
    public const string Sqlite = "Sqlite";

    /// <summary>SQL Server 2016+ (Express incluso). Permite Power BI por DirectQuery.</summary>
    public const string SqlServer = "SqlServer";

    public const string DefaultSqliteConnectionString = "Data Source=krf-reader.db";

    /// <summary>Instância nomeada padrão do instalador do Express, autenticação do Windows (sem senha em arquivo).</summary>
    public const string DefaultSqlServerConnectionString =
        @"Server=localhost\SQLEXPRESS;Database=KrfReader;Integrated Security=True;Encrypt=True;TrustServerCertificate=True";

    public static IReadOnlyList<string> All { get; } = [Sqlite, SqlServer];

    /// <summary>Nome canônico (ignora caixa) ou null se desconhecido.</summary>
    public static string? Normalize(string? provider) =>
        All.FirstOrDefault(p => string.Equals(p, provider?.Trim(), StringComparison.OrdinalIgnoreCase));
}
