using System.Net;
using System.Text;
using System.Text.Json;
using Cadence.Web.Data;
using Cadence.Web.Models;
using Cadence.Web.Services;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cadence.Tests;

public sealed class CsvTests
{
    [Fact]
    public void Parses_quotes_escaped_quotes_semicolons_and_crlf()
    {
        var rows = Csv.Parse("company;notes\r\n\"Acme; Inc\";\"said \"\"hi\"\"\"\r\nBeta;\r\n");
        Assert.Equal(3, rows.Count);
        Assert.Equal(["Acme; Inc", "said \"hi\""], rows[1]);
        Assert.Equal("Beta", rows[2][0]);
    }

    [Fact]
    public void Maps_common_export_headers_and_builds_leads()
    {
        var rows = Csv.Parse("Company Name,First Name,Last Name,Job Title,Email Address,Domain,# Employees\nHarbor Dental,Elena,Ruiz,Owner,elena@harbor.com,harbor.com,14 employees\n,No,Company,,x@y.com,,\n");
        var map = Csv.MapHeaders(rows[0]);
        var lead = Csv.ToLead(rows[1], map)!;
        Assert.Equal("Harbor Dental", lead.Company);
        Assert.Equal("Elena Ruiz", lead.ContactName);
        Assert.Equal("Owner", lead.ContactTitle);
        Assert.Equal("https://harbor.com", lead.Website);
        Assert.Equal(14, lead.Employees);
        Assert.Null(Csv.ToLead(rows[2], map)); // no company: skipped
    }
}

public sealed class SecretBoxTests
{
    private static SecretBox Box(string secret) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Secrets:Key"] = secret }).Build());

    [Fact]
    public void Round_trips_and_never_stores_plaintext()
    {
        var box = Box("server-secret");
        var stored = box.Protect("sk-ant-api03-example-key-1234");
        Assert.DoesNotContain("sk-ant", stored);
        Assert.Equal("sk-ant-api03-example-key-1234", box.Unprotect(stored));
        Assert.NotEqual(stored, box.Protect("sk-ant-api03-example-key-1234")); // random nonce
    }

    [Fact]
    public void Wrong_server_secret_or_tampering_yields_null()
    {
        var stored = Box("one").Protect("secret-value-long-enough");
        Assert.Null(Box("two").Unprotect(stored));
        var bytes = Convert.FromBase64String(stored);
        bytes[15] ^= 0xFF;
        Assert.Null(Box("one").Unprotect(Convert.ToBase64String(bytes)));
        Assert.Null(Box("one").Unprotect("not base64!"));
    }
}

public sealed class IntakeServiceTests : IAsyncLifetime
{
    private TestDatabase _db = null!;
    private IntakeService _intake = null!;
    private LeadRepository _leads = null!;
    private SettingsService _settings = null!;

    public async Task InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync(seed: false);
        _leads = new LeadRepository(_db.Db);
        var members = new MemberRepository(_db.Db);
        var suggestions = new SuggestionRepository(_db.Db);
        _settings = new SettingsService(_db.Db);
        var config = new ConfigurationBuilder().Build();
        var ai = new AiClient(_settings, new SecretBox(config), new NoHttp(), NullLogger<AiClient>.Instance);
        IEnrichmentProvider[] providers = [new HeuristicEnrichmentProvider(), new LlmEnrichmentProvider(ai, _settings)];
        var jobs = new BackgroundJobs();
        _intake = new IntakeService(_leads, members, new EnrichmentService(providers, suggestions, _leads), providers, _settings, ai, jobs, new NotificationService(_settings, jobs));

        await _db.Db.ExecuteAsync("INSERT INTO members (id, name, email, role) VALUES (1, 'Lead', 'l@t', 'team_lead'), (2, 'Ana', 'a@t', 'sdr'), (3, 'Bo', 'b@t', 'sdr')");
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    [Fact]
    public async Task Dedupes_by_email_and_assigns_the_sdr_with_the_lightest_load()
    {
        // Ana already has two open leads, Bo has none.
        await _leads.CreateAsync(new LeadInput { Company = "A1", OwnerId = 2 }, 1, "manual");
        await _leads.CreateAsync(new LeadInput { Company = "A2", OwnerId = 2 }, 1, "manual");

        var first = await _intake.CreateAsync(new LeadInput { Company = "Acme", Email = "jo@acme.com" }, "website-form", null);
        Assert.Equal("created", first.Status);
        Assert.Equal("Bo", first.AssignedTo);
        Assert.True(first.Suggestions >= 1); // website from the email domain

        var again = await _intake.CreateAsync(new LeadInput { Company = "Acme again", Email = "JO@acme.com" }, "zapier", null);
        Assert.Equal("duplicate", again.Status);
        Assert.Equal(first.LeadId, again.LeadId);
    }

    [Fact]
    public async Task Manual_leads_are_not_auto_assigned_and_ai_stays_off_without_a_key()
    {
        var r = await _intake.CreateAsync(new LeadInput { Company = "Solo" }, "manual", 1);
        Assert.Null(r.AssignedTo);
        var status = await new AiClient(_settings, new SecretBox(new ConfigurationBuilder().Build()), new NoHttp(), NullLogger<AiClient>.Instance).StatusAsync();
        Assert.False(status.Active);
        Assert.Equal("none", status.Provider);
    }

    [Fact]
    public async Task Settings_round_trip_and_generate_an_intake_key()
    {
        var s = await _settings.GetAsync();
        Assert.StartsWith("ck_", s.IntakeKey);
        await _settings.SetAsync(new() { ["workspace_name"] = "Northstar", ["icp"] = "Clinics", ["ai_provider"] = "openrouter", ["ai_model"] = "bad model!" });
        s = await _settings.GetAsync();
        Assert.True(s.HasProfile);
        Assert.Equal("openrouter", s.AiProvider);
        Assert.Equal("openrouter/auto", s.AiModel); // invalid model names fall back
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No network in tests.");
    }
}

/// <summary>Website form and Zapier-style key auth through the real HTTP pipeline.</summary>
public sealed class IntakeEndpointTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private string _dbPath = null!;

    public Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"cadence-intake-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Database:Path", _dbPath);
            b.UseSetting("Webhooks:Secret", "test-secret");
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
    }

    private async Task<string> IntakeKeyAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<SettingsService>().GetAsync()).IntakeKey;
    }

    [Fact]
    public async Task Html_form_post_creates_a_lead_and_redirects_and_the_honeypot_blocks_bots()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var key = await IntakeKeyAsync();

        var ok = await client.PostAsync($"/intake/{key}", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["company"] = "Riverstone Physio", ["name"] = "Kim Park", ["email"] = "kim@riverstonephysio.com", ["_redirect"] = "https://example.com/thanks",
        }));
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.Equal("https://example.com/thanks", ok.Headers.Location!.ToString());

        var bot = await client.PostAsync($"/intake/{key}", new FormUrlEncodedContent(new Dictionary<string, string> { ["company"] = "Spam Co", ["email"] = "s@spam.co", ["_gotcha"] = "x" }));
        Assert.Equal(HttpStatusCode.OK, bot.StatusCode);

        var wrong = await client.PostAsync("/intake/ck_wrong", new FormUrlEncodedContent(new Dictionary<string, string> { ["company"] = "X" }));
        Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);

        var leads = await client.GetStringAsync("/leads?q=Riverstone");
        Assert.Contains("Riverstone Physio", leads);
        Assert.DoesNotContain("Spam Co", await client.GetStringAsync("/leads?q=Spam"));
    }

    [Fact]
    public async Task Json_webhook_accepts_the_intake_key_header()
    {
        var client = _factory.CreateClient();
        var key = await IntakeKeyAsync();
        var body = JsonSerializer.Serialize(new { source = "zapier", company = "Keyed Inc", email = "k@keyed.io" });

        using var bad = new HttpRequestMessage(HttpMethod.Post, "/webhooks/leads") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        bad.Headers.Add("X-Cadence-Key", "ck_nope");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(bad)).StatusCode);

        using var good = new HttpRequestMessage(HttpMethod.Post, "/webhooks/leads") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        good.Headers.Add("X-Cadence-Key", key);
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(good)).StatusCode);
    }

    [Fact]
    public async Task Settings_setup_and_guide_pages_render()
    {
        var client = _factory.CreateClient();
        foreach (var path in new[] { "/settings", "/settings?tab=team", "/settings?tab=ai", "/settings?tab=sources", "/setup/1", "/setup/5", "/guide", "/automations", "/leads/import" })
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
    }
}

public sealed class DeleteLeadTests : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async Task InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync(seed: false);
        await _db.Db.ExecuteAsync("INSERT INTO members (id, name, email, role) VALUES (1, 'Ana', 'a@t', 'sdr')");
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    [Fact]
    public async Task Deleting_a_lead_cascades_to_its_history_but_keeps_the_delivery_log()
    {
        var leads = new LeadRepository(_db.Db);
        var id = await leads.CreateAsync(new LeadInput { Company = "Gone Co", Email = "g@gone.co" }, 1, "manual");
        await new SuggestionRepository(_db.Db).AddAsync(id, "industry", "Software", null, "heuristic", 0.6, null);
        await new ActivityRepository(_db.Db).AddAsync(id, 1, ActivityKind.Call, "Called", null);
        await new WebhookRepository(_db.Db).LogAsync("test", "lead.created", "{}", true, "accepted", "Created", id);

        Assert.Equal(1, await leads.DeleteAsync(id));
        Assert.Null(await leads.FindAsync(id));
        foreach (var table in new[] { "enrichment_suggestions", "activities", "lead_stage_events" })
            Assert.Equal(0, (await _db.Db.QueryAsync($"SELECT COUNT(*) AS n FROM {table} WHERE lead_id = ?", id))[0].Int("n"));
        var log = await _db.Db.QueryAsync("SELECT lead_id FROM webhook_deliveries");
        Assert.Single(log);
        Assert.Null(log[0].LongOrNull("lead_id"));
    }
}

public sealed class OpenRouterAndCalendarTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private string _dbPath = null!;

    public Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"cadence-or-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("Database:Path", _dbPath));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task Sign_in_with_OpenRouter_redirects_with_a_S256_challenge_and_a_verifier_cookie()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.GetAsync("/settings/ai/openrouter/connect?next=%2Fsetup%2F5");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith("https://openrouter.ai/auth?callback_url=", location);
        Assert.Contains("code_challenge_method=S256", location);
        Assert.Contains("%2Fsettings%2Fai%2Fopenrouter%2Fcallback", location);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("cadence_or_pkce=") && c.Contains("httponly", StringComparison.OrdinalIgnoreCase));

        // A callback without the cookie is rejected without calling OpenRouter.
        var cb = await client.GetAsync("/settings/ai/openrouter/callback?code=abc");
        Assert.Equal(HttpStatusCode.Redirect, cb.StatusCode);
    }

    [Fact]
    public async Task Standup_calendar_renders_past_days_with_history()
    {
        var client = _factory.CreateClient();
        var day = DateTime.UtcNow.AddDays(-7).ToString("yyyy-MM-dd");
        var html = await client.GetStringAsync($"/standups?day={day}");
        Assert.Contains("cal-day", html);
        Assert.Contains("is-selected", html);
    }

    [Fact]
    public async Task Calendar_paging_lands_on_a_friday_next_to_the_current_range()
    {
        var client = _factory.CreateClient();
        var wednesday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-60);
        while (wednesday.DayOfWeek != DayOfWeek.Wednesday) wednesday = wednesday.AddDays(-1);
        var html = await client.GetStringAsync($"/standups?day={wednesday:yyyy-MM-dd}");
        var gridStart = wednesday.AddDays(-2 - 14);

        var earlier = DateOnly.ParseExact(Regex.Match(html, @"href=""/standups\?day=([0-9-]{10})"" aria-label=""Earlier weeks""").Groups[1].Value, "yyyy-MM-dd");
        var later = DateOnly.ParseExact(Regex.Match(html, @"href=""/standups\?day=([0-9-]{10})"" aria-label=""Later weeks""").Groups[1].Value, "yyyy-MM-dd");
        Assert.Equal(DayOfWeek.Friday, earlier.DayOfWeek);
        Assert.Equal(gridStart.AddDays(-3), earlier);
        Assert.Equal(DayOfWeek.Friday, later.DayOfWeek);
        Assert.Equal(gridStart.AddDays(21 + 18), later);
    }

    [Theory]
    [InlineData("<html>Bad gateway</html>")]
    [InlineData("{\"key\": 42}")]
    [InlineData("{\"key\": \"ab\"}")]
    public async Task A_bad_OpenRouter_answer_redirects_with_an_error_instead_of_failing(string body)
    {
        var client = WithOpenRouter(body).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await client.GetAsync("/settings/ai/openrouter/connect");
        var cb = await client.GetAsync("/settings/ai/openrouter/callback?code=abc");
        Assert.Equal(HttpStatusCode.Redirect, cb.StatusCode);
        Assert.Equal("/settings?tab=ai", cb.Headers.Location!.ToString());
    }

    [Fact]
    public async Task A_key_from_OpenRouter_is_saved_encrypted_with_its_hint()
    {
        var factory = WithOpenRouter("{\"key\": \"sk-or-v1-0123456789abcdefWXYZ\"}");
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await client.GetAsync("/settings/ai/openrouter/connect");
        var cb = await client.GetAsync("/settings/ai/openrouter/callback?code=abc");
        Assert.Equal(HttpStatusCode.Redirect, cb.StatusCode);

        using var scope = factory.Services.CreateScope();
        var saved = await scope.ServiceProvider.GetRequiredService<SettingsService>().GetAsync();
        Assert.Equal("openrouter", saved.AiProvider);
        Assert.Equal("openrouter/auto", saved.AiModel);
        Assert.Equal("WXYZ", saved.AiKeyHint);
        Assert.DoesNotContain("sk-or-v1", saved.AiKeyEncrypted);
    }

    private WebApplicationFactory<Program> WithOpenRouter(string body) =>
        _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddHttpClient("openrouter").ConfigurePrimaryHttpMessageHandler(() => new FixedResponse(body))));

    private sealed class FixedResponse(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}

public sealed class SeedMigrationTests
{
    [Fact]
    public async Task Standup_history_seed_skips_members_and_leads_that_were_removed()
    {
        var source = TestDatabase.FindMigrations();
        var dir = Directory.CreateTempSubdirectory("cadence-mig-");
        foreach (var f in Directory.GetFiles(source, "*.sql").Where(f => !f.EndsWith("0004_standup_history_seed.sql")))
            File.Copy(f, Path.Combine(dir.FullName, Path.GetFileName(f)));
        var path = Path.Combine(Path.GetTempPath(), $"cadence-seed-{Guid.NewGuid():N}.db");
        var cs = SqliteSqlExecutor.BuildConnectionString(path);
        try
        {
            await new MigrationRunner(cs, dir.FullName, NullLogger<MigrationRunner>.Instance).ApplyAsync();
            var db = new SqliteSqlExecutor(cs);
            await db.ExecuteAsync("DELETE FROM members WHERE id = 4");
            await db.ExecuteAsync("DELETE FROM leads WHERE id = 10");

            File.Copy(Path.Combine(source, "0004_standup_history_seed.sql"), Path.Combine(dir.FullName, "0004_standup_history_seed.sql"));
            await new MigrationRunner(cs, dir.FullName, NullLogger<MigrationRunner>.Instance).ApplyAsync();

            Assert.True((await db.QueryAsync("SELECT COUNT(*) AS n FROM standups"))[0].Int("n") > 40);
            Assert.Equal(0, (await db.QueryAsync("SELECT COUNT(*) AS n FROM standups WHERE member_id = 4"))[0].Int("n"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(path); dir.Delete(true); } catch { }
        }
    }
}
