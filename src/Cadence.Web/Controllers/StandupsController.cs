using Cadence.Web.Data;
using Cadence.Web.Models;
using Cadence.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Cadence.Web.Controllers;

public sealed class StandupsViewModel
{
    public required string Day { get; init; }
    public required IReadOnlyList<Standup> Standups { get; init; }
    public required IReadOnlyList<Member> Team { get; init; }
    public required Member? Me { get; init; }
    public required Standup? Mine { get; init; }
    public required IReadOnlyList<string> RecentDays { get; init; }
    public required string Report { get; init; }
    public bool IsToday => Day == DateTime.UtcNow.ToString("yyyy-MM-dd");
    public bool CanSend { get; init; }
}

[Route("standups")]
public sealed class StandupsController(StandupRepository standups, MemberRepository members, LeadRepository leads, ActivityRepository activities, SuggestionRepository suggestions,
    CurrentMember current, NotificationService notifications, OutboundSender sender, SettingsService settings) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(string? day)
    {
        day = Normalize(day);
        var me = await current.GetAsync();
        var team = await members.AllAsync();
        var list = await standups.ForDayAsync(day);
        return View(new StandupsViewModel
        {
            Day = day,
            Standups = list,
            Team = team,
            Me = me,
            Mine = me is null ? null : list.FirstOrDefault(s => s.MemberId == me.Id),
            RecentDays = await standups.RecentDaysAsync(10),
            Report = StandupReportBuilder.Build(day, list, await MetricsAsync(day), team),
            CanSend = !string.IsNullOrWhiteSpace((await settings.GetAsync()).OutboundUrl),
        });
    }

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(string day, string yesterday, string today, string metric, string blockers, string help)
    {
        var me = await current.GetAsync();
        if (me is null) return BadRequest();
        if (string.IsNullOrWhiteSpace(today)) { TempData["error"] = "Say what you are going to accomplish today; it is the one line the team reads."; return RedirectToAction(nameof(Index), new { day }); }
        await standups.UpsertAsync(me.Id, Normalize(day), yesterday ?? "", today, metric ?? "", blockers ?? "", help ?? "");
        TempData["toast"] = "Stand-up saved.";
        return RedirectToAction(nameof(Index), new { day });
    }

    /// <summary>Posts the day's management report to the notification URL (Slack, Teams, Discord or any endpoint).</summary>
    [HttpPost("send")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Send(string? day)
    {
        day = Normalize(day);
        var text = StandupReportBuilder.Build(day, await standups.ForDayAsync(day), await MetricsAsync(day), await members.AllAsync());
        var (ok, message) = await notifications.SendNowAsync(sender, "report.daily", "```\n" + text + "\n```", new { day, report = text });
        TempData[ok ? "toast" : "error"] = ok ? "Report sent. " + message : message;
        return RedirectToAction(nameof(Index), new { day });
    }

    [HttpGet("report.txt")]
    public async Task<IActionResult> ReportText(string? day)
    {
        day = Normalize(day);
        var text = StandupReportBuilder.Build(day, await standups.ForDayAsync(day), await MetricsAsync(day), await members.AllAsync());
        return Content(text, "text/plain; charset=utf-8");
    }

    private async Task<ReportMetrics> MetricsAsync(string day)
    {
        var dayStart = DateTimeOffset.Parse(day + "T00:00:00Z");
        var since = dayStart.AddDays(-1);
        var transitions = (await leads.TransitionsSinceAsync(since)).Where(t => string.CompareOrdinal(t.Day, day) <= 0).ToList();
        int Of(Stage s) => transitions.Where(t => t.To == s).Sum(t => t.Count);
        var (pending, _, _) = await suggestions.CountsAsync();
        return new ReportMetrics(Of(Stage.New), Of(Stage.Validated), Of(Stage.Contacted), Of(Stage.Qualified), Of(Stage.Disqualified), await activities.CountSinceAsync(since), pending);
    }

    private static string Normalize(string? day) =>
        DateOnly.TryParseExact(day, "yyyy-MM-dd", out var d) ? d.ToString("yyyy-MM-dd") : DateTime.UtcNow.ToString("yyyy-MM-dd");
}
