using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace KrfReader.Infrastructure.Persistence;

/// <summary>Migração embutida (db/migrations/NNNN_nome.sql). <see cref="Version"/> = prefixo numérico.</summary>
internal sealed record Migration(int Version, string Name, string Sql);

/// <summary>
/// Migrações versionadas por <c>PRAGMA user_version</c>. Cada migração roda uma vez, em transação IMMEDIATE,
/// junto com a gravação da nova versão: falha no meio = rollback, banco fica na versão anterior.
/// Re-executar é no-op. Banco com versão maior que a do executável é recusado (não há downgrade).
/// </summary>
internal static class SchemaMigrator
{
    internal const string ResourcePrefix = "KrfReader.Infrastructure.migrations.";

    private static readonly Lazy<IReadOnlyList<Migration>> Embedded = new(LoadEmbedded);

    public static IReadOnlyList<Migration> Migrations => Embedded.Value;

    public static int LatestVersion => Migrations[^1].Version;

    /// <summary>Aplica as migrações pendentes. Retorna a versão anterior e a final.</summary>
    public static Task<(int From, int To)> MigrateAsync(
        SqliteConnection connection, ILogger logger, CancellationToken cancellationToken) =>
        MigrateAsync(connection, Migrations, logger, cancellationToken);

    internal static async Task<(int From, int To)> MigrateAsync(
        SqliteConnection connection, IReadOnlyList<Migration> migrations, ILogger logger, CancellationToken cancellationToken)
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

            // deferred: false = BEGIN IMMEDIATE: segunda instância espera (busy_timeout) em vez de migrar junto.
            await using var transaction = connection.BeginTransaction(deferred: false);
            var current = await GetVersionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (current >= migration.Version)
                continue; // outra instância aplicou enquanto esperávamos o lock

            await connection.ExecuteAsync(new CommandDefinition(migration.Sql, transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            // PRAGMA não aceita parâmetro; Version é int vindo do nome do recurso.
            await connection.ExecuteAsync(new CommandDefinition(
                $"PRAGMA user_version = {migration.Version.ToString(CultureInfo.InvariantCulture)};",
                transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            logger.LogInformation("Migração {Version:0000} ({Name}) aplicada.", migration.Version, migration.Name);
        }

        return (from, await GetVersionAsync(connection, null, cancellationToken).ConfigureAwait(false));
    }

    public static Task<int> GetVersionAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken) =>
        connection.ExecuteScalarAsync<int>(new CommandDefinition("PRAGMA user_version;", transaction: transaction, cancellationToken: cancellationToken));

    /// <summary>Lê e valida os recursos: versões 1..N contíguas, sem repetição.</summary>
    internal static IReadOnlyList<Migration> Parse(IEnumerable<(string ResourceName, string Sql)> resources)
    {
        var list = new List<Migration>();
        foreach (var (resourceName, sql) in resources)
        {
            var fileName = resourceName[ResourcePrefix.Length..];
            var separator = fileName.IndexOf('_', StringComparison.Ordinal);
            if (separator <= 0 || !int.TryParse(fileName[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out var version))
                throw new InvalidOperationException($"Nome de migração inválido: '{fileName}' (esperado NNNN_nome.sql).");
            list.Add(new Migration(version, Path.GetFileNameWithoutExtension(fileName[(separator + 1)..]), sql));
        }

        list.Sort((a, b) => a.Version.CompareTo(b.Version));
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Version != i + 1)
                throw new InvalidOperationException(
                    $"Sequência de migrações quebrada: esperado {i + 1}, encontrado {list[i].Version} ({list[i].Name}).");
        }
        if (list.Count == 0)
            throw new InvalidOperationException("Nenhuma migração embutida encontrada.");
        return list;
    }

    private static IReadOnlyList<Migration> LoadEmbedded()
    {
        var assembly = typeof(SchemaMigrator).Assembly;
        var resources = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(n =>
            {
                using var stream = assembly.GetManifestResourceStream(n)!;
                using var reader = new StreamReader(stream);
                return (n, reader.ReadToEnd());
            });
        return Parse(resources);
    }
}
