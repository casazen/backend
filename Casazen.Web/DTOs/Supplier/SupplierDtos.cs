using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;

namespace Casazen.Web.DTOs.Supplier;

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

    [MaxLength(256)]
    public string? InviteToken { get; set; }
}

public class SupplierInviteLookupRequest
{
    [Required, MaxLength(256)]
    public string Token { get; set; } = string.Empty;
}

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

    /// <summary>
    /// ISTAT codes of the comuni chosen from the official list (SU-04): replaces the stored ones; omitted keeps them. 422
    /// <c>comune_istat_unknown</c> for a code that is not an active comune of the list, <c>comuni_dataset_unavailable</c>
    /// while the list is not imported.
    /// </summary>
    public IEnumerable<string>? ComuneIstatCodes { get; set; }

    [MaxLength(2000)]
    public string? Bio { get; set; }

    public IEnumerable<string>? PhotoUrls { get; set; }
}

public class CompleteActivationRequest
{
    [Required]
    public bool TosAccepted { get; set; }

    [Required, MaxLength(100)]
    public string TosVersion { get; set; } = string.Empty;
}

public class AcceptSupplierTosRequest
{
    [Required, MaxLength(100)]
    public string TosVersion { get; set; } = string.Empty;
}

public class SetActivationStepRequest
{
    [Range(1, 5)]
    public int Step { get; set; }
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

    public string? RolesSyncError { get; set; }

    /// <summary>
    /// Anonymous self-serve registration only (SU-02): the secret that links the Auth0 account created afterwards to
    /// this profile (<c>POST /api/suppliers/claim</c>). Returned once, never stored in clear; null otherwise.
    /// </summary>
    public string? ClaimToken { get; set; }

    public DateTime? ClaimExpiresAt { get; set; }
}

public class SupplierClaimResponse
{
    public Guid OrgId { get; set; }

    public string RedirectUrl { get; set; } = string.Empty;

    /// <summary>
    /// True when the Auth0 <c>Supplier</c> role was assigned (additive, FD-14). Supplier access already works through the
    /// DB link; when false the role reaches the token only after a successful retry (the claim is idempotent).
    /// </summary>
    public bool RolesSynced { get; set; }

    public string? RolesSyncError { get; set; }
}

public class SupplierInviteLookupResponse
{
    public string Email { get; set; } = string.Empty;
    public string ComuneCode { get; set; } = string.Empty;

    /// <summary>Name of the comune when it is a configured pilot comune, otherwise null (show the code).</summary>
    public string? ComuneName { get; set; }

    public IEnumerable<string> Categories { get; set; } = [];
    public DateTime ExpiresAt { get; set; }
}

public class SupplierRegistrationOptionsResponse
{
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

    public IEnumerable<string> ComuneIstatCodes { get; set; } = [];

    public IEnumerable<ComuneDto> OperatingComuni { get; set; } = [];
    public string? Bio { get; set; }
    public IEnumerable<string> PhotoUrls { get; set; } = [];
    public DateTime? TosAcceptedAt { get; set; }
}

public class ActivationStatusDto
{
    public string Status { get; set; } = string.Empty;

    public int CurrentStep { get; set; }

    public IEnumerable<ActivationStepDto> Steps { get; set; } = [];
    public SupplierTosDto Tos { get; set; } = new();
}

public class ActivationStepDto
{
    public string Id { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string? Blocker { get; set; }

    public bool Required { get; set; }
}

public class SupplierTosDto
{
    public string CurrentVersion { get; set; } = string.Empty;
    public string? AcceptedVersion { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public bool ReacceptanceRequired { get; set; }

    public bool BlocksActions { get; set; }
}

public class CompleteActivationResponse
{
    public string Status { get; set; } = string.Empty;
}

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
    public bool IncludeTrusted { get; set; }
    public bool AccountScope { get; set; }
    public string? Comune { get; set; }
    public Guid? PropertyId { get; set; }
    public string? Category { get; set; }

    public string? Q { get; set; }

    public string? Sort { get; set; }
}

public class SupplierPickerDto
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? HostOrigin { get; set; }

    public Guid OrgId { get; set; }
    public string LegalName { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public IEnumerable<string> Categories { get; set; } = [];
    public IEnumerable<string> Comuni { get; set; } = [];
    public string? Bio { get; set; }
    public IEnumerable<string> PhotoUrls { get; set; } = [];

    public IEnumerable<SupplierPickerServiceDto> Services { get; set; } = [];

    public int? PriceFromCents { get; set; }

    public SupplierServicePriceUnit? PriceFromUnit { get; set; }

    public int? MedianResponseMinutes { get; set; }

    /// <summary>
    /// The "Verificato" badge: an active profile with a VAT number (<see cref="SupplierVerification.IsProfileVerified"/>). Only the
    /// boolean is sent, never the VAT number; it does not depend on the Stripe account.
    /// </summary>
    public bool Verified { get; set; }

    public string? ShowcaseSlug { get; set; }

    /// <summary>
    /// "Consigliato" (SP-REL): this is the best-ranked supplier of the list, strictly ahead of the next one, in the default order
    /// (<c>sort=recommended</c>). The score behind it (load, reliability) is never sent: only whether this one is the recommended one.
    /// <c>false</c> in any other order, on a tie and on the page of a single supplier.
    /// </summary>
    public bool Recommended { get; set; }

    /// <summary>
    /// The supplier as a host sees it when choosing one (short-rent and long-rent search alike). <c>comuni</c> lists the names
    /// of the comuni the supplier chose from the official list (<paramref name="listed"/>, by ISTAT code) and then what it
    /// wrote as text.
    /// </summary>
    public static SupplierPickerDto From(SupplierProfile sp, IReadOnlyDictionary<string, Comune>? listed = null) => new()
    {
        OrgId = sp.OrgId,
        LegalName = sp.LegalName,
        Phone = sp.Phone,
        Email = sp.Email,
        Categories = JsonSerializer.Deserialize<IEnumerable<string>>(sp.CategoriesJson, JsonOpts) ?? [],
        Comuni = SupplierComuniView.Names(sp, listed),
        Bio = sp.Bio,
        PhotoUrls = JsonSerializer.Deserialize<IEnumerable<string>>(sp.PhotoUrlsJson, JsonOpts) ?? [],
        Verified = SupplierVerification.IsProfileVerified(sp.Status, sp.VatNumber),
        ShowcaseSlug = string.IsNullOrWhiteSpace(sp.ShowcaseSlug) ? null : sp.ShowcaseSlug,
    };

    public static SupplierPickerDto From(
        SupplierProfile sp,
        IReadOnlyDictionary<string, Comune>? listed,
        IReadOnlyList<SupplierPickerService> services,
        int? medianResponseMinutes,
        int maxServices = SupplierPickerLimits.MaxServices, bool? hostOrigin = null)
    {
        var dto = From(sp, listed);
        dto.HostOrigin = hostOrigin;
        var cheapest = services.Where(s => s.PriceFromCents is not null).OrderBy(s => s.PriceFromCents).FirstOrDefault();

        dto.Services = services.Take(maxServices).Select(SupplierPickerServiceDto.From).ToList();
        dto.PriceFromCents = cheapest?.PriceFromCents;
        dto.PriceFromUnit = cheapest?.PriceUnit;
        dto.MedianResponseMinutes = medianResponseMinutes;
        return dto;
    }
}

public class SupplierPickerServiceDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public int? DurationMinutes { get; set; }

    public int? PriceFromCents { get; set; }

    public SupplierServicePriceUnit PriceUnit { get; set; }

    public bool RequiresQuote { get; set; }

    public static SupplierPickerServiceDto From(SupplierPickerService service) => new()
    {
        Id = service.Id,
        Name = service.Name,
        Category = service.Category,
        DurationMinutes = service.DurationMinutes,
        PriceFromCents = service.PriceFromCents,
        PriceUnit = service.PriceUnit,
        RequiresQuote = service.RequiresQuote,
    };
}

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

    public string LastSyncStatus { get; set; } = "None";

    public string? CalendarSyncErrorCode { get; set; }

    public string? CalendarSyncError { get; set; }

    public int PendingFreeEventsCount { get; set; }
}

public class SupplierFreeEventDto
{
    public Guid Id { get; set; }

    public string? Summary { get; set; }

    public bool IsAllDay { get; set; }

    public bool Recurring { get; set; }

    public int Occurrences { get; set; }

    public DateTime NextStartUtc { get; set; }

    public DateTime NextEndUtc { get; set; }

    public DateTime FirstSeenAt { get; set; }
}

public class SupplierFreeEventsResponse
{
    public IReadOnlyList<SupplierFreeEventDto> Items { get; set; } = [];

    public int PendingCount { get; set; }
}

public class SupplierFreeEventDecisionRequest
{
    [Required, MaxLength(20)]
    public string Decision { get; set; } = string.Empty;
}

public class FixOrphanedSupplierOrgsResponse
{
    public bool DryRun { get; set; }

    public int ProfilesScanned { get; set; }

    public int DuplicateGroups { get; set; }

    public int DuplicatesMerged { get; set; }

    public int ServiceRequestsMoved { get; set; }

    public IReadOnlyList<SupplierDuplicateMergeDto> Merges { get; set; } = [];

    public IReadOnlyList<string> DanglingLinksCleared { get; set; } = [];

    public IReadOnlyList<string> SupplierLinksBackfilled { get; set; } = [];

    public IReadOnlyList<Guid> OrphanProfiles { get; set; } = [];

    public IReadOnlyList<SupplierManualInterventionDto> ManualInterventions { get; set; } = [];
}

public class SupplierDuplicateMergeDto
{
    public Guid KeeperOrgId { get; set; }
    public Guid DuplicateOrgId { get; set; }
    public int ServiceRequestsMoved { get; set; }
    public int AvailabilityDaysMoved { get; set; }

    public int AvailabilityDaysDropped { get; set; }

    public IReadOnlyList<string> CategoriesAdded { get; set; } = [];
    public IReadOnlyList<string> ComuniAdded { get; set; } = [];

    public int SupplierLinksMoved { get; set; }

    public int OrgMembersMoved { get; set; }

    public int DevicesMoved { get; set; }

    public int ServiceListingsMoved { get; set; }

    public int AgendaRowsMoved { get; set; }

    public int ShowcaseRowsMoved { get; set; }

    public bool DuplicateOrgDeleted { get; set; }
}

public class SupplierManualInterventionDto
{
    public string Code { get; set; } = string.Empty;

    public IReadOnlyList<Guid> OrgIds { get; set; } = [];
    public IReadOnlyList<string> UserIds { get; set; } = [];
}

public class ServiceCategoryDto
{
    public string Code { get; set; } = string.Empty;
}

public class ServiceCategoriesResponse
{
    public IReadOnlyList<ServiceCategoryDto> Items { get; set; } = [];
}

public class UnmappedServiceCategoryDto
{
    public string Source { get; set; } = string.Empty;

    public Guid Id { get; set; }

    public string Value { get; set; } = string.Empty;
}

public class UnmappedServiceCategoriesResponse
{
    public IReadOnlyList<UnmappedServiceCategoryDto> Items { get; set; } = [];
    public int Total { get; set; }
}

public class SupplierDashboardDto
{
    public int ProfileCompletionPercent { get; set; }
    public string Status { get; set; } = string.Empty;
    public double AvailabilityRate { get; set; }
    public CalendarSyncStatusDto CalendarSyncStatus { get; set; } = new();
    public DateTime LastUpdated { get; set; }
}

public class SupplierKpisDto
{
    public string Period { get; set; } = string.Empty;

    public DateOnly From { get; set; }

    public DateOnly To { get; set; }

    public string TimeZone { get; set; } = string.Empty;

    public int Completed { get; set; }

    public int Rejected { get; set; }

    public int AwaitingAcceptance { get; set; }

    public int Upcoming { get; set; }

    public int TotalRequests { get; set; }
}

public class SupplierPhotoUploadResponse
{
    public IEnumerable<string> Urls { get; set; } = [];
}

/// <summary>
/// What the public page <c>/fornitori/{slug}</c> shows of a supplier (<c>GET /api/public/suppliers/{slug}</c>) and what the
/// owner previews (<c>GET /api/supplier/showcase</c>): never the phone, the email, the VAT number or the status.
/// </summary>
public class SupplierShowcaseDto
{
    public string? Slug { get; set; }

    public string LegalName { get; set; } = string.Empty;
    public IReadOnlyList<string> Categories { get; set; } = [];

    public IReadOnlyList<string> Comuni { get; set; } = [];

    public string? Bio { get; set; }
    public IReadOnlyList<string> PhotoUrls { get; set; } = [];

    public IReadOnlyList<AvailabilityEntryDto> Availability { get; set; } = [];

    /// <summary>
    /// SP-09: the published services with their "from" price, unit and VAT declaration (<see cref="PublicSupplierServiceDto"/>).
    /// <b>Left out of the answer (not an empty list) while the feature flag <c>SupplierShowcaseBooking</c> is off</b> and in the
    /// owner's preview: the page then reads exactly as it did before SP-09. Present and possibly empty once the flag is on.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<PublicSupplierServiceDto>? Services { get; set; }

    /// <summary>
    /// SP-09: the typical time the supplier takes to answer, in minutes: the <b>median</b> over the requests it really took in
    /// the last 90 days, shown only when there are at least 5 (never written by hand). Left out of the answer when not
    /// measured, while the flag is off, and in the owner's preview.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MedianResponseMinutes { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PublicShowcaseContentDto? Content { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Paused { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? BookingsOpen { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PublicSupplierTrustDto? Trust { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RespondWithinMinutes { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AcceptsOnlinePayments { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrivacyNoticeVersion { get; set; }
}

public class SupplierShowcasePreviewDto
{
    public SupplierShowcaseDto Showcase { get; set; } = new();

    public string Status { get; set; } = string.Empty;

    public bool Published { get; set; }

    public string? Slug { get; set; }

    public string? PublicPath { get; set; }

    public string? PublicUrl { get; set; }

    public bool Indexable { get; set; }
}
