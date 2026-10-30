using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;

namespace Casazen.Core.Services;

/// <summary>Which people the tenants page lists. A person is <b>current</b> while at least one of their leases has not concluded.</summary>
public enum TenantListView
{
    /// <summary>Everybody (the default).</summary>
    All = 0,

    /// <summary>At least one lease that has not concluded (in preparation, in force or about to end).</summary>
    Current = 1,

    /// <summary>Every lease concluded (<see cref="LeaseStage.Concluded"/>, the end of the period they are in, LR-14).</summary>
    Former = 2,
}

/// <summary>What the tenants list is asked for: a view, a text looked for in the names and in the property, and a page.</summary>
public sealed record TenantListQuery(TenantListView View = TenantListView.All, string? Search = null, int Page = 1, int PageSize = TenantListQuery.DefaultPageSize)
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;

    /// <summary>Longest search text taken into account (a longer one is cut).</summary>
    public const int MaxSearchLength = 100;

    /// <summary>The search text trimmed and cut, or <c>null</c> when there is nothing to look for.</summary>
    public string? NormalizedSearch
    {
        get
        {
            var text = Search?.Trim();
            if (string.IsNullOrEmpty(text))
                return null;
            return text.Length > MaxSearchLength ? text[..MaxSearchLength].TrimEnd() : text;
        }
    }
}

/// <summary>Whether the tenant is a citizen of the Union; the citizenship itself is not part of the page (it is in the export).</summary>
public enum TenantCitizenship
{
    Eu,
    NonEu,
}

/// <summary>A lease the person is a tenant on: where, in which stage of the path, for how much, and when the period it is in ends.</summary>
public sealed record TenantLease(Guid LeaseId, Guid PropertyId, string PropertyName, LeaseStage? Stage, decimal MonthlyRent, DateTime CurrentTermEndDate);

/// <summary>Installments due so far (<c>Total</c>) and how many of them were paid within the grace period (<c>OnTime</c>).</summary>
public sealed record TenantPunctuality(int OnTime, int Total);

/// <summary>
/// One person of the tenants list. <c>Id</c> is opaque: the id of one of the person's party rows (never the fiscal code), which any of the
/// person's rows resolves to the same person. The e-mail is masked and the fiscal code is not part of the row (decision D29). A person on
/// several leases is one row (same normalized fiscal code, <see cref="LongRentTenantRules.PersonKey"/>), described by the lease they have
/// most recently started.
/// </summary>
public sealed record TenantRow(
    Guid Id,
    string FirstName,
    string LastName,
    TenantCitizenship Citizenship,
    string ContactEmailMasked,
    bool IsFormer,
    IReadOnlyList<TenantLease> Leases,
    TenantPunctuality Punctuality);

/// <summary>How many people each view of the list holds, for the search asked.</summary>
public sealed record TenantCounts(int All, int Current, int Former);

/// <summary>A page of the tenants list, ordered by last name then first name.</summary>
public sealed record TenantPage(IReadOnlyList<TenantRow> Items, int Total, int Page, int PageSize, TenantCounts Counts);

/// <summary>An installment of one of the person's leases, as the sheet lists it. <c>DaysOverdue</c> only while it is overdue.</summary>
public sealed record TenantPayment(
    Guid Id,
    Guid LeaseId,
    string PropertyName,
    DateOnly DueDate,
    decimal Amount,
    RentLedgerStatus Status,
    DateOnly? PaidOn,
    bool IsOverdue,
    int? DaysOverdue);

/// <summary>What is past due across the person's leases: how many installments, how much, and the oldest one (the month to look at).</summary>
public sealed record TenantOverdue(int Count, decimal Amount, Guid OldestInstallmentId, DateOnly OldestDueDate, int DaysOverdue);

/// <summary>
/// The sheet of a person: the row, the six latest installments due, what is overdue (null when nothing is) and the grace period the
/// punctuality used (<c>OnTimeDays</c>, <c>LongRentTenants:OnTimeDays</c>).
/// </summary>
public sealed record TenantDetail(TenantRow Tenant, IReadOnlyList<TenantPayment> Payments, TenantOverdue? Overdue, int OnTimeDays);

/// <summary>A lease of the person together with its property, as the controller needs it to authorize it.</summary>
public sealed record TenantLeaseRef(Guid LeaseId, Guid PropertyId);

// ─── The copy of the personal data of one person (art. 15 and 20 GDPR) ─────────────────────────────────────────────────────────────

/// <summary>The data the lease holds about the person, in clear: what the export is for. Never the data of another party.</summary>
public sealed record TenantExportParty(
    string FirstName,
    string LastName,
    string FiscalCode,
    string Citizenship,
    string ContactEmail,
    bool IsExtraEU,
    string? Residence,
    DateOnly? DocumentsVerifiedOn);

/// <summary>One installment of a lease of the person.</summary>
public sealed record TenantExportRent(
    DateOnly DueDate,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal Amount,
    RentLedgerStatus Status,
    DateOnly? PaidOn,
    RentPaymentChannel? PaidVia);

/// <summary>An event in the history of a lease: its type and when, never its payload.</summary>
public sealed record TenantExportEvent(LeaseEventType Type, DateTime OccurredAt);

/// <summary>A lease of the person: the terms, the property they live in, their own data as a party, the installments and the events.</summary>
public sealed record TenantExportLease(
    Guid LeaseId,
    string PropertyName,
    string PropertyAddress,
    string PropertyCity,
    LeaseStatus Status,
    LeaseContractType ContractType,
    DateTime StartDate,
    DateTime EndDate,
    DateTime CurrentTermEndDate,
    decimal MonthlyRent,
    decimal? SecurityDeposit,
    DateTime? NoticeOn,
    DateTime? TerminatedOn,
    bool ErasureRequested,
    TenantExportParty Party,
    IReadOnlyList<TenantExportRent> Rents,
    IReadOnlyList<TenantExportEvent> Events);

/// <summary>The copy of the person's data over the leases the caller may handle. <c>SchemaVersion</c> changes when a field changes meaning.</summary>
public sealed record TenantDataExport(int SchemaVersion, DateTime GeneratedAt, IReadOnlyList<TenantExportLease> Leases)
{
    public const int CurrentSchemaVersion = 1;
}

// ─── The erasure request ───────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// What the request did on one lease of the person, with the outcome of <see cref="ILeasePartyPrivacyService"/>: <c>Anonymized</c> now,
/// <c>PartiallyAnonymized</c> (the person is also on a lease that has not ended), or <c>Scheduled</c> (the lease has not ended: nothing is
/// removed before <c>AnonymizationFrom</c>, the day after it ends).
/// </summary>
public sealed record TenantErasureLease(Guid LeaseId, string PropertyName, LeaseErasureStatus Status, DateTime? AnonymizationFrom, int PartiesKept);

/// <summary>The erasure request of a person, lease by lease.</summary>
public sealed record TenantErasureResult(IReadOnlyList<TenantErasureLease> Leases);

/// <summary>
/// The tenants of the long-term area (LR-09): who lives in the landlord's properties, since when, for how much and how punctually, and the
/// two duties the law puts on the landlord about a person's data, the copy of them and their erasure. There is no tenant record: a person
/// is read from the parties of the leases (<see cref="LongRentTenantRules"/>). Every read is restricted in SQL to the caller's org and, unless
/// org-wide, to the properties they reach (<see cref="HostScope"/>); a person who has no lease in that reach does not exist for the caller.
/// </summary>
public interface ILongRentTenantService
{
    /// <summary>The people of the scope, filtered by the view and the search, one page at a time.</summary>
    Task<TenantPage> GetTenantsAsync(HostScope scope, TenantListQuery query, CancellationToken cancellationToken = default);

    /// <summary>The sheet of a person, or <c>null</c> when <paramref name="tenantId"/> is not a person the scope reaches.</summary>
    Task<TenantDetail?> GetTenantAsync(HostScope scope, Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>The leases of the person that the scope reaches, or <c>null</c> when there is no such person.</summary>
    Task<IReadOnlyList<TenantLeaseRef>?> FindLeasesAsync(HostScope scope, Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The copy of the person's data over their leases that are in <paramref name="allowedLeaseIds"/> (the ones the caller may handle), or
    /// <c>null</c> when there is no such person. Only the person's own party row of each lease: not the other parties, not the landlord.
    /// </summary>
    Task<TenantDataExport?> ExportAsync(HostScope scope, Guid tenantId, IReadOnlySet<Guid> allowedLeaseIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the erasure of the person's data on each of their leases in <paramref name="allowedLeaseIds"/>, with the rules the lease
    /// button already has: nothing is removed before the lease has ended, and a person still on a lease that has not ended keeps their data
    /// until that one ends. <c>null</c> when there is no such person.
    /// </summary>
    Task<TenantErasureResult?> RequestErasureAsync(HostScope scope, Guid tenantId, IReadOnlySet<Guid> allowedLeaseIds, CancellationToken cancellationToken = default);
}
