using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Validation;
using Casazen.Web.DTOs.Supplier;

namespace Casazen.Web.DTOs.ServiceRequests;

/// <summary>
/// Body of <c>POST api/service-requests</c> (short-rent: <see cref="BookingId"/> optional, a stay of the property when given, SR-D3) and
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

    /// <summary>
    /// The stay the request is for (short-rent only), a booking of the property: optional since SR-08a, a request can be for the house
    /// alone. A booking that is not a stay of the property is 422 <c>service_request_booking_mismatch</c>.
    /// </summary>
    [NotEmptyGuid(ErrorMessage = "ServiceRequestBookingInvalid")]
    public Guid? BookingId { get; set; }

    /// <summary>
    /// The lease contract a long-term rental request is made for (LR-03c, the sheet of a contract): a lease of the property, in the host's
    /// org (404 <c>lease_not_found</c> otherwise, 422 <c>service_request_lease_mismatch</c> for the lease of another property). Long-rent only:
    /// the short-rent <c>POST api/service-requests</c> does not read it. A link and nothing more: nothing of the tenant goes to the supplier.
    /// </summary>
    [NotEmptyGuid(ErrorMessage = "ServiceRequestLeaseInvalid")]
    public Guid? LeaseId { get; set; }

    [NotEmptyGuid(ErrorMessage = "ServiceRequestSupplierRequired")]
    public Guid SupplierOrgId { get; set; }

    [Required(ErrorMessage = "ServiceCategoryRequired")]
    public string Category { get; set; } = string.Empty;

    public ServiceRequestUrgency Urgency { get; set; } = ServiceRequestUrgency.Normal;

    [MaxLength(1000, ErrorMessage = "ServiceRequestNotesTooLong")]
    public string? Notes { get; set; }

    public bool ChargeToGuest { get; set; }

    [NotEmptyGuid(ErrorMessage = "ServiceRequestServiceInvalid")]
    public Guid? ServiceListingId { get; set; }

    public DateTime? ScheduledStartUtc { get; set; }

    public int? DurationMinutes { get; set; }

    public bool? UseBackupSuppliers { get; set; }
}

public class RejectServiceRequestRequest
{
    [Required(ErrorMessage = "ServiceRequestRejectReasonRequired")]
    [MaxLength(500, ErrorMessage = "ServiceRequestRejectReasonTooLong")]
    public string Reason { get; set; } = string.Empty;
}

public class TakeServiceRequestRequest
{
    public DateTime? ScheduledStartUtc { get; set; }

    public DateTime? ScheduledEndUtc { get; set; }

    [Range(1, ServiceRequestLimits.MaxAmountCents, ErrorMessage = "ServiceRequestAmountOutOfRange")]
    public int? QuotedAmountCents { get; set; }
}

public class ServiceRequestExtraRequest
{
    [Required(ErrorMessage = "ServiceRequestExtraLabelRequired")]
    [MaxLength(ServiceRequestLimits.ExtraLabelMaxLength, ErrorMessage = "ServiceRequestExtraLabelTooLong")]
    public string Label { get; set; } = string.Empty;

    [Range(1, ServiceRequestLimits.MaxAmountCents, ErrorMessage = "ServiceRequestAmountOutOfRange")]
    public int AmountCents { get; set; }
}

public class CompleteServiceRequestRequest
{
    [MaxLength(1000, ErrorMessage = "ServiceRequestNotesTooLong")]
    public string? Notes { get; set; }

    [Range(1, ServiceRequestLimits.MaxAmountCents, ErrorMessage = "ServiceRequestAmountOutOfRange")]
    public int? FinalAmountCents { get; set; }

    [MaxLength(ServiceRequestLimits.MaxExtras, ErrorMessage = "ServiceRequestExtrasTooMany")]
    public List<ServiceRequestExtraRequest>? Extras { get; set; }
}

public class CancelServiceRequestRequest
{
    [Required(ErrorMessage = "ServiceRequestCancelReasonRequired")]
    [MaxLength(ServiceRequestLimits.CancellationReasonMaxLength, ErrorMessage = "ServiceRequestCancelReasonTooLong")]
    public string Reason { get; set; } = string.Empty;
}

public class ProposeServiceRequestTimeRequest
{
    [Required(ErrorMessage = "ServiceRequestProposalStartRequired")]
    public DateTime? StartUtc { get; set; }

    public DateTime? EndUtc { get; set; }

    [MaxLength(ServiceRequestLimits.ProposalMessageMaxLength, ErrorMessage = "ServiceRequestProposalMessageTooLong")]
    public string? Message { get; set; }
}

public class ServiceRequestQuoteLineRequest
{
    [Required(ErrorMessage = "ServiceRequestQuoteLineLabelRequired")]
    [MaxLength(ServiceRequestLimits.QuoteLineLabelMaxLength, ErrorMessage = "ServiceRequestQuoteLineLabelTooLong")]
    public string Label { get; set; } = string.Empty;

    [Range(1, ServiceRequestLimits.MaxAmountCents, ErrorMessage = "ServiceRequestAmountOutOfRange")]
    public int AmountCents { get; set; }
}

public class SendServiceRequestQuoteRequest
{
    [Required(ErrorMessage = "ServiceRequestQuoteLinesRequired")]
    [MinLength(1, ErrorMessage = "ServiceRequestQuoteLinesRequired")]
    [MaxLength(ServiceRequestLimits.MaxQuoteLines, ErrorMessage = "ServiceRequestQuoteTooManyLines")]
    public List<ServiceRequestQuoteLineRequest>? Lines { get; set; }

    [MaxLength(ServiceRequestLimits.QuoteNoteMaxLength, ErrorMessage = "ServiceRequestQuoteNoteTooLong")]
    public string? Note { get; set; }

    /// <summary>
    /// Another time for the work, instead of the one the request already has (optional): it replaces it only if the customer accepts, and
    /// is checked with the supplier's agenda (409 <c>supplier_slot_unavailable</c>). It does not hold the slot.
    /// </summary>
    public DateTime? ScheduledStartUtc { get; set; }

    public DateTime? ScheduledEndUtc { get; set; }
}

public class AnswerServiceRequestQuoteRequest
{
    [Required(ErrorMessage = "ServiceRequestQuoteSentAtRequired")]
    public DateTime? QuoteSentAt { get; set; }
}

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

    public string RentalContext { get; set; } = string.Empty;

    public Guid? PropertyId { get; set; }
    public string? PropertyName { get; set; }

    /// <summary>
    /// The lease contract a long-rent request was made for, when it was asked from the sheet of that contract (LR-03c); <c>null</c> otherwise.
    /// <b>Only the host gets it</b>: the supplier's views leave it out (decision LR-D22, nothing of the tenancy goes to the supplier).
    /// </summary>
    public Guid? LeaseId { get; set; }
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

    public string Source { get; set; } = SupplierRequestSources.CasaZen;

    public Guid? ServiceListingId { get; set; }

    public string? ServiceName { get; set; }

    public DateTime? ScheduledStart { get; set; }

    public DateTime? ScheduledEnd { get; set; }

    /// <summary>The instant the supplier has to answer by, while the request is new (<c>null</c> otherwise and for older requests).</summary>
    public DateTime? RespondBy { get; set; }

    public DateTime? StartedAt { get; set; }

    public ServiceRequestPriceDto Price { get; set; } = new();

    public DateTime? CancelledAt { get; set; }

    public string? CancelledBy { get; set; }

    public string? CancellationReason { get; set; }

    public string? CompletionNotes { get; set; }

    public IEnumerable<ServiceRequestWorkPhotoDto> WorkPhotos { get; set; } = [];

    public ServiceRequestProposalDto? Proposal { get; set; }

    public ServiceRequestQuoteDto? Quote { get; set; }

    public DateTime? LastRemindedAt { get; set; }

    public string PaymentMode { get; set; } = nameof(ServiceRequestPaymentMode.Manual);

    public bool AutoAccepted { get; set; }

    public string? PropertyCity { get; set; }

    public ServiceRequestStayDto? Stay { get; set; }

    public ServiceRequestSupplierContactDto? SupplierContact { get; set; }

    public ServiceRequestHostActionDto? HostAction { get; set; }

    public IEnumerable<string>? AvailableActions { get; set; }

    public bool? ScheduleDefaulted { get; set; }

    public ServiceRequestGroupDto? Group { get; set; }
}

public class ServiceRequestStayDto
{
    public Guid BookingId { get; set; }

    public string GuestName { get; set; } = string.Empty;

    public DateOnly CheckIn { get; set; }

    public DateOnly CheckOut { get; set; }
}

public class ServiceRequestSupplierContactDto
{
    public string? Phone { get; set; }
    public string? Email { get; set; }
}

public class ServiceRequestHostActionDto
{
    public string Kind { get; set; } = HostRequestActionKinds.None;

    public DateTime? RemindAvailableAt { get; set; }
}

public class ServiceRequestCountsDto
{
    public int All { get; set; }
    public int Pending { get; set; }
    public int Active { get; set; }
    public int ToPay { get; set; }
    public int Done { get; set; }
    public int NeedsAction { get; set; }

    public static ServiceRequestCountsDto From(HostServiceRequestCounts counts) => new()
    {
        All = counts.All,
        Pending = counts.Pending,
        Active = counts.Active,
        ToPay = counts.ToPay,
        Done = counts.Done,
        NeedsAction = counts.NeedsAction,
    };
}

public class ServiceRequestGroupDto
{
    public Guid Id { get; set; }
    public string Phase { get; set; } = string.Empty;
    public DateTime PrimaryDueAt { get; set; }
    public DateTime? BackupDueAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ClosedAt { get; set; }

    public IEnumerable<ServiceRequestGroupMemberDto> Members { get; set; } = [];
}

public class ServiceRequestGroupMemberDto
{
    public Guid RequestId { get; set; }
    public string Role { get; set; } = string.Empty;
    public Guid SupplierOrgId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime? AcceptedAt { get; set; }
    public bool TookTheJob { get; set; }
    public string? CancellationReason { get; set; }
}

public class ServiceRequestPriceDto
{
    public int? EstimatedAmountCents { get; set; }

    public int? QuotedAmountCents { get; set; }

    public int? FinalAmountCents { get; set; }

    public int? AmountCents { get; set; }

    public string? Basis { get; set; }

    public bool NeedsCustomerConfirmation { get; set; }

    public IEnumerable<ServiceRequestPriceLineDto> Lines { get; set; } = [];
}

public class ServiceRequestPriceLineDto
{
    public string Kind { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int AmountCents { get; set; }
}

public class ServiceRequestQuoteDto
{
    public DateTime SentAt { get; set; }

    public IEnumerable<ServiceRequestQuoteLineDto> Lines { get; set; } = [];

    public int TotalCents { get; set; }

    public string? Note { get; set; }

    /// <summary>Start of another time the quote offers instead of the request's own (UTC); <c>null</c> when it leans on the request's time. It is not held.</summary>
    public DateTime? ScheduledStart { get; set; }

    public DateTime? ScheduledEnd { get; set; }

    public DateTime? AnswerBy { get; set; }
}

public class ServiceRequestQuoteLineDto
{
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

public class ServiceRequestWorkPhotoDto
{
    public Guid Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public DateTime UploadedAt { get; set; }
}

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

    public static ServiceRequestGroupDto ToGroupDto(ServiceRequestGroupView group) => new()
    {
        Id = group.Id,
        Phase = group.Phase.ToString(),
        PrimaryDueAt = group.PrimaryDueAt,
        BackupDueAt = group.BackupDueAt,
        CreatedAt = group.CreatedAt,
        ClosedAt = group.ClosedAt,
        Members = group.Members.Select(member => new ServiceRequestGroupMemberDto
        {
            RequestId = member.RequestId,
            Role = member.Role.ToString(),
            SupplierOrgId = member.SupplierOrgId,
            SupplierName = member.SupplierName,
            Status = member.Status.ToString(),
            AcceptedAt = member.AcceptedAt,
            TookTheJob = member.TookTheJob,
            CancellationReason = member.CancellationReason,
        }).ToList(),
    };

    public static ServiceRequestQuoteDto? ToQuoteDto(ServiceRequestQuote? quote) =>
        quote is null
            ? null
            : new ServiceRequestQuoteDto
            {
                SentAt = AsUtc(quote.SentAt),
                Lines = quote.Lines.Select(line => new ServiceRequestQuoteLineDto { Label = line.Label, AmountCents = line.AmountCents }).ToList(),
                TotalCents = quote.TotalCents,
                Note = quote.Note,
                ScheduledStart = quote.ScheduledStartUtc is { } start ? AsUtc(start) : null,
                ScheduledEnd = quote.ScheduledEndUtc is { } end ? AsUtc(end) : null,
                AnswerBy = quote.AnswerBy is { } answerBy ? AsUtc(answerBy) : null,
            };

    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

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
