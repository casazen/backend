namespace Casazen.Core.Services;

public static class VatTreatments
{
    public const string It22 = "IT_22";
    public const string EuReverseCharge = "EU_REVERSE_CHARGE";
    public const string EuOss = "EU_OSS";
    public const string EuBelowThreshold = "EU_BELOW_THRESHOLD";
}

public sealed record VatCalculationResult(string VatTreatment, decimal VatAmount, bool OssApplied);

public interface IVatCalculationService
{
    VatCalculationResult Calculate(decimal amountExVat, string billingCountry, string? vatId, bool viesValidated, bool ossThresholdReached);
}
