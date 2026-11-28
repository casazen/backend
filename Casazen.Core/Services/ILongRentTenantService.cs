using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;

namespace Casazen.Core.Services;

public enum TenantListView
{
    All = 0,

    Current = 1,

    Former = 2,
}

public sealed record TenantListQuery(TenantListView View = TenantListView.All, string? Search = null, int Page = 1, int PageSize = TenantListQuery.DefaultPageSize)
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;

    public const int MaxSearchLength = 100;

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

public enum TenantCitizenship
{
    Eu,
    NonEu,
}

public sealed record TenantLease(Guid LeaseId, Guid PropertyId, string PropertyName, LeaseStage? Stage, decimal MonthlyRent, DateTime CurrentTermEndDate);

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

public sealed record TenantCounts(int All, int Current, int Former);

public sealed record TenantPage(IReadOnlyList<TenantRow> Items, int Total, int Page, int PageSize, TenantCounts Counts);

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

public sealed record TenantOverdue(int Count, decimal Amount, Guid OldestInstallmentId, DateOnly OldestDueDate, int DaysOverdue);

public sealed record TenantDetail(TenantRow Tenant, IReadOnlyList<TenantPayment> Payments, TenantOverdue? Overdue, int OnTimeDays);

public sealed record TenantLeaseRef(Guid LeaseId, Guid PropertyId);

// ─── The copy of the personal data of one person (art. 15 and 20 GDPR) ─────────────────────────────────────────────────────────────

public sealed record TenantExportParty(
    string FirstName,
    string LastName,
    string FiscalCode,
    string Citizenship,
    string ContactEmail,
    bool IsExtraEU,
    string? Residence,
    DateOnly? DocumentsVerifiedOn);

public sealed record TenantExportRent(
    DateOnly DueDate,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal Amount,
    RentLedgerStatus Status,
    DateOnly? PaidOn,
    RentPaymentChannel? PaidVia);

public sealed record TenantExportEvent(LeaseEventType Type, DateTime OccurredAt);

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

public sealed record TenantDataExport(int SchemaVersion, DateTime GeneratedAt, IReadOnlyList<TenantExportLease> Leases)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record TenantErasureLease(Guid LeaseId, string PropertyName, LeaseErasureStatus Status, DateTime? AnonymizationFrom, int PartiesKept);

public sealed record TenantErasureResult(IReadOnlyList<TenantErasureLease> Leases);

/// <summary>
/// The tenants of the long-term area (LR-09): who lives in the landlord's properties, since when, for how much and how punctually, and the
/// two duties the law puts on the landlord about a person's data, the copy of them and their erasure. There is no tenant record: a person
/// is read from the parties of the leases (<see cref="LongRentTenantRules"/>). Every read is restricted in SQL to the caller's org and, unless
/// org-wide, to the properties they reach (<see cref="HostScope"/>); a person who has no lease in that reach does not exist for the caller.
/// </summary>
public interface ILongRentTenantService
{
    Task<TenantPage> GetTenantsAsync(HostScope scope, TenantListQuery query, CancellationToken cancellationToken = default);

    Task<TenantDetail?> GetTenantAsync(HostScope scope, Guid tenantId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TenantLeaseRef>?> FindLeasesAsync(HostScope scope, Guid tenantId, CancellationToken cancellationToken = default);

    Task<TenantDataExport?> ExportAsync(HostScope scope, Guid tenantId, IReadOnlySet<Guid> allowedLeaseIds, CancellationToken cancellationToken = default);

    Task<TenantErasureResult?> RequestErasureAsync(HostScope scope, Guid tenantId, IReadOnlySet<Guid> allowedLeaseIds, CancellationToken cancellationToken = default);
}
