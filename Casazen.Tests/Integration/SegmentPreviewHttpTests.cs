using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Unit.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

public class SegmentPreviewHttpTests(MarketingWorkflowFactory factory) : IClassFixture<MarketingWorkflowFactory>
{
    private const string PreviewUrl = "/api/campaigns/segments/preview";

    [Fact]
    public async Task Owner_GetsTheCountTheSavedSegmentWouldGet()
    {
        var (owner, org) = await OrgTeamHttp.SeedOwnerWithTeamRowsAsync(factory);
        var casa = await factory.SeedPropertyAsync(owner);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var atCasa = AddGuest(db, org, stays: 2);
            atCasa.PropertyIdsJson = JsonSerializer.Serialize(new[] { casa.Id });
            AddGuest(db, org, stays: 2);
            AddGuest(db, org, stays: 1);
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateAuthenticatedClient(owner, roles: "PropertyOwner");
        var condition = Rule("totalStays", "greaterThan", 1);
        var segment = await client.PostAsJsonAsync("/api/campaigns/segments", new { name = "Ospiti di ritorno", condition });
        segment.EnsureSuccessStatusCode();
        var segmentId = (await segment.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        Assert.Equal(2, await CountAsync(client.PostAsJsonAsync(PreviewUrl, new { condition })));
        Assert.Equal(2, await CountAsync(client.GetAsync($"/api/campaigns/segments/{segmentId}/preview")));
        Assert.Equal(1, await CountAsync(client.PostAsJsonAsync(PreviewUrl, new { condition, propertyId = casa.Id })));
        Assert.Equal(1, await CountAsync(client.GetAsync($"/api/campaigns/segments/{segmentId}/preview?propertyId={casa.Id}")));
    }

    [Theory]
    [InlineData("passportNumber", "equals")]
    [InlineData("totalStays", "contains")]
    [InlineData("channel", "equals")]
    public async Task InvalidRule_IsRefusedAsTheCreationOfTheSegmentRefusesIt(string fieldId, string @operator)
    {
        var (owner, _) = await OrgTeamHttp.SeedOwnerWithTeamRowsAsync(factory);
        using var client = factory.CreateAuthenticatedClient(owner, roles: "PropertyOwner");
        var condition = Rule(fieldId, @operator, "1");

        await OrgTeamHttp.AssertProblemAsync(
            await client.PostAsJsonAsync(PreviewUrl, new { condition }), HttpStatusCode.BadRequest, "invalid_segment_rules");
        await OrgTeamHttp.AssertProblemAsync(
            await client.PostAsJsonAsync("/api/campaigns/segments", new { name = "Gruppo", condition }), HttpStatusCode.BadRequest, "invalid_segment_rules");
    }

    [Fact]
    public async Task WithoutTheTokenOrTheMarketingPermission_IsRefused()
    {
        var (_, org) = await OrgTeamHttp.SeedOwnerWithTeamRowsAsync(factory);
        var collaborator = await OrgTeamHttp.AddMemberAsync(factory, org, OrgRole.Collaborator, ["short-rent"]);
        using var anonymous = factory.CreateClient();
        using var member = factory.CreateAuthenticatedClient(collaborator, roles: "");
        var body = new { condition = Rule("totalStays", "greaterThan", 1) };

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(PreviewUrl, body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync(PreviewUrl, body)).StatusCode);
    }

    private static object Rule(string fieldId, string @operator, object value) =>
        new { version = "1.0", groups = new[] { new { @operator = "AND", rules = new[] { new { fieldId, @operator, value } } } } };

    private static Core.Entities.GuestCrmProfile AddGuest(AppDbContext db, Guid org, int stays)
    {
        var profile = CampaignServiceTests.AddContact(db, org, $"{Guid.NewGuid():N}@example.com");
        profile.TotalStays = stays;
        return profile;
    }

    private static async Task<int> CountAsync(Task<HttpResponseMessage> request)
    {
        var response = await request;
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("count").GetInt32();
    }
}
