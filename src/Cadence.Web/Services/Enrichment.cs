using System.Text.Json;
using System.Text.RegularExpressions;
using Cadence.Web.Data;
using Cadence.Web.Models;

namespace Cadence.Web.Services;

public sealed record EnrichmentProposal(string Field, string Value, double Confidence, string Rationale);

/// <summary>Something that proposes values for a lead. Proposals are never applied without a person accepting them.</summary>
public interface IEnrichmentProvider
{
    string Source { get; }
    /// <summary>Slow providers (network, LLM) run in the background for automated intake instead of blocking the request.</summary>
    bool IsSlow { get; }
    Task<IReadOnlyList<EnrichmentProposal>> ProposeAsync(Lead lead, CancellationToken ct = default);
}

/// <summary>
/// Deterministic rules that work offline: website from the email domain, industry from the company name,
/// phone formats, country from the domain. Cheap, explainable, and always on.
/// </summary>
public sealed partial class HeuristicEnrichmentProvider : IEnrichmentProvider
{
    public string Source => "heuristic";
    public bool IsSlow => false;

    private static readonly (string Industry, string[] Keywords)[] IndustryHints =
    [
        ("Agriculture & food", ["agri", "farm", "produce", "kilimo", "harvest", "dairy"]),
        ("Legal services", ["law", "legal", "attorney", "abogado"]),
        ("Accounting & tax", ["accounting", "cpa", "tax", "bookkeeping", "contabil"]),
        ("Healthcare", ["clinic", "dental", "medical", "health", "care", "pharma", "therapy", "vet"]),
        ("Construction", ["construction", "builders", "roofing", "contracting", "plumbing", "hvac", "electric"]),
        ("Real estate", ["realty", "real estate", "properties", "imob"]),
        ("Software", ["software", "labs", "tech", "digital", "cloud", "data", "systems"]),
        ("Logistics", ["logistics", "freight", "trucking", "shipping", "transport"]),
        ("Hospitality", ["hotel", "restaurant", "cafe", "catering", "bistro"]),
        ("Marketing agency", ["marketing", "agency", "media", "creative", "studio"]),
        ("Manufacturing", ["manufacturing", "industries", "industrial", "fabrication", "metal"]),
        ("Education", ["school", "academy", "education", "learning", "training"]),
        ("Financial services", ["capital", "financial", "insurance", "wealth", "lending"]),
    ];

    private static readonly HashSet<string> FreeMailDomains = ["gmail.com", "yahoo.com", "hotmail.com", "outlook.com", "icloud.com", "live.com", "aol.com", "proton.me"];

    public Task<IReadOnlyList<EnrichmentProposal>> ProposeAsync(Lead lead, CancellationToken ct = default)
    {
        var proposals = new List<EnrichmentProposal>();

        if (string.IsNullOrWhiteSpace(lead.Website) && !string.IsNullOrWhiteSpace(lead.Email))
        {
            var domain = lead.Email.Split('@').LastOrDefault()?.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(domain) && domain.Contains('.') && !FreeMailDomains.Contains(domain))
                proposals.Add(new("website", $"https://{domain}", 0.85, $"Derived from the email domain {domain}; not a free-mail provider."));
        }

        if (string.IsNullOrWhiteSpace(lead.Industry))
        {
            var name = lead.Company.ToLowerInvariant();
            foreach (var (industry, keywords) in IndustryHints)
            {
                var hit = keywords.FirstOrDefault(k => name.Contains(k));
                if (hit is null) continue;
                proposals.Add(new("industry", industry, 0.6, $"Company name contains \"{hit}\"."));
                break;
            }
        }

        if (!string.IsNullOrWhiteSpace(lead.Phone) && !lead.Phone.TrimStart().StartsWith('+'))
        {
            var digits = DigitsOnly().Replace(lead.Phone, "");
            if (digits.Length == 10)
                proposals.Add(new("phone", $"+1 ({digits[..3]}) {digits[3..6]}-{digits[6..]}", 0.7, "Normalised a 10-digit number to the North American format; confirm the country."));
            else if (digits.Length == 11 && digits[2] == '9')
                proposals.Add(new("phone", $"+55 {digits[..2]} {digits[2..7]}-{digits[7..]}", 0.7, "Looks like a Brazilian mobile number (area code + 9 digits); normalised to +55 format."));
            else if (digits.Length == 13 && digits.StartsWith("55"))
                proposals.Add(new("phone", $"+55 {digits[2..4]} {digits[4..9]}-{digits[9..]}", 0.7, "Brazilian number with country code; normalised to +55 format."));
        }

        if (string.IsNullOrWhiteSpace(lead.Country) && !string.IsNullOrWhiteSpace(lead.Website))
        {
            var tld = lead.Website.ToLowerInvariant().TrimEnd('/').Split('.').LastOrDefault();
            var country = tld switch { "br" => "Brazil", "uk" => "United Kingdom", "ca" => "Canada", "au" => "Australia", "de" => "Germany", "ke" => "Kenya", "mx" => "Mexico", _ => null };
            if (country is not null) proposals.Add(new("country", country, 0.55, $"Top-level domain .{tld} usually maps to {country}."));
        }

        return Task.FromResult<IReadOnlyList<EnrichmentProposal>>(proposals);
    }

    [GeneratedRegex(@"\D")]
    private static partial Regex DigitsOnly();
}

/// <summary>
/// The connected AI model (Anthropic or OpenRouter) as a research assistant. It sees the workspace's product and ideal customer profile, proposes values for
/// missing fields, and writes a short research brief (fit + suggested opener). Structured outputs guarantee the JSON shape;
/// everything it returns is still only a proposal.
/// </summary>
public sealed class LlmEnrichmentProvider(AiClient claude, SettingsService settings) : IEnrichmentProvider
{
    public string Source => "llm";
    public bool IsSlow => true;

    private static readonly string[] Fillable = ["industry", "country", "employees", "contact_title", "website"];

    private static readonly Dictionary<string, JsonElement> Schema = new()
    {
        ["type"] = JsonSerializer.SerializeToElement("object"),
        ["properties"] = JsonSerializer.SerializeToElement(new
        {
            proposals = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        field = new { type = "string", @enum = Fillable },
                        value = new { type = "string" },
                        confidence = new { type = "number" },
                        rationale = new { type = "string" },
                    },
                    required = new[] { "field", "value", "confidence", "rationale" },
                    additionalProperties = false,
                },
            },
            fit = new { type = "string", @enum = new[] { "strong", "possible", "weak", "unknown" } },
            fit_reason = new { type = "string" },
            opener = new { type = "string" },
        }),
        ["required"] = JsonSerializer.SerializeToElement(new[] { "proposals", "fit", "fit_reason", "opener" }),
        ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
    };

    public async Task<IReadOnlyList<EnrichmentProposal>> ProposeAsync(Lead lead, CancellationToken ct = default)
    {
        var ws = await settings.GetAsync();
        var missing = new[]
        {
            ("industry", lead.Industry), ("country", lead.Country), ("employees", lead.Employees?.ToString()),
            ("contact_title", lead.ContactTitle), ("website", lead.Website),
        }.Where(f => string.IsNullOrWhiteSpace(f.Item2)).Select(f => f.Item1).ToArray();

        var system = $"""
            You are a research assistant for a sales-development team at {Or(ws.WorkspaceName, "a B2B company")}.
            What they sell: {Or(ws.Product, "not specified")}
            Ideal customer: {Or(ws.Icp, "not specified")}
            Target markets: {Or(ws.Markets, "not specified")}

            Every answer is reviewed by a person before anything is saved, so be honest about uncertainty.
            Only propose a field value when the data below supports it; a wrong value costs the team more than a missing one.
            Confidence is 0.0 to 1.0. Employees must be a whole number written as digits.
            The opener is one natural sentence an SDR could send as the first line of an email, grounded in the lead's details.
            """;

        var prompt = $"""
            Lead
            Company: {lead.Company}
            Website: {lead.Website ?? "unknown"}
            Contact: {lead.ContactName ?? "unknown"}, {lead.ContactTitle ?? "title unknown"}
            Email: {lead.Email ?? "unknown"}
            Phone: {lead.Phone ?? "unknown"}
            Industry: {lead.Industry ?? "unknown"}
            Country: {lead.Country ?? "unknown"}
            Employees: {lead.Employees?.ToString() ?? "unknown"}
            Notes: {lead.Notes ?? "none"}

            Fields that are missing and may be proposed: {(missing.Length == 0 ? "none" : string.Join(", ", missing))}.
            Then judge how well this lead fits the ideal customer and write the opener.
            """;

        var result = await claude.CompleteJsonAsync(system, prompt, Schema, ct);
        if (result is not { } json) return [];

        var list = new List<EnrichmentProposal>();
        foreach (var p in json.GetProperty("proposals").EnumerateArray())
        {
            var field = p.GetProperty("field").GetString();
            var value = p.GetProperty("value").GetString();
            if (field is null || !missing.Contains(field) || string.IsNullOrWhiteSpace(value)) continue;
            if (field == "employees" && !int.TryParse(value, out _)) continue;
            list.Add(new(field, value.Trim(), Math.Clamp(p.GetProperty("confidence").GetDouble(), 0, 1), p.GetProperty("rationale").GetString() ?? ""));
        }

        var fit = json.GetProperty("fit").GetString() ?? "unknown";
        var brief = $"Fit: {fit}. {json.GetProperty("fit_reason").GetString()}\nOpener: {json.GetProperty("opener").GetString()}";
        list.Add(new("research_brief", brief.Trim(), fit switch { "strong" => 0.8, "possible" => 0.6, "weak" => 0.4, _ => 0.3 },
            "Written by the AI research assistant from the lead's details and your ideal customer profile. Accepting adds it to the notes."));
        return list;
    }

    private static string Or(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;
}

/// <summary>Runs providers, de-duplicates against pending suggestions, and stores proposals for review.</summary>
public sealed class EnrichmentService(IEnumerable<IEnrichmentProvider> providers, SuggestionRepository suggestions, LeadRepository leads)
{
    /// <param name="includeSlow">False for automated intake: slow providers are queued in the background instead.</param>
    public async Task<int> ProposeAsync(long leadId, bool includeSlow = true, CancellationToken ct = default)
    {
        var lead = await leads.FindAsync(leadId) ?? throw new InvalidOperationException("Lead not found.");
        var added = 0;
        foreach (var provider in providers.Where(p => includeSlow || !p.IsSlow))
        {
            foreach (var p in await provider.ProposeAsync(lead, ct))
            {
                var current = Current(lead, p.Field);
                if (string.Equals(current, p.Value, StringComparison.OrdinalIgnoreCase)) continue;
                if (await suggestions.ExistsPendingAsync(leadId, p.Field, p.Value)) continue;
                if (p.Field == "research_brief" && await suggestions.ExistsPendingFieldAsync(leadId, "research_brief")) continue;
                await suggestions.AddAsync(leadId, p.Field, p.Value, current, provider.Source, p.Confidence, p.Rationale);
                added++;
            }
        }
        return added;
    }

    /// <summary>The human decision. Accepting writes the value to the lead; rejecting only records the review.</summary>
    public async Task ReviewAsync(long suggestionId, bool accept, long? reviewerId)
    {
        var s = await suggestions.FindAsync(suggestionId) ?? throw new InvalidOperationException("Suggestion not found.");
        if (s.Status != SuggestionStatus.Pending) throw new InvalidOperationException("Suggestion was already reviewed.");
        var changed = await suggestions.ReviewAsync(suggestionId, accept ? SuggestionStatus.Accepted : SuggestionStatus.Rejected, reviewerId);
        if (changed == 0) throw new InvalidOperationException("Suggestion was reviewed by someone else.");
        if (!accept) return;

        if (s.Field == "research_brief") await leads.AppendNoteAsync(s.LeadId, s.SuggestedValue);
        else await leads.ApplyFieldAsync(s.LeadId, s.Field, s.SuggestedValue);
        if (await leads.FindAsync(s.LeadId) is { } lead) await leads.SetScoreAsync(lead.Id, LeadPipelineService.Score(lead));
    }

    public static bool HasSlowProviders(IEnumerable<IEnrichmentProvider> providers) => providers.Any(p => p.IsSlow);

    private static string? Current(Lead lead, string field) => field switch
    {
        "website" => lead.Website,
        "industry" => lead.Industry,
        "country" => lead.Country,
        "employees" => lead.Employees?.ToString(),
        "contact_title" => lead.ContactTitle,
        "email" => lead.Email,
        "phone" => lead.Phone,
        _ => null,
    };
}
