using Casazen.Core.Entities;
using Casazen.Core.Exceptions;

namespace Casazen.Core.Services;

public static class StayDiscountErrorCodes
{
    public const string DiscountsInvalid = "property_stay_discounts_invalid";
}

public static class StayDiscountRules
{
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
    Task<IReadOnlyList<StayDiscountTier>?> GetAsync(Guid propertyId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StayDiscountTier>?> ReplaceAsync(
        Guid propertyId,
        IReadOnlyList<StayDiscountTier> tiers,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StayDiscountTier>> GetForPricingAsync(Guid propertyId, CancellationToken cancellationToken = default);
}
