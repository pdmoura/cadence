namespace Cadence.Web.Models;

public enum Stage { New, Researching, Validated, Contacted, Qualified, Disqualified }

public enum MemberRole { Sdr, TeamLead, Developer }

public enum ActivityKind { Call, Email, Linkedin, Meeting, Note }

public enum SuggestionStatus { Pending, Accepted, Rejected }

public static class Stages
{
    /// <summary>Pipeline order used by the board and the reports.</summary>
    public static readonly Stage[] Ordered = [Stage.New, Stage.Researching, Stage.Validated, Stage.Contacted, Stage.Qualified, Stage.Disqualified];

    public static string Key(this Stage s) => s.ToString().ToLowerInvariant();

    public static Stage ParseStage(string key) => Enum.Parse<Stage>(key, ignoreCase: true);

    public static string Label(this Stage s) => s switch
    {
        Stage.New => "New",
        Stage.Researching => "Researching",
        Stage.Validated => "Validated",
        Stage.Contacted => "Contacted",
        Stage.Qualified => "Qualified",
        Stage.Disqualified => "Disqualified",
        _ => s.ToString(),
    };

    /// <summary>Which moves a person may make from each stage. Everything else is rejected by the service.</summary>
    public static IReadOnlyList<Stage> AllowedNext(this Stage s) => s switch
    {
        Stage.New => [Stage.Researching, Stage.Disqualified],
        Stage.Researching => [Stage.Validated, Stage.Disqualified],
        Stage.Validated => [Stage.Contacted, Stage.Researching, Stage.Disqualified],
        Stage.Contacted => [Stage.Qualified, Stage.Disqualified],
        Stage.Qualified => [],
        Stage.Disqualified => [Stage.Researching],
        _ => [],
    };
}

public sealed record Member(long Id, string Name, string Email, MemberRole Role, string Timezone)
{
    public string Initials => string.Concat(Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => char.ToUpperInvariant(p[0])));
    public string RoleLabel => Role switch { MemberRole.Sdr => "SDR", MemberRole.TeamLead => "Team Lead", MemberRole.Developer => "Developer", _ => Role.ToString() };
}

public sealed record Lead(
    long Id,
    string Company,
    string? Website,
    string? ContactName,
    string? ContactTitle,
    string? Email,
    string? Phone,
    string? Industry,
    string? Country,
    int? Employees,
    string Source,
    Stage Stage,
    long? OwnerId,
    string? OwnerName,
    int Score,
    string? Notes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int PendingSuggestions = 0);

public sealed record StageEvent(long Id, long LeadId, Stage? From, Stage To, long? MemberId, string? MemberName, string? Reason, DateTimeOffset CreatedAt);

public sealed record Suggestion(
    long Id,
    long LeadId,
    string Field,
    string SuggestedValue,
    string? CurrentValue,
    string Source,
    double Confidence,
    string? Rationale,
    SuggestionStatus Status,
    long? ReviewedBy,
    string? ReviewerName,
    DateTimeOffset? ReviewedAt,
    DateTimeOffset CreatedAt)
{
    public string FieldLabel => Field switch
    {
        "website" => "Website",
        "industry" => "Industry",
        "country" => "Country",
        "employees" => "Employees",
        "contact_title" => "Contact title",
        "email" => "Email",
        "phone" => "Phone",
        "research_brief" => "Research brief",
        _ => Field,
    };
}

public sealed record Activity(long Id, long LeadId, long? MemberId, string? MemberName, ActivityKind Kind, string Summary, string? Outcome, DateTimeOffset OccurredAt);

public sealed record Standup(long Id, long MemberId, string MemberName, string MemberRole, string Day, string Yesterday, string Today, string Metric, string Blockers, string HelpNeeded, DateTimeOffset UpdatedAt);

public sealed record WebhookDelivery(long Id, string Source, string Event, string Payload, bool SignatureValid, string Status, string? Detail, long? LeadId, DateTimeOffset ReceivedAt, string Direction = "inbound");
