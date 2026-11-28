using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The rules of the issuer of the documents in one place (FIS-02, NF-82, NF-112, NF-113, E11/F01/US01/T01): CasaZen is always the issuer (decision of
/// 2026-11-14), every active booking can have its document whatever its source, and the profile of the issuer is complete when it has what its country
/// and its regime need.
/// </summary>
public static class FiscalDocumentRules
{
    public const string Italy = "IT";

    public const string Switzerland = "CH";

    public static bool IsRetiredMode(DocumentIssuerMode mode) => Enum.IsDefined(mode) && mode != DocumentIssuerMode.CasaZen;

    public static string CountryOfRegime(DocumentRegime regime) =>
        regime is DocumentRegime.SwissVatRegistered or DocumentRegime.SwissNotRegistered ? Switzerland : Italy;

    public static bool IsRegimeOfCountry(DocumentRegime regime, string? countryCode) =>
        IsSwiss(countryCode) == (CountryOfRegime(regime) == Switzerland);

    /// <summary>True for a VAT-registered regime: an Italian VAT number (ordinary or flat-rate) or a Swiss MWST registration.</summary>
    public static bool IsVatRegisteredRegime(DocumentRegime regime) =>
        regime is DocumentRegime.VatOrdinary or DocumentRegime.VatFlatRate or DocumentRegime.SwissVatRegistered;

    public static bool IsSwiss(string? countryCode) => string.Equals(countryCode?.Trim(), Switzerland, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The fields the issuer still has to give for CasaZen to issue its documents (<see cref="IssuerFields"/>), in the order of the wizard; empty when the
    /// profile is complete. Always the legal name, the structured address and the regime. In Italy also the province, and for a regime with a VAT number
    /// the FatturaPA regime code (<paramref name="effectiveRegimeCode"/>, the profile's own or the rule's) and the VAT number of the organization; for a
    /// private person the fiscal code of the organization. In Switzerland no Italian tax id: a registered issuer needs its Swiss VAT number (UID), an
    /// issuer that is not registered nothing more.
    /// </summary>
    /// <param name="issuer">The profile of the issuer.</param>
    /// <param name="regime">The regime, once the issuer has said it.</param>
    /// <param name="effectiveRegimeCode">The regime code of FatturaPA that would be used.</param>
    /// <param name="orgVatNumber">The VAT number on the organization.</param>
    /// <param name="orgFiscalCode">The fiscal code on the organization.</param>
    public static IReadOnlyList<string> MissingIssuerFields(
        IssuerData issuer,
        DocumentRegime? regime,
        string? effectiveRegimeCode,
        string? orgVatNumber,
        string? orgFiscalCode)
    {
        ArgumentNullException.ThrowIfNull(issuer);

        var swiss = IsSwiss(issuer.CountryCode);
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(issuer.LegalName))
            missing.Add(IssuerFields.LegalName);
        if (string.IsNullOrWhiteSpace(issuer.AddressStreet))
            missing.Add(IssuerFields.AddressStreet);
        if (string.IsNullOrWhiteSpace(issuer.PostalCode))
            missing.Add(IssuerFields.PostalCode);
        if (string.IsNullOrWhiteSpace(issuer.City))
            missing.Add(IssuerFields.City);
        if (string.Equals(issuer.CountryCode, Italy, StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(issuer.Province))
            missing.Add(IssuerFields.Province);

        switch (regime)
        {
            case null:
                missing.Add(IssuerFields.Regime);
                break;
            case DocumentRegime.SwissVatRegistered:
                if (string.IsNullOrWhiteSpace(issuer.SwissVatNumber))
                    missing.Add(IssuerFields.SwissVatNumber);
                break;
            case DocumentRegime.SwissNotRegistered:
                break;
            case DocumentRegime.PrivatePerson:
                if (!swiss && string.IsNullOrWhiteSpace(orgFiscalCode))
                    missing.Add(IssuerFields.FiscalCode);
                break;
            default:
                if (string.IsNullOrWhiteSpace(effectiveRegimeCode))
                    missing.Add(IssuerFields.FatturaPaRegimeCode);
                if (string.IsNullOrWhiteSpace(orgVatNumber))
                    missing.Add(IssuerFields.VatNumber);
                break;
        }

        return missing;
    }
}
