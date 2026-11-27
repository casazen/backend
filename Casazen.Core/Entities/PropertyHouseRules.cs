using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Exceptions;

namespace Casazen.Core.Entities;

/// <summary>
/// Rules of the fields the wizard «Aggiungi immobile» gives a <see cref="Property"/> (SR-07a): the limits, the way each is
/// stored, and the one link between them and the rest of the property, the two amenities the structured house rules decide.
/// One place for the host forms, the property service and the tests.
/// </summary>
/// <remarks>
/// The structured rules are smoking, pets, parties, children (<see cref="Property.ChildrenPolicy"/>, NF-37) and the quiet hours. Smoking and pets
/// decide an amenity; children, parties and the quiet hours decide nothing else: they are stored as they are said, and
/// <see cref="Property.ChildrenWelcome"/> of the site profile (the label of the public page) is its own answer.
/// </remarks>
public static class PropertyHouseRules
{
    public const int MinBeds = 1;

    public const int MaxBeds = 100;

    public const int MinAreaSqm = 1;

    public const int MaxAreaSqm = 9999;

    public const int ListingTitleMaxLength = 60;

    public const decimal MaxExtraGuestFeePerNight = 1000m;

    public const int ExtraGuestFeeScale = 2;

    public const string QuietHoursIncompleteCode = "property_quiet_hours_incomplete";

    public static string? NormalizeListingTitle(string? title) => string.IsNullOrWhiteSpace(title) ? null : title.Trim();

    public static TimeOnly? ToWholeMinutes(TimeOnly? time) => time is { } value ? new TimeOnly(value.Hour, value.Minute) : null;

    public static decimal? NormalizeExtraGuestFee(decimal? fee) =>
        fee is { } value ? Math.Round(value, ExtraGuestFeeScale, MidpointRounding.AwayFromZero) : null;

    public static bool AreQuietHoursComplete(TimeOnly? from, TimeOnly? to) => (from is null) == (to is null);

    public static void Normalize(Property property)
    {
        ArgumentNullException.ThrowIfNull(property);

        property.ListingTitle = NormalizeListingTitle(property.ListingTitle);
        property.CheckInFrom = ToWholeMinutes(property.CheckInFrom);
        property.CheckOutBy = ToWholeMinutes(property.CheckOutBy);
        property.QuietHoursFrom = ToWholeMinutes(property.QuietHoursFrom);
        property.QuietHoursTo = ToWholeMinutes(property.QuietHoursTo);
        property.ExtraGuestFeePerNight = NormalizeExtraGuestFee(property.ExtraGuestFeePerNight);

        if (!AreQuietHoursComplete(property.QuietHoursFrom, property.QuietHoursTo))
            throw new DomainRuleException(QuietHoursIncompleteCode, "PropertyQuietHoursIncomplete");

        SyncAmenities(property);
    }

    /// <summary>
    /// Keeps <see cref="PropertyAmenity.PetFriendly"/> and <see cref="PropertyAmenity.SmokingAllowed"/> in step with the rules the
    /// host stated: when a rule is set, the amenity is in the list exactly when the rule says <c>Yes</c> («Benvenuti», «Consentito»);
    /// «Su richiesta» and «Solo all'aperto» are not a plain yes, so they do not advertise the amenity. A rule that was never set
    /// (null) leaves the amenity as the list says, which is how every property that existed before the rules behaves. So the
    /// answer given in the step «Regole» of the wizard is never contradicted by the amenities of the public listing, whatever the
    /// order in which the wizard saves its steps.
    /// </summary>
    public static void SyncAmenities(Property property)
    {
        ArgumentNullException.ThrowIfNull(property);

        Follow(property, PropertyAmenity.PetFriendly, property.PetsPolicy is { } pets ? pets == PetsPolicy.Yes : null);
        Follow(property, PropertyAmenity.SmokingAllowed, property.SmokingPolicy is { } smoking ? smoking == SmokingPolicy.Yes : null);
    }

    private static void Follow(Property property, PropertyAmenity amenity, bool? wanted)
    {
        if (wanted is null)
            return;

        var present = property.Amenities.Contains(amenity);
        if (wanted == true && !present)
            property.Amenities = [.. property.Amenities, amenity];
        else if (wanted == false && present)
            property.Amenities = [.. property.Amenities.Where(a => a != amenity)];
    }
}
