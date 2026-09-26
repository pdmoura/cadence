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
    public required string Report { get; init; }
    /// <summary>Today in the acting member's timezone.</summary>
    public required string Today { get; init; }
    public bool IsToday => Day == Today;
    public bool CanSend { get; init; }
    public IReadOnlyList<CalendarDay> Calendar { get; init; } = [];
    public string? EarlierDay { get; init; }
    public string? LaterDay { get; init; }
    public string RangeLabel { get; init; } = "";
}

public sealed record CalendarDay(DateOnly Date, int Posted, int TeamSize, bool IsToday, bool IsSelected, bool IsFuture)
{
    public string Key => Date.ToString("yyyy-MM-dd");
    public bool IsWeekend => Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
}

[Route("standups")]
public sealed class StandupsController(StandupRepository standups, MemberRepository members, LeadRepository leads, ActivityRepository activities, SuggestionRepository suggestions,
    CurrentMember current, NotificationService notifications, OutboundSender sender, SettingsService settings) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(string? day)
    {
        var todayDate = await current.TodayAsync();
        day = Normalize(day, todayDate);
        var me = await current.GetAsync();
        var team = await members.AllAsync();
        var list = await standups.ForDayAsync(day);

        // Three-week calendar ending with the selected day's week (Monday first).
        var selected = DateOnly.ParseExact(day, "yyyy-MM-dd");
        var weekStart = selected.AddDays(-(((int)selected.DayOfWeek + 6) % 7));
        var gridStart = weekStart.AddDays(-14);
        var gridEnd = weekStart.AddDays(6);
        var counts = await standups.CountsByDayAsync(gridStart.ToString("yyyy-MM-dd"), gridEnd.ToString("yyyy-MM-dd"));
        var calendar = Enumerable.Range(0, 21).Select(i => gridStart.AddDays(i)).Select(d => new CalendarDay(
            d, counts.GetValueOrDefault(d.ToString("yyyy-MM-dd")), team.Count, d == todayDate, d == selected, d > todayDate)).ToList();
        // Paging lands on the Friday of the nearest new week, so the page opens on a working day rather than an empty Sunday.
        var later = gridEnd.AddDays(19) > todayDate ? todayDate : gridEnd.AddDays(19);
        return View(new StandupsViewModel
        {
            Day = day,
            Standups = list,
            Team = team,
            Me = me,
            Mine = me is null ? null : list.FirstOrDefault(s => s.MemberId == me.Id),
            Report = StandupReportBuilder.Build(day, list, await MetricsAsync(day), team),
            Today = todayDate.ToString("yyyy-MM-dd"),
            CanSend = !string.IsNullOrWhiteSpace((await settings.GetAsync()).OutboundUrl),
            Calendar = calendar,
            EarlierDay = gridStart.AddDays(-3).ToString("yyyy-MM-dd"),
            LaterDay = gridEnd < todayDate ? later.ToString("yyyy-MM-dd") : null,
            RangeLabel = gridStart.Month == gridEnd.Month ? $"{gridStart.Day} to {gridEnd:d MMMM yyyy}" : $"{gridStart:d MMM} to {gridEnd:d MMM yyyy}",
        });
    }

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(string day, string yesterday, string today, string metric, string blockers, string help)
    {
        var me = await current.GetAsync();
        if (me is null) return BadRequest();
        if (string.IsNullOrWhiteSpace(today)) { TempData["error"] = "Say what you are going to accomplish today; it is the one line the team reads."; return RedirectToAction(nameof(Index), new { day }); }
        await standups.UpsertAsync(me.Id, Normalize(day, await current.TodayAsync()), yesterday ?? "", today, metric ?? "", blockers ?? "", help ?? "");
        TempData["toast"] = "Stand-up saved.";
        return RedirectToAction(nameof(Index), new { day });
    }

    /// <summary>Posts the day's management report to the notification URL (Slack, Teams, Discord or any endpoint).</summary>
    [HttpPost("send")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Send(string? day)
    {
        day = Normalize(day, await current.TodayAsync());
        var text = StandupReportBuilder.Build(day, await standups.ForDayAsync(day), await MetricsAsync(day), await members.AllAsync());
        var (ok, message) = await notifications.SendNowAsync(sender, "report.daily", "```\n" + text + "\n```", new { day, report = text });
        TempData[ok ? "toast" : "error"] = ok ? "Report sent. " + message : message;
        return RedirectToAction(nameof(Index), new { day });
    }

    [HttpGet("report.txt")]
    public async Task<IActionResult> ReportText(string? day)
    {
        day = Normalize(day, await current.TodayAsync());
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

    private static string Normalize(string? day, DateOnly today) =>
        (DateOnly.TryParseExact(day, "yyyy-MM-dd", out var d) ? d : today).ToString("yyyy-MM-dd");
}
