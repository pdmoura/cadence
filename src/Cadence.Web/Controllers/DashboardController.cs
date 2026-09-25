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
    public string Today => DateTime.UtcNow.ToString("yyyy-MM-dd");
    public int TotalOpen => ByStage.Where(kv => kv.Key is not (Stage.Qualified or Stage.Disqualified)).Sum(kv => kv.Value);
}

public sealed class DashboardController(LeadRepository leads, SuggestionRepository suggestions, ActivityRepository activities, StandupRepository standups, MemberRepository members, CurrentMember current) : Controller
{
    [HttpGet("/")]
    public async Task<IActionResult> Index()
    {
        var me = await current.GetAsync();
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var weekAgo = DateTimeOffset.UtcNow.AddDays(-7);
        var transitions = await leads.TransitionsSinceAsync(weekAgo);
        var (pending, _, _) = await suggestions.CountsAsync();
        var team = await members.AllAsync();

        return View(new DashboardViewModel
        {
            Me = me,
            ByStage = await leads.CountByStageAsync(),
            PendingSuggestions = pending,
            ActivitiesToday = await activities.CountSinceAsync(DateTimeOffset.UtcNow.AddHours(-24)),
            ValidatedThisWeek = transitions.Where(t => t.To == Stage.Validated).Sum(t => t.Count),
            QualifiedThisWeek = transitions.Where(t => t.To == Stage.Qualified).Sum(t => t.Count),
            NeedsReview = (await leads.ListAsync(new LeadFilter(PendingOnly: true))).Take(6).ToList(),
            Recent = await activities.RecentAsync(8),
            MyStandup = me is null ? null : await standups.FindAsync(me.Id, today),
            StandupsToday = (await standups.ForDayAsync(today)).Count,
            TeamSize = team.Count,
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
