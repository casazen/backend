using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Exceptions;
using Casazen.Web.DTOs;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SR-07a: the rules a host states in the wizard decide the two amenities the public listing shows (never contradicted, in
/// whatever order the steps are saved), the quiet hours are a pair, and the plan usage the list reads says nothing of the
/// plan or the billing.
/// </summary>
public class PropertyHouseRulesTests
{
    [Theory]
    [InlineData(PetsPolicy.Yes, true)]
    [InlineData(PetsPolicy.OnRequest, false)]
    [InlineData(PetsPolicy.No, false)]
    public void SyncAmenities_PetFriendlyIsInTheListExactlyWhenThePetsRuleSaysYes(PetsPolicy rule, bool expected)
    {
        // Whatever the list said before: the stated rule wins.
        foreach (var listedBefore in new[] { false, true })
        {
            var property = new Property
            {
                PetsPolicy = rule,
                Amenities = listedBefore ? [PropertyAmenity.WiFi, PropertyAmenity.PetFriendly] : [PropertyAmenity.WiFi],
            };

            PropertyHouseRules.SyncAmenities(property);

            Assert.Equal(expected, property.Amenities.Contains(PropertyAmenity.PetFriendly));
            Assert.Contains(PropertyAmenity.WiFi, property.Amenities);
        }
    }

    [Theory]
    [InlineData(SmokingPolicy.Yes, true)]
    [InlineData(SmokingPolicy.OutdoorsOnly, false)]
    [InlineData(SmokingPolicy.No, false)]
    public void SyncAmenities_SmokingAllowedIsInTheListExactlyWhenTheSmokingRuleSaysYes(SmokingPolicy rule, bool expected)
    {
        var property = new Property { SmokingPolicy = rule, Amenities = [PropertyAmenity.SmokingAllowed] };
        PropertyHouseRules.SyncAmenities(property);
        Assert.Equal(expected, property.Amenities.Contains(PropertyAmenity.SmokingAllowed));

        var other = new Property { SmokingPolicy = rule, Amenities = [] };
        PropertyHouseRules.SyncAmenities(other);
        Assert.Equal(expected, other.Amenities.Contains(PropertyAmenity.SmokingAllowed));
    }

    [Fact]
    public void SyncAmenities_NoRuleStated_LeavesTheListAsTheHostMadeIt()
    {
        // Every property that existed before the rules: nothing changes for it.
        var property = new Property { Amenities = [PropertyAmenity.PetFriendly, PropertyAmenity.SmokingAllowed, PropertyAmenity.WiFi] };

        PropertyHouseRules.SyncAmenities(property);

        Assert.Equal(
            new[] { PropertyAmenity.PetFriendly, PropertyAmenity.SmokingAllowed, PropertyAmenity.WiFi },
            property.Amenities.ToArray());
    }

    [Fact]
    public void Normalize_OnlyOneEndOfTheQuietHours_Is422_AndBothOrNeitherPass()
    {
        var one = new Property { QuietHoursFrom = new TimeOnly(22, 0) };
        var error = Assert.Throws<DomainRuleException>(() => PropertyHouseRules.Normalize(one));
        Assert.Equal(PropertyHouseRules.QuietHoursIncompleteCode, error.Code);
        Assert.Equal("PropertyQuietHoursIncomplete", error.MessageKey);

        var both = new Property { QuietHoursFrom = new TimeOnly(22, 0, 30), QuietHoursTo = new TimeOnly(8, 0) };
        PropertyHouseRules.Normalize(both);
        Assert.Equal(new TimeOnly(22, 0), both.QuietHoursFrom);

        PropertyHouseRules.Normalize(new Property());
    }

    [Fact]
    public void PlanUsageResponse_SaysHowManyAndHowMany_AndNothingOfThePlanOrTheBilling()
    {
        var fields = typeof(PlanUsageResponse).GetProperties().Select(p => p.Name).Order().ToArray();

        Assert.Equal(new[] { "CanAdd", "Max", "Unlimited", "Used" }, fields);
    }
}
