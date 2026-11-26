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
    public async Task OwnerGrantLetsCollaboratorReadOnlyCampaignsAndRevocationTakesEffectImmediately()
    {
        var (owner, org) = await OrgTeamHttp.SeedOwnerWithTeamRowsAsync(factory);
        var collaborator = await OrgTeamHttp.AddMemberAsync(factory, org, OrgRole.Collaborator, ["short-rent"]);
        using var ownerClient = factory.CreateAuthenticatedClient(owner, roles: "PropertyOwner");
        using var delegateClient = factory.CreateAuthenticatedClient(collaborator, roles: "");
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var member = await db.OrgMembers.SingleAsync(m => m.OrgId == org && m.UserId == collaborator);
        Core.Entities.ApprovalQueueItem Item(string type) => new()
        {
            OrgId = org,
            ItemId = Guid.NewGuid(),
            ItemType = type,
            ContentText = "Test",
            ExpiresAt = DateTime.UtcNow.AddDays(1)
        };
        var campaign = Item(Core.Services.ApprovalItemTypes.Campaign);
        var message = Item(Core.Services.ApprovalItemTypes.MessageDraft);
        db.ApprovalQueueItems.AddRange(campaign, message);
        await db.SaveChangesAsync();

        const string queue = "/api/approval-queue";
        var grant = $"/api/orgs/me/members/{member.Id}/marketing";
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await delegateClient.GetAsync(queue)).StatusCode);
        (await ownerClient.PutAsJsonAsync(grant, new { enabled = true })).EnsureSuccessStatusCode();
        var items = await delegateClient.GetFromJsonAsync<JsonElement>(queue);
        Assert.Equal(campaign.Id, Assert.Single(items.EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await delegateClient.PostAsync($"{queue}/{message.Id}/approve", null)).StatusCode);
        (await ownerClient.PutAsJsonAsync(grant, new { enabled = false })).EnsureSuccessStatusCode();
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await delegateClient.GetAsync(queue)).StatusCode);
        await db.Entry(message).ReloadAsync();
        Assert.Equal(ApprovalStatus.Pending, message.Status);
    }

    [Fact]
    public async Task ApprovingAndDismissingSocialAssetsStayWithinTheHostScope()
    {
        var (owner, org) = await OrgTeamHttp.SeedOwnerWithTeamRowsAsync(factory);
        var (otherOwner, _) = await OrgTeamHttp.SeedOwnerWithTeamRowsAsync(factory);
        var property = await factory.SeedPropertyAsync(owner);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var approve = new Core.Entities.MarketingAsset
        {
            OrgId = org,
            PropertyId = property.Id,
            SegmentId = Guid.NewGuid(),
            Channel = "instagram",
            Body = "Post",
            Subject = "Ospitalit‡"
        };
        var dismiss = new Core.Entities.MarketingAsset
        {
            OrgId = org,
            PropertyId = property.Id,
            SegmentId = approve.SegmentId,
            Channel = "facebook",
            Body = "Post",
            Subject = "Estate"
        };
        db.MarketingAssets.AddRange(approve, dismiss);
        await db.SaveChangesAsync();

        using var other = factory.CreateAuthenticatedClient(otherOwner, roles: "PropertyOwner");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await other.PostAsync($"/api/campaigns/studio/assets/{approve.Id}/approve", null)).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await other.PostAsync($"/api/campaigns/studio/assets/{dismiss.Id}/dismiss", null)).StatusCode);
        using var host = factory.CreateAuthenticatedClient(owner, roles: "PropertyOwner");
        (await host.PostAsync($"/api/campaigns/studio/assets/{approve.Id}/approve", null)).EnsureSuccessStatusCode();
        (await host.PostAsync($"/api/campaigns/studio/assets/{dismiss.Id}/dismiss", null)).EnsureSuccessStatusCode();
        await db.Entry(approve).ReloadAsync();
        await db.Entry(dismiss).ReloadAsync();
        Assert.NotNull(approve.ApprovedAt);
        Assert.NotNull(dismiss.DismissedAt);
    }

    [Fact]
    public async Task OwnerCanCreateAudienceAndApproveDraftWithoutSending()
    {
        var (owner, org) = await OrgTeamHttp.SeedOwnerWithTeamRowsAsync(factory);
        using var client = factory.CreateAuthenticatedClient(owner, roles: "PropertyOwner");
        var settings = await client.GetFromJsonAsync<JsonElement>("/api/campaigns/studio/settings");
        Assert.True(settings.GetProperty("canEdit").GetBoolean());
        Assert.Equal(2, settings.GetProperty("monthlyEmailLimit").GetInt32());
        var segment = await client.PostAsJsonAsync("/api/campaigns/segments", new
        {
            name = "Ospiti di ritorno",
            condition = new
            {
                version = "1.0",
                groups = new[] { new { @operator = "AND", rules = new[] {
                new { fieldId = "totalStays", @operator = "greaterThan", value = 1 } } } }
            }
        });
        segment.EnsureSuccessStatusCode();
        var segmentId = (await segment.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var preview = await client.GetFromJsonAsync<JsonElement>($"/api/campaigns/segments/{segmentId}/preview");
        Assert.Equal(0, preview.GetProperty("count").GetInt32());
        var draft = await client.PostAsJsonAsync("/api/campaigns", new { segmentId, subject = "Novit√†", body = "Un invito per voi." });
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
