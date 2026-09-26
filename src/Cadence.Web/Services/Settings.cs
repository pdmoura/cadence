using System.Security.Cryptography;
using Cadence.Web.Data;

namespace Cadence.Web.Services;

/// <summary>Typed view of the settings table. Defaults apply when a key is missing (fresh install).</summary>
public sealed class WorkspaceSettings
{
    public string WorkspaceName { get; init; } = "";
    public string Product { get; init; } = "";
    public string Icp { get; init; } = "";
    public string Markets { get; init; } = "";
    public int TargetDailyTouches { get; init; } = 15;
    public int TargetWeeklyValidated { get; init; } = 5;
    public int TargetWeeklyQualified { get; init; } = 2;
    public bool AutoEnrich { get; init; } = true;
    public bool AutoAssign { get; init; } = true;
    public bool AiEnabled { get; init; } = true;
    public string AiProvider { get; init; } = "none";
    public string AiModel { get; init; } = AiClient.DefaultModel;
    public string AiKeyEncrypted { get; init; } = "";
    public string AiKeyHint { get; init; } = "";
    public string IntakeKey { get; init; } = "";
    public string OutboundUrl { get; init; } = "";
    public IReadOnlySet<string> OutboundEvents { get; init; } = new HashSet<string>();
    public bool Onboarded { get; init; }

    public bool HasProfile => !string.IsNullOrWhiteSpace(WorkspaceName) && !string.IsNullOrWhiteSpace(Icp);
}

public sealed class SettingsService(ISqlExecutor db)
{
    public static readonly string[] AllEvents = ["lead.created", "lead.qualified", "lead.disqualified", "report.daily"];

    private WorkspaceSettings? _cached;

    public async Task<WorkspaceSettings> GetAsync()
    {
        if (_cached is not null) return _cached;
        var rows = await db.QueryAsync("SELECT key, value FROM settings");
        var d = rows.ToDictionary(r => r.Str("key"), r => r.Str("value"));
        string S(string k, string def = "") => d.TryGetValue(k, out var v) ? v : def;
        int I(string k, int def) => int.TryParse(S(k), out var v) ? v : def;
        bool B(string k, bool def) => d.TryGetValue(k, out var v) ? v == "1" : def;

        var intake = S("intake_key");
        if (string.IsNullOrEmpty(intake))
        {
            intake = NewKey();
            await SetAsync(new() { ["intake_key"] = intake });
        }

        _cached = new WorkspaceSettings
        {
            WorkspaceName = S("workspace_name"),
            Product = S("product"),
            Icp = S("icp"),
            Markets = S("markets"),
            TargetDailyTouches = I("target_daily_touches", 15),
            TargetWeeklyValidated = I("target_weekly_validated", 5),
            TargetWeeklyQualified = I("target_weekly_qualified", 2),
            AutoEnrich = B("auto_enrich", true),
            AutoAssign = B("auto_assign", true),
            AiEnabled = B("ai_enabled", true),
            AiProvider = S("ai_provider", "none") is "anthropic" or "openrouter" ? S("ai_provider") : "none",
            AiModel = AiClient.NormalizeModel(S("ai_provider", "none"), S("ai_model")),
            AiKeyEncrypted = S("ai_key"),
            AiKeyHint = S("ai_key_hint"),
            IntakeKey = intake,
            OutboundUrl = S("outbound_url"),
            OutboundEvents = S("outbound_events").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(),
            Onboarded = B("onboarded", false),
        };
        return _cached;
    }

    /// <summary>Upserts several keys atomically. Values are trimmed; null removes nothing, it stores "".</summary>
    public async Task SetAsync(Dictionary<string, string?> values)
    {
        var statements = values.Select(kv => new SqlStatement(
            "INSERT INTO settings (key, value) VALUES (?, ?) ON CONFLICT (key) DO UPDATE SET value = excluded.value, updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')",
            kv.Key, (kv.Value ?? "").Trim())).ToList();
        if (statements.Count > 0) await db.BatchAsync(statements);
        _cached = null;
    }

    public static string NewKey() => "ck_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}
