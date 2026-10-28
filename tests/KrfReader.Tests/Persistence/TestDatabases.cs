using KrfReader.Application.Configuration;
using KrfReader.Application.Persistence;
using KrfReader.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace KrfReader.Tests.Persistence;

/// <summary>Banco descartável por teste. <see cref="SkipReason"/> != null = backend indisponível nesta máquina.</summary>
internal interface ITestDatabase<out TRepo> : IAsyncDisposable
    where TRepo : IInventoryRepository, ITagCatalogRepository
{
    string? SkipReason { get; }

    /// <summary>Nova instância apontando para o mesmo banco (ainda não inicializada).</summary>
    TRepo NewRepository();
}

internal sealed class SqliteTestDatabase : ITestDatabase<SqliteInventoryRepository>
{
    public string Path { get; } = TestUtil.TempDbPath();

    public string? SkipReason => null;

    public SqliteInventoryRepository NewRepository() =>
        new(new DatabaseOptions { Provider = DatabaseProviders.Sqlite, ConnectionString = $"Data Source={Path}" });

    public ValueTask DisposeAsync()
    {
        TestUtil.DeleteDb(Path);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// SQL Server real. Ativado pela variável <c>KRF_TEST_SQLSERVER</c> = connection string do servidor, sem Database
/// (ex.: <c>Server=localhost,1433;User Id=sa;Password=...;TrustServerCertificate=True</c>). Cada teste cria
/// <c>KrfTest_&lt;guid&gt;</c> (pelo próprio InitializeAsync do repositório) e o apaga no fim.
/// </summary>
internal sealed class SqlServerTestDatabase : ITestDatabase<SqlServerInventoryRepository>
{
    public const string EnvironmentVariable = "KRF_TEST_SQLSERVER";

    private readonly string? _server = Environment.GetEnvironmentVariable(EnvironmentVariable);

    public SqlServerTestDatabase()
    {
        if (_server is null)
            return;
        ConnectionString = new SqlConnectionStringBuilder(_server) { InitialCatalog = DatabaseName }.ConnectionString;
    }

    public string DatabaseName { get; } = $"KrfTest_{Guid.NewGuid():N}";

    public string ConnectionString { get; } = "";

    public string? SkipReason => _server is null ? $"{EnvironmentVariable} não definida (SQL Server de teste)." : null;

    public SqlServerInventoryRepository NewRepository() =>
        new(new DatabaseOptions { Provider = DatabaseProviders.SqlServer, ConnectionString = ConnectionString });

    public async ValueTask DisposeAsync()
    {
        if (_server is null)
            return;

        SqlConnection.ClearAllPools();
        var master = new SqlConnectionStringBuilder(_server) { InitialCatalog = "master" }.ConnectionString;
        await using var connection = new SqlConnection(master);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            IF DB_ID(@Name) IS NOT NULL
            BEGIN
                DECLARE @sql NVARCHAR(400) = N'ALTER DATABASE ' + QUOTENAME(@Name) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ' + QUOTENAME(@Name) + N';';
                EXEC (@sql);
            END
            """;
        command.Parameters.AddWithValue("@Name", DatabaseName);
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// Base das suítes de persistência que rodam iguais em SQLite e SQL Server. Teste em backend indisponível vira Skip
/// (ao acessar <see cref="Repo"/>), não falso verde.
/// </summary>
public abstract class PersistenceContract<TRepo> : IAsyncLifetime
    where TRepo : class, IInventoryRepository, ITagCatalogRepository
{
    private ITestDatabase<TRepo>? _db;
    private TRepo? _repo;

    private protected abstract ITestDatabase<TRepo> CreateDatabase();

    private protected ITestDatabase<TRepo> Database => _db!;

    protected TRepo Repo
    {
        get
        {
            Skip.If(_db?.SkipReason is not null, _db?.SkipReason);
            return _repo!;
        }
    }

    protected ITagCatalogRepository Catalog => Repo;

    /// <summary>Segunda instância no mesmo banco, já inicializada.</summary>
    protected async Task<TRepo> OpenAnotherAsync()
    {
        _ = Repo; // skip se indisponível
        var other = _db!.NewRepository();
        await other.InitializeAsync();
        return other;
    }

    public async Task InitializeAsync()
    {
        _db = CreateDatabase();
        if (_db.SkipReason is not null)
            return;
        _repo = _db.NewRepository();
        await _repo.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        if (_db is not null)
            await _db.DisposeAsync();
    }
}
