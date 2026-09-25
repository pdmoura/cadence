using System.Net;
using System.Net.Http.Json;
using System.Text;
using Cadence.Web.Models;
using Cadence.Web.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Cadence.Tests;

public sealed class WebhookSignatureTests
{
    [Fact]
    public void Signature_is_stable_and_verifies()
    {
        var body = Encoding.UTF8.GetBytes("{\"company\":\"Acme\"}");
        var sig = WebhookSignature.Compute("secret", body);
        Assert.StartsWith("sha256=", sig);
        Assert.True(WebhookSignature.IsValid("secret", body, sig));
        Assert.False(WebhookSignature.IsValid("other", body, sig));
        Assert.False(WebhookSignature.IsValid("secret", Encoding.UTF8.GetBytes("{\"company\":\"Acme!\"}"), sig));
        Assert.False(WebhookSignature.IsValid("secret", body, null));
        Assert.False(WebhookSignature.IsValid("", body, sig));
    }
}

public sealed class StandupReportTests
{
    [Fact]
    public void Report_lists_metrics_blockers_and_missing_members()
    {
        var team = new List<Member> { new(1, "Naomi Achieng", "n@x", MemberRole.TeamLead, "UTC"), new(2, "Brian Otieno", "b@x", MemberRole.Sdr, "UTC") };
        var standups = new List<Standup> { new(1, 2, "Brian Otieno", "sdr", "2026-09-25", "12 touches", "15 touches\nsecond line", "2 replies", "Waiting on case study", "Naomi: approve reuse", DateTimeOffset.UtcNow) };
        var text = StandupReportBuilder.Build("2026-09-25", standups, new ReportMetrics(3, 2, 1, 1, 0, 9, 4), team);

        Assert.Contains("Cadence daily report - 2026-09-25", text);
        Assert.Contains("Leads created: 3", text);
        Assert.Contains("1/2 submitted, missing: Naomi Achieng", text);
        Assert.Contains("- Brian Otieno: 15 touches", text);
        Assert.DoesNotContain("second line", text);
        Assert.Contains("BLOCKERS\n- Brian Otieno: Waiting on case study", text.Replace("\r\n", "\n"));
        Assert.Contains("DECISIONS / HELP NEEDED\n- Brian Otieno: Naomi: approve reuse", text.Replace("\r\n", "\n"));
    }
}

/// <summary>End-to-end through the real HTTP pipeline with a temp SQLite database.</summary>
public sealed class WebhookEndpointTests : IAsyncLifetime
{
    private const string Secret = "test-secret";
    private WebApplicationFactory<Program> _factory = null!;
    private string _dbPath = null!;

    public Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"cadence-e2e-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Database:Path", _dbPath);
            b.UseSetting("Webhooks:Secret", Secret);
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task Signed_payload_creates_a_lead_and_unsigned_is_rejected()
    {
        var client = _factory.CreateClient();
        var body = "{\"source\":\"website-form\",\"company\":\"Acme Plumbing\",\"contact_name\":\"Jordan Lee\",\"email\":\"jordan@acmeplumbing.com\",\"phone\":\"2125550147\"}";
        var bytes = Encoding.UTF8.GetBytes(body);

        using var bad = new HttpRequestMessage(HttpMethod.Post, "/webhooks/leads") { Content = new ByteArrayContent(bytes) };
        bad.Content.Headers.ContentType = new("application/json");
        bad.Headers.Add("X-Cadence-Signature", "sha256=deadbeef");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(bad)).StatusCode);

        using var good = new HttpRequestMessage(HttpMethod.Post, "/webhooks/leads") { Content = new ByteArrayContent(bytes) };
        good.Content.Headers.ContentType = new("application/json");
        good.Headers.Add("X-Cadence-Signature", WebhookSignature.Compute(Secret, bytes));
        var response = await client.SendAsync(good);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CreatedResponse>();
        Assert.Equal("created", created!.Status);
        Assert.True(created.Suggestions >= 2); // website from domain + phone normalisation

        using var dup = new HttpRequestMessage(HttpMethod.Post, "/webhooks/leads") { Content = new ByteArrayContent(bytes) };
        dup.Content.Headers.ContentType = new("application/json");
        dup.Headers.Add("X-Cadence-Signature", WebhookSignature.Compute(Secret, bytes));
        var dupResponse = await client.SendAsync(dup);
        Assert.Equal(HttpStatusCode.OK, dupResponse.StatusCode);
        Assert.Equal("duplicate", (await dupResponse.Content.ReadFromJsonAsync<CreatedResponse>())!.Status);

        var page = await client.GetStringAsync($"/leads/{created.LeadId}");
        Assert.Contains("Acme Plumbing", page);
        Assert.Contains("https://acmeplumbing.com", page);

        var health = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    private sealed record CreatedResponse(string Status, long LeadId, int Suggestions);
}
