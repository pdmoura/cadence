using System.Text;
using System.Text.Json;
using Cadence.Web.Data;
using Cadence.Web.Models;
using Cadence.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Cadence.Web.Controllers;

public sealed class AutomationsViewModel
{
    public required IReadOnlyList<WebhookDelivery> Deliveries { get; init; }
    public required bool SecretConfigured { get; init; }
    public required string ExampleSignature { get; init; }
    public required string ExampleBody { get; init; }
}

public sealed class AutomationsController(WebhookRepository webhooks, LeadRepository leads, EnrichmentService enrichment, IConfiguration config, ILogger<AutomationsController> logger) : Controller
{
    private const string ExampleBody = """{"source":"website-form","event":"lead.created","company":"Acme Plumbing","contact_name":"Jordan Lee","email":"jordan@acmeplumbing.com","phone":"2125550147"}""";

    [HttpGet("/automations")]
    public async Task<IActionResult> Index()
    {
        var secret = config["Webhooks:Secret"] ?? "";
        return View(new AutomationsViewModel
        {
            Deliveries = await webhooks.RecentAsync(50),
            SecretConfigured = !string.IsNullOrEmpty(secret),
            ExampleBody = ExampleBody,
            ExampleSignature = string.IsNullOrEmpty(secret) ? "(set Webhooks:Secret)" : WebhookSignature.Compute(secret, Encoding.UTF8.GetBytes(ExampleBody)),
        });
    }

    /// <summary>
    /// Inbound lead webhook (website forms, ad platforms, Zapier). Verifies the HMAC signature over the raw body,
    /// de-duplicates by email, creates the lead and queues enrichment suggestions. Every delivery is logged.
    /// </summary>
    [HttpPost("/webhooks/leads")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> InboundLead()
    {
        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms);
        var body = ms.ToArray();
        var text = Encoding.UTF8.GetString(body);
        var secret = config["Webhooks:Secret"] ?? "";
        var valid = WebhookSignature.IsValid(secret, body, Request.Headers["X-Cadence-Signature"].FirstOrDefault());

        string source = "unknown", @event = "lead.created";
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("source", out var s)) source = s.GetString() ?? source;
            if (doc.RootElement.TryGetProperty("event", out var e)) @event = e.GetString() ?? @event;

            if (!valid)
            {
                await webhooks.LogAsync(source, @event, Truncate(text), false, "rejected", "Signature mismatch", null);
                return Unauthorized(new { error = "invalid signature" });
            }

            var company = doc.RootElement.TryGetProperty("company", out var c) ? c.GetString() : null;
            var email = doc.RootElement.TryGetProperty("email", out var em) ? em.GetString() : null;
            if (string.IsNullOrWhiteSpace(company))
            {
                await webhooks.LogAsync(source, @event, Truncate(text), true, "error", "Missing company", null);
                return BadRequest(new { error = "company is required" });
            }

            if (!string.IsNullOrWhiteSpace(email) && await leads.FindByEmailAsync(email) is { } existing)
            {
                await webhooks.LogAsync(source, @event, Truncate(text), true, "accepted", $"Duplicate of lead #{existing.Id}; not created", existing.Id);
                return Ok(new { status = "duplicate", leadId = existing.Id });
            }

            var input = new LeadInput
            {
                Company = company,
                ContactName = Get(doc, "contact_name"),
                ContactTitle = Get(doc, "contact_title"),
                Email = email,
                Phone = Get(doc, "phone"),
                Website = Get(doc, "website"),
                Notes = Get(doc, "notes"),
            };
            var id = await leads.CreateAsync(input, null, "webhook");
            var proposals = await enrichment.ProposeAsync(id);
            await webhooks.LogAsync(source, @event, Truncate(text), true, "accepted", $"Created lead #{id}; {proposals} suggestion(s) queued", id);
            return Created($"/leads/{id}", new { status = "created", leadId = id, suggestions = proposals });
        }
        catch (JsonException)
        {
            await webhooks.LogAsync(source, @event, Truncate(text), valid, "error", "Body is not valid JSON", null);
            return BadRequest(new { error = "invalid json" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Webhook processing failed");
            await webhooks.LogAsync(source, @event, Truncate(text), valid, "error", ex.Message, null);
            return StatusCode(500, new { error = "processing failed" });
        }
    }

    private static string? Get(JsonDocument doc, string name) => doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Truncate(string s) => s.Length > 4000 ? s[..4000] : s;
}
