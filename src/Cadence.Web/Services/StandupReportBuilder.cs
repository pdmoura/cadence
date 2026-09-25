using System.Text;
using Cadence.Web.Models;

namespace Cadence.Web.Services;

public sealed record ReportMetrics(int LeadsCreated, int Validated, int Contacted, int Qualified, int Disqualified, int Activities, int PendingSuggestions);

/// <summary>
/// Turns the team's stand-ups plus the day's pipeline numbers into the concise report the Team Lead sends to management:
/// progress, metrics, blockers and decisions that need attention. Plain text so it pastes anywhere.
/// </summary>
public static class StandupReportBuilder
{
    public static string Build(string day, IReadOnlyList<Standup> standups, ReportMetrics metrics, IReadOnlyList<Member> team)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Cadence daily report - {day}");
        sb.AppendLine(new string('=', 30));
        sb.AppendLine();

        sb.AppendLine("METRICS (last 24h)");
        sb.AppendLine($"- Leads created: {metrics.LeadsCreated}");
        sb.AppendLine($"- Validated: {metrics.Validated}  |  Contacted: {metrics.Contacted}  |  Qualified: {metrics.Qualified}  |  Disqualified: {metrics.Disqualified}");
        sb.AppendLine($"- Outreach activities logged: {metrics.Activities}");
        sb.AppendLine($"- AI suggestions waiting for human review: {metrics.PendingSuggestions}");
        sb.AppendLine();

        var missing = team.Where(m => standups.All(s => s.MemberId != m.Id)).Select(m => m.Name).ToList();
        sb.AppendLine($"STAND-UP ({standups.Count}/{team.Count} submitted{(missing.Count > 0 ? ", missing: " + string.Join(", ", missing) : "")})");
        foreach (var s in standups)
        {
            sb.AppendLine($"- {s.MemberName}: {FirstLine(s.Today)}");
            if (!string.IsNullOrWhiteSpace(s.Metric)) sb.AppendLine($"    target: {FirstLine(s.Metric)}");
        }
        sb.AppendLine();

        var blockers = standups.Where(s => !string.IsNullOrWhiteSpace(s.Blockers)).ToList();
        sb.AppendLine(blockers.Count == 0 ? "BLOCKERS: none reported" : "BLOCKERS");
        foreach (var s in blockers) sb.AppendLine($"- {s.MemberName}: {FirstLine(s.Blockers)}");
        sb.AppendLine();

        var help = standups.Where(s => !string.IsNullOrWhiteSpace(s.HelpNeeded)).ToList();
        sb.AppendLine(help.Count == 0 ? "DECISIONS / HELP NEEDED: none" : "DECISIONS / HELP NEEDED");
        foreach (var s in help) sb.AppendLine($"- {s.MemberName}: {FirstLine(s.HelpNeeded)}");

        return sb.ToString().TrimEnd();
    }

    private static string FirstLine(string text)
    {
        var line = text.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        return line.Length > 160 ? line[..157] + "..." : line;
    }
}
