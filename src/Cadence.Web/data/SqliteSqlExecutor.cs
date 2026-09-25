using Microsoft.Data.Sqlite;

namespace Cadence.Web.Data;

/// <summary>Local / container backend: a SQLite file (WAL mode) opened per call.</summary>
public sealed class SqliteSqlExecutor : ISqlExecutor
{
    private readonly string _connectionString;

    public SqliteSqlExecutor(string connectionString) => _connectionString = connectionString;

    public static string BuildConnectionString(string path) =>
        new SqliteConnectionStringBuilder { DataSource = path, Cache = SqliteCacheMode.Shared }.ToString();

    public async Task<IReadOnlyList<Row>> QueryAsync(string sql, params object?[] args)
    {
        await using var conn = await OpenAsync();
        await using var cmd = Prepare(conn, sql, args);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<Row>();
        while (await reader.ReadAsync())
        {
            var dict = new Dictionary<string, object?>(reader.FieldCount);
            for (var i = 0; i < reader.FieldCount; i++)
                dict[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(new Row(dict));
        }
        return rows;
    }

    public async Task<int> ExecuteAsync(string sql, params object?[] args)
    {
        await using var conn = await OpenAsync();
        await using var cmd = Prepare(conn, sql, args);
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task<long> InsertAsync(string sql, params object?[] args)
    {
        await using var conn = await OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var cmd = Prepare(conn, sql, args))
        {
            cmd.Transaction = (SqliteTransaction)tx;
            await cmd.ExecuteNonQueryAsync();
        }
        await using var idCmd = conn.CreateCommand();
        idCmd.Transaction = (SqliteTransaction)tx;
        idCmd.CommandText = "SELECT last_insert_rowid()";
        var id = (long)(await idCmd.ExecuteScalarAsync() ?? 0L);
        await tx.CommitAsync();
        return id;
    }

    public async Task BatchAsync(IReadOnlyList<SqlStatement> statements)
    {
        await using var conn = await OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        foreach (var s in statements)
        {
            await using var cmd = Prepare(conn, s.Sql, s.Args);
            cmd.Transaction = (SqliteTransaction)tx;
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 5000;";
        await pragma.ExecuteNonQueryAsync();
        return conn;
    }

    /// <summary>Positional <c>?</c> placeholders map to $p0, $p1, ... so the SQL text is shared with D1.</summary>
    private static SqliteCommand Prepare(SqliteConnection conn, string sql, object?[] args)
    {
        var cmd = conn.CreateCommand();
        var text = new System.Text.StringBuilder(sql.Length + args.Length * 3);
        var index = 0;
        foreach (var ch in sql)
        {
            if (ch == '?')
            {
                text.Append("$p").Append(index);
                cmd.Parameters.AddWithValue($"$p{index}", Normalize(args.ElementAtOrDefault(index)));
                index++;
            }
            else text.Append(ch);
        }
        if (index != args.Length)
            throw new ArgumentException($"SQL expects {index} parameters but {args.Length} were supplied.");
        cmd.CommandText = text.ToString();
        return cmd;
    }

    private static object Normalize(object? value) => value switch
    {
        null => DBNull.Value,
        bool b => b ? 1 : 0,
        DateTimeOffset d => d.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
        DateTime d => d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
        Enum e => e.ToString().ToLowerInvariant(),
        _ => value,
    };
}
