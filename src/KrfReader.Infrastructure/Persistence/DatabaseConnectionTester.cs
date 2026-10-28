using KrfReader.Application.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

namespace KrfReader.Infrastructure.Persistence;

/// <summary>Validação de configuração de banco para a tela Configurações. Não cria banco nem aplica migração.</summary>
public static class DatabaseConnectionTester
{
    /// <summary>Testa a conexão e devolve uma linha legível. Lança com a mensagem do provider em caso de falha.</summary>
    public static async Task<string> TestAsync(DatabaseOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            throw new ArgumentException("Connection string vazia.");

        switch (DatabaseProviders.Normalize(options.Provider))
        {
            case DatabaseProviders.Sqlite:
                var builder = new SqliteConnectionStringBuilder(options.ConnectionString);
                if (string.IsNullOrWhiteSpace(builder.DataSource))
                    throw new ArgumentException("Informe Data Source=<arquivo.db>.");
                var path = Path.GetFullPath(builder.DataSource);
                return File.Exists(path)
                    ? $"OK · arquivo {path} existe."
                    : $"OK · arquivo {path} será criado ao abrir.";

            case DatabaseProviders.SqlServer:
                return await SqlServerInventoryRepository.TestConnectionAsync(options.ConnectionString, cancellationToken).ConfigureAwait(false);

            default:
                throw new NotSupportedException($"Provider '{options.Provider}' não suportado ({string.Join(", ", DatabaseProviders.All)}).");
        }
    }

    /// <summary>Servidor e banco a informar no Power BI (Obter dados → SQL Server). Null se não for SQL Server ou se a string for inválida.</summary>
    public static (string Server, string Database)? PowerBiTarget(DatabaseOptions options)
    {
        if (DatabaseProviders.Normalize(options.Provider) != DatabaseProviders.SqlServer)
            return null;
        try
        {
            var builder = new SqlConnectionStringBuilder(options.ConnectionString);
            return string.IsNullOrWhiteSpace(builder.DataSource) || string.IsNullOrWhiteSpace(builder.InitialCatalog)
                ? null
                : (builder.DataSource, builder.InitialCatalog);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
