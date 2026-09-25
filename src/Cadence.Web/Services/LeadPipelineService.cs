using Cadence.Web.Data;
using Cadence.Web.Models;

namespace Cadence.Web.Services;

public sealed class PipelineException(string message) : Exception(message);

/// <summary>Business rules for moving leads through the pipeline. Repositories only persist; this decides.</summary>
public sealed class LeadPipelineService(LeadRepository leads, SuggestionRepository suggestions)
{
    public async Task MoveAsync(long leadId, Stage to, long? actorId, string? reason)
    {
        var lead = await leads.FindAsync(leadId) ?? throw new PipelineException("Lead not found.");
        if (lead.Stage == to) throw new PipelineException($"Lead is already in {to.Label()}.");
        if (!lead.Stage.AllowedNext().Contains(to))
            throw new PipelineException($"A lead in {lead.Stage.Label()} cannot move to {to.Label()}. Allowed: {string.Join(", ", lead.Stage.AllowedNext().Select(s => s.Label()))}.");

        if (to == Stage.Validated)
        {
            // Validation is a human decision: nothing may still be waiting for review, and the basics must be filled.
            var pending = (await suggestions.ForLeadAsync(leadId)).Count(s => s.Status == SuggestionStatus.Pending);
            if (pending > 0) throw new PipelineException($"Review the {pending} pending suggestion(s) before validating this lead.");
            if (string.IsNullOrWhiteSpace(lead.Email) && string.IsNullOrWhiteSpace(lead.Phone))
                throw new PipelineException("A validated lead needs at least an email or a phone number.");
            if (string.IsNullOrWhiteSpace(lead.ContactName)) throw new PipelineException("A validated lead needs a contact name.");
        }
        if (to == Stage.Disqualified && string.IsNullOrWhiteSpace(reason))
            throw new PipelineException("Give a short reason when disqualifying a lead; it feeds the weekly report.");

        await leads.MoveAsync(leadId, lead.Stage, to, actorId, reason);
        await leads.SetScoreAsync(leadId, Score(lead with { Stage = to }));
    }

    /// <summary>A transparent 0-100 score so SDRs can sort their day. Deliberately simple and explainable.</summary>
    public static int Score(Lead lead)
    {
        var score = 10;
        if (!string.IsNullOrWhiteSpace(lead.Email)) score += 20;
        if (!string.IsNullOrWhiteSpace(lead.Phone)) score += 10;
        if (!string.IsNullOrWhiteSpace(lead.ContactTitle))
        {
            var t = lead.ContactTitle.ToLowerInvariant();
            score += t.Contains("owner") || t.Contains("founder") || t.Contains("ceo") || t.Contains("president") ? 20
                   : t.Contains("director") || t.Contains("vp") || t.Contains("head") ? 15
                   : t.Contains("manager") ? 10 : 5;
        }
        if (lead.Employees is >= 10 and <= 500) score += 15;
        else if (lead.Employees is > 500) score += 5;
        if (!string.IsNullOrWhiteSpace(lead.Website)) score += 10;
        score += lead.Stage switch { Stage.Validated => 5, Stage.Contacted => 10, Stage.Qualified => 15, Stage.Disqualified => -40, _ => 0 };
        return Math.Clamp(score, 0, 100);
    }
}
