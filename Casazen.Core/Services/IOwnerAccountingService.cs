using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The owner accounting of an agency (E06/F05/US02/T02, NF-25, NF-110, ADR-017): the append-only owner sub-ledger posted from the payments, the channel
/// commissions, the mandate commissions, the approved charges and the paid wires; the statement versions generated from a ledger cut-off; the month-end
/// readiness, the approval and the publication (two distinct permissions); the final close reconciled to the paid wires; the retention and the legal hold.
/// </summary>
/// <remarks>
/// Every amount of a version is reproducible from the ledger lines (their hash is on the version), and every line carries its source, dates, share,
/// currencies with the rate, and rule version. A posted line is never changed: changed facts post a reversal and a replacement line. The service names
/// the org of everything it reads and writes; the web layer decides who may call it.
/// </remarks>
public interface IOwnerAccountingService
{
    /// <summary>
    /// Posts the ledger lines of the month from its sources (idempotent: the same facts post nothing; changed facts reverse the old line and post the new
    /// one) and marks dirty the months whose last version is older than a change. The months already closed of an owner are left as they are. 422
    /// <c>owner_period_invalid</c>.
    /// </summary>
    Task<OwnerPostingResult> PostPeriodAsync(Guid orgId, int year, int month, string? userId, CancellationToken ct = default);

    /// <summary>The month-end readiness of the org and of each owner with lines or a version in the month.</summary>
    Task<OwnerCloseReadiness> GetReadinessAsync(Guid orgId, int year, int month, CancellationToken ct = default);

    /// <summary>
    /// A new version of the owner's statement of the month from the ledger now (the cut-off): <c>Flagged</c> with blocking issues, else <c>Draft</c>; the
    /// version before is superseded. 409 <c>owner_period_closed</c>, 409 <c>owner_statement_unchanged</c> (the last version is approved or published and
    /// nothing changed after it), 404 for an owner of another org.
    /// </summary>
    Task<OwnerStatementVersion> GenerateVersionAsync(Guid orgId, Guid ownerId, int year, int month, string? userId, CancellationToken ct = default);

    /// <summary>The versions of the owner's month, the newest first.</summary>
    Task<IReadOnlyList<OwnerStatementVersion>> ListVersionsAsync(Guid orgId, Guid ownerId, int year, int month, CancellationToken ct = default);

    /// <summary>A version with the ledger lines it was made of (the drill-down to the sources); 404 <c>owner_statement_version_not_found</c>.</summary>
    Task<OwnerStatementVersionDetail> GetVersionAsync(Guid orgId, Guid versionId, CancellationToken ct = default);

    /// <summary>
    /// Approves a draft version (<c>agency.statement.manage</c>). 409 <c>owner_statement_not_draft</c> or <c>owner_period_dirty</c>; 422
    /// <c>owner_statement_flagged</c>.
    /// </summary>
    Task<OwnerStatementVersion> ApproveVersionAsync(Guid orgId, Guid versionId, string? userId, CancellationToken ct = default);

    /// <summary>
    /// Publishes an approved version (<c>agency.statement.publish</c>): its file (the JSON of the version and of its lines) and its SHA-256 are stored and never
    /// change. 422 <c>owner_statement_not_approved</c>; 409 <c>owner_period_dirty</c>.
    /// </summary>
    Task<OwnerStatementVersion> PublishVersionAsync(Guid orgId, Guid versionId, string? userId, CancellationToken ct = default);

    /// <summary>
    /// Closes the owner's month once it is over, on its last version, approved or published, reconciled to the confirmed payouts of the month (or, when it has
    /// none, the paid wires of its statements). The readiness is read again: a blocking issue (a source not posted, a gap of the shares, a rate missing) is 422
    /// <c>owner_close_not_ready</c> with the <c>issues</c>. A difference or a negative balance needs <see cref="OwnerCloseCommand.WaiverReason"/> and
    /// <paramref name="mayWaive"/> (<c>agency.owner.financial.manage</c>): 422 <c>owner_close_unreconciled</c> without a reason, 403
    /// <c>owner_close_waiver_forbidden</c> without the permission. 422 <c>owner_statement_not_approved</c>; 409 <c>owner_period_dirty</c>,
    /// <c>owner_period_closed</c> or <c>owner_period_not_ended</c> (the month is not over).
    /// </summary>
    Task<OwnerClosePeriod> CloseAsync(Guid orgId, Guid ownerId, int year, int month, OwnerCloseCommand command, bool mayWaive, string? userId, CancellationToken ct = default);

    /// <summary>Sets or lifts the legal hold of the owner's month; setting it needs a reason (422 <c>owner_legal_hold_reason_required</c>).</summary>
    Task<OwnerClosePeriod> SetLegalHoldAsync(Guid orgId, Guid ownerId, int year, int month, bool hold, string? reason, string? userId, CancellationToken ct = default);

    /// <summary>The evidence of the owner's month: the close, every version with its published file, the ledger lines and the charges they come from.</summary>
    Task<OwnerAccountingEvidence> ExportEvidenceAsync(Guid orgId, Guid ownerId, int year, int month, CancellationToken ct = default);

    /// <summary>A charge or an expense, pending approval. 422 <c>owner_charge_invalid</c> (with the <c>field</c>); 409 <c>owner_charge_duplicate</c>.</summary>
    Task<OwnerCharge> CreateChargeAsync(Guid orgId, OwnerChargeCommand command, string? userId, CancellationToken ct = default);

    /// <summary>Approves a pending charge: it is posted with its months. 409 <c>owner_charge_not_pending</c>.</summary>
    Task<OwnerCharge> ApproveChargeAsync(Guid orgId, Guid chargeId, string? userId, CancellationToken ct = default);

    /// <summary>Cancels a charge: what it posted in the open months is reversed. 409 <c>owner_charge_not_pending</c> when already cancelled.</summary>
    Task<OwnerCharge> CancelChargeAsync(Guid orgId, Guid chargeId, string? userId, CancellationToken ct = default);

    /// <summary>The charges of the org, or of one property, the newest first.</summary>
    Task<IReadOnlyList<OwnerCharge>> ListChargesAsync(Guid orgId, Guid? propertyId, CancellationToken ct = default);

    /// <summary>An exchange rate for the owner accounting. 422 <c>owner_fx_rate_invalid</c> (with the <c>field</c>); 409 <c>owner_fx_rate_exists</c>.</summary>
    Task<OwnerFxRate> AddFxRateAsync(Guid orgId, OwnerFxRateCommand command, string? userId, CancellationToken ct = default);

    /// <summary>The exchange rates of the org, the newest first.</summary>
    Task<IReadOnlyList<OwnerFxRate>> ListFxRatesAsync(Guid orgId, CancellationToken ct = default);

    /// <summary>
    /// A manual correction with its reason: the line it replaces (if any) is reversed, then the correction is posted. 422 <c>owner_correction_invalid</c>;
    /// 409 <c>owner_ledger_entry_already_reversed</c>; 404 <c>owner_ledger_entry_not_found</c>.
    /// </summary>
    Task<OwnerLedgerEntry> PostCorrectionAsync(Guid orgId, OwnerCorrectionCommand command, string? userId, CancellationToken ct = default);

    /// <summary>
    /// Links an approved or published version to the invoice the agency's external system issued (E06/F01/US02/T05): the link has no amount, VAT, number
    /// or submission state of CasaZen and changes nothing of the version. 422 <c>owner_external_document_invalid</c> (with the <c>field</c>),
    /// <c>owner_statement_not_approved</c>; 409 <c>owner_external_document_duplicate</c>.
    /// </summary>
    Task<OwnerExternalDocumentReference> AddExternalDocumentAsync(
        Guid orgId,
        Guid versionId,
        OwnerExternalDocumentCommand command,
        string? userId,
        CancellationToken ct = default);

    /// <summary>Records that a person with the finance permission checked the link against the external system. 409 <c>owner_external_document_removed</c>.</summary>
    Task<OwnerExternalDocumentReference> VerifyExternalDocumentAsync(Guid orgId, Guid referenceId, string? userId, CancellationToken ct = default);

    /// <summary>Removes a mistaken link with its reason: the row, its author and time stay. 409 <c>owner_external_document_removed</c>.</summary>
    Task<OwnerExternalDocumentReference> RemoveExternalDocumentAsync(Guid orgId, Guid referenceId, string? reason, string? userId, CancellationToken ct = default);

    /// <summary>The links of a version, the removed ones included.</summary>
    Task<IReadOnlyList<OwnerExternalDocumentReference>> ListExternalDocumentsAsync(Guid orgId, Guid versionId, CancellationToken ct = default);

    /// <summary>The links an owner of the portal may read: of its own published versions, not removed.</summary>
    Task<IReadOnlyList<OwnerExternalDocumentReference>> ListPublishedExternalDocumentsForOwnerUserAsync(Guid ownerUserId, CancellationToken ct = default);

    /// <summary>
    /// Posts the VAT an owner invoice added on top (<see cref="OwnerLedgerEntryType.AgencyVat"/>, a debit of <paramref name="vat"/>, E14/F01/US01/T01, ⚖ R4) in
    /// the transaction of its issue: once (a second call finds the line and posts nothing), in the month of the invoice, or in the month of today when that
    /// month of the owner is closed; the month is marked dirty. Nothing for a VAT of 0. Returns the month posted in, or <c>null</c>.
    /// </summary>
    Task<OwnerLedgerPosting?> PostOwnerInvoiceVatAsync(FiscalDocument invoice, decimal vat, string? userId, CancellationToken ct = default);

    /// <summary>
    /// Posts the reversal of the VAT of an owner invoice for a credit note of it, in the transaction of the credit note: the reversal of the whole line when the
    /// note credits the whole invoice at once, else the VAT of the note (the rest of the VAT with the note that completes the credit). Nothing when the invoice
    /// posted no VAT (a mandate «IVA inclusa», a rate of 0). Returns the month posted in, or <c>null</c>.
    /// </summary>
    Task<OwnerLedgerPosting?> ReverseOwnerInvoiceVatAsync(FiscalDocument creditNote, FiscalDocument invoice, string? userId, CancellationToken ct = default);

    /// <summary>
    /// Generates again the statement version of the owner's month when its current version is a draft or flagged (an owner invoice posted a line in it); an
    /// approved or published version is left as it is: its month is dirty and the readiness says <c>owner_invoice_after_approval</c>, so the agency generates
    /// it again. Returns the new version, or <c>null</c> when none was generated.
    /// </summary>
    Task<OwnerStatementVersion?> RegenerateOpenVersionAsync(Guid orgId, Guid ownerId, int year, int month, string? userId, CancellationToken ct = default);
}

/// <summary>The month of an owner a line of an owner invoice was posted in (E14/F01/US01/T01).</summary>
public sealed record OwnerLedgerPosting(Guid OwnerId, int Year, int Month);

/// <summary>A link to an invoice issued by the agency's external fiscal or accounting system.</summary>
public sealed record OwnerExternalDocumentCommand
{
    public string? Provider { get; init; }

    public string? ExternalId { get; init; }

    public string? ExternalNumber { get; init; }

    public DateOnly ExternalDate { get; init; }

    /// <summary>Where the issued document is (https), optional.</summary>
    public string? DocumentUrl { get; init; }

    /// <summary>The SHA-256 (hex) of the issued document, optional.</summary>
    public string? DocumentSha256 { get; init; }
}

/// <summary>An issue of the month-end readiness: a stable code, what it is about, and whether it blocks the approval and the close.</summary>
public sealed record OwnerReadinessIssue(string Code, Guid? OwnerId = null, Guid? PropertyId = null, Guid? SourceId = null, bool Blocking = true);

/// <summary>What a posting did: the lines posted, the lines reversed, the lines already there, and the issues that kept sources out.</summary>
public sealed record OwnerPostingResult(int Year, int Month, int Posted, int Reversed, int Unchanged, IReadOnlyList<OwnerReadinessIssue> Issues);

/// <summary>The readiness of one owner's month.</summary>
public sealed record OwnerPeriodReadiness(
    Guid OwnerId,
    OwnerClosePeriodStatus Status,
    bool IsDirty,
    int? LatestVersion,
    OwnerStatementVersionStatus? LatestStatus,
    IReadOnlyList<OwnerReadinessIssue> Issues)
{
    public bool Ready => Issues.All(i => !i.Blocking);
}

/// <summary>The month-end readiness: the issues of the org (sources without owners) and of each owner.</summary>
public sealed record OwnerCloseReadiness(int Year, int Month, IReadOnlyList<OwnerReadinessIssue> Issues, IReadOnlyList<OwnerPeriodReadiness> Owners);

/// <summary>A version with its ledger lines.</summary>
public sealed record OwnerStatementVersionDetail(OwnerStatementVersion Version, IReadOnlyList<OwnerLedgerEntry> Entries);

/// <summary>The evidence of an owner's month.</summary>
public sealed record OwnerAccountingEvidence(
    Guid OwnerId,
    int Year,
    int Month,
    OwnerClosePeriod? Period,
    IReadOnlyList<OwnerStatementVersion> Versions,
    IReadOnlyList<OwnerLedgerEntry> Entries,
    IReadOnlyList<OwnerCharge> Charges);

/// <summary>The close of a month: the reason of the waiver when the balance does not reconcile.</summary>
public sealed record OwnerCloseCommand
{
    public string? WaiverReason { get; init; }
}

/// <summary>A new charge or expense.</summary>
public sealed record OwnerChargeCommand
{
    public Guid PropertyId { get; init; }

    /// <summary>The one owner; <c>null</c> = split by the shares in force.</summary>
    public Guid? OwnerId { get; init; }

    public OwnerChargeKind Kind { get; init; }

    public OwnerChargeRecurrence Recurrence { get; init; }

    public string? Description { get; init; }

    public decimal Amount { get; init; }

    public string? Currency { get; init; }

    public DateOnly EffectiveFrom { get; init; }

    public DateOnly? EffectiveTo { get; init; }

    public string? AttachmentReference { get; init; }
}

/// <summary>A new exchange rate: one <see cref="BaseCurrency"/> is <see cref="Rate"/> <see cref="QuoteCurrency"/> on <see cref="RateDate"/>.</summary>
public sealed record OwnerFxRateCommand
{
    public string? BaseCurrency { get; init; }

    public string? QuoteCurrency { get; init; }

    public DateOnly RateDate { get; init; }

    public decimal Rate { get; init; }

    public string? Source { get; init; }
}

/// <summary>A manual correction of an owner's ledger, signed (positive is owed to the owner), with its reason.</summary>
public sealed record OwnerCorrectionCommand
{
    public Guid OwnerId { get; init; }

    public Guid? PropertyId { get; init; }

    public decimal Amount { get; init; }

    public string? Currency { get; init; }

    public DateOnly Date { get; init; }

    public string? Description { get; init; }

    /// <summary>The line it replaces: reversed first.</summary>
    public Guid? ReplacesEntryId { get; init; }

    public string? AttachmentReference { get; init; }
}

/// <summary>The stable codes of the owner accounting.</summary>
public static class OwnerAccountingErrors
{
    public const string PeriodInvalid = "owner_period_invalid";
    public const string PeriodClosed = "owner_period_closed";
    public const string PeriodDirty = "owner_period_dirty";
    public const string LedgerImmutable = "owner_ledger_immutable";
    public const string EntryNotFound = "owner_ledger_entry_not_found";
    public const string EntryAlreadyReversed = "owner_ledger_entry_already_reversed";
    public const string CorrectionInvalid = "owner_correction_invalid";
    public const string VersionNotFound = "owner_statement_version_not_found";
    public const string VersionUnchanged = "owner_statement_unchanged";
    public const string VersionFlagged = "owner_statement_flagged";
    public const string VersionNotDraft = "owner_statement_not_draft";
    public const string VersionNotApproved = "owner_statement_not_approved";
    public const string VersionNotPublished = "owner_statement_not_published";
    public const string CloseUnreconciled = "owner_close_unreconciled";
    public const string CloseNotReady = "owner_close_not_ready";
    public const string PeriodNotEnded = "owner_period_not_ended";
    public const string CloseWaiverForbidden = "owner_close_waiver_forbidden";
    public const string LegalHoldReasonRequired = "owner_legal_hold_reason_required";
    public const string ChargeInvalid = "owner_charge_invalid";
    public const string ChargeDuplicate = "owner_charge_duplicate";
    public const string ChargeNotFound = "owner_charge_not_found";
    public const string ChargeNotPending = "owner_charge_not_pending";
    public const string FxRateInvalid = "owner_fx_rate_invalid";
    public const string FxRateExists = "owner_fx_rate_exists";
    public const string ExternalDocumentInvalid = "owner_external_document_invalid";
    public const string ExternalDocumentDuplicate = "owner_external_document_duplicate";
    public const string ExternalDocumentNotFound = "owner_external_document_not_found";
    public const string ExternalDocumentRemoved = "owner_external_document_removed";
}

/// <summary>The codes of the month-end readiness issues (besides the codes of the fiscal profile, <c>owner_fiscal_*</c>).</summary>
public static class OwnerReadinessCodes
{
    public const string OwnershipMissing = "ownership_missing";
    public const string OwnershipShareGap = "ownership_share_gap";
    public const string FxMissing = "fx_missing";
    public const string CurrencyMixed = "currency_mixed";
    public const string SourceAmountMissing = "source_amount_missing";
    public const string UnpostedSource = "unposted_source";
    public const string DuplicateEntry = "duplicate_entry";
    public const string LedgerStatementImbalance = "ledger_statement_imbalance";
    public const string PeriodDirty = "period_dirty";
    public const string PeriodClosed = "period_closed";
    public const string StatementMissing = "statement_missing";
    public const string StatementNotApproved = "statement_not_approved";
    public const string NegativeBalance = "negative_balance";

    /// <summary>A source of a closed month that the ledger does not hold (it arrived or changed after the close): told, never posted (not blocking).</summary>
    public const string LateSourceAfterClose = "late_source_after_close";

    /// <summary>
    /// A stay of an owner with no mandate in force on its day (E13/F01/US01/T02): its revenue is posted, no commission and no flat fee is, and the version is
    /// held back (blocking) until a mandate covers the day: never a commission of zero by default.
    /// </summary>
    public const string MandateMissingForStay = "mandate_missing_for_stay";

    /// <summary>
    /// A stay of a portal whose reservation brings no commission, with no rule of the agency in force at the check-out and no configured default for the
    /// channel (E13/F01/US01/T02): the fee is not guessed, the owners of the property are held back (blocking) until a rule says it.
    /// </summary>
    public const string ChannelCommissionMissing = "channel_commission_missing";

    /// <summary>
    /// An owner invoice was issued after the version of the month was approved or published and posted its VAT in the month (E14/F01/US01/T01): the version is no
    /// longer the picture of the ledger and the agency generates it again (blocking, with the invoice as the source).
    /// </summary>
    public const string OwnerInvoiceAfterApproval = "owner_invoice_after_approval";
}

/// <summary>The kinds of the flat fees of a mandate, as the <see cref="OwnerLedgerEntry.SourceReference"/> of their lines (E13/F01/US01/T02).</summary>
public static class OwnerLedgerFlatFees
{
    /// <summary>The cleaning of a stay (<see cref="OwnerMandate.CleaningFlatRate"/>).</summary>
    public const string Cleaning = "cleaning";

    /// <summary>The linen of a stay (<see cref="OwnerMandate.LinenFlatRate"/>).</summary>
    public const string Linen = "linen";
}

/// <summary>
/// The money rules of the posting of a stay (E13/F01/US01/T02, NF-115), pure: the revenue of a payment without the tourist tax, the gross of a stay posted
/// from its booking and the base a commission is a percentage of. Every amount is rounded to the cent half to even, as the rest of the owner ledger.
/// </summary>
public static class OwnerLedgerRules
{
    /// <summary>
    /// The revenue of a collected payment for the owners: what it collected net of its refunds (<paramref name="collected"/>) less its part of the tourist
    /// tax of the stay, in proportion to the part of the total of the stay it covers (<paramref name="touristTax"/> / <paramref name="stayTotal"/>, which
    /// includes the tax): a deposit, a balance and a partial refund each keep their own part. The part of the tax is rounded half to even and never more
    /// than the tax or the payment. Without a tax or a total the whole amount is revenue.
    /// </summary>
    public static decimal RevenueOfPayment(decimal collected, decimal touristTax, decimal stayTotal)
    {
        if (collected <= 0m)
            return 0m;
        if (touristTax <= 0m || stayTotal <= 0m)
            return collected;

        var tax = Math.Round(collected * touristTax / stayTotal, 2, MidpointRounding.ToEven);
        return collected - Math.Min(tax, Math.Min(touristTax, collected));
    }

    /// <summary>
    /// The part of <paramref name="amount"/> (the gross of a stay without the tourist tax, or the revenue a payment brought of it) a commission of
    /// <paramref name="basis"/> is a percentage of: all of it, or without the share of the cleaning of the stay (<paramref name="stayCleaning"/> /
    /// <paramref name="stayGross"/>, rounded half to even). The tourist tax is never in it.
    /// </summary>
    public static decimal CommissionBaseOf(CommissionBase basis, decimal amount, decimal stayGross, decimal stayCleaning)
    {
        if (basis != CommissionBase.GrossExclTouristTaxAndCleaning || amount <= 0m || stayGross <= 0m || stayCleaning <= 0m)
            return amount;

        var cleaning = Math.Min(stayCleaning, stayGross);
        return amount - Math.Round(amount * cleaning / stayGross, 2, MidpointRounding.ToEven);
    }

    /// <summary>
    /// The gross, without the tourist tax, of a stay posted from its booking (no collected payment). For a booking of a portal
    /// (<paramref name="fromPortal"/>) it is the total of the reservation less the tourist tax: the room, the cleaning and the extras the portal charged, which
    /// the booking does not keep apart (<c>BasePrice</c> of an imported reservation is its room only, <c>CleaningFee</c> stays 0). It is a prudent choice to be
    /// checked on the first real payload of the channel manager, and never less than the room (<paramref name="basePrice"/>): a total that leaves the tax out
    /// is not taxed twice. Any other booking is priced by CasaZen: its <c>BasePrice</c> already holds the cleaning (PC-07) and it has no extras of its own.
    /// </summary>
    public static decimal GrossOfStay(bool fromPortal, decimal basePrice, decimal totalPrice, decimal touristTax)
    {
        if (!fromPortal || totalPrice <= 0m)
            return basePrice;

        return Math.Max(basePrice, totalPrice - Math.Max(touristTax, 0m));
    }
}

/// <summary>
/// The retention of the evidence of a closed month (E06/F05/US02/T02, AC-NF110-8): the years of the approved rule set of the owner when it says more,
/// else the floor of the jurisdiction (Italy 10 years, Switzerland 20 for the evidence of immovable property, any other 10), counted from the end of the
/// close year. A legal hold stops the expiry.
/// </summary>
public static class OwnerRetention
{
    public const int DefaultYears = 10;

    private static readonly IReadOnlyDictionary<string, int> FloorByJurisdiction = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["IT"] = 10,
        ["CH"] = 20,
    };

    /// <summary>The years the evidence is kept: the larger of the floor of the jurisdiction and the years of the rule set.</summary>
    public static int YearsFor(string? jurisdiction, int? ruleYears)
    {
        var floor = jurisdiction is not null && FloorByJurisdiction.TryGetValue(jurisdiction, out var years) ? years : DefaultYears;
        return Math.Max(floor, ruleYears ?? 0);
    }

    /// <summary>31 December of the year of <paramref name="closedOn"/> plus <paramref name="years"/>.</summary>
    public static DateOnly RetainUntil(DateOnly closedOn, int years) => new(closedOn.Year + years, 12, 31);

    /// <summary>Whether the evidence of the month may be deleted on <paramref name="today"/>: closed, past its retention, and not on legal hold.</summary>
    public static bool MayPurge(OwnerClosePeriod period, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(period);
        return period.Status == OwnerClosePeriodStatus.Closed && !period.LegalHold && period.RetainUntil is { } until && until < today;
    }
}
