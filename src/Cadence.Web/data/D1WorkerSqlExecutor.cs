using System.Net.Http.Json;
using System.Text.Json;

namespace Cadence.Web.Data;

/// <summary>
/// Cloudflare backend. D1 is only reachable from a Worker binding, so the container sends SQL to the
/// Worker's internal endpoint (see <c>worker/src/index.ts</c>), authenticated with a shared token.
/// The SQL text and the positional parameters are exactly what <see cref="SqliteSqlExecutor"/> runs locally.
/// </summary>
public sealed class D1WorkerSqlExecutor : ISqlExecutor
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public D1WorkerSqlExecutor(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<Row>> QueryAsync(string sql, params object?[] args)
    {
        var result = await SendAsync("/internal/d1/query", new { sql, @params = Normalize(args) });
        return result.Results.Select(ToRow).ToList();
    }

    public async Task<int> ExecuteAsync(string sql, params object?[] args)
    {
        var result = await SendAsync("/internal/d1/query", new { sql, @params = Normalize(args) });
        return result.Meta?.Changes ?? 0;
    }

    public async Task<long> InsertAsync(string sql, params object?[] args)
    {
        var result = await SendAsync("/internal/d1/query", new { sql, @params = Normalize(args) });
        return result.Meta?.LastRowId ?? 0;
    }

    public async Task BatchAsync(IReadOnlyList<SqlStatement> statements)
    {
        var body = new { statements = statements.Select(s => new { sql = s.Sql, @params = Normalize(s.Args) }) };
        using var response = await _http.PostAsJsonAsync("/internal/d1/batch", body, Json);
        response.EnsureSuccessStatusCode();
    }

    private async Task<D1Result> SendAsync(string path, object body)
    {
        using var response = await _http.PostAsJsonAsync(path, body, Json);
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"D1 worker returned {(int)response.StatusCode}: {text}");
        }
        return await response.Content.ReadFromJsonAsync<D1Result>(Json) ?? new D1Result();
    }

    private static object?[] Normalize(object?[] args) => args.Select(a => a switch
    {
        DateTimeOffset d => d.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
        DateTime d => d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
        bool b => b ? 1 : 0,
        Enum e => e.ToString().ToLowerInvariant(),
        _ => a,
    }).ToArray();

    private static Row ToRow(Dictionary<string, JsonElement> json)
    {
        var dict = new Dictionary<string, object?>(json.Count);
        foreach (var (key, el) in json)
        {
            dict[key] = el.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
                JsonValueKind.True => 1L,
                JsonValueKind.False => 0L,
                _ => el.GetString(),
            };
        }
        return new Row(dict);
    }

    private sealed class D1Result
    {
        public List<Dictionary<string, JsonElement>> Results { get; set; } = [];
        public D1Meta? Meta { get; set; }
    }

    private sealed class D1Meta
    {
        public int Changes { get; set; }
        public long LastRowId { get; set; }
    }
}
