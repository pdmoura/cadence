using Microsoft.Data.Sqlite;

namespace Cadence.Web.Data;

/// <summary>
/// Applies <c>db/migrations/*.sql</c> to the local SQLite database in file-name order, once each.
/// On Cloudflare the same files are applied by <c>wrangler d1 migrations apply</c>, so this runner is
/// only registered when the SQLite backend is in use.
/// </summary>
public sealed class MigrationRunner
{
    private readonly string _connectionString;
    private readonly string _migrationsDir;
    private readonly ILogger<MigrationRunner> _logger;

    public MigrationRunner(string connectionString, string migrationsDir, ILogger<MigrationRunner> logger)
    {
        _connectionString = connectionString;
        _migrationsDir = migrationsDir;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> ApplyAsync(CancellationToken ct = default)
    {
        var applied = new List<string>();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using (var init = conn.CreateCommand())
        {
            init.CommandText = "CREATE TABLE IF NOT EXISTS schema_migrations (name TEXT PRIMARY KEY, applied_at TEXT NOT NULL)";
            await init.ExecuteNonQueryAsync(ct);
        }

        var done = new HashSet<string>(StringComparer.Ordinal);
        await using (var read = conn.CreateCommand())
        {
            read.CommandText = "SELECT name FROM schema_migrations";
            await using var reader = await read.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) done.Add(reader.GetString(0));
        }

        foreach (var file in Directory.GetFiles(_migrationsDir, "*.sql").OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            if (done.Contains(name)) continue;

            var sql = await File.ReadAllTextAsync(file, ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = (SqliteTransaction)tx;
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await using (var mark = conn.CreateCommand())
            {
                mark.Transaction = (SqliteTransaction)tx;
                mark.CommandText = "INSERT INTO schema_migrations (name, applied_at) VALUES ($n, $t)";
                mark.Parameters.AddWithValue("$n", name);
                mark.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
                await mark.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            applied.Add(name);
            _logger.LogInformation("Applied migration {Migration}", name);
        }

        return applied;
    }
}
