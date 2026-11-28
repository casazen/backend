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

    Task<OwnerCloseReadiness> GetReadinessAsync(Guid orgId, int year, int month, CancellationToken ct = default);

    Task<OwnerStatementVersion> GenerateVersionAsync(Guid orgId, Guid ownerId, int year, int month, string? userId, CancellationToken ct = default);

    Task<IReadOnlyList<OwnerStatementVersion>> ListVersionsAsync(Guid orgId, Guid ownerId, int year, int month, CancellationToken ct = default);

    Task<OwnerStatementVersionDetail> GetVersionAsync(Guid orgId, Guid versionId, CancellationToken ct = default);

    Task<OwnerStatementVersion> ApproveVersionAsync(Guid orgId, Guid versionId, string? userId, CancellationToken ct = default);

    /// <summary>
    /// Publishes an approved version (<c>agency.statement.publish</c>): its file (the JSON of the version and of its lines) and its SHA-256 are stored and never
    /// change, and so is its PDF, an owner document of type statement stored in the same save (E13/F01/US02/T01); the owner is told by e-mail after it
    /// (E13/F01/US02/T02, no amount in it). 422 <c>owner_statement_not_approved</c>; 409 <c>owner_period_dirty</c>.
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

    Task<OwnerClosePeriod> SetLegalHoldAsync(Guid orgId, Guid ownerId, int year, int month, bool hold, string? reason, string? userId, CancellationToken ct = default);

    Task<OwnerAccountingEvidence> ExportEvidenceAsync(Guid orgId, Guid ownerId, int year, int month, CancellationToken ct = default);

    Task<OwnerCharge> CreateChargeAsync(Guid orgId, OwnerChargeCommand command, string? userId, CancellationToken ct = default);

    Task<OwnerCharge> ApproveChargeAsync(Guid orgId, Guid chargeId, string? userId, CancellationToken ct = default);

    Task<OwnerCharge> CancelChargeAsync(Guid orgId, Guid chargeId, string? userId, CancellationToken ct = default);

    Task<IReadOnlyList<OwnerCharge>> ListChargesAsync(Guid orgId, Guid? propertyId, CancellationToken ct = default);

    Task<OwnerFxRate> AddFxRateAsync(Guid orgId, OwnerFxRateCommand command, string? userId, CancellationToken ct = default);

    Task<IReadOnlyList<OwnerFxRate>> ListFxRatesAsync(Guid orgId, CancellationToken ct = default);

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

    Task<OwnerExternalDocumentReference> VerifyExternalDocumentAsync(Guid orgId, Guid referenceId, string? userId, CancellationToken ct = default);

    Task<OwnerExternalDocumentReference> RemoveExternalDocumentAsync(Guid orgId, Guid referenceId, string? reason, string? userId, CancellationToken ct = default);

    Task<IReadOnlyList<OwnerExternalDocumentReference>> ListExternalDocumentsAsync(Guid orgId, Guid versionId, CancellationToken ct = default);

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

    Task<OwnerStatementVersion?> RegenerateOpenVersionAsync(Guid orgId, Guid ownerId, int year, int month, string? userId, CancellationToken ct = default);

    /// <summary>
    /// The owners one step of the month-end runs for (E13/F01/US03/T01), the one place that checks a bulk request before anything is done: the ones listed, each once in
    /// the order given, or, with no list, every owner the readiness of the month knows (a line, a source, a version or a close in it), by id. 422
    /// <c>owner_bulk_action_invalid</c>, <c>owner_bulk_owners_empty</c> (an empty list is never «everyone»), <c>owner_bulk_too_many_owners</c>,
    /// <c>owner_period_invalid</c>; 409 <c>owner_period_not_ended</c> for a close of a month that is not over.
    /// </summary>
    Task<IReadOnlyList<Guid>> ResolveBulkOwnersAsync(Guid orgId, int year, int month, OwnerBulkAction action, IReadOnlyList<Guid>? ownerIds, CancellationToken ct = default);

    /// <summary>
    /// Runs one step of the month-end for every owner of <paramref name="ownerIds"/> (E13/F01/US03/T01): the posting of the month, the generation, the approval, the
    /// publication or the close of the version, each through the very operation of one owner, with the same rules and the same refusals; the permission of the step is
    /// checked by the web layer. Each owner is its own transaction and its own result (<see cref="OwnerBulkOwnerResult"/>): an owner that fails leaves nothing behind
    /// and never stops the others, and a run never reads as «all done» while one failed. Idempotent: an owner already in the state the step leads to (a version
    /// already approved, a month already closed; a month closed on a version that is only approved is still published) is an <see cref="OwnerBulkOwnerResult.Unchanged"/> result and
    /// changes nothing. The posting is one transaction of the
    /// whole org (a ledger is posted from the sources of the month under one lock) and answers each owner by what it kept out of it. A flagged version is never approved;
    /// the close never waives a difference. <paramref name="progress"/> is told after each owner with the results so far.
    /// </summary>
    Task<IReadOnlyList<OwnerBulkOwnerResult>> RunBulkAsync(
        Guid orgId,
        int year,
        int month,
        OwnerBulkAction action,
        IReadOnlyList<Guid> ownerIds,
        string? userId,
        OwnerBulkProgress? progress = null,
        CancellationToken ct = default);
}

public enum OwnerBulkAction
{
    Post = 1,

    Generate = 2,

    /// <summary>Approves the current version, unless it is flagged.</summary>
    Approve = 3,

    Publish = 4,

    Close = 5,
}

/// <summary>
/// What a bulk step did for one owner (E13/F01/US03/T01). <see cref="Ok"/> is the truth: the owner is in the state the step leads to (done now, or already so:
/// <see cref="Unchanged"/>). <see cref="Code"/> is the stable code of the reason: why the step was not done (<c>ok</c> false), or a note on an <c>ok</c> row (a
/// closed month, a version generated <c>owner_statement_flagged</c>). <see cref="Issues"/> are the blocking issue codes behind it, <see cref="Details"/> the facts a
/// person needs to act on it (<c>due</c>, <c>paid</c>, <c>difference</c> of a close that does not reconcile). Never a name, an amount of the ledger or an address.
/// </summary>
public sealed record OwnerBulkOwnerResult(
    Guid OwnerId,
    bool Ok,
    string? Code = null,
    bool Unchanged = false,
    IReadOnlyList<string>? Issues = null,
    IReadOnlyDictionary<string, object?>? Details = null);

public delegate Task OwnerBulkProgress(IReadOnlyList<OwnerBulkOwnerResult> done, CancellationToken ct);

public static class OwnerBulk
{
    public const int SyncMaxOwners = 20;

    public const int MaxListedOwners = 5_000;
}

public sealed record OwnerLedgerPosting(Guid OwnerId, int Year, int Month);

public sealed record OwnerExternalDocumentCommand
{
    public string? Provider { get; init; }

    public string? ExternalId { get; init; }

    public string? ExternalNumber { get; init; }

    public DateOnly ExternalDate { get; init; }

    public string? DocumentUrl { get; init; }

    public string? DocumentSha256 { get; init; }
}

public sealed record OwnerReadinessIssue(string Code, Guid? OwnerId = null, Guid? PropertyId = null, Guid? SourceId = null, bool Blocking = true);

public sealed record OwnerPostingResult(int Year, int Month, int Posted, int Reversed, int Unchanged, IReadOnlyList<OwnerReadinessIssue> Issues);

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

public sealed record OwnerCloseReadiness(int Year, int Month, IReadOnlyList<OwnerReadinessIssue> Issues, IReadOnlyList<OwnerPeriodReadiness> Owners);

public sealed record OwnerStatementVersionDetail(OwnerStatementVersion Version, IReadOnlyList<OwnerLedgerEntry> Entries);

public sealed record OwnerAccountingEvidence(
    Guid OwnerId,
    int Year,
    int Month,
    OwnerClosePeriod? Period,
    IReadOnlyList<OwnerStatementVersion> Versions,
    IReadOnlyList<OwnerLedgerEntry> Entries,
    IReadOnlyList<OwnerCharge> Charges);

public sealed record OwnerCloseCommand
{
    public string? WaiverReason { get; init; }
}

public sealed record OwnerChargeCommand
{
    public Guid PropertyId { get; init; }

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

public sealed record OwnerFxRateCommand
{
    public string? BaseCurrency { get; init; }

    public string? QuoteCurrency { get; init; }

    public DateOnly RateDate { get; init; }

    public decimal Rate { get; init; }

    public string? Source { get; init; }
}

public sealed record OwnerCorrectionCommand
{
    public Guid OwnerId { get; init; }

    public Guid? PropertyId { get; init; }

    public decimal Amount { get; init; }

    public string? Currency { get; init; }

    public DateOnly Date { get; init; }

    public string? Description { get; init; }

    public Guid? ReplacesEntryId { get; init; }

    public string? AttachmentReference { get; init; }
}

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
    public const string StatementCurrencyConflict = "owner_statement_currency_conflict";
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

    public const string BulkActionInvalid = "owner_bulk_action_invalid";
    public const string BulkOwnersEmpty = "owner_bulk_owners_empty";
    public const string BulkTooManyOwners = "owner_bulk_too_many_owners";
    public const string BulkJobNotFound = "owner_bulk_job_not_found";

    public const string BulkFailed = "owner_bulk_failed";
}

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

    public const string LateSourceAfterClose = "late_source_after_close";

    public const string MandateMissingForStay = "mandate_missing_for_stay";

    public const string ChannelCommissionMissing = "channel_commission_missing";

    /// <summary>
    /// An owner invoice was issued after the version of the month was approved or published and posted its VAT in the month (E14/F01/US01/T01): the version is no
    /// longer the picture of the ledger and the agency generates it again (blocking, with the invoice as the source).
    /// </summary>
    public const string OwnerInvoiceAfterApproval = "owner_invoice_after_approval";

    public const string ShortLetBusinessPresumed = "short_let_business_presumed";
}

public static class OwnerLedgerFlatFees
{
    public const string Cleaning = "cleaning";

    public const string Linen = "linen";
}

public static class OwnerLedgerCleaningRecharges
{
    public const int ReferenceLength = 8;

    public static string ReferenceOf(Guid requestId) => requestId.ToString("N")[..ReferenceLength].ToUpperInvariant();
}

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

public static class OwnerLedgerBalance
{
    public static bool Counts(OwnerLedgerEntryType type) => type != OwnerLedgerEntryType.ChannelFeeBorneByAgency;

    public static bool IsChannelFee(OwnerLedgerEntryType type) => type is OwnerLedgerEntryType.ChannelFee or OwnerLedgerEntryType.ChannelFeeBorneByAgency;

    public static IQueryable<OwnerLedgerEntry> CountingInBalance(this IQueryable<OwnerLedgerEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries.Where(e => e.EntryType != OwnerLedgerEntryType.ChannelFeeBorneByAgency);
    }
}

public static class OwnerRetention
{
    public const int DefaultYears = 10;

    private static readonly IReadOnlyDictionary<string, int> FloorByJurisdiction = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["IT"] = 10,
        ["CH"] = 20,
    };

    public static int YearsFor(string? jurisdiction, int? ruleYears)
    {
        var floor = jurisdiction is not null && FloorByJurisdiction.TryGetValue(jurisdiction, out var years) ? years : DefaultYears;
        return Math.Max(floor, ruleYears ?? 0);
    }

    public static DateOnly RetainUntil(DateOnly closedOn, int years) => new(closedOn.Year + years, 12, 31);

    public static bool MayPurge(OwnerClosePeriod period, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(period);
        return period.Status == OwnerClosePeriodStatus.Closed && !period.LegalHold && period.RetainUntil is { } until && until < today;
    }
}
