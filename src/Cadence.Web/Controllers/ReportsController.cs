using Cadence.Web.Data;
using Cadence.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace Cadence.Web.Controllers;

public sealed class ReportsViewModel
{
    public required IReadOnlyList<string> Days { get; init; }
    public required Dictionary<Stage, int[]> PerDay { get; init; }
    public required Dictionary<Stage, int> ByStage { get; init; }
    public required (int Pending, int Accepted, int Rejected) Suggestions { get; init; }
    public required IReadOnlyList<(Member Member, int Open, int Qualified)> ByOwner { get; init; }
    public int Total(Stage s) => PerDay[s].Sum();
    // Bars stack three stages per day, so the scale is the largest stacked total, not the largest single stage.
    public int Max => Enumerable.Range(0, Days.Count).Select(i => PerDay[Stage.Validated][i] + PerDay[Stage.Contacted][i] + PerDay[Stage.Qualified][i]).DefaultIfEmpty(0).Max();
    public double AcceptanceRate => Suggestions.Accepted + Suggestions.Rejected == 0 ? 0 : (double)Suggestions.Accepted / (Suggestions.Accepted + Suggestions.Rejected);
}

public sealed class ReportsController(LeadRepository leads, SuggestionRepository suggestions, MemberRepository members) : Controller
{
    [HttpGet("/reports")]
    public async Task<IActionResult> Index()
    {
        var days = Enumerable.Range(0, 14).Select(i => DateTime.UtcNow.AddDays(-13 + i).ToString("yyyy-MM-dd")).ToList();
        var transitions = await leads.TransitionsSinceAsync(DateTimeOffset.UtcNow.AddDays(-14));
        var perDay = Stages.Ordered.ToDictionary(s => s, _ => new int[days.Count]);
        foreach (var (day, to, count) in transitions)
        {
            var idx = days.IndexOf(day);
            if (idx >= 0) perDay[to][idx] += count;
        }

        var all = await leads.ListAsync(new LeadFilter());
        var team = await members.AllAsync();
        var byOwner = team
            .Select(m => (m, all.Count(l => l.OwnerId == m.Id && l.Stage is not (Stage.Qualified or Stage.Disqualified)), all.Count(l => l.OwnerId == m.Id && l.Stage == Stage.Qualified)))
            .Where(t => t.Item2 + t.Item3 > 0)
            .ToList();

        return View(new ReportsViewModel
        {
            Days = days,
            PerDay = perDay,
            ByStage = await leads.CountByStageAsync(),
            Suggestions = await suggestions.CountsAsync(),
            ByOwner = byOwner,
        });
    }
}
