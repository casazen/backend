using Casazen.Core.Authorization;

namespace Casazen.Core.Services;

/// <summary>
/// The owner portfolio of an agency in the onboarding (E10/F02/US02/T01, NF-109, ONB-06): the import of the owners and of their shares of the
/// properties from a CSV or Excel file, and the readiness of every owner and property for the owner accounting and for the portal. It is
/// separate from the import of the per-property data (<see cref="IOnboardingBulkImportService"/>), which it never reads nor writes.
/// </summary>
/// <remarks>
/// <para><b>Two calls, one answer.</b> <see cref="ImportAsync"/> reads and judges the whole file; with <c>dryRun</c> it stops there, otherwise it
/// writes ONLY the valid rows, one unit of work for each property (its owners and its ownership periods together, because the shares of a
/// property are right or wrong as a whole). An invalid row never blocks a valid row of another property. <b>It never sends an invitation</b>:
/// the invitations are a separate, explicit act (<see cref="IOwnerInvitationService"/>: preview, then confirm).</para>
/// <para><b>Who the owner of a row is.</b> In this order: the explicit <c>ownerId</c> of the row (a CasaZen id, the manual choice for an
/// ambiguous row); the <c>ownerKey</c> the agency gave the owner (unique in the org); the normalized fiscal identifier (fiscal code, then VAT
/// number). <b>The e-mail never identifies an owner</b>: two owners may share an address, and an address alone neither matches a row nor
/// creates an owner (a row needs a key or a fiscal identifier, <see cref="OwnerImportRowCodes.OwnerIdentityMissing"/>). More than one owner with
/// the same fiscal identifier is <see cref="OwnerImportRowCodes.OwnerAmbiguous"/> and lists the candidates.</para>
/// <para><b>Periods.</b> A period is preserved, never overwritten: a period that already ended is history and the file cannot change it; a
/// period in force can only be closed or its end moved to today or later; a period that has not started can be changed. The shares of the
/// property after the file (what is stored plus the rows) must add up to exactly 100.00 on every day from the first day the file touches
/// (<see cref="OwnerOwnershipTimeline"/>), or none of the rows of that property is written.</para>
/// <para><b>Reach.</b> Every property and owner is looked up in the org of the scope and inside what the scope reaches (<c>InScope</c>); a
/// property of another org, one outside the reach and one that does not exist are the same row status. A caller with a partial reach sees and
/// touches only the owners that hold something inside it.</para>
/// <para><b>Secrets.</b> The fiscal code, the VAT number, the IBAN and the BIC are stored encrypted (ADR-012) and appear in no answer and in no
/// log: the answer holds the codes of the rows and the names the agency itself keeps in its registry.</para>
/// </remarks>
public interface IOwnerOnboardingService
{
    /// <summary>
    /// Reads <paramref name="content"/> (the bytes of the file named <paramref name="fileName"/>), judges every row and, unless
    /// <paramref name="dryRun"/>, writes the valid ones.
    /// </summary>
    /// <remarks>
    /// Errors, the ones of <see cref="IOnboardingBulkImportService"/>: 415 <see cref="BulkImportCodes.FileTypeUnsupported"/>, 413
    /// <see cref="BulkImportCodes.FileTooLarge"/>, 422 <see cref="BulkImportCodes.TooManyRows"/>, <see cref="BulkImportCodes.FileUnreadable"/>,
    /// <see cref="BulkImportCodes.FileEmpty"/>, and 422 <see cref="BulkImportCodes.ColumnsUnrecognized"/> when the header has no column that names
    /// the property, no column that names the owner or no share.
    /// </remarks>
    Task<OwnerImportResult> ImportAsync(HostScope scope, string actorUserId, string fileName, byte[] content, bool dryRun, CancellationToken ct);

    /// <summary>
    /// For every owner and every property inside the reach of <paramref name="scope"/>: what is missing for the registry, the ownership, the mandate,
    /// the fiscal profile, the payment data and the portal, each fact by its code. Read only, computed on every call; the readiness of the owner
    /// accounting never depends on, and never changes, the readiness of the OTA channels. An owner whose every period is over is history, not a
    /// person to complete: it is not listed and not counted. The owners are cut at <see cref="OwnerOnboardingLimits.MaxListed"/> unless
    /// <paramref name="listEveryOwner"/> (the invitations, which must see every owner, ask for them all).
    /// </summary>
    /// <param name="propertyId">When provided, restricts the report to this property inside the existing scope, and its owners.</param>
    Task<OwnerReadinessReport> GetReadinessAsync(HostScope scope, CancellationToken ct, bool listEveryOwner = false, Guid? propertyId = null);
}

public static class OwnerImportColumns
{
    public const string PropertyId = BulkImportColumns.PropertyId;
    public const string CinCode = BulkImportColumns.CinCode;

    public const string OwnerId = "ownerId";

    public const string OwnerKey = "ownerKey";

    public const string LegalName = "legalName";
    public const string Email = "email";

    public const string EntityKind = "entityKind";

    public const string TaxResidence = "taxResidence";

    public const string FiscalCode = "fiscalCode";
    public const string VatNumber = "vatNumber";
    public const string BillingStreet = "billingStreet";
    public const string BillingCivic = "billingCivic";
    public const string BillingPostalCode = "billingPostalCode";
    public const string BillingCity = "billingCity";
    public const string BillingProvince = "billingProvince";
    public const string BillingCountry = "billingCountry";

    public const string SharePercent = "sharePercent";

    public const string OwnershipStart = "ownershipStart";

    public const string OwnershipEnd = "ownershipEnd";

    public const string SettlementCurrency = "settlementCurrency";
    public const string Iban = "iban";
    public const string Bic = "bic";
}

public static class OwnerImportRowCodes
{
    public const string Ok = BulkImportRowCodes.Ok;

    /// <summary>No property of the caller has that id or CIN (also another org's, one outside the reach, a row with no key).</summary>
    public const string PropertyNotFound = BulkImportRowCodes.PropertyNotFound;

    public const string PropertyAmbiguous = BulkImportRowCodes.PropertyAmbiguous;

    public const string DuplicateRow = BulkImportRowCodes.DuplicateRow;

    public const string OwnerIdentityMissing = "owner_identity_missing";

    public const string OwnerKeyInvalid = "owner_key_invalid";

    public const string OwnerNotFound = "owner_not_found";

    public const string OwnerAmbiguous = "owner_ambiguous";

    public const string OwnerIdentityConflict = "owner_identity_conflict";

    public const string OwnerFieldConflict = "owner_field_conflict";

    public const string OwnerSharedOutsideReach = "owner_shared_outside_reach";

    public const string LegalNameRequired = "legal_name_required";
    public const string LegalNameInvalid = "legal_name_invalid";
    public const string EntityKindInvalid = "entity_kind_invalid";
    public const string TaxResidenceInvalid = "tax_residence_invalid";
    public const string FiscalCodeInvalid = "fiscal_code_invalid";
    public const string VatNumberInvalid = "vat_number_invalid";
    public const string EmailInvalid = "email_invalid";
    public const string AddressInvalid = "billing_address_invalid";
    public const string IbanInvalid = "iban_invalid";
    public const string BicInvalid = "bic_invalid";
    public const string CurrencyInvalid = "currency_invalid";
    public const string ShareInvalid = "share_invalid";
    public const string DateInvalid = "date_invalid";

    public const string PeriodInvalid = "period_invalid";

    public const string OwnershipStartRequired = "ownership_start_required";

    public const string OwnershipOverlap = "ownership_overlap";

    public const string OwnershipPeriodConflict = "ownership_period_conflict";

    public const string OwnershipSharesInvalid = "ownership_shares_invalid";

    public const string MandateOutsideOwnership = "mandate_outside_ownership";
}

public static class OwnerImportActions
{
    public const string CreateOwner = "create_owner";
    public const string MatchOwner = "match_owner";
    public const string CreateOwnership = "create_ownership";

    public const string UpdateFuturePeriod = "update_future_period";

    public const string Unchanged = "unchanged";

    public const string Conflict = "conflict";
}

public static class OwnerMatchKinds
{
    public const string OwnerId = "owner_id";
    public const string ExternalKey = "external_key";
    public const string FiscalIdentifier = "fiscal_identifier";
}

public sealed record OwnerImportCandidate(Guid OwnerId, string? ExternalKey, string DisplayName);

/// <summary>
/// One row of the owner file. The row is <c>ok</c> or has the code of its first error as <paramref name="Status"/> and all of them in
/// <paramref name="Errors"/>. No field holds a fiscal identifier, a VAT number, an IBAN or a BIC.
/// </summary>
/// <param name="RowNumber">The row as the person sees it in the sheet: the header is row 1, the first row of data row 2.</param>
/// <param name="PropertyRef">What the row names the property by (the id as written, or the CIN normalized); <c>null</c> when it names none.</param>
/// <param name="PropertyId">The property, when the row found one of the caller's.</param>
/// <param name="OwnerKey">The key the row gives the owner (an agency code), as written; <c>null</c> when it has none.</param>
/// <param name="OwnerId">The owner the row found (existing); <c>null</c> for an owner that would be created.</param>
/// <param name="MatchedBy">How the owner was found: one of <see cref="OwnerMatchKinds"/>; <c>null</c> for a new owner.</param>
/// <param name="OwnerAction"><see cref="OwnerImportActions.CreateOwner"/>, <see cref="OwnerImportActions.MatchOwner"/> or <see cref="OwnerImportActions.Conflict"/>.</param>
/// <param name="OwnershipAction"><see cref="OwnerImportActions.CreateOwnership"/>, <see cref="OwnerImportActions.UpdateFuturePeriod"/>, <see cref="OwnerImportActions.Unchanged"/> or <see cref="OwnerImportActions.Conflict"/>; <c>null</c> when the row cannot be judged.</param>
/// <param name="Candidates">The owners an ambiguous row could be.</param>
/// <param name="Applied">True when the row was valid and changed something (never in a dry run).</param>
public sealed record OwnerImportRow(
    int RowNumber,
    string? PropertyRef,
    Guid? PropertyId,
    string? OwnerKey,
    Guid? OwnerId,
    string? MatchedBy,
    string Status,
    IReadOnlyList<BulkImportRowError> Errors,
    string? OwnerAction,
    string? OwnershipAction,
    IReadOnlyList<OwnerImportCandidate> Candidates,
    bool Applied);

public sealed record OwnerImportPropertyIssue(Guid PropertyId, IReadOnlyList<OwnershipIssue> Issues);

public sealed record OwnerImportSummary(
    int Rows,
    int Valid,
    int Invalid,
    int Applied,
    int OwnersToCreate,
    int OwnersMatched,
    int OwnershipsToCreate,
    int PeriodsToUpdate);

/// <summary>The answer of an owner import.</summary>
/// <param name="DryRun">True when nothing was written.</param>
/// <param name="Fingerprint">
/// SHA-256 of the org and of the normalized content of the file (not of its bytes: a re-saved sheet is the same import). The dry run and the
/// apply of the same file carry the same one, and it is the one the activity log keeps (its first 32 characters).
/// </param>
/// <param name="Summary">The counts.</param>
/// <param name="UnknownColumns">The header cells that name no column of the import (or repeat one), once each.</param>
/// <param name="Rows">One entry for each row of data.</param>
/// <param name="PropertyIssues">The properties whose shares would be wrong after the file, with the days.</param>
public sealed record OwnerImportResult(
    bool DryRun,
    string Fingerprint,
    OwnerImportSummary Summary,
    IReadOnlyList<string> UnknownColumns,
    IReadOnlyList<OwnerImportRow> Rows,
    IReadOnlyList<OwnerImportPropertyIssue> PropertyIssues);

public static class OwnerReadinessDimensions
{
    public const string Registry = "registry";
    public const string Ownership = "ownership";
    public const string Mandate = "mandate";
    public const string FiscalProfile = "fiscalProfile";
    public const string Payment = "payment";
    public const string Portal = "portal";

    public static IReadOnlyList<string> All { get; } = [Registry, Ownership, Mandate, FiscalProfile, Payment, Portal];
}

public static class OwnerReadinessReasons
{
    public const string LegalNameMissing = "legal_name_missing";
    public const string EntityKindMissing = "entity_kind_missing";

    public const string OwnershipMissing = "ownership_missing";
    public const string SharesGap = "shares_gap";
    public const string SharesShortfall = "shares_shortfall";
    public const string SharesExcess = "shares_excess";
    public const string OwnerOverlap = "owner_overlap";

    public const string MandateMissing = "mandate_missing";
    public const string MandateDocumentMissing = "mandate_document_missing";
    public const string MandateOutsideOwnership = "mandate_outside_ownership";

    public const string TaxResidenceMissing = "tax_residence_missing";
    public const string FiscalIdMissing = "fiscal_id_missing";
    public const string FiscalCodeInvalid = "fiscal_code_invalid";
    public const string VatNumberInvalid = "vat_number_invalid";
    public const string BillingAddressIncomplete = "billing_address_incomplete";

    public const string IbanMissing = "iban_missing";
    public const string IbanInvalid = "iban_invalid";
    public const string BicMissing = "bic_missing";
    public const string BicInvalid = "bic_invalid";
    public const string SettlementCurrencyMissing = "settlement_currency_missing";

    public const string EmailMissing = "email_missing";
    public const string EmailInvalid = "email_invalid";
    public const string RegistryNotReady = "registry_not_ready";
    public const string OwnershipNotReady = "ownership_not_ready";
}

public sealed record OwnerReadinessReason(string Dimension, string Code, string? Field = null, Guid? PropertyId = null);

public sealed record OwnerReadinessFlags(bool Registry, bool Ownership, bool Mandate, bool FiscalProfile, bool Payment, bool Portal);

public static class OwnerPortalStates
{
    public const string NotReady = "notReady";
    public const string Ready = "ready";
    public const string Invited = "invited";
    public const string Accepted = "accepted";
    public const string Expired = "expired";
    public const string Revoked = "revoked";
}

public sealed record OwnerReadinessOwner(
    Guid OwnerId,
    string? ExternalKey,
    string DisplayName,
    string? EntityKind,
    OwnerReadinessFlags Ready,
    bool AccountingReady,
    string PortalState,
    IReadOnlyList<OwnerReadinessReason> Reasons)
{
    /// <summary>
    /// What the agency should know about the owner that does not stop its accounting (E18/F01/US03/T01): a warning never changes <see cref="Ready"/> or
    /// <see cref="AccountingReady"/>. Empty unless <c>Features:TaxReportingExports</c> is on.
    /// </summary>
    public IReadOnlyList<OwnerReadinessWarning> Warnings { get; init; } = [];
}

public static class OwnerReadinessWarnings
{
    public const string ShortLetBusinessPresumed = "short_let_business_presumed";
}

/// <summary>What a warning suggests doing (cross-repo contract): never rename one.</summary>
public static class OwnerReadinessSuggestions
{
    /// <summary>Make the issuer profile of the owner (<c>api/owners/{id}/issuer-profile</c>) that of a business: a VAT regime and its VAT number.</summary>
    public const string IssuerProfile = "issuer_profile";
}

/// <summary>
/// One warning of an owner: its <paramref name="Code"/> (<see cref="OwnerReadinessWarnings"/>), the counts behind it (the apartments the agency manages, the ones the owner
/// declared, the limit) and what it <paramref name="Suggestion"/> to do (<see cref="OwnerReadinessSuggestions"/>), when it does. Counts and codes: never a value.
/// </summary>
public sealed record OwnerReadinessWarning(string Code, int ManagedUnits, int ExternalUnits, int MaxUnits, string? Suggestion);

public sealed record OwnerReadinessProperty(
    Guid PropertyId,
    string Name,
    bool OwnershipReady,
    IReadOnlyList<OwnerReadinessReason> Reasons,
    IReadOnlyList<OwnershipIssue> Issues);

public sealed record OwnerReadinessCount(int Ready, int ToComplete);

public sealed record OwnerReadinessSummary(
    int Owners,
    int Properties,
    OwnerReadinessCount Registry,
    OwnerReadinessCount Ownership,
    OwnerReadinessCount Mandate,
    OwnerReadinessCount FiscalProfile,
    OwnerReadinessCount Payment,
    OwnerReadinessCount Portal,
    int AccountingReadyOwners,
    int PropertiesWithoutOwner,
    int PropertiesToComplete,
    bool FinancialReady);

/// <summary>The answer of the readiness.</summary>
/// <param name="AsOf">Today in Rome: the day the periods in force and the mandates are judged on.</param>
/// <param name="Owners">The owners inside the reach, not ready first.</param>
/// <param name="Properties">The properties inside the reach whose ownership is not ready (no owner, or shares that are wrong).</param>
/// <param name="Truncated">True when a list was cut at <see cref="OwnerOnboardingLimits.MaxListed"/> rows (the counts of the summary are always complete).</param>
public sealed record OwnerReadinessReport(
    DateOnly AsOf,
    OwnerReadinessSummary Summary,
    IReadOnlyList<OwnerReadinessOwner> Owners,
    IReadOnlyList<OwnerReadinessProperty> Properties,
    bool Truncated);

public static class OwnerOnboardingLimits
{
    public const int MaxListed = 2_000;

    public const int MaxCandidates = 5;
}
