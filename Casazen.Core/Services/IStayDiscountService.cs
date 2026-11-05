using Casazen.Core.Entities;
using Casazen.Core.Exceptions;

namespace Casazen.Core.Services;

/// <summary>Stable <c>code</c> values of the length-of-stay discounts (NF-35): part of the API contract, never rename one.</summary>
public static class StayDiscountErrorCodes
{
    /// <summary>422: the tiers break a rule (too many, a minimum of nights out of range, two with the same nights, a percentage out of range).</summary>
    public const string DiscountsInvalid = "property_stay_discounts_invalid";
}

/// <summary>
/// The rules of the length-of-stay discounts of a property (NF-35, ADR-028-R2): at most <see cref="PropertyStayDiscount.MaxPerProperty"/> tiers, each
/// with its own minimum of nights from <see cref="PropertyStayDiscount.MinNightsLowest"/> to <see cref="PropertyStayDiscount.MinNightsHighest"/>, and a
/// percentage above 0 and at most 100 with two decimals. One place for the service and the tests.
/// </summary>
public static class StayDiscountRules
{
    /// <summary>
    /// The tiers as they will be stored: percentages rounded to two decimals (half away from zero), in order of nights. Refuses the whole list at
    /// the first rule broken.
    /// </summary>
    /// <exception cref="DomainRuleException">422 <see cref="StayDiscountErrorCodes.DiscountsInvalid"/>, with the message of the rule that broke.</exception>
    public static IReadOnlyList<StayDiscountTier> Normalize(IReadOnlyList<StayDiscountTier>? tiers)
    {
        tiers ??= [];
        if (tiers.Count > PropertyStayDiscount.MaxPerProperty)
            throw Invalid("PropertyStayDiscountsTooMany", PropertyStayDiscount.MaxPerProperty);

        var normalized = new List<StayDiscountTier>(tiers.Count);
        foreach (var tier in tiers)
        {
            if (tier.MinNights < PropertyStayDiscount.MinNightsLowest || tier.MinNights > PropertyStayDiscount.MinNightsHighest)
            {
                throw Invalid(
                    "PropertyStayDiscountsMinNightsInvalid", PropertyStayDiscount.MinNightsLowest, PropertyStayDiscount.MinNightsHighest);
            }

            var percent = Math.Round(tier.PercentOff, PropertyStayDiscount.PercentScale, MidpointRounding.AwayFromZero);
            if (percent <= 0m || percent > PropertyStayDiscount.MaxPercentOff)
                throw Invalid("PropertyStayDiscountsPercentInvalid", PropertyStayDiscount.MaxPercentOff);

            if (normalized.Any(other => other.MinNights == tier.MinNights))
                throw Invalid("PropertyStayDiscountsDuplicate");

            normalized.Add(new StayDiscountTier(tier.MinNights, percent));
        }

        return [.. normalized.OrderBy(tier => tier.MinNights)];
    }

    private static DomainRuleException Invalid(string messageKey, params object[] args) =>
        new(StayDiscountErrorCodes.DiscountsInvalid, messageKey, args);
}

/// <summary>
/// The length-of-stay discounts of a property (NF-35): the tiers the quote and the checkout apply (<see cref="StayPricing"/>). The host reads and
/// writes those of one property of its org (the tenant filter hides another org's property, so it is simply not found); the permission is the
/// caller's to check (<c>PropertyListingDataController</c>).
/// </summary>
public interface IStayDiscountService
{
    /// <summary>The tiers in order of nights; <c>null</c> when the property does not exist for the caller. A property with none gives an empty list.</summary>
    Task<IReadOnlyList<StayDiscountTier>?> GetAsync(Guid propertyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the tiers with <paramref name="tiers"/> (an empty list removes them all) and answers with what is stored; <c>null</c> when the property
    /// does not exist for the caller.
    /// </summary>
    /// <exception cref="DomainRuleException">
    /// 422 <see cref="StayDiscountErrorCodes.DiscountsInvalid"/>, or <c>property_not_bookable_in_long_mode</c> (a long-rent property has no stay to
    /// reward). Nothing is saved.
    /// </exception>
    Task<IReadOnlyList<StayDiscountTier>?> ReplaceAsync(
        Guid propertyId,
        IReadOnlyList<StayDiscountTier> tiers,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The tiers of a property for pricing a stay, whoever asks: the price of a stay is public data of the property (the quote and the checkout of
    /// the booking site), so a signed-in host of another org who looks at the site sees the same price as everybody else. Empty for a property
    /// with none or that does not exist. Not for the host's editor: that is <see cref="GetAsync"/>.
    /// </summary>
    Task<IReadOnlyList<StayDiscountTier>> GetForPricingAsync(Guid propertyId, CancellationToken cancellationToken = default);
}
