using Cadence.Web.Data;
using Cadence.Web.Models;
using Cadence.Web.Services;

namespace Cadence.Tests;

public sealed class PipelineTests : IAsyncLifetime
{
    private TestDatabase _db = null!;
    private LeadRepository _leads = null!;
    private SuggestionRepository _suggestions = null!;
    private LeadPipelineService _pipeline = null!;

    public async Task InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync(seed: false);
        _leads = new LeadRepository(_db.Db);
        _suggestions = new SuggestionRepository(_db.Db);
        _pipeline = new LeadPipelineService(_leads, _suggestions);
        await _db.Db.ExecuteAsync("INSERT INTO members (id, name, email, role) VALUES (1, 'Test SDR', 'sdr@test', 'sdr')");
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    [Fact]
    public async Task Migrations_apply_once_and_create_the_schema()
    {
        var tables = await _db.Db.QueryAsync("SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name");
        var names = tables.Select(t => t.Str("name")).ToList();
        Assert.Contains("leads", names);
        Assert.Contains("lead_stage_events", names);
        Assert.Contains("enrichment_suggestions", names);
        Assert.Contains("standups", names);
        Assert.Contains("webhook_deliveries", names);
    }

    [Fact]
    public async Task Creating_a_lead_records_the_first_stage_event()
    {
        var id = await _leads.CreateAsync(new LeadInput { Company = "Acme", Email = "a@acme.com" }, 1, "manual");
        var history = await _leads.HistoryAsync(id);
        var e = Assert.Single(history);
        Assert.Null(e.From);
        Assert.Equal(Stage.New, e.To);
        Assert.Equal(1, e.MemberId);
    }

    [Fact]
    public async Task Moving_follows_the_allowed_transitions_only()
    {
        var id = await _leads.CreateAsync(new LeadInput { Company = "Acme", ContactName = "Ann", Email = "a@acme.com" }, 1, "manual");
        var ex = await Assert.ThrowsAsync<PipelineException>(() => _pipeline.MoveAsync(id, Stage.Qualified, 1, null));
        Assert.Contains("cannot move", ex.Message);

        await _pipeline.MoveAsync(id, Stage.Researching, 1, null);
        await _pipeline.MoveAsync(id, Stage.Validated, 1, null);
        var lead = await _leads.FindAsync(id);
        Assert.Equal(Stage.Validated, lead!.Stage);
        Assert.Equal(3, (await _leads.HistoryAsync(id)).Count);
    }

    [Fact]
    public async Task Validation_is_blocked_while_suggestions_are_pending()
    {
        var id = await _leads.CreateAsync(new LeadInput { Company = "Acme", ContactName = "Ann", Email = "a@acme.com" }, 1, "manual");
        await _pipeline.MoveAsync(id, Stage.Researching, 1, null);
        await _suggestions.AddAsync(id, "industry", "Software", null, "heuristic", 0.6, "test");

        var ex = await Assert.ThrowsAsync<PipelineException>(() => _pipeline.MoveAsync(id, Stage.Validated, 1, null));
        Assert.Contains("pending suggestion", ex.Message);
    }

    [Fact]
    public async Task Validation_requires_a_contact_and_a_way_to_reach_them()
    {
        var id = await _leads.CreateAsync(new LeadInput { Company = "Acme" }, 1, "manual");
        await _pipeline.MoveAsync(id, Stage.Researching, 1, null);
        var ex = await Assert.ThrowsAsync<PipelineException>(() => _pipeline.MoveAsync(id, Stage.Validated, 1, null));
        Assert.Contains("email or a phone", ex.Message);
    }

    [Fact]
    public async Task Disqualifying_needs_a_reason()
    {
        var id = await _leads.CreateAsync(new LeadInput { Company = "Acme" }, 1, "manual");
        await Assert.ThrowsAsync<PipelineException>(() => _pipeline.MoveAsync(id, Stage.Disqualified, 1, "  "));
        await _pipeline.MoveAsync(id, Stage.Disqualified, 1, "No budget");
        Assert.Equal(Stage.Disqualified, (await _leads.FindAsync(id))!.Stage);
    }

    [Fact]
    public void Score_rewards_reachability_and_decision_makers()
    {
        var bare = new Lead(1, "X", null, null, null, null, null, null, null, null, "manual", Stage.New, null, null, 0, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var owner = bare with { Email = "o@x.com", Phone = "1", ContactTitle = "Owner", Employees = 40, Website = "https://x.com" };
        Assert.True(LeadPipelineService.Score(owner) > LeadPipelineService.Score(bare));
        Assert.InRange(LeadPipelineService.Score(owner), 0, 100);
        Assert.True(LeadPipelineService.Score(owner with { Stage = Stage.Disqualified }) < LeadPipelineService.Score(owner));
    }

    [Fact]
    public async Task List_filters_by_search_stage_and_pending()
    {
        var a = await _leads.CreateAsync(new LeadInput { Company = "Harbor Dental", Email = "h@harbor.com" }, 1, "manual");
        await _leads.CreateAsync(new LeadInput { Company = "Summit Roofing" }, 1, "manual");
        await _suggestions.AddAsync(a, "industry", "Healthcare", null, "heuristic", 0.6, null);

        Assert.Single(await _leads.ListAsync(new LeadFilter(Search: "harbor")));
        Assert.Equal(2, (await _leads.ListAsync(new LeadFilter(Stage: Stage.New))).Count);
        var pending = await _leads.ListAsync(new LeadFilter(PendingOnly: true));
        Assert.Equal(a, Assert.Single(pending).Id);
        Assert.Equal(1, pending[0].PendingSuggestions);
    }
}
