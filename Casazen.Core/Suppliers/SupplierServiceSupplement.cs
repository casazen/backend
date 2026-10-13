namespace Casazen.Core.Suppliers;

/// <summary>
/// One supplement of a service (an extra the customer can add to the base price), in the structured form stored in
/// <c>SupplierServiceListing.SupplementsJson</c> and exposed by the API: <c>{ code, label, amountCents, per, max }</c>.
/// Structured on purpose: the price estimate of the booking (SP-09) computes the total from them, so a free text line
/// would not do. The value is validated by <see cref="SupplierServiceListingRules"/>.
/// </summary>
/// <param name="Code">Stable identifier inside the service (lowercase letters, digits and hyphens), what a booking refers to.</param>
/// <param name="Label">What the customer reads (one line).</param>
/// <param name="AmountCents">The amount of one unit, in cents, above zero.</param>
/// <param name="Per">What one unit is: a <see cref="SupplierServiceSupplementUnits"/> code.</param>
/// <param name="Max">Most units a customer can pick; <c>null</c> for no limit.</param>
public sealed record SupplierServiceSupplement(string Code, string Label, int AmountCents, string Per, int? Max);

/// <summary>The values of <see cref="SupplierServiceSupplement.Per"/> (lowercase codes, stored and exposed as they are).</summary>
public static class SupplierServiceSupplementUnits
{
    /// <summary>A fixed amount, added once.</summary>
    public const string Flat = "flat";

    /// <summary>Per extra bathroom.</summary>
    public const string Bathroom = "bathroom";

    /// <summary>Per 30 square meters (the estimate of the booking decides from which surface it counts, SP-09).</summary>
    public const string Sqm30 = "sqm30";

    /// <summary>Per set, e.g. of linen.</summary>
    public const string Set = "set";

    /// <summary>Per extra hour.</summary>
    public const string Hour = "hour";

    /// <summary>Every unit, in the order the console offers them.</summary>
    public static IReadOnlyList<string> All { get; } = [Flat, Bathroom, Sqm30, Set, Hour];
}
