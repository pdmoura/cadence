using Cadence.Web.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cadence.Tests;

/// <summary>A throw-away SQLite file with the real migrations applied. Each test class gets its own.</summary>
public sealed class TestDatabase : IAsyncDisposable
{
    public string Path { get; }
    public SqliteSqlExecutor Db { get; }

    private TestDatabase(string path, SqliteSqlExecutor db) { Path = path; Db = db; }

    public static async Task<TestDatabase> CreateAsync(bool seed = true)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cadence-test-{Guid.NewGuid():N}.db");
        var cs = SqliteSqlExecutor.BuildConnectionString(path);
        var dir = FindMigrations();
        if (!seed)
        {
            // Apply only the schema migration by pointing the runner at a temp folder with the first file.
            var tmp = Directory.CreateTempSubdirectory("cadence-mig-");
            File.Copy(System.IO.Path.Combine(dir, "0001_init.sql"), System.IO.Path.Combine(tmp.FullName, "0001_init.sql"));
            dir = tmp.FullName;
        }
        await new MigrationRunner(cs, dir, NullLogger<MigrationRunner>.Instance).ApplyAsync();
        return new TestDatabase(path, new SqliteSqlExecutor(cs));
    }

    public static string FindMigrations()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = System.IO.Path.Combine(dir, "db", "migrations");
            if (Directory.Exists(candidate)) return candidate;
            dir = System.IO.Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("db/migrations not found from " + AppContext.BaseDirectory);
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(Path); File.Delete(Path + "-wal"); File.Delete(Path + "-shm"); } catch { /* best effort */ }
        return ValueTask.CompletedTask;
    }
}
