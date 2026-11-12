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
    /// Closes the owner's month on its last version, approved or published, reconciled to the paid wires of the month's statements. A difference or a
    /// negative balance needs <see cref="OwnerCloseCommand.WaiverReason"/> and <paramref name="mayWaive"/> (<c>agency.owner.financial.manage</c>): 422
    /// <c>owner_close_unreconciled</c> without a reason, 403 <c>owner_close_waiver_forbidden</c> without the permission. 422
    /// <c>owner_statement_not_approved</c>; 409 <c>owner_period_dirty</c> or <c>owner_period_closed</c>.
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
    public const string CloseWaiverForbidden = "owner_close_waiver_forbidden";
    public const string LegalHoldReasonRequired = "owner_legal_hold_reason_required";
    public const string ChargeInvalid = "owner_charge_invalid";
    public const string ChargeDuplicate = "owner_charge_duplicate";
    public const string ChargeNotFound = "owner_charge_not_found";
    public const string ChargeNotPending = "owner_charge_not_pending";
    public const string FxRateInvalid = "owner_fx_rate_invalid";
    public const string FxRateExists = "owner_fx_rate_exists";
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
