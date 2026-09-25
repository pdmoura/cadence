using Cadence.Web.Data;
using Cadence.Web.Models;
using Cadence.Web.Services;

namespace Cadence.Tests;

public sealed class EnrichmentTests : IAsyncLifetime
{
    private TestDatabase _db = null!;
    private LeadRepository _leads = null!;
    private SuggestionRepository _suggestions = null!;
    private EnrichmentService _service = null!;

    public async Task InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync(seed: false);
        _leads = new LeadRepository(_db.Db);
        _suggestions = new SuggestionRepository(_db.Db);
        _service = new EnrichmentService([new HeuristicEnrichmentProvider()], _suggestions, _leads);
        await _db.Db.ExecuteAsync("INSERT INTO members (id, name, email, role) VALUES (1, 'Reviewer', 'r@test', 'team_lead')");
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private static Lead Lead(string company, string? email = null, string? phone = null, string? website = null) =>
        new(1, company, website, null, null, email, phone, null, null, null, "manual", Stage.New, null, null, 0, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Fact]
    public async Task Heuristics_derive_website_from_a_business_email_domain()
    {
        var proposals = await new HeuristicEnrichmentProvider().ProposeAsync(Lead("Acme", "jo@acmeplumbing.com"));
        var web = Assert.Single(proposals, p => p.Field == "website");
        Assert.Equal("https://acmeplumbing.com", web.Value);
    }

    [Fact]
    public async Task Heuristics_ignore_free_mail_domains()
    {
        var proposals = await new HeuristicEnrichmentProvider().ProposeAsync(Lead("Acme", "jo@gmail.com"));
        Assert.DoesNotContain(proposals, p => p.Field == "website");
    }

    [Theory]
    [InlineData("Lakeside Family Law", "Legal services")]
    [InlineData("Atlas HVAC & Electric", "Construction")]
    [InlineData("Verde Contabilidade", "Accounting & tax")]
    public async Task Heuristics_infer_industry_from_company_name(string company, string industry)
    {
        var proposals = await new HeuristicEnrichmentProvider().ProposeAsync(Lead(company));
        Assert.Equal(industry, Assert.Single(proposals, p => p.Field == "industry").Value);
    }

    [Fact]
    public async Task Heuristics_normalise_phone_numbers()
    {
        var us = await new HeuristicEnrichmentProvider().ProposeAsync(Lead("X", phone: "2125550147"));
        Assert.Equal("+1 (212) 555-0147", Assert.Single(us, p => p.Field == "phone").Value);

        var br = await new HeuristicEnrichmentProvider().ProposeAsync(Lead("X", phone: "11987654321"));
        Assert.Equal("+55 11 98765-4321", Assert.Single(br, p => p.Field == "phone").Value);
    }

    [Fact]
    public async Task Proposals_are_stored_once_and_only_applied_when_accepted()
    {
        var id = await _leads.CreateAsync(new LeadInput { Company = "Harbor Point Dental", Email = "e@harborpointdental.com" }, 1, "manual");

        var first = await _service.ProposeAsync(id);
        var second = await _service.ProposeAsync(id);
        Assert.Equal(2, first);   // website + industry
        Assert.Equal(0, second);  // de-duplicated against pending suggestions

        var lead = await _leads.FindAsync(id);
        Assert.Null(lead!.Website); // nothing applied yet

        var pending = await _suggestions.ForLeadAsync(id);
        var website = pending.Single(s => s.Field == "website");
        var industry = pending.Single(s => s.Field == "industry");

        await _service.ReviewAsync(website.Id, accept: true, reviewerId: 1);
        await _service.ReviewAsync(industry.Id, accept: false, reviewerId: 1);

        lead = await _leads.FindAsync(id);
        Assert.Equal("https://harborpointdental.com", lead!.Website);
        Assert.Null(lead.Industry);

        var reviewed = await _suggestions.ForLeadAsync(id);
        Assert.All(reviewed, s => Assert.NotEqual(SuggestionStatus.Pending, s.Status));
        Assert.Equal("Reviewer", reviewed.First().ReviewerName);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.ReviewAsync(website.Id, true, 1));
    }

    [Fact]
    public async Task Field_names_are_whitelisted_before_touching_sql()
    {
        var id = await _leads.CreateAsync(new LeadInput { Company = "X" }, 1, "manual");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _leads.ApplyFieldAsync(id, "stage; DROP TABLE leads", "x"));
    }
}
