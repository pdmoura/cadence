using System.Security.Cryptography;
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
    public required WorkspaceSettings Settings { get; init; }
    public required bool SecretConfigured { get; init; }
    public required string ExampleSignature { get; init; }
    public required string ExampleBody { get; init; }
    public required string BaseUrl { get; init; }
    public required Dictionary<string, (int Count, DateTimeOffset Last)> BySource { get; init; }
    public required AiStatus Ai { get; init; }
    public string IntakeUrl => $"{BaseUrl}/intake/{Settings.IntakeKey}";
    public (int Count, DateTimeOffset Last)? Source(string name) => BySource.TryGetValue(name, out var v) ? v : null;
}

public sealed class AutomationsController(
    WebhookRepository webhooks, IntakeService intake, SettingsService settings, AiClient claude,
    NotificationService notifications, OutboundSender sender, IConfiguration config, ILogger<AutomationsController> logger) : Controller
{
    private const string ExampleBody = """{"source":"zapier","company":"Acme Plumbing","contact_name":"Jordan Lee","email":"jordan@acmeplumbing.com","phone":"2125550147"}""";

    [HttpGet("/automations")]
    public async Task<IActionResult> Index()
    {
        var secret = config["Webhooks:Secret"] ?? "";
        return View(new AutomationsViewModel
        {
            Deliveries = await webhooks.RecentAsync(40),
            Settings = await settings.GetAsync(),
            SecretConfigured = !string.IsNullOrEmpty(secret),
            ExampleBody = ExampleBody,
            ExampleSignature = string.IsNullOrEmpty(secret) ? "(set Webhooks:Secret)" : WebhookSignature.Compute(secret, Encoding.UTF8.GetBytes(ExampleBody)),
            BaseUrl = $"{Request.Scheme}://{Request.Host}",
            BySource = await webhooks.InboundBySourceAsync(),
            Ai = await claude.StatusAsync(),
        });
    }

    /// <summary>
    /// JSON lead webhook for Zapier, Make, n8n or custom code. Two ways to authenticate:
    /// <c>X-Cadence-Key: &lt;intake key&gt;</c> (no-code tools) or <c>X-Cadence-Signature: sha256=&lt;HMAC&gt;</c> (developers).
    /// </summary>
    [HttpPost("/webhooks/leads")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> InboundLead()
    {
        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms);
        var body = ms.ToArray();
        var text = Encoding.UTF8.GetString(body);
        var ws = await settings.GetAsync();

        var secret = config["Webhooks:Secret"] ?? "";
        var signed = WebhookSignature.IsValid(secret, body, Request.Headers["X-Cadence-Signature"].FirstOrDefault());
        var keyed = KeyMatches(ws.IntakeKey, Request.Headers["X-Cadence-Key"].FirstOrDefault());
        var valid = signed || keyed;

        string source = "webhook", @event = "lead.created";
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (Get(doc, "source") is { } s) source = s;
            if (Get(doc, "event") is { } e) @event = e;

            if (!valid)
            {
                await webhooks.LogAsync(source, @event, Truncate(text), false, "rejected", "Missing or wrong X-Cadence-Key / X-Cadence-Signature", null);
                return Unauthorized(new { error = "send X-Cadence-Key with your intake key, or X-Cadence-Signature with an HMAC of the body" });
            }

            var input = new LeadInput
            {
                Company = Get(doc, "company") ?? "",
                ContactName = Get(doc, "contact_name") ?? Get(doc, "name"),
                ContactTitle = Get(doc, "contact_title") ?? Get(doc, "title"),
                Email = Get(doc, "email"),
                Phone = Get(doc, "phone"),
                Website = Get(doc, "website"),
                Notes = Get(doc, "notes") ?? Get(doc, "message"),
            };
            if (string.IsNullOrWhiteSpace(input.Company))
            {
                await webhooks.LogAsync(source, @event, Truncate(text), true, "error", "Missing company", null);
                return BadRequest(new { error = "company is required" });
            }

            var result = await intake.CreateAsync(input, source, null);
            await webhooks.LogAsync(source, @event, Truncate(text), true, "accepted",
                result.Status == "duplicate" ? $"Duplicate of lead #{result.LeadId}" : $"Created lead #{result.LeadId}{(result.AssignedTo is null ? "" : $", assigned to {result.AssignedTo}")}", result.LeadId);
            return result.Status == "duplicate"
                ? Ok(new { status = "duplicate", leadId = result.LeadId })
                : Created($"/leads/{result.LeadId}", new { status = "created", leadId = result.LeadId, suggestions = result.Suggestions, assignedTo = result.AssignedTo });
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

    /// <summary>
    /// Website form endpoint: point any HTML form's action here (Webflow, WordPress, Wix, a landing page).
    /// Accepts form posts or JSON. The <c>_gotcha</c> field is a honeypot; <c>_redirect</c> sends the visitor to your thank-you page.
    /// </summary>
    [HttpPost("/intake/{key}")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Intake(string key)
    {
        AddCors();
        var ws = await settings.GetAsync();
        if (!KeyMatches(ws.IntakeKey, key)) return NotFound();

        Dictionary<string, string?> fields;
        if (Request.HasFormContentType)
        {
            var form = await Request.ReadFormAsync();
            fields = form.ToDictionary(kv => kv.Key.ToLowerInvariant(), kv => (string?)kv.Value.ToString());
        }
        else
        {
            using var reader = new StreamReader(Request.Body);
            var raw = await reader.ReadToEndAsync();
            try
            {
                using var doc = JsonDocument.Parse(raw);
                fields = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name.ToLowerInvariant(), p => (string?)(p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ToString()));
            }
            catch (JsonException) { return BadRequest(new { error = "send a form post or a JSON object" }); }
        }

        string? F(params string[] names) => names.Select(n => fields.GetValueOrDefault(n)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
        var redirect = F("_redirect");
        var payload = JsonSerializer.Serialize(fields.Where(kv => !kv.Key.StartsWith('_')).ToDictionary());

        if (!string.IsNullOrEmpty(F("_gotcha")))
        {
            await webhooks.LogAsync("website-form", "lead.created", Truncate(payload), true, "rejected", "Honeypot field filled (likely spam)", null);
            return Done(redirect, spam: true);
        }

        var company = F("company", "company_name", "organization", "business");
        var email = F("email", "email_address");
        if (company is null && email is null)
        {
            await webhooks.LogAsync("website-form", "lead.created", Truncate(payload), true, "error", "Neither company nor email was sent", null);
            return BadRequest(new { error = "send at least company or email" });
        }

        var input = new LeadInput
        {
            Company = company ?? email!.Split('@').Last(),
            ContactName = F("name", "contact_name", "full_name") ?? string.Join(' ', new[] { F("first_name"), F("last_name") }.Where(x => x is not null)),
            ContactTitle = F("title", "job_title", "role"),
            Email = email,
            Phone = F("phone", "tel", "whatsapp"),
            Website = F("website", "url"),
            Notes = F("message", "notes", "comments"),
        };
        if (string.IsNullOrWhiteSpace(input.ContactName)) input.ContactName = null;

        var result = await intake.CreateAsync(input, "website-form", null);
        await webhooks.LogAsync("website-form", "lead.created", Truncate(payload), true, "accepted",
            result.Status == "duplicate" ? $"Duplicate of lead #{result.LeadId}" : $"Created lead #{result.LeadId}{(result.AssignedTo is null ? "" : $", assigned to {result.AssignedTo}")}", result.LeadId);
        return Done(redirect, spam: false);
    }

    [HttpOptions("/intake/{key}")]
    public IActionResult IntakePreflight(string key) { AddCors(); return NoContent(); }

    /// <summary>Creates a realistic sample lead through the real intake path, so a new team sees the whole flow.</summary>
    [HttpPost("/automations/test-lead")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TestLead(string? returnUrl)
    {
        var samples = new[]
        {
            ("Brightside Pediatric Dental", "Dr. Maya Chen", "Owner", "maya@brightsidepediatric.com", "6175550188"),
            ("Keystone Freight Partners", "Luis Ortega", "Operations Director", "l.ortega@keystonefreight.com", "3125550143"),
            ("Evergreen Roofing Co.", "Sam Patel", "General Manager", "sam@evergreenroofingco.com", "5035550177"),
            ("Lumen Accounting Group", "Ruth Adeyemi", "Managing Partner", "ruth@lumenaccounting.com", "2125550109"),
        };
        var pick = samples[RandomNumberGenerator.GetInt32(samples.Length)];
        var suffix = RandomNumberGenerator.GetInt32(100, 999);
        var result = await intake.CreateAsync(new LeadInput
        {
            Company = pick.Item1, ContactName = pick.Item2, ContactTitle = pick.Item3,
            Email = pick.Item4.Replace("@", $"+demo{suffix}@"), Phone = pick.Item5, Notes = "Sample lead created from the Automations page.",
        }, "test", null);
        await webhooks.LogAsync("test", "lead.created", JsonSerializer.Serialize(new { company = pick.Item1 }), true, "accepted", $"Created lead #{result.LeadId}", result.LeadId);
        TempData["toast"] = $"Sample lead created{(result.AssignedTo is null ? "" : $" and assigned to {result.AssignedTo}")}. {result.Suggestions} suggestion(s) are waiting.";
        return Redirect(returnUrl is { Length: > 0 } && Url.IsLocalUrl(returnUrl) ? returnUrl : $"/leads/{result.LeadId}");
    }

    [HttpPost("/automations/test-notification")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TestNotification()
    {
        var ws = await settings.GetAsync();
        var (ok, message) = await notifications.SendNowAsync(sender, "test", $"Cadence is connected. Notifications from {Or(ws.WorkspaceName, "your workspace")} will arrive here.", new { test = true });
        TempData[ok ? "toast" : "error"] = message;
        return Redirect("/automations#notifications");
    }

    private IActionResult Done(string? redirect, bool spam)
    {
        if (redirect is not null && Uri.TryCreate(redirect, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp))
            return Redirect(redirect);
        if (Request.Headers.Accept.ToString().Contains("application/json") || !Request.HasFormContentType)
            return Ok(new { status = "received" });
        return Content("<!doctype html><meta name=viewport content='width=device-width'><title>Thanks</title><body style='font-family:system-ui;display:grid;place-items:center;min-height:90vh;color:#0b1227'><div style='text-align:center'><h1>Thank you</h1><p>We received your details and will be in touch shortly.</p></div>", "text/html");
    }

    private void AddCors()
    {
        Response.Headers["Access-Control-Allow-Origin"] = "*";
        Response.Headers["Access-Control-Allow-Methods"] = "POST, OPTIONS";
        Response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
    }

    private static bool KeyMatches(string expected, string? provided) =>
        !string.IsNullOrEmpty(expected) && !string.IsNullOrEmpty(provided) &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(provided.Trim()));

    private static string? Get(JsonDocument doc, string name) =>
        doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;

    private static string Or(string v, string f) => string.IsNullOrWhiteSpace(v) ? f : v;
    private static string Truncate(string s) => s.Length > 4000 ? s[..4000] : s;
}
