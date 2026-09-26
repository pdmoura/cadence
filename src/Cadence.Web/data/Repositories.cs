using Cadence.Web.Models;

namespace Cadence.Web.Data;

public sealed class MemberRepository(ISqlExecutor db)
{
    public async Task<IReadOnlyList<Member>> AllAsync() =>
        (await db.QueryAsync("SELECT id, name, email, role, timezone FROM members ORDER BY CASE role WHEN 'team_lead' THEN 0 ELSE 1 END, name"))
        .Select(Map).ToList();

    public async Task<Member?> FindAsync(long id) =>
        (await db.QueryAsync("SELECT id, name, email, role, timezone FROM members WHERE id = ?", id)).Select(Map).FirstOrDefault();

    public Task<long> AddAsync(string name, string email, MemberRole role, string timezone) =>
        db.InsertAsync("INSERT INTO members (name, email, role, timezone) VALUES (?, ?, ?, ?)",
            name.Trim(), email.Trim().ToLowerInvariant(), RoleKey(role), string.IsNullOrWhiteSpace(timezone) ? "UTC" : timezone.Trim());

    public Task<int> RemoveAsync(long id) => db.ExecuteAsync("DELETE FROM members WHERE id = ?", id);

    public async Task<bool> EmailTakenAsync(string email) =>
        (await db.QueryAsync("SELECT 1 FROM members WHERE email = ? COLLATE NOCASE", email.Trim())).Count > 0;

    public static string RoleKey(MemberRole role) => role switch { MemberRole.TeamLead => "team_lead", MemberRole.Developer => "developer", _ => "sdr" };

    internal static Member Map(Row r) => new(
        r.Long("id"), r.Str("name"), r.Str("email"),
        r.Str("role") switch { "team_lead" => MemberRole.TeamLead, "developer" => MemberRole.Developer, _ => MemberRole.Sdr },
        r.Str("timezone"));
}

public sealed record LeadFilter(string? Search = null, Stage? Stage = null, long? OwnerId = null, bool PendingOnly = false);

public sealed class LeadRepository(ISqlExecutor db)
{
    private const string Select = """
        SELECT l.id, l.company, l.website, l.contact_name, l.contact_title, l.email, l.phone, l.industry, l.country,
               l.employees, l.source, l.stage, l.owner_id, m.name AS owner_name, l.score, l.notes, l.created_at, l.updated_at,
               (SELECT COUNT(*) FROM enrichment_suggestions s WHERE s.lead_id = l.id AND s.status = 'pending') AS pending_suggestions
        FROM leads l
        LEFT JOIN members m ON m.id = l.owner_id
        """;

    public async Task<IReadOnlyList<Lead>> ListAsync(LeadFilter filter)
    {
        var where = new List<string>();
        var args = new List<object?>();

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            where.Add("(l.company LIKE ? COLLATE NOCASE OR l.contact_name LIKE ? COLLATE NOCASE OR l.email LIKE ? COLLATE NOCASE)");
            var like = $"%{filter.Search.Trim()}%";
            args.AddRange([like, like, like]);
        }
        if (filter.Stage is { } stage) { where.Add("l.stage = ?"); args.Add(stage.Key()); }
        if (filter.OwnerId is { } owner) { where.Add("l.owner_id = ?"); args.Add(owner); }
        if (filter.PendingOnly) where.Add("EXISTS (SELECT 1 FROM enrichment_suggestions s WHERE s.lead_id = l.id AND s.status = 'pending')");

        var sql = Select + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") + " ORDER BY l.updated_at DESC LIMIT 200";
        return (await db.QueryAsync(sql, args.ToArray())).Select(Map).ToList();
    }

    public async Task<Lead?> FindAsync(long id) =>
        (await db.QueryAsync(Select + " WHERE l.id = ?", id)).Select(Map).FirstOrDefault();

    public async Task<Lead?> FindByEmailAsync(string email) =>
        (await db.QueryAsync(Select + " WHERE l.email = ? COLLATE NOCASE", email)).Select(Map).FirstOrDefault();

    public async Task<long> CreateAsync(LeadInput input, long? actorId, string source)
    {
        var id = await db.InsertAsync(
            """
            INSERT INTO leads (company, website, contact_name, contact_title, email, phone, industry, country, employees, source, owner_id, notes)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            input.Company.Trim(), Clean(input.Website), Clean(input.ContactName), Clean(input.ContactTitle), Clean(input.Email)?.ToLowerInvariant(),
            Clean(input.Phone), Clean(input.Industry), Clean(input.Country), input.Employees, source, input.OwnerId, Clean(input.Notes));
        await db.ExecuteAsync("INSERT INTO lead_stage_events (lead_id, from_stage, to_stage, member_id, reason) VALUES (?, NULL, 'new', ?, ?)", id, actorId, $"Created from {source}");
        if (await FindAsync(id) is { } created)
            await SetScoreAsync(id, Services.LeadPipelineService.Score(created));
        return id;
    }

    public Task<int> UpdateAsync(long id, LeadInput input) => db.ExecuteAsync(
        """
        UPDATE leads SET company = ?, website = ?, contact_name = ?, contact_title = ?, email = ?, phone = ?, industry = ?, country = ?,
                         employees = ?, owner_id = ?, notes = ?, updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
        WHERE id = ?
        """,
        input.Company.Trim(), Clean(input.Website), Clean(input.ContactName), Clean(input.ContactTitle), Clean(input.Email)?.ToLowerInvariant(),
        Clean(input.Phone), Clean(input.Industry), Clean(input.Country), input.Employees, input.OwnerId, Clean(input.Notes), id);

    /// <summary>Stage move + audit event in one atomic batch.</summary>
    public Task MoveAsync(long id, Stage from, Stage to, long? actorId, string? reason) => db.BatchAsync([
        new SqlStatement("UPDATE leads SET stage = ?, updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now') WHERE id = ? AND stage = ?", to.Key(), id, from.Key()),
        new SqlStatement("INSERT INTO lead_stage_events (lead_id, from_stage, to_stage, member_id, reason) VALUES (?, ?, ?, ?, ?)", id, from.Key(), to.Key(), actorId, reason),
    ]);

    /// <summary>Applies an accepted suggestion to the lead's column. Field names are whitelisted, never interpolated from input.</summary>
    public Task<int> ApplyFieldAsync(long id, string field, string value)
    {
        var column = field switch
        {
            "website" => "website",
            "industry" => "industry",
            "country" => "country",
            "employees" => "employees",
            "contact_title" => "contact_title",
            "email" => "email",
            "phone" => "phone",
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Field is not enrichable."),
        };
        object? typed = column == "employees" ? (int.TryParse(value, out var n) ? n : null) : value;
        return db.ExecuteAsync($"UPDATE leads SET {column} = ?, updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now') WHERE id = ?", typed, id);
    }

    public Task<int> AppendNoteAsync(long id, string text) => db.ExecuteAsync(
        "UPDATE leads SET notes = CASE WHEN notes IS NULL OR notes = '' THEN ? ELSE notes || char(10) || char(10) || ? END, updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now') WHERE id = ?",
        text, text, id);

    public async Task<Dictionary<long, int>> OpenCountByOwnerAsync() =>
        (await db.QueryAsync("SELECT owner_id, COUNT(*) AS n FROM leads WHERE owner_id IS NOT NULL AND stage NOT IN ('qualified', 'disqualified') GROUP BY owner_id"))
        .ToDictionary(r => r.Long("owner_id"), r => r.Int("n"));

    public async Task<HashSet<string>> EmailsAsync() =>
        (await db.QueryAsync("SELECT lower(email) AS email FROM leads WHERE email IS NOT NULL")).Select(r => r.Str("email")).ToHashSet();

    public async Task<int> CountAsync() => (await db.QueryAsync("SELECT COUNT(*) AS n FROM leads"))[0].Int("n");

    /// <summary>Deletes a lead. Suggestions, activities and stage events go with it (ON DELETE CASCADE); webhook log rows are kept and unlinked.</summary>
    public Task<int> DeleteAsync(long id) => db.ExecuteAsync("DELETE FROM leads WHERE id = ?", id);

    public Task<int> SetScoreAsync(long id, int score) =>
        db.ExecuteAsync("UPDATE leads SET score = ? WHERE id = ?", Math.Clamp(score, 0, 100), id);

    public async Task<IReadOnlyList<StageEvent>> HistoryAsync(long leadId) =>
        (await db.QueryAsync(
            """
            SELECT e.id, e.lead_id, e.from_stage, e.to_stage, e.member_id, m.name AS member_name, e.reason, e.created_at
            FROM lead_stage_events e LEFT JOIN members m ON m.id = e.member_id
            WHERE e.lead_id = ? ORDER BY e.created_at DESC, e.id DESC
            """, leadId))
        .Select(r => new StageEvent(r.Long("id"), r.Long("lead_id"),
            r.StrOrNull("from_stage") is { } f ? Stages.ParseStage(f) : null, Stages.ParseStage(r.Str("to_stage")),
            r.LongOrNull("member_id"), r.StrOrNull("member_name"), r.StrOrNull("reason"), r.Time("created_at")))
        .ToList();

    public async Task<Dictionary<Stage, int>> CountByStageAsync()
    {
        var rows = await db.QueryAsync("SELECT stage, COUNT(*) AS n FROM leads GROUP BY stage");
        var dict = Stages.Ordered.ToDictionary(s => s, _ => 0);
        foreach (var r in rows) dict[Stages.ParseStage(r.Str("stage"))] = r.Int("n");
        return dict;
    }

    /// <summary>Stage transitions per day for the reports page, from the audit table rather than from mutable rows.</summary>
    public async Task<IReadOnlyList<(string Day, Stage To, int Count)>> TransitionsSinceAsync(DateTimeOffset since) =>
        (await db.QueryAsync(
            "SELECT substr(created_at, 1, 10) AS day, to_stage, COUNT(*) AS n FROM lead_stage_events WHERE created_at >= ? GROUP BY day, to_stage ORDER BY day",
            since))
        .Select(r => (r.Str("day"), Stages.ParseStage(r.Str("to_stage")), r.Int("n"))).ToList();

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    internal static Lead Map(Row r) => new(
        r.Long("id"), r.Str("company"), r.StrOrNull("website"), r.StrOrNull("contact_name"), r.StrOrNull("contact_title"),
        r.StrOrNull("email"), r.StrOrNull("phone"), r.StrOrNull("industry"), r.StrOrNull("country"), r.IntOrNull("employees"),
        r.Str("source"), Stages.ParseStage(r.Str("stage")), r.LongOrNull("owner_id"), r.StrOrNull("owner_name"), r.Int("score"),
        r.StrOrNull("notes"), r.Time("created_at"), r.Time("updated_at"), r.Has("pending_suggestions") ? r.Int("pending_suggestions") : 0);
}

public sealed class LeadInput
{
    public string Company { get; set; } = string.Empty;
    public string? Website { get; set; }
    public string? ContactName { get; set; }
    public string? ContactTitle { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Industry { get; set; }
    public string? Country { get; set; }
    public int? Employees { get; set; }
    public long? OwnerId { get; set; }
    public string? Notes { get; set; }
}

public sealed class SuggestionRepository(ISqlExecutor db)
{
    private const string Select = """
        SELECT s.id, s.lead_id, s.field, s.suggested_value, s.current_value, s.source, s.confidence, s.rationale, s.status,
               s.reviewed_by, m.name AS reviewer_name, s.reviewed_at, s.created_at
        FROM enrichment_suggestions s LEFT JOIN members m ON m.id = s.reviewed_by
        """;

    public async Task<IReadOnlyList<Suggestion>> ForLeadAsync(long leadId) =>
        (await db.QueryAsync(Select + " WHERE s.lead_id = ? ORDER BY CASE s.status WHEN 'pending' THEN 0 ELSE 1 END, s.created_at DESC", leadId)).Select(Map).ToList();

    public async Task<Suggestion?> FindAsync(long id) => (await db.QueryAsync(Select + " WHERE s.id = ?", id)).Select(Map).FirstOrDefault();

    public async Task<bool> ExistsPendingAsync(long leadId, string field, string value) =>
        (await db.QueryAsync("SELECT 1 FROM enrichment_suggestions WHERE lead_id = ? AND field = ? AND suggested_value = ? AND status = 'pending' LIMIT 1", leadId, field, value)).Count > 0;

    public async Task<bool> ExistsPendingFieldAsync(long leadId, string field) =>
        (await db.QueryAsync("SELECT 1 FROM enrichment_suggestions WHERE lead_id = ? AND field = ? AND status = 'pending' LIMIT 1", leadId, field)).Count > 0;

    public Task<long> AddAsync(long leadId, string field, string value, string? current, string source, double confidence, string? rationale) =>
        db.InsertAsync("INSERT INTO enrichment_suggestions (lead_id, field, suggested_value, current_value, source, confidence, rationale) VALUES (?, ?, ?, ?, ?, ?, ?)",
            leadId, field, value, current, source, Math.Round(confidence, 2), rationale);

    public Task<int> ReviewAsync(long id, SuggestionStatus status, long? reviewerId) =>
        db.ExecuteAsync("UPDATE enrichment_suggestions SET status = ?, reviewed_by = ?, reviewed_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now') WHERE id = ? AND status = 'pending'",
            status.ToString().ToLowerInvariant(), reviewerId, id);

    public async Task<(int Pending, int Accepted, int Rejected)> CountsAsync()
    {
        var rows = await db.QueryAsync("SELECT status, COUNT(*) AS n FROM enrichment_suggestions GROUP BY status");
        int p = 0, a = 0, rj = 0;
        foreach (var r in rows)
            switch (r.Str("status")) { case "pending": p = r.Int("n"); break; case "accepted": a = r.Int("n"); break; case "rejected": rj = r.Int("n"); break; }
        return (p, a, rj);
    }

    private static Suggestion Map(Row r) => new(
        r.Long("id"), r.Long("lead_id"), r.Str("field"), r.Str("suggested_value"), r.StrOrNull("current_value"), r.Str("source"), r.Dbl("confidence"),
        r.StrOrNull("rationale"), Enum.Parse<SuggestionStatus>(r.Str("status"), true), r.LongOrNull("reviewed_by"), r.StrOrNull("reviewer_name"),
        r.TimeOrNull("reviewed_at"), r.Time("created_at"));
}

public sealed class ActivityRepository(ISqlExecutor db)
{
    private const string Select = """
        SELECT a.id, a.lead_id, a.member_id, m.name AS member_name, a.kind, a.summary, a.outcome, a.occurred_at, l.company
        FROM activities a LEFT JOIN members m ON m.id = a.member_id JOIN leads l ON l.id = a.lead_id
        """;

    public async Task<IReadOnlyList<Activity>> ForLeadAsync(long leadId) =>
        (await db.QueryAsync(Select + " WHERE a.lead_id = ? ORDER BY a.occurred_at DESC", leadId)).Select(Map).ToList();

    public async Task<IReadOnlyList<(Activity Activity, string Company)>> RecentAsync(int limit) =>
        (await db.QueryAsync(Select + " ORDER BY a.occurred_at DESC LIMIT ?", limit)).Select(r => (Map(r), r.Str("company"))).ToList();

    public Task<long> AddAsync(long leadId, long? memberId, ActivityKind kind, string summary, string? outcome) =>
        db.InsertAsync("INSERT INTO activities (lead_id, member_id, kind, summary, outcome) VALUES (?, ?, ?, ?, ?)", leadId, memberId, kind, summary.Trim(), string.IsNullOrWhiteSpace(outcome) ? null : outcome.Trim());

    public async Task<int> CountSinceAsync(DateTimeOffset since) =>
        (await db.QueryAsync("SELECT COUNT(*) AS n FROM activities WHERE occurred_at >= ?", since))[0].Int("n");

    private static Activity Map(Row r) => new(r.Long("id"), r.Long("lead_id"), r.LongOrNull("member_id"), r.StrOrNull("member_name"),
        Enum.Parse<ActivityKind>(r.Str("kind"), true), r.Str("summary"), r.StrOrNull("outcome"), r.Time("occurred_at"));
}

public sealed class StandupRepository(ISqlExecutor db)
{
    private const string Select = """
        SELECT s.id, s.member_id, m.name AS member_name, m.role AS member_role, s.day, s.yesterday, s.today, s.metric, s.blockers, s.help_needed, s.updated_at
        FROM standups s JOIN members m ON m.id = s.member_id
        """;

    public async Task<IReadOnlyList<Standup>> ForDayAsync(string day) =>
        (await db.QueryAsync(Select + " WHERE s.day = ? ORDER BY CASE m.role WHEN 'team_lead' THEN 0 ELSE 1 END, m.name", day)).Select(Map).ToList();

    public async Task<Standup?> FindAsync(long memberId, string day) =>
        (await db.QueryAsync(Select + " WHERE s.member_id = ? AND s.day = ?", memberId, day)).Select(Map).FirstOrDefault();

    /// <summary>SQLite/D1 upsert: one row per member per day.</summary>
    public Task UpsertAsync(long memberId, string day, string yesterday, string today, string metric, string blockers, string help) => db.ExecuteAsync(
        """
        INSERT INTO standups (member_id, day, yesterday, today, metric, blockers, help_needed) VALUES (?, ?, ?, ?, ?, ?, ?)
        ON CONFLICT (member_id, day) DO UPDATE SET
          yesterday = excluded.yesterday, today = excluded.today, metric = excluded.metric, blockers = excluded.blockers,
          help_needed = excluded.help_needed, updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
        """,
        memberId, day, yesterday.Trim(), today.Trim(), metric.Trim(), blockers.Trim(), help.Trim());

    public async Task<Dictionary<string, int>> CountsByDayAsync(string from, string to) =>
        (await db.QueryAsync("SELECT day, COUNT(*) AS n FROM standups WHERE day >= ? AND day <= ? GROUP BY day", from, to))
        .ToDictionary(r => r.Str("day"), r => r.Int("n"));

    private static Standup Map(Row r) => new(r.Long("id"), r.Long("member_id"), r.Str("member_name"), r.Str("member_role"), r.Str("day"),
        r.Str("yesterday"), r.Str("today"), r.Str("metric"), r.Str("blockers"), r.Str("help_needed"), r.Time("updated_at"));
}

public sealed class WebhookRepository(ISqlExecutor db)
{
    public Task<long> LogAsync(string source, string @event, string payload, bool signatureValid, string status, string? detail, long? leadId, string direction = "inbound") =>
        db.InsertAsync("INSERT INTO webhook_deliveries (source, event, payload, signature_valid, status, detail, lead_id, direction) VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
            source, @event, payload, signatureValid, status, detail, leadId, direction);

    public async Task<Dictionary<string, (int Count, DateTimeOffset Last)>> InboundBySourceAsync() =>
        (await db.QueryAsync("SELECT source, COUNT(*) AS n, MAX(received_at) AS last FROM webhook_deliveries WHERE direction = 'inbound' AND status = 'accepted' GROUP BY source"))
        .ToDictionary(r => r.Str("source"), r => (r.Int("n"), r.Time("last")));

    public async Task<IReadOnlyList<WebhookDelivery>> RecentAsync(int limit) =>
        (await db.QueryAsync("SELECT id, source, event, payload, signature_valid, status, detail, lead_id, received_at, direction FROM webhook_deliveries ORDER BY received_at DESC, id DESC LIMIT ?", limit))
        .Select(r => new WebhookDelivery(r.Long("id"), r.Str("source"), r.Str("event"), r.Str("payload"), r.Bool("signature_valid"), r.Str("status"), r.StrOrNull("detail"), r.LongOrNull("lead_id"), r.Time("received_at"), r.Str("direction")))
        .ToList();
}
