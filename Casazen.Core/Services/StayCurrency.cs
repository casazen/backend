using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// The currency of a stay, and the one place the booking money path reads it from (CHF-MONEY, 2026-10-30). A stay is priced, charged,
/// refunded and shown in <see cref="Property.Currency"/>: <c>CHF</c> for a house in Switzerland, <c>EUR</c> for any other
/// (<c>PropertyService</c> derives it from the country when the house is created; it is not editable afterwards, so a payment and its
/// refunds can never change currency). The quote, the booking views, the cancellation preview, the PaymentIntent of the checkout and of
/// the deferred charge, and the e-mails take the code from here and carry it on the object they build; none of them writes <c>EUR</c>.
/// </summary>
/// <remarks>
/// Both supported currencies have two decimals in Stripe's table of minimum units (it has zero-decimal currencies such as JPY and
/// three-decimal ones such as KWD, which this product does not take), so an amount is worth <c>amount × 100</c> minor units whichever of
/// the two it is. A currency is added to <see cref="IsSupported"/> only together with the check of its decimals, never by a string in a
/// service. Amounts of two currencies are never added up nor converted.
/// </remarks>
public static class StayCurrency
{
    /// <summary>The euro: the currency of the houses in Italy.</summary>
    public const string Euro = "EUR";

    /// <summary>The Swiss franc: the currency of the houses in Switzerland.</summary>
    public const string SwissFranc = "CHF";

    /// <summary>What a house without a currency costs in, and what a missing code means (the property row says <c>EUR</c> by default).</summary>
    public const string Default = Euro;

    /// <summary>
    /// <paramref name="currency"/> as an upper-case ISO 4217 code (<c>chf</c> and <c>CHF </c> are <c>CHF</c>); <see cref="Default"/> when
    /// there is none.
    /// </summary>
    public static string Normalize(string? currency) =>
        string.IsNullOrWhiteSpace(currency) ? Default : currency.Trim().ToUpperInvariant();

    /// <summary>The currency of the stays of <paramref name="property"/>; <see cref="Default"/> when the property is not known.</summary>
    public static string ForProperty(Property? property) => Normalize(property?.Currency);

    /// <summary>The code the way Stripe wants it (<c>chf</c>: three letters, lower case) for an amount in the currency of a stay.</summary>
    public static string ForStripe(string? currency) => Normalize(currency).ToLowerInvariant();

    /// <summary>The currencies a stay can be charged in: the two of the houses CasaZen has (Italy, Switzerland).</summary>
    public static bool IsSupported(string? currency) => Normalize(currency) is Euro or SwissFranc;
}
