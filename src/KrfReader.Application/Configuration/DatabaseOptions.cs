namespace KrfReader.Application.Configuration;

/// <summary>Seção <c>Database</c> do appsettings.json.</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Hoje só "Sqlite". SqlServer/PostgreSql = nova implementação de <see cref="Persistence.IInventoryRepository"/>.</summary>
    public string Provider { get; set; } = "Sqlite";

    public string ConnectionString { get; set; } = "Data Source=krf-reader.db";
}
