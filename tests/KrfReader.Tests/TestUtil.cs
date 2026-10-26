namespace KrfReader.Tests;

internal static class TestUtil
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Polling curto até a condição valer; falha com <paramref name="because"/> após o timeout.</summary>
    public static async Task Eventually(Func<bool> condition, string because, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"Timeout aguardando: {because}");
            await Task.Delay(10);
        }
    }

    public static Task WithTimeout(this Task task, TimeSpan? timeout = null) =>
        task.WaitAsync(timeout ?? DefaultTimeout);

    public static async Task WithTimeout(this ValueTask task, TimeSpan? timeout = null) =>
        await task.AsTask().WaitAsync(timeout ?? DefaultTimeout);

    public static string TempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"krf-test-{Guid.NewGuid():N}.db");

    public static void DeleteDb(string path)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var p in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
        {
            try { File.Delete(p); } catch (IOException) { }
        }
    }
}
