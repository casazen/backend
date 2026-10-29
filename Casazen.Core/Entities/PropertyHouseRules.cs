using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Exceptions;

namespace Casazen.Core.Entities;

/// <summary>
/// Rules of the fields the wizard «Aggiungi immobile» gives a <see cref="Property"/> (SR-07a): the limits, the way each is
/// stored, and the one link between them and the rest of the property, the two amenities the structured house rules decide.
/// One place for the host forms, the property service and the tests.
/// </summary>
public static class PropertyHouseRules
{
    /// <summary>Fewest beds a host can state.</summary>
    public const int MinBeds = 1;

    /// <summary>Most beds a host can state: the same ceiling as the bedrooms.</summary>
    public const int MaxBeds = 100;

    /// <summary>Smallest living area a host can state, in square metres.</summary>
    public const int MinAreaSqm = 1;

    /// <summary>Largest living area a host can state, in square metres: four digits, the width of the field.</summary>
    public const int MaxAreaSqm = 9999;

    /// <summary>Longest headline of the listing, in characters (the column is <c>character varying(60)</c>).</summary>
    public const int ListingTitleMaxLength = 60;

    /// <summary>Highest fee per extra guest and night, in euros: above it is a typing mistake.</summary>
    public const decimal MaxExtraGuestFeePerNight = 1000m;

    /// <summary>Decimals kept for the fee (the column is <c>numeric(18,2)</c>).</summary>
    public const int ExtraGuestFeeScale = 2;

    /// <summary>
    /// 422 (service level, behind the 400 of the forms): only one end of the quiet hours is set. They are a pair: «dalle 22:00»
    /// without «fino alle» says nothing to a guest.
    /// </summary>
    public const string QuietHoursIncompleteCode = "property_quiet_hours_incomplete";

    /// <summary>The headline as stored: trimmed, and null when it is blank (the listing then uses the name of the property).</summary>
    public static string? NormalizeListingTitle(string? title) => string.IsNullOrWhiteSpace(title) ? null : title.Trim();

    /// <summary>A time of the day as stored: whole minutes (a clock time on a house rule has no seconds), null stays null.</summary>
    public static TimeOnly? ToWholeMinutes(TimeOnly? time) => time is { } value ? new TimeOnly(value.Hour, value.Minute) : null;

    /// <summary>The fee with the stored precision (<see cref="ExtraGuestFeeScale"/> decimals, half away from zero), null stays null.</summary>
    public static decimal? NormalizeExtraGuestFee(decimal? fee) =>
        fee is { } value ? Math.Round(value, ExtraGuestFeeScale, MidpointRounding.AwayFromZero) : null;

    /// <summary>True when both ends of the quiet hours are set or neither is.</summary>
    public static bool AreQuietHoursComplete(TimeOnly? from, TimeOnly? to) => (from is null) == (to is null);

    /// <summary>
    /// The fields of the wizard as they are stored: headline trimmed, times in whole minutes, fee rounded to the cent, and the
    /// two amenities that follow the structured rules (<see cref="SyncAmenities"/>). Run on every create and update.
    /// </summary>
    /// <exception cref="DomainRuleException"><see cref="QuietHoursIncompleteCode"/> (422): one end of the quiet hours is set.</exception>
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
