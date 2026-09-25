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

/// <summary>Data of the advisory that CasaZen does not hold; each may be missing.</summary>
/// <param name="WrittenPages">Written pages ("facciate") of one copy of the contract.</param>
/// <param name="Lines">Lines of one copy of the contract, when known.</param>
/// <param name="Copies">Copies to register.</param>
/// <param name="OtherTaxableIncomeEur">The landlord's IRPEF taxable income of the year without this rent.</param>
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
    /// <summary>The rate of residential leases.</summary>
    Standard = 0,

    /// <summary>The reduced rate of a canone concordato lease in a verified ATA comune.</summary>
    ConcordatoAta = 1,
}

/// <summary>The ordinary regime option: registration tax, stamp duty and IRPEF.</summary>
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

/// <summary>
/// Stamp duty: <c>EurPerUnit × max(⌈pages / PagesPerUnit⌉, ⌈lines / LinesPerUnit⌉) × copies</c> (fiscale.md L10).
/// <see cref="AdvisoryEstimateStatus.InputRequired"/> without pages and copies: then only the rule is shown.
/// </summary>
/// <param name="LinesConsidered">False when the lines were not given: the amount counts the pages only.</param>
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
    /// <summary>The amount is computed.</summary>
    Computed = 0,

    /// <summary>The landlord must give data CasaZen does not hold.</summary>
    InputRequired = 1,

    /// <summary>CasaZen does not compute it: to be assessed with the accountant (<see cref="IrpefEstimate.ReasonCode"/>).</summary>
    NotComputed = 2,
}

/// <summary>Why the IRPEF comparison is not computed.</summary>
public static class IrpefNotComputedReasons
{
    /// <summary>No brackets configured.</summary>
    public const string NotConfigured = "not_configured";

    /// <summary>The configured brackets are of an earlier tax year.</summary>
    public const string BracketsOutdated = "brackets_outdated";

    /// <summary>Total income above the configured limit: the brackets alone are not the tax.</summary>
    public const string IncomeOverLimit = "income_over_limit";

    /// <summary>
    /// Canone concordato in a verified ATA comune: the IRPEF relief of that case is not among the verified rules.
    /// </summary>
    public const string ConcordatoAtaReliefNotVerified = "concordato_ata_relief_not_verified";
}

/// <summary>Notes of the advisory (<see cref="CedolareAdvisoryResult.Notes"/>), localized by the client.</summary>
public static class LeaseTaxAdvisoryNoteCodes
{
    /// <summary>Canone concordato in an ATA candidate not verified: 21% and full base shown; 10% and 70% if confirmed.</summary>
    public const string AtaUnverified = "ata_unverified";

    /// <summary>Canone concordato in a comune not in CasaZen's ATA list: 21% and full base shown.</summary>
    public const string AtaNotListed = "ata_not_listed";

    /// <summary>The 10% also applies in comuni with a state of emergency (fiscale.md L12): no official list, not checked.</summary>
    public const string EmergencyComuniNotChecked = "emergency_comuni_not_checked";

    /// <summary>The canone concordato reliefs need the attestation of conformity for contracts without assistance.</summary>
    public const string ConcordatoAttestationRequired = "concordato_attestation_required";

    /// <summary>Transitorio lease: whether the reduced rate and base apply is to be confirmed by the accountant.</summary>
    public const string TransitorioReliefsToConfirm = "transitorio_reliefs_to_confirm";

    /// <summary>Term shorter than a year: the figures are per year (monthly rent × 12).</summary>
    public const string ShortTermAnnualized = "short_term_annualized";

    /// <summary>Older canone concordato lease without a recorded tax regime: both options are shown.</summary>
    public const string TaxRegimeUnknown = "tax_regime_unknown";

    /// <summary>Extra-EU tenant: the registration does not replace the 48-hour communication to the Questura (L13-L14).</summary>
    public const string QuesturaNotReplaced = "questura_not_replaced";
}

public interface IRliExportService
{
    /// <summary>RLI prefill of the lease, or <c>null</c> when it is not visible. The caller authorizes the lease first (TN-3).</summary>
    Task<RliExportResult?> ExportAsync(Guid leaseId, CancellationToken cancellationToken = default);
}

public record RliExportResult(byte[] PdfBytes, string FileName);

public interface IRliChecklistService
{
    /// <summary>
    /// Checklist of <paramref name="lease"/>, loaded with its property, parties and registration and already
    /// authorized by the caller (TN-3). Items carry a stable key, never a text: the API localizes the labels.
    /// </summary>
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
public record RliChecklistResult(
    DateTime? RegistrationDeadline,
    int? DaysRemaining,
    string TosVersion,
    string AttestationText,
    bool ProviderFilingAvailable,
    IReadOnlyList<RliChecklistItem> Items);

/// <summary>
/// A checklist item. <paramref name="Done"/> is true only when the step really happened (LT-01: the registration item
/// only with the registration recorded and its receipt); <paramref name="Failed"/> marks a step whose last attempt
/// failed, so the UI shows the error and the way forward instead of a tick.
/// </summary>
public record RliChecklistItem(string Key, bool Done, bool Failed = false);

/// <summary>Stable keys of the RLI checklist items (labels: <c>RliChecklist_{key}</c> in the API resources).</summary>
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
