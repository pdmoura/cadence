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

[Route("leads")]
public sealed class LeadsController(LeadRepository leads, SuggestionRepository suggestions, ActivityRepository activities, MemberRepository members,
    LeadPipelineService pipeline, EnrichmentService enrichment, CurrentMember current) : Controller
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

        var id = await leads.CreateAsync(input, await current.IdAsync(), "manual");
        var proposals = await enrichment.ProposeAsync(id);
        TempData["toast"] = proposals > 0 ? $"Lead created. {proposals} suggestion(s) are waiting for your review." : "Lead created.";
        return RedirectToAction(nameof(Details), new { id });
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
            await pipeline.MoveAsync(id, Stages.ParseStage(to), await current.IdAsync(), reason);
            TempData["toast"] = $"Moved to {Stages.ParseStage(to).Label()}.";
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
        var added = await enrichment.ProposeAsync(id);
        TempData["toast"] = added > 0 ? $"{added} new suggestion(s) to review." : "Nothing new to suggest: the fields we can infer are already filled or pending.";
        return RedirectToAction(nameof(Details), new { id });
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
