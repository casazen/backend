using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

public sealed class MarketingWorkflowFactory : CasazenWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Features:CrmMarketing"] = "true" }));
    }
}

public class MarketingStudioWorkflowHttpTests(MarketingWorkflowFactory factory) : IClassFixture<MarketingWorkflowFactory>
{
    [Fact]
    public async Task OwnerCanCreateAudienceAndApproveDraftWithoutSending()
    {
        var (owner, org) = await OrgTeamHttp.SeedOwnerWithTeamRowsAsync(factory);
        using var client = factory.CreateAuthenticatedClient(owner, roles: "PropertyOwner");
        var settings = await client.GetFromJsonAsync<JsonElement>("/api/campaigns/studio/settings");
        Assert.True(settings.GetProperty("canEdit").GetBoolean());
        Assert.Equal(2, settings.GetProperty("monthlyEmailLimit").GetInt32());
        var segment = await client.PostAsJsonAsync("/api/campaigns/segments", new { name = "Ospiti di ritorno",
            condition = new { version = "1.0", groups = new[] { new { @operator = "AND", rules = new[] {
                new { fieldId = "totalStays", @operator = "greaterThan", value = 1 } } } } } });
        segment.EnsureSuccessStatusCode();
        var segmentId = (await segment.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var preview = await client.GetFromJsonAsync<JsonElement>($"/api/campaigns/segments/{segmentId}/preview");
        Assert.Equal(0, preview.GetProperty("count").GetInt32());
        var draft = await client.PostAsJsonAsync("/api/campaigns", new { segmentId, subject = "Novità", body = "Un invito per voi." });
        draft.EnsureSuccessStatusCode();
        var campaignId = (await draft.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await client.PostAsync($"/api/campaigns/{campaignId}/submit", null)).EnsureSuccessStatusCode();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var item = await db.ApprovalQueueItems.AsNoTracking().SingleAsync(i => i.OrgId == org && i.ItemId == campaignId);
        (await client.PostAsync($"/api/approval-queue/{item.Id}/approve", null)).EnsureSuccessStatusCode();
        var campaign = await db.Campaigns.AsNoTracking().SingleAsync(c => c.Id == campaignId && c.OrgId == org);
        Assert.Equal(CampaignStatus.Approved, campaign.Status);
        Assert.False(await db.CampaignDeliveries.AnyAsync(d => d.CampaignId == campaignId));
    }
}
