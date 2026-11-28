using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Regulatory;

namespace Casazen.Core.Services;

public interface ICedolareAdvisoryService
{
    /// <summary>
    /// Tax advisory of the lease (LT-08), or <c>null</c> when it does not exist in the caller's org (tenant filter). No
    /// ownership check: the caller authorizes the lease first (TN-3). <paramref name="input"/> carries the data CasaZen
    /// does not hold (pages and copies for the stamp duty, the landlord's other income for IRPEF): used for the
    /// computation only, never stored or logged.
    /// </summary>
    Task<CedolareAdvisoryResult?> EvaluateAsync(
        Guid leaseId, CedolareAdvisoryInput? input = null, CancellationToken cancellationToken = default);
}

public sealed record CedolareAdvisoryInput(
    int? WrittenPages = null,
    int? Lines = null,
    int? Copies = null,
    decimal? OtherTaxableIncomeEur = null)
{
    public static CedolareAdvisoryInput None { get; } = new();
}

/// <summary>
/// Non-binding comparison of the cedolare secca with the ordinary regime for a lease (LT-08, A7-09). Figures come from
/// the configured parameters (<c>CedolareAdvisoryOptions</c>) and the rules of <c>fiscale.md</c>; what CasaZen cannot
/// compute has a status, never an invented amount. The texts are localized by the client from the codes.
/// </summary>
/// <param name="LeaseRegime">Legacy combined value of the lease.</param>
/// <param name="TaxRegime">Regime chosen by the landlord; null for older canone concordato leases (unknown).</param>
/// <param name="AnnualRent">Monthly rent × 12.</param>
/// <param name="Ata">ATA status of the property's comune (<see cref="HighTensionArea"/>).</param>
/// <param name="ConcordatoAtaReliefs">True when the canone concordato reliefs apply: concordato lease in a verified ATA comune.</param>
/// <param name="Notes">Codes of <see cref="LeaseTaxAdvisoryNoteCodes"/>.</param>
public sealed record CedolareAdvisoryResult(
    FiscalRegime LeaseRegime,
    LeaseContractType ContractType,
    LeaseTaxRegime? TaxRegime,
    decimal AnnualRent,
    HighTensionAreaStatus Ata,
    bool ConcordatoAtaReliefs,
    CedolareEstimate Cedolare,
    OrdinaryRegimeEstimate Ordinary,
    IReadOnlyList<string> Notes);

/// <summary>
/// The cedolare secca option: the substitute tax on the annual rent. Registration tax and stamp duty are not due
/// (<paramref name="RegistroEur"/> and <paramref name="BolloEur"/> are 0, fiscale.md L4 and L10); the contract must be
/// registered within the deadline all the same.
/// </summary>
public sealed record CedolareEstimate(
    decimal Rate,
    CedolareRateBasis RateBasis,
    decimal AnnualTaxEur,
    decimal RegistroEur,
    decimal BolloEur,
    string Source);

public enum CedolareRateBasis
{
    Standard = 0,

    ConcordatoAta = 1,
}

public sealed record OrdinaryRegimeEstimate(
    RegistroEstimate Registro,
    BolloEstimate Bollo,
    IrpefEstimate Irpef);

/// <summary>
/// Registration tax of the first annuity: <c>max(minimum, annual rent × base share × rate)</c> (fiscale.md L5-L8). The
/// later annuities are due on the rent of each annuity, without minimum.
/// </summary>
/// <param name="BaseShare">1, or the configured share (70%) with <see cref="CedolareAdvisoryResult.ConcordatoAtaReliefs"/>.</param>
/// <param name="TaxableBaseEur">Annual rent × base share.</param>
/// <param name="ComputedEur">Taxable base × rate, before the minimum.</param>
/// <param name="FirstYearEur">The amount of the first annuity.</param>
public sealed record RegistroEstimate(
    decimal Rate,
    decimal BaseShare,
    decimal TaxableBaseEur,
    decimal ComputedEur,
    decimal FirstYearMinimumEur,
    bool MinimumApplied,
    decimal FirstYearEur,
    string Source);

public sealed record BolloEstimate(
    AdvisoryEstimateStatus Status,
    decimal EurPerUnit,
    int PagesPerUnit,
    int LinesPerUnit,
    int? Units,
    int? Copies,
    decimal? AmountEur,
    bool LinesConsidered,
    string Source);

/// <summary>
/// Additional gross IRPEF on the rent: <c>tax(other income + rent × (1 − flat reduction)) − tax(other income)</c> with the
/// configured brackets (fiscale.md C11-C12). Regional and municipal surcharges and deductions are not included.
/// </summary>
/// <param name="ReasonCode">Why it is not computed (<see cref="IrpefNotComputedReasons"/>); null otherwise.</param>
/// <param name="TaxableRentEur">Annual rent reduced by the flat reduction.</param>
public sealed record IrpefEstimate(
    AdvisoryEstimateStatus Status,
    string? ReasonCode,
    int? TaxYear,
    decimal? RentFlatReduction,
    decimal? TaxableRentEur,
    decimal? AdditionalGrossIrpefEur,
    string? Source);

public enum AdvisoryEstimateStatus
{
    Computed = 0,

    InputRequired = 1,

    NotComputed = 2,
}

public static class IrpefNotComputedReasons
{
    public const string NotConfigured = "not_configured";

    public const string BracketsOutdated = "brackets_outdated";

    public const string IncomeOverLimit = "income_over_limit";

    public const string ConcordatoAtaReliefNotVerified = "concordato_ata_relief_not_verified";
}

public static class LeaseTaxAdvisoryNoteCodes
{
    public const string AtaUnverified = "ata_unverified";

    public const string AtaNotListed = "ata_not_listed";

    public const string EmergencyComuniNotChecked = "emergency_comuni_not_checked";

    public const string ConcordatoAttestationRequired = "concordato_attestation_required";

    public const string TransitorioReliefsToConfirm = "transitorio_reliefs_to_confirm";

    public const string ShortTermAnnualized = "short_term_annualized";

    public const string TaxRegimeUnknown = "tax_regime_unknown";

    /// <summary>Extra-EU tenant: the registration does not replace the 48-hour communication to the Questura (L13-L14).</summary>
    public const string QuesturaNotReplaced = "questura_not_replaced";
}

public interface IRliExportService
{
    Task<RliExportResult?> ExportAsync(Guid leaseId, CancellationToken cancellationToken = default);
}

public record RliExportResult(byte[] PdfBytes, string FileName);

public interface IRliChecklistService
{
    Task<RliChecklistResult> GetAsync(LeaseContract lease, CancellationToken cancellationToken = default);
}

/// <param name="RegistrationDeadline">
/// <c>min(stipula, start) + 30</c> days (LT-04, <c>RliRegistrationDeadline</c>); null while it is to be determined.
/// </param>
/// <param name="DaysRemaining">Days to the deadline on the Rome calendar: 0 on the deadline day, negative after it; null with no deadline.</param>
/// <param name="ProviderFilingAvailable">
/// The provider path exists (<c>Features:RliProvider</c> on and a configured provider): only then the delega item is
/// listed and the frontend offers "submit through the provider". Otherwise the landlord registers manually (LT-01).
/// </param>
/// <param name="Questura">
/// The communication to the public-security authority for an extra-EU tenant (LT-07); null when no tenant is extra-EU.
/// </param>
public record RliChecklistResult(
    DateTime? RegistrationDeadline,
    int? DaysRemaining,
    string TosVersion,
    string AttestationText,
    bool ProviderFilingAvailable,
    IReadOnlyList<RliChecklistItem> Items,
    QuesturaCommunicationStatus? Questura = null);

/// <summary>
/// State of the Questura communication of a lease with an extra-EU tenant (art. 7 D.Lgs. 286/1998, LT-07). Dates are
/// midnight UTC of the Rome calendar date.
/// </summary>
/// <param name="DeliveryDate">Delivery of the property: the declared date, or the start date when none was declared.</param>
/// <param name="DeliveryDateDeclared">False while <paramref name="DeliveryDate"/> is the start date used by default.</param>
/// <param name="Deadline">The day the 48 hours from the delivery end at the latest.</param>
/// <param name="DaysRemaining">Days to <paramref name="Deadline"/>: 0 on that day, negative once it has passed.</param>
/// <param name="CommunicationDate">Date of the communication declared by the landlord; null until the explicit declaration.</param>
/// <param name="HasReceipt">A receipt was uploaded with the declaration (downloadable through the API).</param>
public record QuesturaCommunicationStatus(
    DateTime DeliveryDate,
    bool DeliveryDateDeclared,
    DateTime Deadline,
    int DaysRemaining,
    DateTime? CommunicationDate,
    bool HasReceipt);

/// <summary>
/// A checklist item. <paramref name="Done"/> is true only when the step really happened (LT-01: the registration item
/// only with the registration recorded and its receipt); <paramref name="Failed"/> marks a step whose last attempt
/// failed, so the UI shows the error and the way forward instead of a tick.
/// </summary>
public record RliChecklistItem(string Key, bool Done, bool Failed = false);

public static class RliChecklistKeys
{
    public const string ContractSigned = "contract_signed";
    public const string DelegaCaptured = "delega_captured";
    public const string RliExported = "rli_exported";
    public const string RliRegistered = "rli_registered";
    public const string QuesturaExtraEu = "questura_extra_eu";

    public static IReadOnlyList<string> All { get; } =
        [ContractSigned, DelegaCaptured, RliExported, RliRegistered, QuesturaExtraEu];
}

public record RegistrationAuthorizationRequest(string TosVersion, bool AttestationAccepted);
