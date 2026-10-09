using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Casazen.Core.Validation;
using Casazen.Web.DTOs.Supplier;

namespace Casazen.Web.DTOs.ServiceRequests;

/// <summary>
/// Body of <c>POST api/service-requests</c> (short-rent: <see cref="BookingId"/> required, a stay of the property) and
/// <c>POST api/long-rent/service-requests</c> (long-rent: no <see cref="BookingId"/>), decision D2 (SU-07).
/// </summary>
/// <remarks>
/// Format and length (A4-18, SU-10): 400 <c>validation_error</c> with localized field errors. The category must be one
/// of the codes of <c>GET /api/service-categories</c>: a value that is not a code is refused by the service with 422
/// <c>invalid_service_category</c> (SU-03).
/// </remarks>
public class CreateServiceRequestRequest
{
    [NotEmptyGuid(ErrorMessage = "ServiceRequestPropertyRequired")]
    public Guid PropertyId { get; set; }

    [NotEmptyGuid(ErrorMessage = "ServiceRequestBookingInvalid")]
    public Guid? BookingId { get; set; }

    [NotEmptyGuid(ErrorMessage = "ServiceRequestSupplierRequired")]
    public Guid SupplierOrgId { get; set; }

    [Required(ErrorMessage = "ServiceCategoryRequired")]
    public string Category { get; set; } = string.Empty;

    public ServiceRequestUrgency Urgency { get; set; } = ServiceRequestUrgency.Normal;

    /// <summary>At most 1000 characters (the column of <c>ServiceRequest.Notes</c>).</summary>
    [MaxLength(1000, ErrorMessage = "ServiceRequestNotesTooLong")]
    public string? Notes { get; set; }

    public bool ChargeToGuest { get; set; }

    /// <summary>
    /// The service of the supplier's catalog the request is for (SP-04): an active service of that supplier, of the request's
    /// category. Gives the name, the estimated price and the duration of the work. Without it the request is by category only.
    /// </summary>
    [NotEmptyGuid(ErrorMessage = "ServiceRequestServiceInvalid")]
    public Guid? ServiceListingId { get; set; }

    /// <summary>
    /// The start of the work (UTC), picked among the supplier's free slots (SP-04): needs <see cref="ServiceListingId"/>, since the
    /// duration of the service gives the end. It is checked with the supplier's agenda (409 <c>supplier_slot_unavailable</c> when it
    /// is not free). Without it the time is "da concordare", as before.
    /// </summary>
    public DateTime? ScheduledStartUtc { get; set; }
}

/// <summary>Body of <c>POST api/service-requests/{id}/reject</c>: the reason is required (A4-18).</summary>
public class RejectServiceRequestRequest
{
    /// <summary>Why the supplier refuses the request, shown to the host: required, at most 500 characters.</summary>
    [Required(ErrorMessage = "ServiceRequestRejectReasonRequired")]
    [MaxLength(500, ErrorMessage = "ServiceRequestRejectReasonTooLong")]
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Optional body of <c>POST api/service-requests/{id}/take</c> (SP-04). Everything is optional: no body accepts the request as it
/// is. The amounts are in cents of euro.
/// </summary>
public class TakeServiceRequestRequest
{
    /// <summary>
    /// The start of the work (UTC) for a request that has no time yet; checked with the supplier's agenda. A request that
    /// already has a time keeps it (422 <c>service_request_time_already_set</c> for another one): propose another with
    /// <c>propose-time</c>.
    /// </summary>
    public DateTime? ScheduledStartUtc { get; set; }

    /// <summary>The end of the work (UTC); when left out, the start plus the duration of the request's service.</summary>
    public DateTime? ScheduledEndUtc { get; set; }

    /// <summary>The price the supplier commits to, in cents of euro (1 to 10,000,000).</summary>
    [Range(1, ServiceRequestLimits.MaxAmountCents, ErrorMessage = "ServiceRequestAmountOutOfRange")]
    public int? QuotedAmountCents { get; set; }
}

/// <summary>An extra the supplier adds to the price when it completes the work (a bathroom more, extra linen).</summary>
public class ServiceRequestExtraRequest
{
    /// <summary>What the extra is for: required, at most 80 characters.</summary>
    [Required(ErrorMessage = "ServiceRequestExtraLabelRequired")]
    [MaxLength(ServiceRequestLimits.ExtraLabelMaxLength, ErrorMessage = "ServiceRequestExtraLabelTooLong")]
    public string Label { get; set; } = string.Empty;

    /// <summary>The amount in cents of euro (1 to 10,000,000).</summary>
    [Range(1, ServiceRequestLimits.MaxAmountCents, ErrorMessage = "ServiceRequestAmountOutOfRange")]
    public int AmountCents { get; set; }
}

/// <summary>
/// Optional body of <c>POST api/service-requests/{id}/complete</c> (SP-04). The photos are uploaded before, with
/// <c>POST api/service-requests/{id}/photos</c>.
/// </summary>
public class CompleteServiceRequestRequest
{
    /// <summary>
    /// What the supplier leaves for the host: at most 1000 characters. It goes in its own field (<c>completionNotes</c>): the
    /// host's own notes are no longer replaced (before SP-04 they were).
    /// </summary>
    [MaxLength(1000, ErrorMessage = "ServiceRequestNotesTooLong")]
    public string? Notes { get; set; }

    /// <summary>
    /// The total the supplier asks, in cents of euro (1 to 10,000,000): the agreed price and the extras together. Left out, it is
    /// the quote (else the estimate) plus the extras. It must cover the extras.
    /// </summary>
    [Range(1, ServiceRequestLimits.MaxAmountCents, ErrorMessage = "ServiceRequestAmountOutOfRange")]
    public int? FinalAmountCents { get; set; }

    /// <summary>The extras on top of the agreed price: at most 10.</summary>
    [MaxLength(ServiceRequestLimits.MaxExtras, ErrorMessage = "ServiceRequestExtrasTooMany")]
    public List<ServiceRequestExtraRequest>? Extras { get; set; }
}

/// <summary>Body of <c>POST api/service-requests/{id}/cancel</c> (SP-04): the reason is required.</summary>
public class CancelServiceRequestRequest
{
    /// <summary>Why the request is cancelled, shown to the other party: required, at most 500 characters.</summary>
    [Required(ErrorMessage = "ServiceRequestCancelReasonRequired")]
    [MaxLength(ServiceRequestLimits.CancellationReasonMaxLength, ErrorMessage = "ServiceRequestCancelReasonTooLong")]
    public string Reason { get; set; } = string.Empty;
}

/// <summary>Body of <c>POST api/service-requests/{id}/propose-time</c> (SP-04): another time for a new request.</summary>
public class ProposeServiceRequestTimeRequest
{
    /// <summary>The start of the proposed work (UTC): required.</summary>
    [Required(ErrorMessage = "ServiceRequestProposalStartRequired")]
    public DateTime? StartUtc { get; set; }

    /// <summary>The end of the proposed work (UTC); when left out, the start plus the duration of the request's service.</summary>
    public DateTime? EndUtc { get; set; }

    /// <summary>An optional message for the host: at most 500 characters.</summary>
    [MaxLength(ServiceRequestLimits.ProposalMessageMaxLength, ErrorMessage = "ServiceRequestProposalMessageTooLong")]
    public string? Message { get; set; }
}

/// <summary>
/// Body of <c>POST api/service-requests/match-supplier</c>. No free text: the host's notes are not accepted, so they
/// can never reach an AI prompt (A8-15); the category is one of the known codes (A8-01).
/// </summary>
public class MatchSupplierRequest
{
    [NotEmptyGuid(ErrorMessage = "ServiceRequestPropertyRequired")]
    public Guid PropertyId { get; set; }

    [Required(ErrorMessage = "ServiceCategoryRequired")]
    [ServiceCategoryCode(ErrorMessage = "ServiceCategoryNotSupported")]
    public string Category { get; set; } = string.Empty;

    [EnumDataType(typeof(ServiceRequestUrgency))]
    public ServiceRequestUrgency Urgency { get; set; } = ServiceRequestUrgency.Normal;
}

public class SupplierMatchCandidateDto
{
    public Guid OrgId { get; set; }
    public string LegalName { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? Bio { get; set; }
    public int MatchScore { get; set; }
    public string MatchReason { get; set; } = string.Empty;
    public string Source { get; set; } = "platform";

    /// <summary>
    /// The reason was written by an AI model: the client shows the AI Act transparency notice next to it (SE-05, A8-27).
    /// False for the static reason (flag off, no provider, provider failure).
    /// </summary>
    public bool ReasonGeneratedByAi { get; set; }
}

public class ExternalSupplierSuggestionDto
{
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public double? Rating { get; set; }
    public int? ReviewCount { get; set; }
    public string? GoogleMapsUrl { get; set; }
    public string? WebsiteUrl { get; set; }
    public string Source { get; set; } = "ai_web_search";
}

public class SupplierMatchResponse
{
    public SupplierMatchCandidateDto? Recommended { get; set; }
    public IEnumerable<SupplierMatchCandidateDto> Alternatives { get; set; } = [];
    public IEnumerable<ExternalSupplierSuggestionDto> ExternalSuggestions { get; set; } = [];
    public bool UsedExternalFallback { get; set; }
}

public class ServiceRequestDto
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid? BookingId { get; set; }

    /// <summary><c>ShortRent</c> (per stay) or <c>LongRent</c> (per property), decision D2.</summary>
    public string RentalContext { get; set; } = string.Empty;
    public Guid PropertyId { get; set; }
    public string? PropertyName { get; set; }
    public Guid SupplierOrgId { get; set; }
    public string? SupplierName { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Urgency { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime? TakenAt { get; set; }
    public string? TakenByUserId { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? PaidAt { get; set; }
    public bool ChargeToGuest { get; set; }
    public string? RejectionReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// The steps of the request, oldest first, each with its date and the party that made it (SU-09, A4-28): requested
    /// (host), taken, started, completed, rejected or cancelled with the reason (supplier, host or CasaZen), paid (host). The
    /// supplier is named by <see cref="SupplierName"/>: a member of the supplier's team is never named to the host, so
    /// <see cref="ServiceRequestHistoryEntryDto.ActorName"/> is always null here.
    /// </summary>
    public IEnumerable<ServiceRequestHistoryEntryDto> History { get; set; } = [];

    // ─── Schedule, price and lifecycle (SP-04) ───

    /// <summary>Where the request comes from: <c>casazen</c> (a host's request; <c>showcase</c> arrives with SP-10).</summary>
    public string Source { get; set; } = SupplierRequestSources.CasaZen;

    /// <summary>The service of the supplier's catalog the request is for, when it is for one.</summary>
    public Guid? ServiceListingId { get; set; }

    /// <summary>The name of that service when the request was made.</summary>
    public string? ServiceName { get; set; }

    /// <summary>Start of the work (UTC); both ends null is "da concordare".</summary>
    public DateTime? ScheduledStart { get; set; }

    /// <summary>End of the work (UTC).</summary>
    public DateTime? ScheduledEnd { get; set; }

    /// <summary>The instant the supplier has to answer by, while the request is new (<c>null</c> otherwise and for older requests).</summary>
    public DateTime? RespondBy { get; set; }

    /// <summary>When the supplier started the work.</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>The price: estimated, quoted and final amounts in cents of euro, the lines of the final price, the confirmation flag (D7).</summary>
    public ServiceRequestPriceDto Price { get; set; } = new();

    /// <summary>When the request was cancelled.</summary>
    public DateTime? CancelledAt { get; set; }

    /// <summary>Who cancelled it: <c>Host</c>, <c>Supplier</c> or <c>System</c> (no answer in time).</summary>
    public string? CancelledBy { get; set; }

    /// <summary>The reason written by the host or the supplier, or the code <c>NoResponse</c> of the automatic cancellation.</summary>
    public string? CancellationReason { get; set; }

    /// <summary>What the supplier wrote when it completed the work.</summary>
    public string? CompletionNotes { get; set; }

    /// <summary>The photos of the work; each <c>url</c> is an authenticated endpoint (the files are private).</summary>
    public IEnumerable<ServiceRequestWorkPhotoDto> WorkPhotos { get; set; } = [];

    /// <summary>The time the supplier proposed, waiting for the host's answer; <c>null</c> when there is none.</summary>
    public ServiceRequestProposalDto? Proposal { get; set; }

    /// <summary>When the host last reminded the supplier (a new reminder is possible 6 hours later).</summary>
    public DateTime? LastRemindedAt { get; set; }
}

/// <summary>
/// The price of a request in cents of euro (SP-04). <c>amountCents</c> is the one to show: the final amount, else the quote,
/// else the estimate; <c>null</c> is "da concordare".
/// </summary>
public class ServiceRequestPriceDto
{
    /// <summary>The price known when the request was made (the "from" price of a per-job service).</summary>
    public int? EstimatedAmountCents { get; set; }

    /// <summary>The price the supplier committed to when it took the request.</summary>
    public int? QuotedAmountCents { get; set; }

    /// <summary>The total the supplier asked when it completed the work.</summary>
    public int? FinalAmountCents { get; set; }

    /// <summary>The amount to show: final, else quoted, else estimated.</summary>
    public int? AmountCents { get; set; }

    /// <summary>Which amount <see cref="AmountCents"/> is: <c>final</c>, <c>quoted</c> or <c>estimated</c> (<c>null</c> when none).</summary>
    public string? Basis { get; set; }

    /// <summary>
    /// Decision D7: the final amount is more than 20 % above the quote, so the customer has to confirm it. SP-04 only gives the
    /// flag; the confirmation itself arrives with the payments (SP-15).
    /// </summary>
    public bool NeedsCustomerConfirmation { get; set; }

    /// <summary>The lines that add up to the final amount: the agreed price and the extras.</summary>
    public IEnumerable<ServiceRequestPriceLineDto> Lines { get; set; } = [];
}

/// <summary>One line of the final price: <c>base</c> (the agreed price) or <c>extra</c>.</summary>
public class ServiceRequestPriceLineDto
{
    public string Kind { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int AmountCents { get; set; }
}

/// <summary>A time proposed by the supplier instead of the requested one.</summary>
public class ServiceRequestProposalDto
{
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public DateTime ProposedAt { get; set; }
    public string? Message { get; set; }
}

/// <summary>A photo of the work: read it with an authenticated <c>GET</c> of <see cref="Url"/> (the files are in a private bucket).</summary>
public class ServiceRequestWorkPhotoDto
{
    public Guid Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public DateTime UploadedAt { get; set; }
}

/// <summary>The parts of the request DTOs that the host and the supplier sides share (price, photos, proposal).</summary>
public static class ServiceRequestDtoParts
{
    public const string ShortRentBasePath = "/api/service-requests";
    public const string LongRentBasePath = "/api/long-rent/service-requests";

    public static ServiceRequestPriceDto ToPriceDto(
        int? estimatedAmountCents,
        int? quotedAmountCents,
        int? finalAmountCents,
        bool needsCustomerConfirmation,
        IReadOnlyList<ServiceRequestPriceLine> lines)
    {
        var (amount, basis) = (finalAmountCents, quotedAmountCents, estimatedAmountCents) switch
        {
            ({ } final, _, _) => ((int?)final, "final"),
            (_, { } quoted, _) => (quoted, "quoted"),
            (_, _, { } estimated) => (estimated, "estimated"),
            _ => ((int?)null, (string?)null),
        };

        return new ServiceRequestPriceDto
        {
            EstimatedAmountCents = estimatedAmountCents,
            QuotedAmountCents = quotedAmountCents,
            FinalAmountCents = finalAmountCents,
            AmountCents = amount,
            Basis = basis,
            NeedsCustomerConfirmation = needsCustomerConfirmation,
            Lines = lines.Select(line => new ServiceRequestPriceLineDto
            {
                Kind = line.Kind,
                Label = line.Label,
                AmountCents = line.AmountCents,
            }).ToList(),
        };
    }

    public static ServiceRequestProposalDto? ToProposalDto(DateTime? startUtc, DateTime? endUtc, DateTime? proposedAt, string? message) =>
        startUtc is { } start && endUtc is { } end && proposedAt is { } at
            ? new ServiceRequestProposalDto
            {
                StartUtc = start,
                EndUtc = end,
                ProposedAt = at,
                Message = string.IsNullOrWhiteSpace(message) ? null : message,
            }
            : null;

    /// <summary>The photos of a request with the address of the endpoint that serves each one under <paramref name="basePath"/>.</summary>
    public static IReadOnlyList<ServiceRequestWorkPhotoDto> ToPhotoDtos(
        Guid requestId,
        IReadOnlyList<ServiceRequestPhoto> photos,
        string basePath) =>
        photos.Select(photo => new ServiceRequestWorkPhotoDto
        {
            Id = photo.Id,
            Url = $"{basePath}/{requestId}/photos/{photo.Id}",
            UploadedAt = photo.UploadedAt,
        }).ToList();
}

public class ServiceRequestListResponse
{
    public IEnumerable<ServiceRequestDto> Items { get; set; } = [];
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}
