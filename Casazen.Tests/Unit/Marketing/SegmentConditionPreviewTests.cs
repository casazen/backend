using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Models;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Marketing;

public class SegmentConditionPreviewTests
{
    private const string EveryGuest = """{"groups":[]}""";
    private const string MoreThanOneStay = """{"groups":[{"operator":"AND","rules":[{"fieldId":"totalStays","operator":"greaterThan","value":1}]}]}""";
    private const string FromSwitzerlandOrThreeStays = """{"groups":[{"operator":"OR","rules":[{"fieldId":"country","operator":"in","value":["CH"]},{"fieldId":"totalStays","operator":"equals","value":3}]}]}""";

    [Theory]
    [InlineData(EveryGuest, false, 3)]
    [InlineData(EveryGuest, true, 2)]
    [InlineData(MoreThanOneStay, false, 2)]
    [InlineData(MoreThanOneStay, true, 2)]
    [InlineData(FromSwitzerlandOrThreeStays, false, 2)]
    [InlineData(FromSwitzerlandOrThreeStays, true, 1)]
    public async Task UnsavedCondition_CountsWhatTheSavedSegmentCounts(string json, bool onlyCasaA, int expected)
    {
        await using var db = NewDb();
        var (org, casaA, _) = await SeedGuestsAsync(db);
        var condition = JsonSerializer.Deserialize<Condition>(json, JsonSerializerOptions.Web)!;
        var segment = new GuestSegment { OrgId = org, Name = "Gruppo", ConditionJson = JsonSerializer.Serialize(condition) };
        db.GuestSegments.Add(segment);
        await db.SaveChangesAsync();
        var service = NewService(db);
        Guid? propertyId = onlyCasaA ? casaA : null;

        var unsaved = await service.PreviewConditionCountAsync(condition, org, new HostScope(org), default, propertyId);
        var saved = await service.PreviewCountAsync(segment.Id, org, new HostScope(org), default, propertyId);

        Assert.Equal(expected, unsaved);
        Assert.Equal(saved, unsaved);
    }

    [Fact]
    public async Task PropertyScopedCall_CountsOnlyTheGuestsOfThatProperty()
    {
        await using var db = NewDb();
        var (org, casaA, casaB) = await SeedGuestsAsync(db);
        var service = NewService(db);
        var everyGuest = new Condition();

        Assert.Equal(2, await service.PreviewConditionCountAsync(everyGuest, org, new HostScope(org), default, casaA));
        Assert.Equal(2, await service.PreviewConditionCountAsync(everyGuest, org, new HostScope(org), default, casaB));
        Assert.Equal(0, await service.PreviewConditionCountAsync(everyGuest, org, new HostScope(org), default, Guid.NewGuid()));
        Assert.Equal(2, await service.PreviewConditionCountAsync(everyGuest, org, new HostScope(org, OwnerId: "host-a")));
    }

    [Fact]
    public async Task SuppressedCappedAndUnconfirmedGuests_AreNotCounted()
    {
        await using var db = NewDb();
        var org = Guid.NewGuid();
        CampaignServiceTests.AddContact(db, org, "ok@example.com");
        CampaignServiceTests.AddContact(db, org, "unconfirmed@example.com", confirmed: false);
        CampaignServiceTests.AddContact(db, org, "unsubscribed@example.com");
        CampaignServiceTests.AddContact(db, org, "legacy@example.com");
        var capped = CampaignServiceTests.AddContact(db, org, "capped@example.com");
        var now = DateTime.UtcNow;
        db.CampaignDeliveries.AddRange(Enumerable.Range(1, 2).Select(slot => new CampaignDelivery
        {
            OrgId = org,
            CampaignId = Guid.NewGuid(),
            ProfileId = capped.Id,
            ContactId = Guid.NewGuid(),
            Month = now.Year * 100 + now.Month,
            Slot = slot,
            CreatedAt = now,
        }));
        db.SuppressionListEntries.Add(new SuppressionListEntry
        {
            OrgId = org,
            EmailHash = SuppressionService.LegacyEmailHash("legacy@example.com"),
            Source = "unsubscribe",
        });
        var segment = new GuestSegment { OrgId = org, Name = "Tutti", ConditionJson = JsonSerializer.Serialize(new Condition()) };
        db.GuestSegments.Add(segment);
        await db.SaveChangesAsync();
        await new SuppressionService(db, CrmTestIndex.Create(), Mock.Of<ILogger<SuppressionService>>())
            .AddToSuppressionListAsync(org, "unsubscribed@example.com", "unsubscribe");
        var service = NewService(db);

        Assert.Equal(1, await service.PreviewConditionCountAsync(new Condition(), org, new HostScope(org)));
        Assert.Equal(1, await service.PreviewCountAsync(segment.Id, org, new HostScope(org)));
    }

    [Theory]
    [InlineData("passportNumber", "equals")]
    [InlineData("totalStays", "contains")]
    [InlineData("channel", "equals")]
    public async Task InvalidCondition_IsRefusedAndCountsNobody(string fieldId, string @operator)
    {
        await using var db = NewDb();
        var (org, _, _) = await SeedGuestsAsync(db);
        var rule = new ConditionRule { FieldId = fieldId, Operator = @operator, Value = "1" };
        var condition = new Condition { Groups = [new ConditionGroup { Rules = [rule] }] };

        Assert.False(MarketingSegmentRules.IsValid(condition, new ConditionEvaluator(Mock.Of<ILogger<ConditionEvaluator>>())));
        Assert.Equal(0, await NewService(db).PreviewConditionCountAsync(condition, org, new HostScope(org)));
    }

    [Fact]
    public async Task ScopeOfAnotherOrg_IsRefused()
    {
        await using var db = NewDb();

        await Assert.ThrowsAsync<DomainRuleException>(
            () => NewService(db).PreviewConditionCountAsync(new Condition(), Guid.NewGuid(), new HostScope(Guid.NewGuid())));
    }

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static CampaignService NewService(AppDbContext db) =>
        CampaignServiceTests.NewService(db, Mock.Of<IEmailService>(), CampaignServiceTests.Links());

    private static async Task<(Guid OrgId, Guid CasaA, Guid CasaB)> SeedGuestsAsync(AppDbContext db)
    {
        var org = Guid.NewGuid();
        var casaA = new Property { OrgId = org, OwnerId = "host-a", Name = "Casa A" };
        var casaB = new Property { OrgId = org, OwnerId = "host-b", Name = "Casa B" };
        db.Properties.AddRange(casaA, casaB);
        AddGuest(db, org, "anna@example.com", 3, "IT", casaA.Id);
        AddGuest(db, org, "bruno@example.com", 1, "CH", casaB.Id);
        AddGuest(db, org, "carla@example.com", 2, "IT", casaA.Id, casaB.Id);
        await db.SaveChangesAsync();
        return (org, casaA.Id, casaB.Id);
    }

    private static void AddGuest(AppDbContext db, Guid orgId, string email, int stays, string country, params Guid[] properties)
    {
        var profile = CampaignServiceTests.AddContact(db, orgId, email);
        profile.TotalStays = stays;
        profile.Country = country;
        profile.PropertyIdsJson = JsonSerializer.Serialize(properties);
    }
}
