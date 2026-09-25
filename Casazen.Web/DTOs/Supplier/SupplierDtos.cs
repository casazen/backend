using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Casazen.Core.Entities;

namespace Casazen.Web.DTOs.Supplier;

// ─── Requests ────────────────────────────────────────────────────────────────

public class SupplierRegisterRequest
{
    [Required, EmailAddress, MaxLength(255)]
    public string Email { get; set; } = string.Empty;

    [Required, MaxLength(300)]
    public string LegalName { get; set; } = string.Empty;

    [Required, MaxLength(50)]
    public string Phone { get; set; } = string.Empty;

    [Required, MaxLength(20)]
    public string ComuneCode { get; set; } = string.Empty;

    /// <summary>Token of the invite link (SU-01). A malformed or truncated value answers <c>supplier_invite_invalid</c>.</summary>
    [MaxLength(256)]
    public string? InviteToken { get; set; }
}

/// <summary>Body of <c>POST /api/suppliers/invites/lookup</c>: the token stays out of URLs and request logs.</summary>
public class SupplierInviteLookupRequest
{
    [Required, MaxLength(256)]
    public string Token { get; set; } = string.Empty;
}

/// <summary>
/// Body of <c>POST /api/suppliers/claim</c> (SU-02). <see cref="ClaimToken"/> is the token of an anonymous
/// self-serve registration; without it the claim relies on the verified account email.
/// </summary>
public class SupplierClaimRequest
{
    [MaxLength(256)]
    public string? ClaimToken { get; set; }
}

public class UpdateSupplierProfileRequest
{
    [MaxLength(300)]
    public string? LegalName { get; set; }

    [MaxLength(20)]
    public string? VatNumber { get; set; }

    [MaxLength(50)]
    public string? Phone { get; set; }

    public IEnumerable<string>? Categories { get; set; }

    public IEnumerable<string>? Comuni { get; set; }

    [MaxLength(2000)]
    public string? Bio { get; set; }

    public IEnumerable<string>? PhotoUrls { get; set; }
}

public class CompleteActivationRequest
{
    [Required]
    public bool TosAccepted { get; set; }
}

public class UpdateAvailabilityRequest
{
    [Required]
    public IEnumerable<AvailabilityEntryDto> Dates { get; set; } = [];
}

public class AvailabilityEntryDto
{
    [Required]
    public DateOnly Date { get; set; }

    public bool Available { get; set; } = true;
}

public class AdminInviteSupplierRequest
{
    [Required, EmailAddress, MaxLength(255)]
    public string Email { get; set; } = string.Empty;

    [Required, MaxLength(20)]
    public string ComuneCode { get; set; } = string.Empty;

    public IEnumerable<string>? Categories { get; set; }

    [MaxLength(1000)]
    public string? Message { get; set; }
}

// ─── Responses ───────────────────────────────────────────────────────────────

public class SupplierRegisterResponse
{
    public Guid OrgId { get; set; }
    public string AuthRedirectUrl { get; set; } = string.Empty;

    /// <summary>
    /// True when the Auth0 <c>Supplier</c> role was assigned (authenticated registrations only).
    /// Backend supplier access works regardless (it derives from the DB link); when false the client
    /// should not expect the role in a refreshed token.
    /// </summary>
    public bool RolesSynced { get; set; }

    /// <summary>Stable error code of the failed Auth0 sync, null when synced or not attempted.</summary>
    public string? RolesSyncError { get; set; }

    /// <summary>
    /// Anonymous self-serve registration only (SU-02): the secret that links the Auth0 account created afterwards to
    /// this profile (<c>POST /api/suppliers/claim</c>). Returned once, never stored in clear; null otherwise.
    /// </summary>
    public string? ClaimToken { get; set; }

    /// <summary>UTC expiry of <see cref="ClaimToken"/>.</summary>
    public DateTime? ClaimExpiresAt { get; set; }
}

/// <summary>Outcome of <c>POST /api/suppliers/claim</c>: the caller is linked to <see cref="OrgId"/>.</summary>
public class SupplierClaimResponse
{
    public Guid OrgId { get; set; }

    /// <summary>Where the web app continues: the activation wizard of the supplier console.</summary>
    public string RedirectUrl { get; set; } = string.Empty;

    /// <summary>
    /// True when the Auth0 <c>Supplier</c> role was assigned (additive, FD-14). Supplier access already works through the
    /// DB link; when false the role reaches the token only after a successful retry (the claim is idempotent).
    /// </summary>
    public bool RolesSynced { get; set; }

    /// <summary>Stable error code of the failed Auth0 sync, null when synced.</summary>
    public string? RolesSyncError { get; set; }
}

/// <summary>What an invite grants, to pre-fill the registration page (email and comune are then locked).</summary>
public class SupplierInviteLookupResponse
{
    public string Email { get; set; } = string.Empty;
    public string ComuneCode { get; set; } = string.Empty;

    /// <summary>Name of the comune when it is a configured pilot comune, otherwise null (show the code).</summary>
    public string? ComuneName { get; set; }

    public IEnumerable<string> Categories { get; set; } = [];
    public DateTime ExpiresAt { get; set; }
}

/// <summary>Self-serve registration settings for the registration page (SU-01).</summary>
public class SupplierRegistrationOptionsResponse
{
    /// <summary>False while no pilot comune is configured: suppliers join by invite only.</summary>
    public bool SelfServeEnabled { get; set; }

    public IEnumerable<SupplierPilotComuneDto> PilotComuni { get; set; } = [];
}

public class SupplierPilotComuneDto
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public class SupplierProfileDto
{
    public Guid OrgId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string LegalName { get; set; } = string.Empty;
    public string? VatNumber { get; set; }
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public IEnumerable<string> Categories { get; set; } = [];
    public IEnumerable<string> Comuni { get; set; } = [];
    public string? Bio { get; set; }
    public IEnumerable<string> PhotoUrls { get; set; } = [];
    public DateTime? TosAcceptedAt { get; set; }
}

public class ActivationStatusDto
{
    public string Status { get; set; } = string.Empty;
    public IEnumerable<ActivationStepDto> Steps { get; set; } = [];
}

public class ActivationStepDto
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Blocker { get; set; }
}

public class CompleteActivationResponse
{
    public string Status { get; set; } = string.Empty;
}

/// <summary>A page of <c>GET /api/supplier/inbox</c> (open requests, history, or one status; SU-08).</summary>
public class SupplierInboxResponse
{
    public IEnumerable<SupplierServiceRequestDto> Items { get; set; } = [];
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

public class UpdateAvailabilityResponse
{
    public int Updated { get; set; }
}

public class SupplierAvailabilityResponse
{
    public IEnumerable<AvailabilityEntryDto> Dates { get; set; } = [];
}

public class AdminInviteResponse
{
    public Guid InviteId { get; set; }
    public DateTime ExpiresAt { get; set; }
}

public class GetSuppliersQuery
{
    public string? Comune { get; set; }
    public Guid? PropertyId { get; set; }
    public string? Category { get; set; }
}

public class SupplierPickerDto
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public Guid OrgId { get; set; }
    public string LegalName { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public IEnumerable<string> Categories { get; set; } = [];
    public IEnumerable<string> Comuni { get; set; } = [];
    public string? Bio { get; set; }
    public IEnumerable<string> PhotoUrls { get; set; } = [];

    /// <summary>The supplier as a host sees it when choosing one (short-rent and long-rent search alike).</summary>
    public static SupplierPickerDto From(SupplierProfile sp) => new()
    {
        OrgId = sp.OrgId,
        LegalName = sp.LegalName,
        Phone = sp.Phone,
        Email = sp.Email,
        Categories = JsonSerializer.Deserialize<IEnumerable<string>>(sp.CategoriesJson, JsonOpts) ?? [],
        Comuni = JsonSerializer.Deserialize<IEnumerable<string>>(sp.ComuniJson, JsonOpts) ?? [],
        Bio = sp.Bio,
        PhotoUrls = JsonSerializer.Deserialize<IEnumerable<string>>(sp.PhotoUrlsJson, JsonOpts) ?? [],
    };
}

// ─── Calendar Sync ─────────────────────────────────────────────────────────────

public class SetIcalFeedRequest
{
    [Required, MaxLength(2048)]
    public string IcalFeedUrl { get; set; } = string.Empty;
}

public class CalendarSyncStatusDto
{
    public string CalendarSyncType { get; set; } = "None";
    public string? IcalFeedUrl { get; set; }
    public DateTime? CalendarLastSyncAt { get; set; }

    /// <summary>
    /// <c>None</c>, <c>Syncing</c> (a sync is queued or running: poll until it changes), <c>Success</c> or
    /// <c>Failure</c> (SU-15). The calendar is synced only once it leaves <c>Syncing</c>.
    /// </summary>
    public string LastSyncStatus { get; set; } = "None";

    /// <summary>Stable code of the last sync error (<c>ical_unreachable</c>, <c>ical_too_large</c>, ...).</summary>
    public string? CalendarSyncErrorCode { get; set; }

    /// <summary>Localized message of <see cref="CalendarSyncErrorCode"/>, never an exception message.</summary>
    public string? CalendarSyncError { get; set; }
}

/// <summary>
/// Report of <c>POST /api/admin/suppliers/fix-orphaned</c> (SU-14). Ids and counts only, no personal data. Runbook
/// <c>docs/runbooks/suppliers.md</c> section 9.
/// </summary>
public class FixOrphanedSupplierOrgsResponse
{
    /// <summary>True: nothing was saved, the report shows what an applied run would do.</summary>
    public bool DryRun { get; set; }

    public int ProfilesScanned { get; set; }

    /// <summary>Emails (trimmed, case-insensitive) used by more than one profile.</summary>
    public int DuplicateGroups { get; set; }

    /// <summary>Duplicate profiles merged into their keeper.</summary>
    public int DuplicatesMerged { get; set; }

    public int ServiceRequestsMoved { get; set; }

    public IReadOnlyList<SupplierDuplicateMergeDto> Merges { get; set; } = [];

    /// <summary>Accounts unlinked from a supplier org that no longer exists.</summary>
    public IReadOnlyList<string> DanglingLinksCleared { get; set; } = [];

    /// <summary>Accounts whose own org is a supplier org and that got the matching supplier link.</summary>
    public IReadOnlyList<string> SupplierLinksBackfilled { get; set; } = [];

    /// <summary>Profiles held by no account and with no account of their email: left for their registrant's claim.</summary>
    public IReadOnlyList<Guid> OrphanProfiles { get; set; } = [];

    /// <summary>Cases left untouched for a decision (codes in the runbook).</summary>
    public IReadOnlyList<SupplierManualInterventionDto> ManualInterventions { get; set; } = [];
}

public class SupplierDuplicateMergeDto
{
    public Guid KeeperOrgId { get; set; }
    public Guid DuplicateOrgId { get; set; }
    public int ServiceRequestsMoved { get; set; }
    public int AvailabilityDaysMoved { get; set; }

    /// <summary>Days the keeper already had: the keeper's value is kept.</summary>
    public int AvailabilityDaysDropped { get; set; }

    public IReadOnlyList<string> CategoriesAdded { get; set; } = [];
    public IReadOnlyList<string> ComuniAdded { get; set; } = [];

    /// <summary>Accounts whose supplier link moved to the keeper.</summary>
    public int SupplierLinksMoved { get; set; }

    /// <summary>Accounts whose <c>orgId</c> moved to the keeper (the duplicate org was deleted).</summary>
    public int OrgMembersMoved { get; set; }

    public int DevicesMoved { get; set; }

    /// <summary>False when the duplicate org also holds host data: only its supplier profile was removed.</summary>
    public bool DuplicateOrgDeleted { get; set; }
}

public class SupplierManualInterventionDto
{
    /// <summary>
    /// <c>supplier_duplicate_several_accounts</c>, <c>supplier_duplicate_suspended</c>,
    /// <c>supplier_duplicate_merge_conflict</c> or <c>supplier_link_requires_claim</c>.
    /// </summary>
    public string Code { get; set; } = string.Empty;

    public IReadOnlyList<Guid> OrgIds { get; set; } = [];
    public IReadOnlyList<string> UserIds { get; set; } = [];
}

// ─── Service categories (SU-03) ───────────────────────────────────────────────

/// <summary>A service category code (<c>cleaning</c>, <c>maintenance</c>, ...). Clients translate the label.</summary>
public class ServiceCategoryDto
{
    public string Code { get; set; } = string.Empty;
}

public class ServiceCategoriesResponse
{
    public IReadOnlyList<ServiceCategoryDto> Items { get; set; } = [];
}

/// <summary>A stored category value that is not a canonical code.</summary>
public class UnmappedServiceCategoryDto
{
    /// <summary><c>supplier_profile</c>, <c>supplier_invite</c> or <c>service_request</c>.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Supplier org id, invite id or service request id, depending on <see cref="Source"/>.</summary>
    public Guid Id { get; set; }

    public string Value { get; set; } = string.Empty;
}

public class UnmappedServiceCategoriesResponse
{
    public IReadOnlyList<UnmappedServiceCategoryDto> Items { get; set; } = [];
    public int Total { get; set; }
}

// ─── Dashboard ───────────────────────────────────────────────────────────────

public class SupplierDashboardDto
{
    public int ProfileCompletionPercent { get; set; }
    public string Status { get; set; } = string.Empty;
    public double AvailabilityRate { get; set; }
    public CalendarSyncStatusDto CalendarSyncStatus { get; set; } = new();
    public DateTime LastUpdated { get; set; }
}

/// <summary>
/// Work KPIs of the caller's supplier org, from its service requests (SU-11, A4-15). <see cref="Completed"/> and
/// <see cref="Rejected"/> count the period; <see cref="AwaitingAcceptance"/> and <see cref="Upcoming"/> are the open work
/// now, whatever the period.
/// </summary>
public class SupplierKpisDto
{
    /// <summary><c>CurrentMonth</c>, <c>PreviousMonth</c>, <c>Last30Days</c> or <c>CurrentYear</c>.</summary>
    public string Period { get; set; } = string.Empty;

    /// <summary>First calendar date of the period in <see cref="TimeZone"/> (included).</summary>
    public DateOnly From { get; set; }

    /// <summary>Last calendar date of the period in <see cref="TimeZone"/> (included).</summary>
    public DateOnly To { get; set; }

    public string TimeZone { get; set; } = string.Empty;

    /// <summary>Requests completed in the period (paid ones included, by completion date).</summary>
    public int Completed { get; set; }

    /// <summary>Requests rejected by the supplier in the period.</summary>
    public int Rejected { get; set; }

    /// <summary>Requests waiting to be taken (<c>Richiesto</c>).</summary>
    public int AwaitingAcceptance { get; set; }

    /// <summary>Requests taken and not completed yet (<c>PresoInCarico</c>, <c>InCorso</c>).</summary>
    public int Upcoming { get; set; }

    /// <summary>Every request ever assigned to the supplier org.</summary>
    public int TotalRequests { get; set; }
}

// ─── Photo Upload ────────────────────────────────────────────────────────────

public class SupplierPhotoUploadResponse
{
    public IEnumerable<string> Urls { get; set; } = [];
}
