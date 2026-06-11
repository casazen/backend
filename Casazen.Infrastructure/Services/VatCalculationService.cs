using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

public class VatCalculationService : IVatCalculationService
{
    private const decimal ItalianVatRate = 0.22m;

    public VatCalculationResult Calculate(
        decimal amountExVat,
        string billingCountry,
        string? vatId,
        bool viesValidated,
        bool ossThresholdReached)
    {
        var country = billingCountry.Trim().ToUpperInvariant();

        if (country == "IT")
        {
            return new VatCalculationResult(
                VatTreatments.It22,
                Math.Round(amountExVat * ItalianVatRate, 2),
                false);
        }

        if (IsEuCountry(country) && !string.IsNullOrWhiteSpace(vatId) && viesValidated)
            return new VatCalculationResult(VatTreatments.EuReverseCharge, 0m, false);

        if (IsEuCountry(country) && ossThresholdReached)
        {
            return new VatCalculationResult(
                VatTreatments.EuOss,
                Math.Round(amountExVat * ItalianVatRate, 2),
                true);
        }

        return new VatCalculationResult(VatTreatments.EuBelowThreshold, 0m, false);
    }

    private static bool IsEuCountry(string country) =>
        country is not ("IT" or "GB") &&
        country.Length == 2 &&
        EuCountryCodes.Contains(country);

    private static readonly HashSet<string> EuCountryCodes =
    [
        "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR", "DE", "GR", "HU", "IE",
        "LV", "LT", "LU", "MT", "NL", "PL", "PT", "RO", "SK", "SI", "ES", "SE",
    ];
}
