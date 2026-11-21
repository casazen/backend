using Casazen.Core.Entities;
using Casazen.Core.Enums;
using Casazen.Core.Regulatory.GuestRegistration;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E31/F02/US01/T01: the regime of many properties with at most two queries (<see cref="Casazen.Core.Services.IGuestRegistrationRegimeResolver.ResolveManyAsync"/>) is exactly what the
/// resolver says of each property alone: one place decides, for one property and for a list (decision D3).
/// </summary>
public class GuestRegistrationRegimeResolverManyTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResolveMany_IsExactlyWhatResolveSaysOfEachProperty_WithTheFlagOnAndOff(bool regimesOn)
    {
        using var world = new SwissNotificationTestWorld(regimesOn);
        var org = world.AddOrg();
        var properties = new List<Property>
        {
            world.AddProperty("TI", org, name: "Lugano"),
            world.AddProperty("SH", org, name: "Schaffhausen"),
            world.AddProperty("ZH", org, GuestNotificationOverride.Required, name: "Zurich chosen"),
            world.AddProperty("SH", org, GuestNotificationOverride.NotRequired, name: "Schaffhausen not required"),
            world.AddProperty("BE", org, name: "Bern"),
            world.AddProperty("TI", org, name: "Como", country: "IT"),
            world.AddProperty("TI", org, name: "Paris", country: "FR"),
        };

        // A Swiss property whose municipality is not known (nothing stored, no postal code): the unknown Swiss place, never a canton chosen silently.
        var unknown = new Property { OrgId = org.Id, Name = "Unknown", Country = " ch ", PostalCode = "", City = "" };
        world.Db.Properties.Add(unknown);
        await world.Db.SaveChangesAsync();
        properties.Add(unknown);

        var resolver = world.Resolver();
        var many = await resolver.ResolveManyAsync(properties);

        Assert.Equal(properties.Count, many.Count);
        foreach (var property in properties)
        {
            var alone = await resolver.ResolveAsync(property);
            var together = many[property.Id];

            Assert.Equal(alone.PropertyId, together.PropertyId);
            Assert.Equal(alone.Regime?.Code, together.Regime?.Code);
            Assert.Equal(alone.EffectiveDuty, together.EffectiveDuty);
            Assert.Equal(alone.Override, together.Override);
            Assert.Equal(alone.IdentifierMissing, together.IdentifierMissing);
            Assert.Equal(alone.Place, together.Place);
        }

        Assert.Equal("CH-UNKNOWN", many[unknown.Id].Regime!.Code);
        Assert.Null(many[properties[6].Id].Regime);

        // The setting of the host is read only while the module is on.
        Assert.Equal(regimesOn ? GuestNotificationOverride.Required : (GuestNotificationOverride?)null, many[properties[2].Id].Override);
        Assert.Equal(regimesOn ? GuestRegistrationEffectiveDuty.Required : GuestRegistrationEffectiveDuty.Ask, many[properties[2].Id].EffectiveDuty);
    }

    [Fact]
    public async Task ResolveMany_AnEmptyList_IsAnEmptyAnswer()
    {
        using var world = new SwissNotificationTestWorld();

        var many = await world.Resolver().ResolveManyAsync([]);

        Assert.Empty(many);
    }
}
