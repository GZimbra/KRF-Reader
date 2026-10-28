using System.Data;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace KrfReader.Infrastructure.Persistence;

/// <summary>
/// Migrações do SQL Server (db/migrations/sqlserver/NNNN_nome.sql), versionadas em <c>dbo.SchemaVersao</c> (uma linha).
/// Cada migração roda numa transação que segura <c>sp_getapplock</c> exclusivo: duas instâncias abrindo juntas
/// serializam, e a segunda vê a versão já gravada. DDL do SQL Server é transacional: falha = rollback completo.
/// Banco com versão maior que a do executável é recusado (não há downgrade).
/// </summary>
internal static partial class SqlServerSchemaMigrator
{
    internal const string ResourcePrefix = "KrfReader.Infrastructure.mssql.";

    private const string AppLockResource = "KrfReader.SchemaMigration";
    private const int AppLockTimeoutMs = 30_000;

    private static readonly Lazy<IReadOnlyList<Migration>> Embedded = new(() => SchemaMigrator.LoadEmbedded(ResourcePrefix));

    public static IReadOnlyList<Migration> Migrations => Embedded.Value;

    public static Task<(int From, int To)> MigrateAsync(SqlConnection connection, ILogger logger, CancellationToken cancellationToken) =>
        MigrateAsync(connection, Migrations, logger, cancellationToken);

    internal static async Task<(int From, int To)> MigrateAsync(
        SqlConnection connection, IReadOnlyList<Migration> migrations, ILogger logger, CancellationToken cancellationToken)
    {
        var latest = migrations[^1].Version;
        var from = await GetVersionAsync(connection, null, cancellationToken).ConfigureAwait(false);
        if (from > latest)
            throw new InvalidOperationException(
                $"Banco na versão {from}, mais nova que a suportada por este executável ({latest}). Atualize o KRF Reader.");

        foreach (var migration in migrations)
        {
            if (migration.Version <= from)
                continue;

            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await AcquireLockAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await EnsureVersionTableAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

            var current = await GetVersionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (current >= migration.Version)
                continue; // outra instância aplicou enquanto esperávamos o lock (dispose = rollback, libera o lock)

            foreach (var batch in SplitBatches(migration.Sql))
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    batch, transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE dbo.SchemaVersao SET Versao = @Versao, AtualizadoEm = SYSUTCDATETIME();",
                new { Versao = migration.Version }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            logger.LogInformation("Migração SQL Server {Version:0000} ({Name}) aplicada.", migration.Version, migration.Name);
        }

        return (from, await GetVersionAsync(connection, null, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Versão atual; 0 se a tabela de controle ainda não existe (banco vazio).</summary>
    public static Task<int> GetVersionAsync(SqlConnection connection, SqlTransaction? transaction, CancellationToken cancellationToken) =>
        connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            IF OBJECT_ID(N'dbo.SchemaVersao', N'U') IS NULL SELECT 0;
            ELSE SELECT ISNULL((SELECT Versao FROM dbo.SchemaVersao WHERE Id = 1), 0);
            """,
            transaction: transaction, cancellationToken: cancellationToken));

    /// <summary>
    /// Separa o script em lotes no separador <c>GO</c> (sozinho na linha, como no SSMS/sqlcmd).
    /// Não interpreta <c>GO</c> dentro de string ou comentário de bloco: as migrações não usam.
    /// </summary>
    internal static IReadOnlyList<string> SplitBatches(string sql) =>
        GoSeparator().Split(sql)
            .Where(b => !string.IsNullOrWhiteSpace(StripLineComments(b)))
            .Select(b => b.Trim())
            .ToList();

    private static string StripLineComments(string batch) => LineComment().Replace(batch, "");

    private static async Task AcquireLockAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        var parameters = new DynamicParameters(new
        {
            Resource = AppLockResource,
            LockMode = "Exclusive",
            LockOwner = "Transaction",
            LockTimeout = AppLockTimeoutMs,
        });
        parameters.Add("Result", dbType: DbType.Int32, direction: ParameterDirection.ReturnValue);
        await connection.ExecuteAsync(new CommandDefinition(
            "sp_getapplock", parameters, transaction, commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        // 0/1 = obtido; negativo = timeout, deadlock ou erro.
        var result = parameters.Get<int>("Result");
        if (result < 0)
            throw new TimeoutException($"Não foi possível obter o lock de migração do banco (sp_getapplock = {result}).");
    }

    private static Task EnsureVersionTableAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition(
            """
            IF OBJECT_ID(N'dbo.SchemaVersao', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.SchemaVersao (
                    Id           TINYINT      NOT NULL CONSTRAINT PK_SchemaVersao PRIMARY KEY CONSTRAINT CK_SchemaVersao_Id CHECK (Id = 1),
                    Versao       INT          NOT NULL,
                    AtualizadoEm DATETIME2(3) NOT NULL
                );
                INSERT INTO dbo.SchemaVersao (Id, Versao, AtualizadoEm) VALUES (1, 0, SYSUTCDATETIME());
            END
            """,
            transaction: transaction, cancellationToken: cancellationToken));

    [GeneratedRegex(@"^[ \t]*GO[ \t]*(?:--[^\r\n]*)?\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GoSeparator();

    [GeneratedRegex(@"--[^\r\n]*", RegexOptions.CultureInvariant)]
    private static partial Regex LineComment();
}
