using System.Text;
using Cadence.Web.Data;
using Cadence.Web.Models;

namespace Cadence.Web.Services;

public sealed record IntakeResult(string Status, long LeadId, int Suggestions, string? AssignedTo);

/// <summary>
/// One door for every automated lead source (website form, Zapier/Make/n8n, signed webhook, CSV):
/// de-duplicate by email, optionally assign an SDR, create, run the fast suggestion rules, queue Claude research,
/// and notify. Manual entry goes through the same service so the behaviour is identical everywhere.
/// </summary>
public sealed class IntakeService(
    LeadRepository leads, MemberRepository members, EnrichmentService enrichment, IEnumerable<IEnrichmentProvider> providers,
    SettingsService settings, AiClient claude, BackgroundJobs jobs, NotificationService notifications)
{
    public async Task<IntakeResult> CreateAsync(LeadInput input, string source, long? actorId)
    {
        if (string.IsNullOrWhiteSpace(input.Company)) throw new ArgumentException("Company is required.");
        if (!string.IsNullOrWhiteSpace(input.Email) && await leads.FindByEmailAsync(input.Email.Trim()) is { } existing)
            return new IntakeResult("duplicate", existing.Id, 0, existing.OwnerName);

        var ws = await settings.GetAsync();
        string? assigned = null;
        if (input.OwnerId is null && ws.AutoAssign && source != "manual" && await NextSdrAsync() is { } sdr)
        {
            input.OwnerId = sdr.Id;
            assigned = sdr.Name;
        }

        var id = await leads.CreateAsync(input, actorId, source);
        var suggestions = 0;
        if (ws.AutoEnrich || source == "manual")
        {
            suggestions = await enrichment.ProposeAsync(id, includeSlow: false);
            if (EnrichmentService.HasSlowProviders(providers) && (await claude.StatusAsync()).Active)
                QueueResearch(id);
        }

        await notifications.NotifyAsync("lead.created",
            $"New lead from {source}: *{input.Company.Trim()}*{(string.IsNullOrWhiteSpace(input.ContactName) ? "" : $" ({input.ContactName.Trim()})")}{(assigned is null ? "" : $", assigned to {assigned}")}.",
            new { leadId = id, company = input.Company, contact = input.ContactName, email = input.Email, source, assignedTo = assigned });
        return new IntakeResult("created", id, suggestions, assigned);
    }

    public void QueueResearch(long leadId) =>
        jobs.Enqueue($"research:{leadId}", (sp, ct) => sp.GetRequiredService<EnrichmentService>().ProposeAsync(leadId, includeSlow: true, ct));

    /// <summary>Round robin by workload: the SDR with the fewest open leads gets the next one.</summary>
    private async Task<Member?> NextSdrAsync()
    {
        var sdrs = (await members.AllAsync()).Where(m => m.Role == MemberRole.Sdr).ToList();
        if (sdrs.Count == 0) return null;
        var load = await leads.OpenCountByOwnerAsync();
        return sdrs.OrderBy(m => load.GetValueOrDefault(m.Id)).ThenBy(m => m.Id).First();
    }
}

/// <summary>Minimal RFC 4180 CSV reader: quoted fields, escaped quotes, commas or semicolons, CRLF or LF.</summary>
public static class Csv
{
    public static List<string[]> Parse(string text)
    {
        var rows = new List<string[]>();
        if (string.IsNullOrWhiteSpace(text)) return rows;
        text = text.TrimStart('﻿');
        var firstLine = text.Split('\n', 2)[0];
        var sep = firstLine.Count(c => c == ';') > firstLine.Count(c => c == ',') ? ';' : ',';

        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == sep) { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString()); field.Clear();
                if (row.Any(f => f.Length > 0)) rows.Add(row.ToArray());
                row = [];
            }
            else field.Append(c);
        }
        row.Add(field.ToString());
        if (row.Any(f => f.Length > 0)) rows.Add(row.ToArray());
        return rows;
    }

    /// <summary>Header aliases so exports from HubSpot, Apollo, LinkedIn Sales Navigator or a hand-made sheet all work.</summary>
    private static readonly Dictionary<string, string[]> Aliases = new()
    {
        ["company"] = ["company", "company name", "organization", "organisation", "account", "account name", "empresa"],
        ["contact"] = ["contact", "contact name", "name", "full name", "person", "nome"],
        ["first"] = ["first name", "firstname", "given name"],
        ["last"] = ["last name", "lastname", "surname", "family name"],
        ["title"] = ["title", "job title", "position", "role", "cargo"],
        ["email"] = ["email", "e-mail", "email address", "work email"],
        ["phone"] = ["phone", "phone number", "mobile", "telephone", "telefone", "whatsapp"],
        ["website"] = ["website", "domain", "url", "company website", "site"],
        ["industry"] = ["industry", "sector", "segment"],
        ["country"] = ["country", "país", "pais", "location country"],
        ["employees"] = ["employees", "company size", "headcount", "# employees", "employee count"],
        ["notes"] = ["notes", "note", "comments", "description"],
    };

    public static Dictionary<string, int> MapHeaders(string[] header)
    {
        var map = new Dictionary<string, int>();
        for (var i = 0; i < header.Length; i++)
        {
            var h = header[i].Trim().Trim('"').ToLowerInvariant().Replace('_', ' ');
            foreach (var (key, names) in Aliases)
                if (!map.ContainsKey(key) && names.Contains(h)) { map[key] = i; break; }
        }
        return map;
    }

    public static LeadInput? ToLead(string[] row, Dictionary<string, int> map)
    {
        string? Get(string key) => map.TryGetValue(key, out var i) && i < row.Length && !string.IsNullOrWhiteSpace(row[i]) ? row[i].Trim() : null;
        var company = Get("company");
        if (company is null) return null;
        var contact = Get("contact") ?? string.Join(' ', new[] { Get("first"), Get("last") }.Where(s => s is not null)).Trim();
        var employees = Get("employees");
        int? emp = employees is null ? null : int.TryParse(new string(employees.TakeWhile(char.IsDigit).ToArray()), out var n) ? n : null;
        return new LeadInput
        {
            Company = company,
            ContactName = string.IsNullOrWhiteSpace(contact) ? null : contact,
            ContactTitle = Get("title"),
            Email = Get("email"),
            Phone = Get("phone"),
            Website = Get("website") is { } w && !w.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? "https://" + w : Get("website"),
            Industry = Get("industry"),
            Country = Get("country"),
            Employees = emp,
            Notes = Get("notes"),
        };
    }
}
