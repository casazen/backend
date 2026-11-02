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
    /// The start of the work (UTC). With <see cref="DurationMinutes"/> the end is the start plus that duration, so the supplier
    /// sees when the job is and how long it lasts. With a catalog service and no duration, the service's duration gives the end
    /// and the start is checked against the agenda (409 <c>supplier_slot_unavailable</c>). Without a start the request gets 10:00
    /// (Europe/Rome) of the stay's check-out day or, without a stay, of today (of tomorrow once today's 10:00 has gone by), for the
    /// duration given, the service's or 120 minutes.
    /// </summary>
    public DateTime? ScheduledStartUtc { get; set; }

    /// <summary>
    /// How long the work lasts, in minutes (5 to 1,440). Together with <see cref="ScheduledStartUtc"/> it is the interval the
    /// supplier cannot overlap with a job already accepted.
    /// </summary>
    public int? DurationMinutes { get; set; }

    /// <summary>
    /// SP-BKP: whether the request is also sent to up to three backup suppliers, declared as such (the chosen supplier has one hour, and the
    /// first backup that accepts has the job if it does not). Left out: the choice of the org (on by default). <c>false</c> sends the request
    /// to the chosen supplier alone. Ignored while the feature flag <c>SupplierRequestBackups</c> is off.
    /// </summary>
    public bool? UseBackupSuppliers { get; set; }
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

/// <summary>One line of a quote (SP-04b): what it is for and how much, in cents of euro.</summary>
public class ServiceRequestQuoteLineRequest
{
    /// <summary>What the line is for: required, at most 120 characters.</summary>
    [Required(ErrorMessage = "ServiceRequestQuoteLineLabelRequired")]
    [MaxLength(ServiceRequestLimits.QuoteLineLabelMaxLength, ErrorMessage = "ServiceRequestQuoteLineLabelTooLong")]
    public string Label { get; set; } = string.Empty;

    /// <summary>The amount in cents of euro (1 to 10,000,000), gross as declared.</summary>
    [Range(1, ServiceRequestLimits.MaxAmountCents, ErrorMessage = "ServiceRequestAmountOutOfRange")]
    public int AmountCents { get; set; }
}

/// <summary>
/// Body of <c>POST api/service-requests/{id}/quote</c> (SP-04b): the supplier answers a new request with a quote. A new quote replaces the
/// one that waits.
/// </summary>
public class SendServiceRequestQuoteRequest
{
    /// <summary>The lines of the quote, from 1 to 20; their sum is the total.</summary>
    [Required(ErrorMessage = "ServiceRequestQuoteLinesRequired")]
    [MinLength(1, ErrorMessage = "ServiceRequestQuoteLinesRequired")]
    [MaxLength(ServiceRequestLimits.MaxQuoteLines, ErrorMessage = "ServiceRequestQuoteTooManyLines")]
    public List<ServiceRequestQuoteLineRequest>? Lines { get; set; }

    /// <summary>An optional note for the customer: at most 500 characters.</summary>
    [MaxLength(ServiceRequestLimits.QuoteNoteMaxLength, ErrorMessage = "ServiceRequestQuoteNoteTooLong")]
    public string? Note { get; set; }

    /// <summary>
    /// Another time for the work, instead of the one the request already has (optional): it replaces it only if the customer accepts, and
    /// is checked with the supplier's agenda (409 <c>supplier_slot_unavailable</c>). It does not hold the slot.
    /// </summary>
    public DateTime? ScheduledStartUtc { get; set; }

    /// <summary>The end of that other time; when left out, the start plus the duration of the request's service.</summary>
    public DateTime? ScheduledEndUtc { get; set; }
}

/// <summary>
/// Body of <c>POST api/service-requests/{id}/quote/accept</c> and <c>…/quote/reject</c> (and the long-rent twins), SP-04b: the quote the
/// host read, by the instant the supplier sent it (<c>quote.sentAt</c> of the request, repeated as it was given). If the supplier replaced
/// the quote meanwhile the answer is 409 <c>service_request_quote_changed</c> and nothing changes.
/// </summary>
public class AnswerServiceRequestQuoteRequest
{
    [Required(ErrorMessage = "ServiceRequestQuoteSentAtRequired")]
    public DateTime? QuoteSentAt { get; set; }
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

    /// <summary><c>ShortRent</c> (per stay), <c>LongRent</c> (per property), decision D2, or <c>Showcase</c> (a customer of the supplier's public showcase, SP-10).</summary>
    public string RentalContext { get; set; } = string.Empty;

    /// <summary>The host's property; <c>null</c> for a request from the supplier's public showcase (SP-10), which has none.</summary>
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

    // ─── Schedule, price and lifecycle (SP-04) ───

    /// <summary>Where the request comes from: <c>casazen</c> (a host's request) or <c>showcase</c> (a customer of the supplier's public showcase, SP-10).</summary>
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

    /// <summary>
    /// The quote the supplier sent, waiting for the customer's answer (SP-04b); <c>null</c> when there is none. The host accepts or rejects
    /// it with <c>POST …/quote/accept</c> or <c>…/quote/reject</c>, sending back <c>quote.sentAt</c>.
    /// </summary>
    public ServiceRequestQuoteDto? Quote { get; set; }

    /// <summary>When the host last reminded the supplier (a new reminder is possible 6 hours later).</summary>
    public DateTime? LastRemindedAt { get; set; }

    /// <summary>
    /// How the request is paid (SP-15a), fixed when the supplier took it: <c>Online</c> (inside CasaZen: the payer pays with the
    /// link, the host cannot mark it paid by hand) or <c>Manual</c> (outside CasaZen, the host marks it paid). <c>Manual</c> for
    /// every earlier request.
    /// </summary>
    public string PaymentMode { get; set; } = nameof(ServiceRequestPaymentMode.Manual);

    /// <summary>
    /// The request was <b>accepted automatically</b> by CasaZen (SP-04d): the supplier accepts the requests of its regular hosts by
    /// itself, so it was born <c>PresoInCarico</c> and no member of the supplier took it. <c>false</c> for every other request.
    /// </summary>
    public bool AutoAccepted { get; set; }

    // ─── What the host reads besides the request (SR-08a): null for the supplier and in the long-rent context ───

    /// <summary>The city of the property, as the host wrote it; <c>null</c> when the supplier reads the request, or the property has none.</summary>
    public string? PropertyCity { get; set; }

    /// <summary>
    /// The stay the request is for (its guest and dates) or <c>null</c> when it is for the house alone. <b>Only the host gets it</b>:
    /// the guest is the host's, and the supplier never sees the guest (decision D9).
    /// </summary>
    public ServiceRequestStayDto? Stay { get; set; }

    /// <summary>
    /// How to reach the supplier, <b>from the moment it took the request</b> (<c>PresoInCarico</c> on); <c>null</c> before it and for the
    /// supplier itself.
    /// </summary>
    public ServiceRequestSupplierContactDto? SupplierContact { get; set; }

    /// <summary>
    /// The one thing the host is asked to do about the request, worked out by the server from the state machine: the page shows it and
    /// has no state logic of its own. <c>null</c> for the supplier and in the long-rent context.
    /// </summary>
    public ServiceRequestHostActionDto? HostAction { get; set; }

    /// <summary>
    /// Every action the host can do now, the <see cref="HostAction"/> first: <c>remind</c>, <c>acceptProposal</c>, <c>rejectProposal</c>,
    /// <c>confirmFinalAmount</c>, <c>pay</c>, <c>markPaid</c>, <c>requestAgain</c>, <c>cancel</c>. <b>An open set</b>. <c>null</c> for the
    /// supplier and in the long-rent context.
    /// </summary>
    public IEnumerable<string>? AvailableActions { get; set; }

    /// <summary>
    /// Only in the answer of <c>POST api/service-requests</c>: <c>true</c> when the host named no time and the request got the default
    /// one (10:00 of the check-out day or, without a stay, of today or, once that has gone by, of tomorrow), so the page can say it.
    /// <c>null</c> everywhere else.
    /// </summary>
    public bool? ScheduleDefaulted { get; set; }

    /// <summary>
    /// SP-BKP: the group of backup suppliers the request belongs to, as its host sees it: where it is, the supplier the host chose and the
    /// backups with their state, and who took the job. <c>null</c> for a request sent to one supplier only. Filled by the host endpoints
    /// that read a request (list, detail, creation); the answers of the actions do not carry it.
    /// </summary>
    public ServiceRequestGroupDto? Group { get; set; }
}

/// <summary>The stay a request is for, as its host reads it (SR-08a).</summary>
public class ServiceRequestStayDto
{
    public Guid BookingId { get; set; }

    /// <summary>First and last name of the guest of the booking.</summary>
    public string GuestName { get; set; } = string.Empty;

    /// <summary>Arrival day (<c>yyyy-MM-dd</c>).</summary>
    public DateOnly CheckIn { get; set; }

    /// <summary>Departure day (<c>yyyy-MM-dd</c>).</summary>
    public DateOnly CheckOut { get; set; }
}

/// <summary>The phone and the e-mail of the supplier of a request it took (SR-08a).</summary>
public class ServiceRequestSupplierContactDto
{
    public string? Phone { get; set; }
    public string? Email { get; set; }
}

/// <summary>What the host is asked to do about a request (SR-08a, <see cref="HostRequestActionKinds"/>).</summary>
public class ServiceRequestHostActionDto
{
    /// <summary><c>none</c>, <c>remind</c>, <c>acceptProposal</c>, <c>confirmFinalAmount</c>, <c>pay</c>, <c>markPaid</c> or <c>requestAgain</c>. An open set.</summary>
    public string Kind { get; set; } = HostRequestActionKinds.None;

    /// <summary>With <c>none</c> on a request that waits for the supplier: the instant the host can remind it again (UTC).</summary>
    public DateTime? RemindAvailableAt { get; set; }
}

/// <summary>How many requests are in each chip of the host's list (SR-08a, <c>GET api/service-requests/counts</c>).</summary>
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

/// <summary>
/// The group of backup suppliers of a request as its host sees it (SP-BKP). <c>phase</c> is <c>PrimaryWindow</c> (the chosen supplier has
/// until <c>primaryDueAt</c>; the backups can only say they would take the job), <c>BackupWindow</c> (the chosen supplier did not take it or
/// refused: every supplier still waiting can, until <c>backupDueAt</c>, and the first has it) or <c>Closed</c>. An open set.
/// </summary>
public class ServiceRequestGroupDto
{
    public Guid Id { get; set; }
    public string Phase { get; set; } = string.Empty;
    public DateTime PrimaryDueAt { get; set; }
    public DateTime? BackupDueAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ClosedAt { get; set; }

    /// <summary>The request the host follows first (the chosen supplier, or the backup that replaced it), then the one it replaced, then the backups.</summary>
    public IEnumerable<ServiceRequestGroupMemberDto> Members { get; set; } = [];
}

/// <summary>
/// One request of a group (SP-BKP). <c>role</c>: <c>Primary</c> (the supplier the host chose), <c>Backup</c> (declared as a backup),
/// <c>ReplacedPrimary</c> (the chosen supplier that did not answer in time and was replaced) or <c>PromotedBackup</c> (the backup that took the
/// job). <c>acceptedAt</c> is when a backup said it would take the job; <c>tookTheJob</c> marks the request that was taken;
/// <c>cancellationReason</c> is the code of a request closed by CasaZen (<c>NoResponse</c>, <c>PrimaryTookIt</c>, <c>TakenByBackup</c>,
/// <c>RequestCancelledByHost</c>, <c>BackupSlotUnavailable</c>).
/// </summary>
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
    /// flag; the host confirms with <c>POST api/service-requests/{id}/final-amount/confirm</c> (SP-15a), which clears it.
    /// </summary>
    public bool NeedsCustomerConfirmation { get; set; }

    /// <summary>The lines that add up to the final amount: the agreed price and the extras.</summary>
    public IEnumerable<ServiceRequestPriceLineDto> Lines { get; set; } = [];
}

/// <summary>
/// One line of the price: <c>base</c> (the agreed price), <c>quote</c> (a line of the quote the customer accepted, SP-04b: the lines add up
/// to the agreed price) or <c>extra</c>. <b>An open set</b>: show a line of a kind you do not know as it comes.
/// </summary>
public class ServiceRequestPriceLineDto
{
    public string Kind { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int AmountCents { get; set; }
}

/// <summary>
/// The quote a supplier sent on a new request and the customer has not answered (SP-04b). The request keeps its own time until the
/// customer accepts: <see cref="ScheduledStart"/> is another time the quote offers, if it does.
/// </summary>
public class ServiceRequestQuoteDto
{
    /// <summary>When the supplier sent it: the version of the quote, which an answer repeats (<see cref="AnswerServiceRequestQuoteRequest.QuoteSentAt"/>).</summary>
    public DateTime SentAt { get; set; }

    /// <summary>The lines of the quote, as the supplier wrote them.</summary>
    public IEnumerable<ServiceRequestQuoteLineDto> Lines { get; set; } = [];

    /// <summary>What the lines add up to, in cents of euro: the price agreed if the quote is accepted.</summary>
    public int TotalCents { get; set; }

    /// <summary>The note of the supplier for the customer.</summary>
    public string? Note { get; set; }

    /// <summary>Start of another time the quote offers instead of the request's own (UTC); <c>null</c> when it leans on the request's time. It is not held.</summary>
    public DateTime? ScheduledStart { get; set; }

    /// <summary>End of that other time (UTC).</summary>
    public DateTime? ScheduledEnd { get; set; }

    /// <summary>Until when the customer of a showcase request can answer (after it the request is cancelled); <c>null</c> for a host, who has no deadline.</summary>
    public DateTime? AnswerBy { get; set; }
}

/// <summary>One line of a quote.</summary>
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

    /// <summary>The group of backup suppliers of a request as its host sees it (SP-BKP).</summary>
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

    /// <summary>The quote that waits for an answer as the API gives it (SP-04b); <c>null</c> when there is none.</summary>
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
