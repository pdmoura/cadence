namespace Cadence.Web.Data;

/// <summary>
/// The only thing repositories depend on. SQL is written once, with positional <c>?</c> parameters,
/// and runs unchanged against a local SQLite file or against Cloudflare D1 through the Worker.
/// </summary>
public interface ISqlExecutor
{
    Task<IReadOnlyList<Row>> QueryAsync(string sql, params object?[] args);

    /// <summary>Runs a statement and returns the number of affected rows.</summary>
    Task<int> ExecuteAsync(string sql, params object?[] args);

    /// <summary>Runs an INSERT and returns the new row id.</summary>
    Task<long> InsertAsync(string sql, params object?[] args);

    /// <summary>
    /// Runs several statements atomically. D1 exposes batches rather than interactive transactions,
    /// so the same shape is used for both backends.
    /// </summary>
    Task BatchAsync(IReadOnlyList<SqlStatement> statements);
}

public sealed record SqlStatement(string Sql, params object?[] Args);

/// <summary>A row as a case-insensitive column bag with typed accessors. Keeps mapping explicit and cheap.</summary>
public sealed class Row
{
    private readonly Dictionary<string, object?> _values;

    public Row(Dictionary<string, object?> values)
    {
        _values = new Dictionary<string, object?>(values, StringComparer.OrdinalIgnoreCase);
    }

    public object? this[string column] => _values.TryGetValue(column, out var v) ? v : null;

    public bool Has(string column) => _values.ContainsKey(column);

    public string Str(string column) => this[column]?.ToString() ?? string.Empty;

    public string? StrOrNull(string column)
    {
        var v = this[column];
        return v is null || v is DBNull ? null : v.ToString();
    }

    public long Long(string column) => Convert.ToInt64(this[column] ?? 0, System.Globalization.CultureInfo.InvariantCulture);

    public int Int(string column) => Convert.ToInt32(this[column] ?? 0, System.Globalization.CultureInfo.InvariantCulture);

    public int? IntOrNull(string column)
    {
        var v = this[column];
        return v is null || v is DBNull ? null : Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture);
    }

    public long? LongOrNull(string column)
    {
        var v = this[column];
        return v is null || v is DBNull ? null : Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture);
    }

    public double Dbl(string column) => Convert.ToDouble(this[column] ?? 0d, System.Globalization.CultureInfo.InvariantCulture);

    public bool Bool(string column) => Long(column) != 0;

    public DateTimeOffset Time(string column) => DateTimeOffset.Parse(Str(column), System.Globalization.CultureInfo.InvariantCulture);

    public DateTimeOffset? TimeOrNull(string column)
    {
        var s = StrOrNull(column);
        return string.IsNullOrEmpty(s) ? null : DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
    }
}
