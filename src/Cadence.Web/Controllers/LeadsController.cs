using Cadence.Web.Data;
using Cadence.Web.Models;
using Cadence.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Cadence.Web.Controllers;

public sealed class LeadListViewModel
{
    public required IReadOnlyList<Lead> Leads { get; init; }
    public required LeadFilter Filter { get; init; }
    public required IReadOnlyList<Member> Members { get; init; }
    public required Dictionary<Stage, int> Counts { get; init; }
}

public sealed class LeadDetailViewModel
{
    public required Lead Lead { get; init; }
    public required IReadOnlyList<Suggestion> Suggestions { get; init; }
    public required IReadOnlyList<Activity> Activities { get; init; }
    public required IReadOnlyList<StageEvent> History { get; init; }
    public required IReadOnlyList<Member> Members { get; init; }
    public IEnumerable<Suggestion> Pending => Suggestions.Where(s => s.Status == SuggestionStatus.Pending);
    public IEnumerable<Suggestion> Reviewed => Suggestions.Where(s => s.Status != SuggestionStatus.Pending);
}

public sealed class LeadFormViewModel
{
    public long? Id { get; init; }
    public required LeadInput Input { get; init; }
    public required IReadOnlyList<Member> Members { get; init; }
}

public sealed class ImportViewModel
{
    public int? Created { get; init; }
    public int Duplicates { get; init; }
    public int Skipped { get; init; }
    public int Suggestions { get; init; }
    public IReadOnlyList<string> MappedColumns { get; init; } = [];
    public string? Error { get; init; }
    public bool AiActive { get; init; }
}

[Route("leads")]
public sealed class LeadsController(LeadRepository leads, SuggestionRepository suggestions, ActivityRepository activities, MemberRepository members,
    LeadPipelineService pipeline, EnrichmentService enrichment, IntakeService intake, AiClient claude, NotificationService notifications, CurrentMember current) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(string? q, string? stage, long? owner, bool pending = false)
    {
        Stage? parsed = string.IsNullOrEmpty(stage) ? null : Stages.ParseStage(stage);
        var filter = new LeadFilter(q, parsed, owner, pending);
        return View(new LeadListViewModel
        {
            Leads = await leads.ListAsync(filter),
            Filter = filter,
            Members = await members.AllAsync(),
            Counts = await leads.CountByStageAsync(),
        });
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Details(long id)
    {
        var lead = await leads.FindAsync(id);
        if (lead is null) return NotFound();
        ViewData["Ai"] = await claude.StatusAsync();
        return View(new LeadDetailViewModel
        {
            Lead = lead,
            Suggestions = await suggestions.ForLeadAsync(id),
            Activities = await activities.ForLeadAsync(id),
            History = await leads.HistoryAsync(id),
            Members = await members.AllAsync(),
        });
    }

    [HttpGet("new")]
    public async Task<IActionResult> Create() =>
        View("Form", new LeadFormViewModel { Input = new LeadInput { OwnerId = await current.IdAsync() }, Members = await members.AllAsync() });

    [HttpPost("new")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(LeadInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Company)) ModelState.AddModelError(nameof(input.Company), "Company is required.");
        if (!string.IsNullOrWhiteSpace(input.Email) && !input.Email.Contains('@')) ModelState.AddModelError(nameof(input.Email), "That does not look like an email.");
        if (!ModelState.IsValid) return View("Form", new LeadFormViewModel { Input = input, Members = await members.AllAsync() });

        var result = await intake.CreateAsync(input, "manual", await current.IdAsync());
        if (result.Status == "duplicate")
        {
            TempData["error"] = "A lead with that email already exists, so it was not created again.";
            return RedirectToAction(nameof(Details), new { id = result.LeadId });
        }
        var ai = (await claude.StatusAsync()).Active;
        TempData["toast"] = (result.Suggestions > 0 ? $"Lead created. {result.Suggestions} suggestion(s) to review." : "Lead created.") + (ai ? " AI research is running in the background." : "");
        return RedirectToAction(nameof(Details), new { id = result.LeadId });
    }

    [HttpGet("{id:long}/edit")]
    public async Task<IActionResult> Edit(long id)
    {
        var lead = await leads.FindAsync(id);
        if (lead is null) return NotFound();
        var input = new LeadInput
        {
            Company = lead.Company, Website = lead.Website, ContactName = lead.ContactName, ContactTitle = lead.ContactTitle, Email = lead.Email,
            Phone = lead.Phone, Industry = lead.Industry, Country = lead.Country, Employees = lead.Employees, OwnerId = lead.OwnerId, Notes = lead.Notes,
        };
        return View("Form", new LeadFormViewModel { Id = id, Input = input, Members = await members.AllAsync() });
    }

    [HttpPost("{id:long}/edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(long id, LeadInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Company)) ModelState.AddModelError(nameof(input.Company), "Company is required.");
        if (!ModelState.IsValid) return View("Form", new LeadFormViewModel { Id = id, Input = input, Members = await members.AllAsync() });
        if (await leads.UpdateAsync(id, input) == 0) return NotFound();
        var lead = await leads.FindAsync(id);
        if (lead is not null) await leads.SetScoreAsync(id, LeadPipelineService.Score(lead));
        TempData["toast"] = "Lead updated.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("{id:long}/move")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Move(long id, string to, string? reason)
    {
        try
        {
            var stage = Stages.ParseStage(to);
            await pipeline.MoveAsync(id, stage, await current.IdAsync(), reason);
            TempData["toast"] = $"Moved to {stage.Label()}.";
            if (stage is Stage.Qualified or Stage.Disqualified && await leads.FindAsync(id) is { } l)
            {
                var who = (await current.GetAsync())?.Name ?? "Someone";
                await notifications.NotifyAsync(stage == Stage.Qualified ? "lead.qualified" : "lead.disqualified",
                    stage == Stage.Qualified ? $"*{l.Company}* was qualified by {who}. Ready for sales." : $"*{l.Company}* was disqualified by {who}: {reason}",
                    new { leadId = id, company = l.Company, stage = stage.Key(), by = who, reason });
            }
        }
        catch (PipelineException ex) { TempData["error"] = ex.Message; }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("{id:long}/activities")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddActivity(long id, string kind, string summary, string? outcome)
    {
        if (string.IsNullOrWhiteSpace(summary)) { TempData["error"] = "Write a one-line summary of the activity."; return RedirectToAction(nameof(Details), new { id }); }
        await activities.AddAsync(id, await current.IdAsync(), Enum.Parse<ActivityKind>(kind, true), summary, outcome);
        TempData["toast"] = "Activity logged.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("{id:long}/enrich")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Enrich(long id)
    {
        try
        {
            var ai = (await claude.StatusAsync()).Active;
            var added = await enrichment.ProposeAsync(id, includeSlow: true, HttpContext.RequestAborted);
            TempData["toast"] = added > 0
                ? $"{added} new suggestion(s) to review{(ai ? ", including AI research" : "")}."
                : ai ? "The rules and the AI found nothing new to suggest." : "Nothing new from the rules. Connect an AI provider in Settings for research briefs.";
        }
        catch (AiException ex) { TempData["error"] = ex.Message; }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpGet("import")]
    public async Task<IActionResult> Import() => View(new ImportViewModel { AiActive = (await claude.StatusAsync()).Active });

    [HttpGet("import/template.csv")]
    public IActionResult ImportTemplate() => File(System.Text.Encoding.UTF8.GetBytes(
        "company,contact name,title,email,phone,website,industry,country,employees,notes\n" +
        "Harbor Point Dental,Dr. Elena Ruiz,Owner,elena@harborpointdental.com,+1 206 555 0142,harborpointdental.com,Healthcare,United States,14,Met at the Seattle dental expo\n"),
        "text/csv", "cadence-leads-template.csv");

    /// <summary>
    /// CSV import: headers from HubSpot, Apollo, Sales Navigator or a hand-made sheet are recognised by name.
    /// Rows without a company are skipped, known emails are skipped as duplicates. Capped at 200 rows per file.
    /// </summary>
    [HttpPost("import")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(2_000_000)]
    public async Task<IActionResult> Import(IFormFile? file)
    {
        var ai = (await claude.StatusAsync()).Active;
        if (file is null || file.Length == 0) return View(new ImportViewModel { Error = "Choose a .csv file first.", AiActive = ai });
        using var reader = new StreamReader(file.OpenReadStream());
        var rows = Csv.Parse(await reader.ReadToEndAsync());
        if (rows.Count < 2) return View(new ImportViewModel { Error = "The file needs a header row and at least one lead.", AiActive = ai });

        var map = Csv.MapHeaders(rows[0]);
        if (!map.ContainsKey("company"))
            return View(new ImportViewModel { Error = "No company column found. Name one of the columns \"company\" (the template shows every supported name).", AiActive = ai });

        var known = await leads.EmailsAsync();
        int created = 0, duplicates = 0, skipped = 0, proposals = 0;
        foreach (var row in rows.Skip(1).Take(200))
        {
            var input = Csv.ToLead(row, map);
            if (input is null) { skipped++; continue; }
            if (input.Email is { } e && !known.Add(e.ToLowerInvariant())) { duplicates++; continue; }
            var result = await intake.CreateAsync(input, "csv", await current.IdAsync());
            if (result.Status == "duplicate") { duplicates++; continue; }
            created++;
            proposals += result.Suggestions;
        }
        skipped += Math.Max(0, rows.Count - 1 - 200);
        return View(new ImportViewModel { Created = created, Duplicates = duplicates, Skipped = skipped, Suggestions = proposals, MappedColumns = map.Keys.ToList(), AiActive = ai });
    }

    [HttpPost("{id:long}/suggestions/{suggestionId:long}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Review(long id, long suggestionId, string decision)
    {
        try
        {
            await enrichment.ReviewAsync(suggestionId, decision == "accept", await current.IdAsync());
            TempData["toast"] = decision == "accept" ? "Suggestion accepted and applied to the lead." : "Suggestion rejected.";
        }
        catch (InvalidOperationException ex) { TempData["error"] = ex.Message; }
        return RedirectToAction(nameof(Details), new { id });
    }
}
