using System.Reflection;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>SE-04 (A8-11, #300 AC3 and AC9): funnel events without personal data and the report of the comuni that convert.</summary>
public class SeoEventServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Now);

    private AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private SeoEventService CreateService(AppDbContext db, int retentionDays = 90) =>
        new(db, Options.Create(new SeoEventOptions { RetentionDays = retentionDays }), NullLogger<SeoEventService>.Instance, _clock);

    private static SeoEventInput Click(string comune = "como") => new("cta_click", comune, null, null, null, null);

    [Fact]
    public void SeoEvent_HoldsNoPersonalData_NoIpUserSessionOrAgentMember()
    {
        // A8-11: the row can never identify a person, so nothing needs a consent, a salt or a retention for GDPR reasons.
        var forbidden = new[] { "ip", "user", "visitor", "session", "cookie", "agent", "email", "fingerprint", "device", "client" };

        var members = typeof(SeoEvent).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).ToList();

        Assert.All(members, name => Assert.DoesNotContain(forbidden, bad => name.Contains(bad, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(
            ["ComuneCode", "Event", "Id", "OccurredAt", "ReferrerHost", "UtmCampaign", "UtmMedium", "UtmSource"],
            members.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task RecordAsync_CtaClick_StoresTheEventTheComuneCodeAndTheMarketingValuesAtTheClockTime()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        await service.RecordAsync(new SeoEventInput("cta_click", " como ", "seo-compliance", "cta", "estate", "www.google.com"));

        var stored = await db.SeoEvents.SingleAsync();
        Assert.Equal(SeoEventType.CtaClick, stored.Event);
        Assert.Equal("013075", stored.ComuneCode);
        Assert.Equal("seo-compliance", stored.UtmSource);
        Assert.Equal("cta", stored.UtmMedium);
        Assert.Equal("estate", stored.UtmCampaign);
        Assert.Equal("www.google.com", stored.ReferrerHost);
        Assert.Equal(Now.UtcDateTime, stored.OccurredAt);
    }

    [Fact]
    public async Task RecordAsync_SignupStartByIstatCode_IsStoredForThatComune()
    {
        await using var db = CreateDb();

        await CreateService(db).RecordAsync(new SeoEventInput("signup_start", "013075", null, null, null, null));

        var stored = await db.SeoEvents.SingleAsync();
        Assert.Equal(SeoEventType.SignupStart, stored.Event);
        Assert.Equal("013075", stored.ComuneCode);
    }

    [Fact]
    public async Task RecordAsync_SameClickTwice_CountsBothBecauseNothingIdentifiesAVisitor()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        await service.RecordAsync(Click());
        await service.RecordAsync(Click());

        Assert.Equal(2, await db.SeoEvents.CountAsync());
    }

    [Theory]
    [InlineData("page_view")]
    [InlineData("CTA_CLICK")]
    [InlineData("")]
    public async Task RecordAsync_UnknownEvent_Throws422AndStoresNothing(string name)
    {
        await using var db = CreateDb();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            CreateService(db).RecordAsync(new SeoEventInput(name, "como", null, null, null, null)));

        Assert.Equal(ISeoEventService.UnknownEventCode, ex.Code);
        Assert.Empty(db.SeoEvents);
    }

    [Theory]
    [InlineData("atlantide")]
    [InlineData("000000")]
    [InlineData("")]
    public async Task RecordAsync_UnknownComune_Throws422AndStoresNothing(string comune)
    {
        await using var db = CreateDb();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => CreateService(db).RecordAsync(Click(comune)));

        Assert.Equal(ISeoEventService.UnknownComuneCode, ex.Code);
        Assert.Empty(db.SeoEvents);
    }

    [Fact]
    public async Task GetTopComuniAsync_CountsClicksStartsAndSignupsPerComuneBestFirst()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        // Como: 3 clicks, 2 signup starts; Milano: 1 click; Roma: 5 clicks.
        for (var i = 0; i < 3; i++) await service.RecordAsync(Click("como"));
        for (var i = 0; i < 2; i++) await service.RecordAsync(new SeoEventInput("signup_start", "como", null, null, null, null));
        await service.RecordAsync(Click("milano"));
        for (var i = 0; i < 5; i++) await service.RecordAsync(Click("roma"));
        // Two host signups attributed to Como in the window, one to Roma, one without a comune.
        db.Orgs.AddRange(NewOrg("a"), NewOrg("b"), NewOrg("c"), NewOrg("d"));
        await db.SaveChangesAsync();
        var orgs = await db.Orgs.OrderBy(o => o.Slug).ToListAsync();
        db.SignupAttributions.AddRange(
            Attribution(orgs[0].Id, "013075", Now.UtcDateTime.AddDays(-2)),
            Attribution(orgs[1].Id, "013075", Now.UtcDateTime.AddDays(-20)),
            Attribution(orgs[2].Id, "058091", Now.UtcDateTime.AddDays(-1)),
            Attribution(orgs[3].Id, null, Now.UtcDateTime.AddDays(-1)));
        await db.SaveChangesAsync();

        var result = await service.GetTopComuniAsync(30, 10);

        Assert.Equal(["058091", "013075", "015146"], result.Items.Select(i => i.ComuneCode));
        var roma = result.Items[0];
        Assert.Equal("Roma", roma.ComuneName);
        Assert.Equal((5, 0, 1), (roma.CtaClicks, roma.SignupStarts, roma.Signups));
        var como = result.Items[1];
        Assert.Equal((3, 2, 2), (como.CtaClicks, como.SignupStarts, como.Signups));
        Assert.Equal(30, result.Days);
        Assert.Equal(90, result.RetentionDays);
        Assert.Equal(Now.UtcDateTime, result.To);
        Assert.Equal(Now.UtcDateTime.AddDays(-30), result.From);
    }

    [Fact]
    public async Task GetTopComuniAsync_EventsOutsideTheWindow_AreNotCounted()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        await service.RecordAsync(Click("como"));
        _clock.Advance(TimeSpan.FromDays(31));
        await service.RecordAsync(Click("roma"));

        var result = await service.GetTopComuniAsync(30, 10);

        var only = Assert.Single(result.Items);
        Assert.Equal("058091", only.ComuneCode);
    }

    [Fact]
    public async Task GetTopComuniAsync_SignupStartsWithoutAClick_AreNotARowOfTheirOwn()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        await service.RecordAsync(new SeoEventInput("signup_start", "como", null, null, null, null));

        var result = await service.GetTopComuniAsync(30, 10);

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task GetTopComuniAsync_WindowLongerThanTheRetention_IsCappedToIt()
    {
        await using var db = CreateDb();

        var result = await CreateService(db, retentionDays: 60).GetTopComuniAsync(3650, 10);

        Assert.Equal(60, result.Days);
        Assert.Equal(60, result.RetentionDays);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    public async Task GetTopComuniAsync_NonPositiveDaysOrLimit_AreRaisedToOne(int value, int expected)
    {
        await using var db = CreateDb();
        await CreateService(db).RecordAsync(Click("como"));
        await CreateService(db).RecordAsync(Click("roma"));

        var result = await CreateService(db).GetTopComuniAsync(value, value);

        Assert.Equal(expected, result.Days);
        Assert.Single(result.Items);
    }

    [Fact]
    public async Task GetTopComuniAsync_LimitAboveTheMaximum_IsCapped()
    {
        await using var db = CreateDb();

        var result = await CreateService(db).GetTopComuniAsync(30, 10_000);

        Assert.Empty(result.Items);
        Assert.Equal(90, result.RetentionDays);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void EffectiveRetentionDays_NonPositive_ReadsAsTheDefault(int configured)
    {
        Assert.Equal(SeoEventOptions.DefaultRetentionDays, new SeoEventOptions { RetentionDays = configured }.EffectiveRetentionDays);
    }

    private static OrgEntity NewOrg(string slug) => new() { Name = slug, Slug = slug, DisplayName = slug, ContactEmail = $"{slug}@example.test" };

    private static SignupAttribution Attribution(Guid orgId, string? comuneCode, DateTime recordedAt) =>
        new() { OrgId = orgId, ComuneCode = comuneCode, RecordedAt = recordedAt };
}
