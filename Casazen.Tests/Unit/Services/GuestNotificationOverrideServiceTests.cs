using Casazen.Core.Entities;
using Casazen.Core.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E29/F02/US01/T01 (NF-125): the one writer of the host's setting about notifying the guests of a Swiss property. It stores the setting (and null, which follows the
/// regime again), changes nothing when the value is the one already stored, refuses a property outside Switzerland (the Italian duty is not the host's to waive) and
/// never reaches another organization's property.
/// </summary>
public class GuestNotificationOverrideServiceTests
{
    private static readonly DateTime Before = new(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid OrgA = Guid.Parse("a1111111-0000-0000-0000-00000000000a");
    private static readonly Guid OrgB = Guid.Parse("b2222222-0000-0000-0000-00000000000b");

    private readonly string _database = "guest-notification-override-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task SetAsync_ASwissProperty_StoresTheSetting_AndNullFollowsTheRegimeAgain()
    {
        var property = await SeedAsync("CH");
        await using var db = NewContext(OrgA);
        var service = Service(db);

        await service.SetAsync(property.Id, GuestNotificationOverride.NotRequired, "auth0|host");
        Assert.Equal((GuestNotificationOverride?)GuestNotificationOverride.NotRequired, (await StoredAsync(property.Id)).GuestNotificationOverride);

        await service.SetAsync(property.Id, GuestNotificationOverride.Required, "auth0|host");
        Assert.Equal((GuestNotificationOverride?)GuestNotificationOverride.Required, (await StoredAsync(property.Id)).GuestNotificationOverride);

        await service.SetAsync(property.Id, null, "auth0|host");
        var stored = await StoredAsync(property.Id);
        Assert.Null(stored.GuestNotificationOverride);
        Assert.Equal(Now.UtcDateTime, stored.UpdatedAt);
    }

    [Fact]
    public async Task SetAsync_TheSameSettingAgain_ChangesNothing()
    {
        var property = await SeedAsync("CH", GuestNotificationOverride.Required);
        await using var db = NewContext(OrgA);

        await Service(db).SetAsync(property.Id, GuestNotificationOverride.Required, "auth0|host");

        var stored = await StoredAsync(property.Id);
        Assert.Equal((GuestNotificationOverride?)GuestNotificationOverride.Required, stored.GuestNotificationOverride);
        Assert.Equal(Before, stored.UpdatedAt);
    }

    /// <summary>An Italian property (any case, or an old row with no country) follows Alloggiati Web: 422 and nothing is stored.</summary>
    [Theory]
    [InlineData("IT")]
    [InlineData("it")]
    [InlineData("")]
    public async Task SetAsync_APropertyOutsideSwitzerland_Is422_AndStoresNothing(string country)
    {
        var property = await SeedAsync(country);
        await using var db = NewContext(OrgA);

        var refused = await Assert.ThrowsAsync<DomainRuleException>(
            () => Service(db).SetAsync(property.Id, GuestNotificationOverride.NotRequired, "auth0|host"));

        Assert.Equal(GuestRegistrationErrorCodes.OverrideNotApplicable, refused.Code);
        Assert.Equal("guest_registration_override_not_applicable", refused.Code);
        Assert.Null((await StoredAsync(property.Id)).GuestNotificationOverride);
    }

    /// <summary>Proof (permission): another organization's property is not found and keeps the setting it had.</summary>
    [Fact]
    public async Task SetAsync_APropertyOfAnotherOrganization_IsNotFound_AndKeepsItsSetting_Test()
    {
        var property = await SeedAsync("CH", GuestNotificationOverride.Required);
        await using var db = NewContext(OrgB);

        var refused = await Assert.ThrowsAsync<NotFoundException>(
            () => Service(db).SetAsync(property.Id, GuestNotificationOverride.NotRequired, "auth0|intruder"));

        Assert.Equal("property_not_found", refused.Code);
        Assert.Equal((GuestNotificationOverride?)GuestNotificationOverride.Required, (await StoredAsync(property.Id)).GuestNotificationOverride);
    }

    [Fact]
    public async Task SetAsync_ASettingThatIsNeitherOfTheTwo_IsRefused_AndAnEmptyUserToo()
    {
        var property = await SeedAsync("CH");
        await using var db = NewContext(OrgA);
        var service = Service(db);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SetAsync(property.Id, (GuestNotificationOverride)7, "auth0|host"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SetAsync(property.Id, GuestNotificationOverride.Required, " "));

        Assert.Null((await StoredAsync(property.Id)).GuestNotificationOverride);
    }

    private GuestNotificationOverrideService Service(AppDbContext db) =>
        new(db, NullLogger<GuestNotificationOverrideService>.Instance, new FixedTimeProvider(Now));

    private AppDbContext NewContext(Guid? tenant = null) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_database).Options, tenant is null ? null : new Tenant(tenant.Value));

    private async Task<Property> SeedAsync(string country, GuestNotificationOverride? setting = null)
    {
        var property = HostScopeScenario.NewProperty(OrgA, "auth0|owner", $"Casa {Guid.NewGuid():N}");
        property.Country = country;
        property.GuestNotificationOverride = setting;
        property.UpdatedAt = Before;
        await using var db = NewContext();
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property;
    }

    private async Task<Property> StoredAsync(Guid propertyId)
    {
        await using var db = NewContext();
        return await db.Properties.AsNoTracking().SingleAsync(p => p.Id == propertyId);
    }

    private sealed class Tenant(Guid orgId) : ITenantContext
    {
        public Guid? OrgId => orgId;

        public bool FilterEnabled => true;
    }
}
