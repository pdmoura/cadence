using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cadence.Web.Data;
using Cadence.Web.Models;

namespace Cadence.Web.Services;

public sealed record EnrichmentProposal(string Field, string Value, double Confidence, string Rationale);

/// <summary>Something that proposes values for empty lead fields. Proposals are never applied without a human accepting them.</summary>
public interface IEnrichmentProvider
{
    string Source { get; }
    Task<IReadOnlyList<EnrichmentProposal>> ProposeAsync(Lead lead, CancellationToken ct = default);
}

/// <summary>
/// Deterministic rules that work offline: derive the website from the email domain, infer the industry from
/// keywords in the company name, normalise phone formats. Cheap, explainable, and a baseline for the LLM provider.
/// </summary>
public sealed partial class HeuristicEnrichmentProvider : IEnrichmentProvider
{
    public string Source => "heuristic";

    private static readonly (string Industry, string[] Keywords)[] IndustryHints =
    [
        ("Legal services", ["law", "legal", "attorney", "abogado"]),
        ("Accounting & tax", ["accounting", "cpa", "tax", "bookkeeping", "contabil"]),
        ("Healthcare", ["clinic", "dental", "medical", "health", "care", "pharma"]),
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

    private static readonly HashSet<string> FreeMailDomains = ["gmail.com", "yahoo.com", "hotmail.com", "outlook.com", "icloud.com", "live.com", "aol.com"];

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

        if (!string.IsNullOrWhiteSpace(lead.Phone))
        {
            var digits = DigitsOnly().Replace(lead.Phone, "");
            if (digits.Length == 10 && lead.Phone != $"+1 ({digits[..3]}) {digits[3..6]}-{digits[6..]}")
                proposals.Add(new("phone", $"+1 ({digits[..3]}) {digits[3..6]}-{digits[6..]}", 0.7, "Normalised a 10-digit number to the North American format; confirm the country."));
            else if (digits.Length == 11 && digits[2] == '9' && !lead.Phone.StartsWith('+'))
                proposals.Add(new("phone", $"+55 {digits[..2]} {digits[2..7]}-{digits[7..]}", 0.7, "Looks like a Brazilian mobile number (DDD + 9 digits); normalised to +55 format."));
            else if (digits.Length == 13 && digits.StartsWith("55") && !lead.Phone.StartsWith('+'))
                proposals.Add(new("phone", $"+55 {digits[2..4]} {digits[4..9]}-{digits[9..]}", 0.7, "Brazilian number with country code; normalised to +55 format."));
        }

        if (string.IsNullOrWhiteSpace(lead.Country) && !string.IsNullOrWhiteSpace(lead.Website))
        {
            var host = lead.Website.ToLowerInvariant();
            var tld = host.TrimEnd('/').Split('.').LastOrDefault();
            var country = tld switch { "br" => "Brazil", "uk" => "United Kingdom", "ca" => "Canada", "au" => "Australia", "de" => "Germany", "ke" => "Kenya", "mx" => "Mexico", _ => null };
            if (country is not null) proposals.Add(new("country", country, 0.55, $"Top-level domain .{tld} usually maps to {country}."));
        }

        return Task.FromResult<IReadOnlyList<EnrichmentProposal>>(proposals);
    }

    [GeneratedRegex(@"\D")]
    private static partial Regex DigitsOnly();
}

/// <summary>
/// Optional LLM provider (Anthropic Messages API). Enabled only when <c>Enrichment:AnthropicApiKey</c> is configured.
/// The model is asked for strict JSON and its output is still just a proposal a person has to accept.
/// </summary>
public sealed class AnthropicEnrichmentProvider(HttpClient http, IConfiguration config, ILogger<AnthropicEnrichmentProvider> logger) : IEnrichmentProvider
{
    public string Source => "llm";

    public async Task<IReadOnlyList<EnrichmentProposal>> ProposeAsync(Lead lead, CancellationToken ct = default)
    {
        var model = config["Enrichment:Model"] ?? "claude-sonnet-5";
        var missing = new[] { ("industry", lead.Industry), ("country", lead.Country), ("employees", lead.Employees?.ToString()), ("contact_title", lead.ContactTitle) }
            .Where(f => string.IsNullOrWhiteSpace(f.Item2)).Select(f => f.Item1).ToArray();
        if (missing.Length == 0) return [];

        var prompt = $$"""
            You help a sales-development team fill missing lead fields. Only propose values you can justify from the data below.
            Return strict JSON: {"proposals":[{"field":"industry|country|employees|contact_title","value":"...","confidence":0.0-1.0,"rationale":"one sentence"}]}
            If nothing can be inferred, return {"proposals":[]}.

            Company: {{lead.Company}}
            Website: {{lead.Website ?? "unknown"}}
            Contact: {{lead.ContactName ?? "unknown"}} ({{lead.ContactTitle ?? "title unknown"}})
            Email: {{lead.Email ?? "unknown"}}
            Notes: {{lead.Notes ?? "none"}}
            Missing fields: {{string.Join(", ", missing)}}
            """;

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        request.Headers.Add("x-api-key", config["Enrichment:AnthropicApiKey"]);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = JsonContent.Create(new { model, max_tokens = 600, messages = new[] { new { role = "user", content = prompt } } });

        try
        {
            using var response = await http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var text = doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "{}";
            var start = text.IndexOf('{'); var end = text.LastIndexOf('}');
            if (start < 0 || end <= start) return [];
            using var json = JsonDocument.Parse(text[start..(end + 1)]);
            var list = new List<EnrichmentProposal>();
            foreach (var p in json.RootElement.GetProperty("proposals").EnumerateArray())
            {
                var field = p.GetProperty("field").GetString();
                var value = p.GetProperty("value").ToString();
                if (field is null || !missing.Contains(field) || string.IsNullOrWhiteSpace(value)) continue;
                list.Add(new(field, value, Math.Clamp(p.TryGetProperty("confidence", out var c) ? c.GetDouble() : 0.5, 0, 1),
                    p.TryGetProperty("rationale", out var r) ? r.GetString() ?? "" : ""));
            }
            return list;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "LLM enrichment failed for lead {LeadId}; continuing without it", lead.Id);
            return [];
        }
    }
}

/// <summary>Runs every provider, de-duplicates against pending suggestions, and stores proposals for review.</summary>
public sealed class EnrichmentService(IEnumerable<IEnrichmentProvider> providers, SuggestionRepository suggestions, LeadRepository leads)
{
    public async Task<int> ProposeAsync(long leadId, CancellationToken ct = default)
    {
        var lead = await leads.FindAsync(leadId) ?? throw new InvalidOperationException("Lead not found.");
        var added = 0;
        foreach (var provider in providers)
        {
            foreach (var p in await provider.ProposeAsync(lead, ct))
            {
                var current = Current(lead, p.Field);
                if (string.Equals(current, p.Value, StringComparison.OrdinalIgnoreCase)) continue;
                if (await suggestions.ExistsPendingAsync(leadId, p.Field, p.Value)) continue;
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
        if (accept)
        {
            await leads.ApplyFieldAsync(s.LeadId, s.Field, s.SuggestedValue);
            var lead = await leads.FindAsync(s.LeadId);
            if (lead is not null) await leads.SetScoreAsync(lead.Id, LeadPipelineService.Score(lead));
        }
    }

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
