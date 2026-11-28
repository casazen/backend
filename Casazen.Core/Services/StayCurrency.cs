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
    public const string Euro = "EUR";

    public const string SwissFranc = "CHF";

    public const string Default = Euro;

    public static string Normalize(string? currency) =>
        string.IsNullOrWhiteSpace(currency) ? Default : currency.Trim().ToUpperInvariant();

    public static string ForProperty(Property? property) => Normalize(property?.Currency);

    public static string ForStripe(string? currency) => Normalize(currency).ToLowerInvariant();

    public static bool IsSupported(string? currency) => Normalize(currency) is Euro or SwissFranc;
}
