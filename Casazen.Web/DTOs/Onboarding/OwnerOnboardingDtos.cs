using System.Globalization;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Onboarding;

/// <summary>
/// The answer of <c>POST api/onboarding/owners/import</c> (E10/F02/US02/T01): the same shape for the check (<c>dryRun</c> true, nothing written)
/// and for the import (only the valid rows written). Codes, ids and the names the agency keeps in its registry; <b>never</b> a fiscal code, a VAT
/// number, an IBAN or a BIC, and the import never sends an invitation. <c>fingerprint</c> is the hash of the content of the file, the same in the
/// check and in the import of the same file.
/// </summary>
public sealed record OwnerImportResultDto(
    bool DryRun,
    string Fingerprint,
    OwnerImportSummaryDto Summary,
    IReadOnlyList<string> UnknownColumns,
    IReadOnlyList<OwnerImportRowDto> Rows,
    IReadOnlyList<OwnerImportPropertyIssueDto> PropertyIssues)
{
    public static OwnerImportResultDto From(OwnerImportResult result) => new(
        result.DryRun,
        result.Fingerprint,
        new OwnerImportSummaryDto(
            result.Summary.Rows,
            result.Summary.Valid,
            result.Summary.Invalid,
            result.Summary.Applied,
            result.Summary.OwnersToCreate,
            result.Summary.OwnersMatched,
            result.Summary.OwnershipsToCreate,
            result.Summary.PeriodsToUpdate),
        [.. result.UnknownColumns],
        [
            .. result.Rows.Select(row => new OwnerImportRowDto(
                row.RowNumber,
                row.PropertyRef,
                row.PropertyId,
                row.OwnerKey,
                row.OwnerId,
                row.MatchedBy,
                row.Status,
                [.. row.Errors.Select(error => new BulkImportRowErrorDto { Column = error.Column, Code = error.Code })],
                row.OwnerAction,
                row.OwnershipAction,
                [.. row.Candidates.Select(c => new OwnerImportCandidateDto(c.OwnerId, c.ExternalKey, c.DisplayName))],
                row.Applied)),
        ],
        [.. result.PropertyIssues.Select(p => new OwnerImportPropertyIssueDto(p.PropertyId, [.. p.Issues.Select(OwnershipIssueDto.Of)]))]);
}

/// <summary>How many rows the file has, how many are valid, how many changed something, and what the valid ones do.</summary>
public sealed record OwnerImportSummaryDto(
    int Rows,
    int Valid,
    int Invalid,
    int Applied,
    int OwnersToCreate,
    int OwnersMatched,
    int OwnershipsToCreate,
    int PeriodsToUpdate);

/// <summary>
/// One row of the owner file. <c>status</c> is <c>ok</c> or the code of its first error; <c>errors</c> lists every error with its column.
/// <c>ownerAction</c> is <c>create_owner</c>, <c>match_owner</c> or <c>conflict</c>; <c>ownershipAction</c> is <c>create_ownership</c>,
/// <c>update_future_period</c>, <c>unchanged</c> or <c>conflict</c>. <c>matchedBy</c> says how an existing owner was found
/// (<c>owner_id</c>, <c>external_key</c>, <c>fiscal_identifier</c>): never by e-mail. <c>candidates</c> lists the owners an ambiguous row could be.
/// </summary>
public sealed record OwnerImportRowDto(
    int RowNumber,
    string? PropertyRef,
    Guid? PropertyId,
    string? OwnerKey,
    Guid? OwnerId,
    string? MatchedBy,
    string Status,
    IReadOnlyList<BulkImportRowErrorDto> Errors,
    string? OwnerAction,
    string? OwnershipAction,
    IReadOnlyList<OwnerImportCandidateDto> Candidates,
    bool Applied);

/// <summary>An owner an ambiguous row could be: its id, its key and the name the agency keeps for it.</summary>
public sealed record OwnerImportCandidateDto(Guid OwnerId, string? ExternalKey, string DisplayName);

/// <summary>The days where the shares of a property would be wrong after the file.</summary>
public sealed record OwnerImportPropertyIssueDto(Guid PropertyId, IReadOnlyList<OwnershipIssueDto> Issues);

/// <summary>
/// A stretch of days where the shares of a property are wrong: <c>kind</c> is <c>gap</c> (nobody holds it), <c>shortfall</c> (less than 100%),
/// <c>excess</c> (more than 100%) or <c>ownerOverlap</c> (the same owner twice); <c>from</c> and <c>to</c> are days (<c>yyyy-MM-dd</c>, both
/// included; <c>to</c> is null for an overlap that never ends); <c>totalPercent</c> is what the shares add up to.
/// </summary>
public sealed record OwnershipIssueDto(string Kind, string From, string? To, decimal TotalPercent, Guid? OwnerId)
{
    public static OwnershipIssueDto Of(OwnershipIssue issue) => new(
        issue.Kind switch
        {
            OwnershipIssueKind.Gap => "gap",
            OwnershipIssueKind.Shortfall => "shortfall",
            OwnershipIssueKind.Excess => "excess",
            OwnershipIssueKind.OwnerOverlap => "ownerOverlap",
            _ => throw new ArgumentOutOfRangeException(nameof(issue), issue.Kind, "Unknown ownership issue."),
        },
        issue.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        issue.To?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        issue.TotalPercent,
        issue.OwnerId);
}

/// <summary>
/// The answer of <c>GET api/onboarding/owners/readiness</c> (E10/F02/US02/T01): for every owner and every property inside the caller's reach, what is
/// missing for the registry, the ownership, the mandate, the fiscal profile, the payment data and the portal. It is separate from the readiness of
/// the OTA channels, which it never reads and never changes. Codes and the names of the fields: never a value.
/// </summary>
public sealed record OwnerReadinessDto(
    string AsOf,
    OwnerReadinessSummaryDto Summary,
    IReadOnlyList<OwnerReadinessOwnerDto> Owners,
    IReadOnlyList<OwnerReadinessPropertyDto> Properties,
    bool Truncated)
{
    public static OwnerReadinessDto From(OwnerReadinessReport report) => new(
        report.AsOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        OwnerReadinessSummaryDto.From(report.Summary),
        [
            .. report.Owners.Select(o => new OwnerReadinessOwnerDto(
                o.OwnerId,
                o.ExternalKey,
                o.DisplayName,
                o.EntityKind,
                new OwnerReadinessFlagsDto(o.Ready.Registry, o.Ready.Ownership, o.Ready.Mandate, o.Ready.FiscalProfile, o.Ready.Payment, o.Ready.Portal),
                o.AccountingReady,
                o.PortalState,
                [.. o.Reasons.Select(OwnerReadinessReasonDto.From)])),
        ],
        [
            .. report.Properties.Select(p => new OwnerReadinessPropertyDto(
                p.PropertyId,
                p.Name,
                p.OwnershipReady,
                [.. p.Reasons.Select(OwnerReadinessReasonDto.From)],
                [.. p.Issues.Select(OwnershipIssueDto.Of)])),
        ],
        report.Truncated);
}

/// <summary>
/// The counts of the readiness. <c>financialReady</c> is true only when there is an owner, every owner is ready for the accounting and every property
/// has its shares right: until then the owner accounting is «to complete», whatever the OTA channels say.
/// </summary>
public sealed record OwnerReadinessSummaryDto(
    int Owners,
    int Properties,
    OwnerReadinessCountDto Registry,
    OwnerReadinessCountDto Ownership,
    OwnerReadinessCountDto Mandate,
    OwnerReadinessCountDto FiscalProfile,
    OwnerReadinessCountDto Payment,
    OwnerReadinessCountDto Portal,
    int AccountingReadyOwners,
    int PropertiesWithoutOwner,
    int PropertiesToComplete,
    bool FinancialReady)
{
    public static OwnerReadinessSummaryDto From(OwnerReadinessSummary s) => new(
        s.Owners,
        s.Properties,
        OwnerReadinessCountDto.From(s.Registry),
        OwnerReadinessCountDto.From(s.Ownership),
        OwnerReadinessCountDto.From(s.Mandate),
        OwnerReadinessCountDto.From(s.FiscalProfile),
        OwnerReadinessCountDto.From(s.Payment),
        OwnerReadinessCountDto.From(s.Portal),
        s.AccountingReadyOwners,
        s.PropertiesWithoutOwner,
        s.PropertiesToComplete,
        s.FinancialReady);
}

/// <summary>How many owners are ready and how many are to complete.</summary>
public sealed record OwnerReadinessCountDto(int Ready, int ToComplete)
{
    public static OwnerReadinessCountDto From(OwnerReadinessCount count) => new(count.Ready, count.ToComplete);
}

/// <summary>
/// One owner of the readiness: <c>ready</c> says for which of the six dimensions it is ready, <c>accountingReady</c> that the first five all are,
/// <c>portalState</c> where its invitation stands (<c>notReady</c>, <c>ready</c>, <c>invited</c>, <c>accepted</c>, <c>expired</c>, <c>revoked</c>),
/// <c>reasons</c> every fact that is missing. <c>entityKind</c> is <c>person</c> or <c>company</c>.
/// </summary>
public sealed record OwnerReadinessOwnerDto(
    Guid OwnerId,
    string? ExternalKey,
    string DisplayName,
    string? EntityKind,
    OwnerReadinessFlagsDto Ready,
    bool AccountingReady,
    string PortalState,
    IReadOnlyList<OwnerReadinessReasonDto> Reasons);

/// <summary>For which of the six dimensions an owner is ready.</summary>
public sealed record OwnerReadinessFlagsDto(bool Registry, bool Ownership, bool Mandate, bool FiscalProfile, bool Payment, bool Portal);

/// <summary>
/// One missing or wrong fact: its <c>dimension</c> (<c>registry</c>, <c>ownership</c>, <c>mandate</c>, <c>fiscalProfile</c>, <c>payment</c>,
/// <c>portal</c>), its <c>code</c>, the <c>field</c> it is about (a column name, never a value) and the <c>propertyId</c> when it is about one property.
/// </summary>
public sealed record OwnerReadinessReasonDto(string Dimension, string Code, string? Field, Guid? PropertyId)
{
    public static OwnerReadinessReasonDto From(OwnerReadinessReason reason) => new(reason.Dimension, reason.Code, reason.Field, reason.PropertyId);
}

/// <summary>A property whose ownership is not ready: no owner at all, or shares that are not exactly 100% on some days (<c>issues</c>).</summary>
public sealed record OwnerReadinessPropertyDto(
    Guid PropertyId,
    string Name,
    bool OwnershipReady,
    IReadOnlyList<OwnerReadinessReasonDto> Reasons,
    IReadOnlyList<OwnershipIssueDto> Issues);
