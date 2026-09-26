using Cadence.Web.Data;
using Cadence.Web.Models;
using Cadence.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Cadence.Web.Controllers;

public sealed class WorkspaceViewModel
{
    public required string Tab { get; init; }
    public required WorkspaceSettings Settings { get; init; }
    public required IReadOnlyList<Member> Members { get; init; }
    public required AiStatus Ai { get; init; }
    public required string BaseUrl { get; init; }
    public int Step { get; init; }
    public bool IsWizard => Step > 0;
    public string Next => IsWizard ? $"/setup/{Step + 1}" : $"/settings?tab={Tab}";
    public string Here => IsWizard ? $"/setup/{Step}" : $"/settings?tab={Tab}";

    public static readonly (int Step, string Key, string Title, string Hint)[] Steps =
    [
        (1, "workspace", "Your business", "What you sell and who you sell to"),
        (2, "team", "Your team", "Who works the pipeline"),
        (3, "goals", "Goals", "What a good day and week look like"),
        (4, "sources", "Lead sources", "How leads get into Cadence"),
        (5, "ai", "AI research", "Connect a provider with your own key"),
    ];

    public static readonly string[] Timezones =
    [
        "Africa/Nairobi", "Africa/Lagos", "Africa/Johannesburg", "America/Sao_Paulo", "America/Mexico_City", "America/New_York",
        "America/Chicago", "America/Denver", "America/Los_Angeles", "Europe/London", "Europe/Lisbon", "Europe/Madrid", "Europe/Berlin", "Asia/Dubai", "Asia/Kolkata", "UTC",
    ];
}

public sealed class WorkspaceController(SettingsService settings, MemberRepository members, AiClient ai, SecretBox secrets, CurrentMember current) : Controller
{
    private static readonly string[] Tabs = ["workspace", "team", "goals", "sources", "ai", "appearance"];

    [HttpGet("/settings")]
    public async Task<IActionResult> Settings(string? tab) =>
        View("Settings", await ModelAsync(Tabs.Contains(tab) ? tab! : "workspace", 0));

    [HttpGet("/setup")]
    [HttpGet("/setup/{step:int}")]
    public async Task<IActionResult> Setup(int step = 1)
    {
        if (step > WorkspaceViewModel.Steps.Length) return Redirect("/setup/finish");
        step = Math.Max(1, step);
        return View("Setup", await ModelAsync(WorkspaceViewModel.Steps[step - 1].Key, step));
    }

    [HttpGet("/setup/finish")]
    public async Task<IActionResult> Finish()
    {
        await settings.SetAsync(new() { ["onboarded"] = "1" });
        TempData["toast"] = "Your workspace is ready. Start with the checklist on the home page.";
        return Redirect("/");
    }

    [HttpPost("/settings/workspace")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveWorkspace(string workspaceName, string product, string icp, string markets, string? next)
    {
        if (string.IsNullOrWhiteSpace(workspaceName)) return Back("Give your workspace a name.", next, error: true);
        await settings.SetAsync(new() { ["workspace_name"] = workspaceName, ["product"] = product, ["icp"] = icp, ["markets"] = markets });
        return Back("Workspace saved.", next);
    }

    [HttpPost("/settings/goals")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveGoals(int targetDailyTouches, int targetWeeklyValidated, int targetWeeklyQualified, string? next)
    {
        await settings.SetAsync(new()
        {
            ["target_daily_touches"] = Math.Clamp(targetDailyTouches, 0, 500).ToString(),
            ["target_weekly_validated"] = Math.Clamp(targetWeeklyValidated, 0, 1000).ToString(),
            ["target_weekly_qualified"] = Math.Clamp(targetWeeklyQualified, 0, 1000).ToString(),
        });
        return Back("Goals saved.", next);
    }

    [HttpPost("/settings/sources")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSources(bool autoEnrich, bool autoAssign, string? outboundUrl, string[]? outboundEvents, string? next)
    {
        outboundUrl = outboundUrl?.Trim();
        if (!string.IsNullOrEmpty(outboundUrl) && !(Uri.TryCreate(outboundUrl, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps))
            return Back("The notification URL must start with https://", next, error: true);
        await settings.SetAsync(new()
        {
            ["auto_enrich"] = autoEnrich ? "1" : "0",
            ["auto_assign"] = autoAssign ? "1" : "0",
            ["outbound_url"] = outboundUrl,
            ["outbound_events"] = string.Join(',', (outboundEvents ?? []).Where(e => SettingsService.AllEvents.Contains(e))),
        });
        return Back("Lead sources and notifications saved.", next);
    }

    [HttpPost("/settings/sources/rotate-key")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RotateKey(string? next)
    {
        await settings.SetAsync(new() { ["intake_key"] = SettingsService.NewKey() });
        return Back("New intake key created. Update your forms and Zapier steps with it; the old key stopped working.", next);
    }

    [HttpPost("/settings/ai")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveAi(string aiProvider, string? aiModel, string? openRouterModel, string? apiKey, bool aiEnabled, bool removeKey, string? next)
    {
        aiProvider = aiProvider is "anthropic" or "openrouter" ? aiProvider : "none";
        var current = await settings.GetAsync();
        var model = AiClient.NormalizeModel(aiProvider, aiProvider == "openrouter" ? openRouterModel : aiModel);
        var values = new Dictionary<string, string?> { ["ai_provider"] = aiProvider, ["ai_model"] = model, ["ai_enabled"] = aiEnabled ? "1" : "0" };

        apiKey = apiKey?.Trim();
        var providerChanged = aiProvider != current.AiProvider;
        if (removeKey || aiProvider == "none" || (providerChanged && string.IsNullOrEmpty(apiKey)))
        {
            values["ai_key"] = "";
            values["ai_key_hint"] = "";
        }
        if (!string.IsNullOrEmpty(apiKey) && aiProvider != "none")
        {
            if (apiKey.Length < 20 || apiKey.Any(char.IsWhiteSpace)) return Back("That does not look like an API key. Paste the whole key.", next, error: true);
            values["ai_key"] = secrets.Protect(apiKey);
            values["ai_key_hint"] = apiKey[^4..];
        }
        await settings.SetAsync(values);

        var message = aiProvider == "none" ? "AI disconnected. The suggestion rules keep working."
            : !string.IsNullOrEmpty(apiKey) ? "Key saved and encrypted. Use Test connection to check it."
            : removeKey ? "Key removed." : "AI settings saved.";
        return Back(message, next);
    }

    [HttpPost("/settings/ai/test")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TestAi(string? next)
    {
        var (ok, message) = await ai.TestAsync(HttpContext.RequestAborted);
        return Back(message, next, error: !ok);
    }

    [HttpPost("/settings/team/add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddMember(string name, string email, string role, string timezone, string? next)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            return Back("Add a name and a valid email.", next, error: true);
        if (await members.EmailTakenAsync(email)) return Back("Someone on the team already uses that email.", next, error: true);
        var r = role switch { "team_lead" => MemberRole.TeamLead, "developer" => MemberRole.Developer, _ => MemberRole.Sdr };
        await members.AddAsync(name, email, r, timezone);
        return Back($"{name.Trim()} joined the team.", next);
    }

    [HttpPost("/settings/team/{id:long}/remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveMember(long id, string? next)
    {
        var all = await members.AllAsync();
        if (all.Count <= 1) return Back("A workspace needs at least one member.", next, error: true);
        var m = all.FirstOrDefault(x => x.Id == id);
        if (m is null) return Back("That member no longer exists.", next, error: true);
        await members.RemoveAsync(id);
        if (await current.IdAsync() == id) Response.Cookies.Delete(CurrentMember.CookieName);
        return Back($"{m.Name} was removed. Their leads are now unassigned.", next);
    }

    private async Task<WorkspaceViewModel> ModelAsync(string tab, int step) => new()
    {
        Tab = tab,
        Step = step,
        Settings = await settings.GetAsync(),
        Members = await members.AllAsync(),
        Ai = await ai.StatusAsync(),
        BaseUrl = $"{Request.Scheme}://{Request.Host}",
    };

    private IActionResult Back(string message, string? next, bool error = false)
    {
        TempData[error ? "error" : "toast"] = message;
        var target = next is { Length: > 0 } && Url.IsLocalUrl(next) ? next : "/settings";
        // On an error inside the wizard, stay on the current step instead of advancing.
        if (error && Request.Headers.Referer.FirstOrDefault() is { } referer && Uri.TryCreate(referer, UriKind.Absolute, out var r) && r.Host == Request.Host.Host)
            target = r.PathAndQuery;
        return Redirect(target);
    }
}
