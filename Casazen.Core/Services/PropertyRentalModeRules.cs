using System.Linq.Expressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;

namespace Casazen.Core.Services;

public static class PropertyRentalModeErrorCodes
{
    public const string NotBookableInLongMode = "property_not_bookable_in_long_mode";

    /// <summary>
    /// 422: the body of <c>PUT /api/properties/{id}</c> carries a <c>rentalMode</c> different from the stored one. The mode
    /// of an existing property is not changed by the generic save (it would skip the checks on the stays and the leases
    /// that the scheduled change of PM-02 makes, under the property lock).
    /// </summary>
    public const string ChangeNotAllowed = "property_rental_mode_change_not_allowed";
}

/// <summary>
/// The rules of <see cref="RentalMode"/> (PM-01, decisions D16 and D19), in one place: which mode a new property gets, and
/// what "short rent" means for every predicate that must ignore the properties in long-term mode (the public listing,
/// the bookings, the compliance status, the CIN alerts and summaries, the cockpit). Runbook:
/// <c>docs/runbooks/property-rental-mode.md</c>.
/// </summary>
public static class PropertyRentalModeRules
{
    /// <summary>
    /// The mode of a property created through the API.
    /// <list type="bullet">
    /// <item>A mode in the request wins: the clients that know the field say what they mean.</item>
    /// <item><b>Compatibility rule</b>: without a mode, a request with <c>MaxGuests = 0</c> <b>and</b> <c>NightlyRate = 0</c>
    /// (what the long-rent form of the landlords has always sent, audit A7-06: a property "not set" for short stays) creates
    /// a <see cref="RentalMode.Long"/> property; anything else (a guest count or a rate) is <see cref="RentalMode.Short"/>.
    /// The rule is for the old clients only: it does not look at the stored properties and never runs on an update.</item>
    /// </list>
    /// </summary>
    public static RentalMode ResolveForCreation(RentalMode? requested, int maxGuests, decimal nightlyRate) =>
        requested ?? (maxGuests == 0 && nightlyRate == 0 ? RentalMode.Long : RentalMode.Short);

    /// <summary>
    /// The properties that behave as short-rent ones: the only ones the public site publishes, that guests and hosts book,
    /// that the compliance status, the CIN alerts and the CIN summaries look at. As an expression, to be translated by the
    /// database. <see cref="Casazen.Core.Services.PublicListing.IsPublished"/> repeats the same comparison (a test keeps the
    /// two together). When <c>Both</c> is added to <see cref="RentalMode"/> this is the one definition to revisit.
    /// </summary>
    public static Expression<Func<Property, bool>> IsShortRent { get; } = p => p.RentalMode == RentalMode.Short;

    public static bool IsShort(Property property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return property.RentalMode == RentalMode.Short;
    }

    public static void EnsureShortRent(Property property)
    {
        if (!IsShort(property))
        {
            throw new DomainRuleException(
                PropertyRentalModeErrorCodes.NotBookableInLongMode, "PropertyNotBookableInLongMode");
        }
    }
}
