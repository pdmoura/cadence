using Cadence.Web.Data;
using Cadence.Web.Models;
using Cadence.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Cadence.Web.Controllers;

public sealed class DashboardViewModel
{
    public required Member? Me { get; init; }
    public required Dictionary<Stage, int> ByStage { get; init; }
    public required int PendingSuggestions { get; init; }
    public required int ActivitiesToday { get; init; }
    public required int ValidatedThisWeek { get; init; }
    public required int QualifiedThisWeek { get; init; }
    public required IReadOnlyList<Lead> NeedsReview { get; init; }
    public required IReadOnlyList<(Activity Activity, string Company)> Recent { get; init; }
    public required Standup? MyStandup { get; init; }
    public required int StandupsToday { get; init; }
    public required int TeamSize { get; init; }
    public int SdrCount { get; init; }
    public required WorkspaceSettings Settings { get; init; }
    public required AiStatus Ai { get; init; }
    public required IReadOnlyList<ChecklistItem> Checklist { get; init; }
    public bool ChecklistDone => Checklist.All(c => c.Done);
    public string Today => DateTime.UtcNow.ToString("yyyy-MM-dd");
    public int TotalOpen => ByStage.Where(kv => kv.Key is not (Stage.Qualified or Stage.Disqualified)).Sum(kv => kv.Value);
}

public sealed record ChecklistItem(string Title, string Hint, string Href, string Action, bool Done);

public sealed class DashboardController(LeadRepository leads, SuggestionRepository suggestions, ActivityRepository activities, StandupRepository standups,
    MemberRepository members, WebhookRepository webhooks, SettingsService settings, AiClient claude, CurrentMember current) : Controller
{
    [HttpGet("/")]
    public async Task<IActionResult> Index()
    {
        var me = await current.GetAsync();
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var weekAgo = DateTimeOffset.UtcNow.AddDays(-7);
        var transitions = await leads.TransitionsSinceAsync(weekAgo);
        var (pending, accepted, rejected) = await suggestions.CountsAsync();
        var team = await members.AllAsync();
        var ws = await settings.GetAsync();
        var ai = await claude.StatusAsync();
        var myStandup = me is null ? null : await standups.FindAsync(me.Id, today);
        var sources = await webhooks.InboundBySourceAsync();

        var checklist = new List<ChecklistItem>
        {
            new("Describe your business", "What you sell and your ideal customer. The AI uses it to judge fit.", "/setup/1", "Open setup", ws.HasProfile),
            new("Add your team", "SDRs, a team lead, anyone who posts a stand-up.", "/settings?tab=team", "Add people", team.Count >= 2),
            new("Connect a lead source", "A website form, Zapier, a CSV, or a test lead.", "/automations", "Connect", sources.Keys.Any(k => k != "unknown")),
            new("Review a suggestion", "Accept or reject what the rules and the AI propose.", "/leads?pending=true", "Review", accepted + rejected > 0),
            new("Post today's stand-up", "Five questions before the daily meeting.", "/standups", "Post", myStandup is not null),
            new("Connect an AI provider", "Paste your Anthropic or OpenRouter key to get research briefs. Optional.", "/settings?tab=ai", "Connect", ai.Active),
        };

        return View(new DashboardViewModel
        {
            Settings = ws,
            Ai = ai,
            Checklist = checklist,
            Me = me,
            ByStage = await leads.CountByStageAsync(),
            PendingSuggestions = pending,
            ActivitiesToday = await activities.CountSinceAsync(DateTimeOffset.UtcNow.AddHours(-24)),
            ValidatedThisWeek = transitions.Where(t => t.To == Stage.Validated).Sum(t => t.Count),
            QualifiedThisWeek = transitions.Where(t => t.To == Stage.Qualified).Sum(t => t.Count),
            NeedsReview = (await leads.ListAsync(new LeadFilter(PendingOnly: true))).Take(6).ToList(),
            Recent = await activities.RecentAsync(8),
            MyStandup = myStandup,
            StandupsToday = (await standups.ForDayAsync(today)).Count,
            TeamSize = team.Count,
            SdrCount = team.Count(m => m.Role == MemberRole.Sdr),
        });
    }

    [HttpPost("/session/member")]
    [ValidateAntiForgeryToken]
    public IActionResult SwitchMember(long memberId, string? returnUrl)
    {
        Response.Cookies.Append(CurrentMember.CookieName, memberId.ToString(), new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromDays(90) });
        return LocalRedirect(string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl);
    }

    [HttpGet("/healthz")]
    public async Task<IActionResult> Health([FromServices] ISqlExecutor db)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await db.QueryAsync("SELECT 1 AS ok");
            return Json(new { status = "ok", database = "ok", latencyMs = sw.ElapsedMilliseconds, timestamp = DateTimeOffset.UtcNow });
        }
        catch (Exception ex)
        {
            return StatusCode(503, new { status = "degraded", database = ex.Message, latencyMs = sw.ElapsedMilliseconds, timestamp = DateTimeOffset.UtcNow });
        }
    }

    [HttpGet("/error")]
    public IActionResult Error(int? code)
    {
        Response.StatusCode = code is > 0 and < 600 ? code.Value : 500;
        return View("Error", code ?? 500);
    }
}
