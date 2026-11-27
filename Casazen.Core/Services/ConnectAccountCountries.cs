namespace Casazen.Core.Services;

/// <summary>
/// The country of the Stripe Express account of an org (issue #1001). Stripe fixes it when the account is created and the hosted
/// onboarding then asks for the data of that country: an Italian form wants an Italian address, tax id and IBAN, so a Swiss
/// company (the pilot's Quokka360: billing country and confirmed fiscal profile <c>CH</c>) can never complete an account that was
/// created as Italian, in test or in production. The account therefore follows the org: the country of its confirmed fiscal
/// profile, else its billing country, else the platform default (<c>Stripe:ConnectDefaultCountry</c>, <c>IT</c>).
/// </summary>
public static class ConnectAccountCountries
{
    public const string Italy = "IT";
    public const string Switzerland = "CH";

    /// <summary>
    /// The countries an org's own data can choose: the two the product supports (the Italian and the Swiss fiscal profiles, the
    /// EUR and CHF stays). An org of another country keeps the platform default, as before.
    /// </summary>
    public static IReadOnlyList<string> OrgCountries { get; } = [Italy, Switzerland];

    /// <summary>
    /// The country of a new account: <paramref name="fiscalProfileCountry"/> (the country of the org's confirmed fiscal profile,
    /// <c>null</c> when it has none) when it is one of <see cref="OrgCountries"/>, else <paramref name="billingCountry"/> under the
    /// same rule, else <paramref name="platformDefault"/>, else <see cref="Italy"/>. Always upper case.
    /// </summary>
    public static string Resolve(string? platformDefault, string? fiscalProfileCountry, string? billingCountry) =>
        OfOrg(fiscalProfileCountry) ?? OfOrg(billingCountry) ?? Normalize(platformDefault) ?? Italy;

    /// <summary>Whether two country codes are the same country, ignoring case and spaces. A blank one is a country that is not known: never the same.</summary>
    public static bool SameCountry(string? first, string? second) =>
        Normalize(first) is { } left && string.Equals(left, Normalize(second), StringComparison.Ordinal);

    private static string? OfOrg(string? country) =>
        Normalize(country) is { } code && OrgCountries.Contains(code) ? code : null;

    private static string? Normalize(string? country) =>
        string.IsNullOrWhiteSpace(country) ? null : country.Trim().ToUpperInvariant();
}
