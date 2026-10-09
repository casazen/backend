using System.ComponentModel.DataAnnotations;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Web.DTOs.ServiceRequests;

namespace Casazen.Web.DTOs.Supplier;

/// <summary>
/// A service request in the supplier console (SU-08, A4-14; SP-04): <c>GET /api/supplier/inbox</c> items. What the supplier sees
/// before and after the take is in <see cref="SupplierJobDisclosure"/> (decision D9: before the take only the comune, the postal
/// code, the day and time, the price and who asks; the name of the property, the host's notes, the street address and the
/// contact come with the take); the guest of the stay is never included.
/// </summary>
public class SupplierServiceRequestDto
{
    public Guid Id { get; set; }

    /// <summary><c>ShortRent</c> (for a stay), <c>LongRent</c> (for the property), decision D2, or <c>Showcase</c> (a customer of the public showcase, SP-10).</summary>
    public string RentalContext { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Urgency { get; set; } = string.Empty;

    /// <summary>The host's notes: <b>null until the supplier takes the request</b> (decision D9).</summary>
    public string? Notes { get; set; }
    public string? RejectionReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? TakenAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? PaidAt { get; set; }

    /// <summary>The host's property; <c>null</c> for a request from the public showcase (SP-10), which has none.</summary>
    public Guid? PropertyId { get; set; }

    /// <summary>The name of the property: <b>null until the supplier takes the request</b> (decision D9).</summary>
    public string? PropertyName { get; set; }

    /// <summary>Comune of the property (of the work, for a showcase request): always shown.</summary>
    public string City { get; set; } = string.Empty;

    /// <summary>Postal code (zone within the comune): always shown when the host or the customer entered it.</summary>
    public string? PostalCode { get; set; }

    /// <summary>Street address: only once the supplier took the request (<see cref="ContactDisclosed"/>).</summary>
    public string? Address { get; set; }

    /// <summary>Floor and apartment of a showcase request (SP-10): only once the supplier took the request.</summary>
    public string? Floor { get; set; }

    /// <summary>The customer's note for the access (doorbell, keys) of a showcase request (SP-10): only once the supplier took the request.</summary>
    public string? AccessNotes { get; set; }

    /// <summary>
    /// Europe/Rome calendar date of the job (<c>YYYY-MM-DD</c>): the day of <see cref="ScheduledStart"/> when the request has a
    /// time, else the check-out day of the stay for a short-rent request; null when there is neither (long-rent, or an older
    /// request not traced to a stay): to agree with the host.
    /// </summary>
    public DateOnly? ScheduledFor { get; set; }

    /// <summary>The stay (short-rent): booking id and dates only, never the guest.</summary>
    public SupplierStayDto? Stay { get; set; }

    /// <summary>True once the supplier took the request: name of the property, notes, address and host contact are shown.</summary>
    public bool ContactDisclosed { get; set; }

    /// <summary>
    /// The host contact: only when <see cref="ContactDisclosed"/>. For a request from the public showcase (SP-10) it is the contact
    /// of the private customer who asked (name, e-mail, phone): the same block, filled by the party that asked.
    /// </summary>
    public SupplierHostContactDto? HostContact { get; set; }

    // ─── Schedule, price and lifecycle (SP-04) ───

    /// <summary>Where the request comes from: <c>casazen</c> (a host's request) or <c>showcase</c> (a customer of the public showcase, SP-10).</summary>
    public string Source { get; set; } = SupplierRequestSources.CasaZen;

    /// <summary>
    /// The customer: for a host's request the host org, whose name decision D9 shows before the take; for a showcase request the
    /// private customer. Filter the inbox with <c>clientId</c>.
    /// </summary>
    public Guid ClientId { get; set; }

    /// <summary>The name of the customer: the host org's display name, or for a private customer "Nome C." until the supplier takes the request (decision D9).</summary>
    public string ClientName { get; set; } = string.Empty;

    /// <summary>The service of the catalog the request is for, when it is for one.</summary>
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

    /// <summary>The time the supplier proposed, waiting for the host's answer; <c>null</c> when there is none.</summary>
    public ServiceRequestProposalDto? Proposal { get; set; }

    /// <summary>When the request was cancelled.</summary>
    public DateTime? CancelledAt { get; set; }

    /// <summary>
    /// Who cancelled it: <c>Host</c>, <c>Supplier</c>, <c>Customer</c> (the customer of a request from the showcase, SP-11) or
    /// <c>System</c> (nobody answered in time).
    /// </summary>
    public string? CancelledBy { get; set; }

    /// <summary>
    /// The reason written by the host, the supplier or the customer, or a code when nobody wrote one: <c>NoResponse</c> (nobody
    /// answered in time), <c>ProposalNotAnswered</c> (the customer did not answer the time proposed in a day) or
    /// <c>CancelledByCustomer</c> (the customer cancelled and gave no reason).
    /// </summary>
    public string? CancellationReason { get; set; }

    /// <summary>What the supplier wrote when it completed the work.</summary>
    public string? CompletionNotes { get; set; }

    /// <summary>The photos of the work; each <c>url</c> is an authenticated endpoint (the files are private).</summary>
    public IEnumerable<ServiceRequestWorkPhotoDto> WorkPhotos { get; set; } = [];
}

/// <summary><c>GET /api/supplier/inbox/{id}</c>: the request and its history (SU-08).</summary>
public class SupplierServiceRequestDetailDto : SupplierServiceRequestDto
{
    /// <summary>The transitions, oldest first, with date and actor.</summary>
    public IEnumerable<ServiceRequestHistoryEntryDto> History { get; set; } = [];
}

/// <summary>The stay of a short-rent request as the supplier sees it: no guest data.</summary>
public class SupplierStayDto
{
    public Guid BookingId { get; set; }

    /// <summary>Check-in day (<c>YYYY-MM-DD</c>, Europe/Rome).</summary>
    public DateOnly CheckIn { get; set; }

    /// <summary>Check-out day (<c>YYYY-MM-DD</c>, Europe/Rome).</summary>
    public DateOnly CheckOut { get; set; }
}

/// <summary>Host contact given to the supplier after the take.</summary>
public class SupplierHostContactDto
{
    /// <summary>Display name of the host org.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Contact email of the host org.</summary>
    public string? Email { get; set; }

    /// <summary>Phone of the property owner's profile, when filled in.</summary>
    public string? Phone { get; set; }
}

/// <summary>One step of the history of a service request.</summary>
public class ServiceRequestHistoryEntryDto
{
    /// <summary>
    /// The status reached (<c>Richiesto</c>, <c>PresoInCarico</c>, <c>InCorso</c>, <c>Completato</c>, <c>Pagato</c>,
    /// <c>Rifiutato</c>, <c>Annullato</c>).
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>UTC instant of the transition.</summary>
    public DateTime At { get; set; }

    /// <summary><c>Host</c>, <c>Supplier</c>, <c>System</c> (the automatic cancellation) or <c>Customer</c> (a private customer of the public showcase, SP-10).</summary>
    public string Actor { get; set; } = string.Empty;

    /// <summary>The supplier member who took the request, when known; host members are never named.</summary>
    public string? ActorName { get; set; }

    /// <summary>
    /// The rejection or cancellation reason, on the <c>Rifiutato</c> and <c>Annullato</c> steps: the text a person wrote, or the code
    /// <c>NoResponse</c>, <c>ProposalNotAnswered</c> or <c>CancelledByCustomer</c> (<see cref="Casazen.Core.Suppliers.ServiceRequestCancellationReasons"/>).
    /// </summary>
    public string? Reason { get; set; }

    internal static ServiceRequestHistoryEntryDto From(ServiceRequestHistoryEntry entry) => new()
    {
        Status = entry.Status.ToString(),
        At = entry.At,
        Actor = entry.Actor.ToString(),
        ActorName = entry.ActorName,
        Reason = entry.Reason,
    };
}

/// <summary>
/// Body of <c>GET api/supplier/today</c> (SP-04): the home of the console. The earnings are an <b>estimate</b> until the payments
/// of SP-15 exist: they add up the final amounts the supplier declared (<see cref="SupplierTodayEarningsDto.Estimated"/>).
/// </summary>
public class SupplierTodayDto
{
    /// <summary>Today in Europe/Rome (<c>YYYY-MM-DD</c>).</summary>
    public DateOnly Date { get; set; }

    /// <summary>The time zone of the days: <c>Europe/Rome</c>.</summary>
    public string TimeZone { get; set; } = string.Empty;

    /// <summary>The jobs of today (taken, in progress, completed or paid), the first in time first.</summary>
    public IEnumerable<SupplierServiceRequestDto> Jobs { get; set; } = [];

    /// <summary>The first new requests waiting for the answer, the one to answer first on top.</summary>
    public IEnumerable<SupplierServiceRequestDto> NewRequests { get; set; } = [];

    /// <summary>How many requests wait for the supplier's answer.</summary>
    public int NewRequestsTotal { get; set; }

    public SupplierTodayEarningsDto Earnings { get; set; } = new();

    /// <summary>
    /// Average minutes from the arrival of a request to its take (<c>takenAt − createdAt</c>) over the requests taken in the
    /// last 90 days; <c>null</c> when there are none.
    /// </summary>
    public int? AverageResponseMinutes { get; set; }
}

/// <summary>The money of the supplier's home, in cents of euro.</summary>
public class SupplierTodayEarningsDto
{
    /// <summary>First day of the month the earnings are for (<c>YYYY-MM-DD</c>, Europe/Rome).</summary>
    public DateOnly MonthFrom { get; set; }

    /// <summary>Last day of the month.</summary>
    public DateOnly MonthTo { get; set; }

    /// <summary>The final amounts of the jobs completed this month.</summary>
    public long MonthAmountCents { get; set; }

    /// <summary>Jobs completed this month, with or without an amount.</summary>
    public int MonthJobs { get; set; }

    /// <summary>The final amounts of the completed jobs not marked as paid yet.</summary>
    public long ToCollectAmountCents { get; set; }

    /// <summary>Completed jobs not marked as paid yet.</summary>
    public int ToCollectJobs { get; set; }

    /// <summary>
    /// True while the amounts are the supplier's declarations and not payments received through CasaZen (always, until SP-15):
    /// the console labels them "stimato".
    /// </summary>
    public bool Estimated { get; set; }
}

/// <summary>Body of <c>GET api/supplier/checklist</c> (SP-04): the steps of the activation, each from what the supplier really did.</summary>
public class SupplierChecklistDto
{
    public int ProfileCompletionPercent { get; set; }

    /// <summary>The profile is complete (100 %).</summary>
    public bool ProfileComplete { get; set; }

    /// <summary>Published services; the step is done with at least one.</summary>
    public int ActiveServices { get; set; }

    /// <summary>The supplier saved working hours with at least one band.</summary>
    public bool HoursConfigured { get; set; }

    /// <summary>When it last saved them; <c>null</c> while it did not.</summary>
    public DateTime? HoursConfiguredAt { get; set; }

    /// <summary>The public showcase is online (an active supplier with its address).</summary>
    public bool ShowcasePublished { get; set; }

    /// <summary>The supplier answered a request at least once (took one or rejected one).</summary>
    public bool FirstRequestAnswered { get; set; }

    /// <summary>
    /// Whether the supplier receives payments through CasaZen. <b>Always <c>null</c> for now</b>: payments are not available yet
    /// (SP-14, SP-15). <c>null</c> means "not available", not "no": the console does not count the step.
    /// </summary>
    public bool? PaymentsActive { get; set; }
}

/// <summary>Body of <c>POST api/supplier/inbox/accept</c> (SP-04): the new requests to take as they are, at most 20.</summary>
public class AcceptServiceRequestsRequest
{
    /// <summary>The ids of the requests: from 1 to 20.</summary>
    [Required(ErrorMessage = "ServiceRequestIdsRequired")]
    [MinLength(1, ErrorMessage = "ServiceRequestIdsRequired")]
    [MaxLength(ServiceRequestLimits.MaxBatchAccept, ErrorMessage = "ServiceRequestIdsTooMany")]
    public List<Guid>? Ids { get; set; }
}

/// <summary>The result of a batch accept: one entry per request, in the order received (a repeated id counts once).</summary>
public class AcceptServiceRequestsResponse
{
    public IEnumerable<AcceptServiceRequestResultDto> Results { get; set; } = [];

    /// <summary>How many requests were taken.</summary>
    public int Accepted { get; set; }

    /// <summary>How many were not.</summary>
    public int Failed { get; set; }
}

/// <summary>
/// The outcome for one request of a batch accept. A request that could not be taken carries the same <c>code</c> and localized
/// <c>message</c> the single <c>take</c> would answer with (<c>service_request_invalid_transition</c> when it is no longer new,
/// <c>service_request_state_changed</c> when it changed meanwhile, <c>service_request_not_found</c> when it is not the supplier's).
/// </summary>
public class AcceptServiceRequestResultDto
{
    public Guid Id { get; set; }

    public bool Accepted { get; set; }

    /// <summary>The status after the take (<c>PresoInCarico</c>) when it was taken.</summary>
    public string? Status { get; set; }

    public string? Code { get; set; }

    public string? Message { get; set; }
}

/// <summary>Maps the supplier view of a request (<see cref="SupplierServiceRequestView"/>) to the API DTOs.</summary>
public static class SupplierServiceRequestMapper
{
    public static SupplierServiceRequestDto ToDto(SupplierServiceRequestView view) =>
        Fill(new SupplierServiceRequestDto(), view);

    public static SupplierServiceRequestDetailDto ToDetailDto(SupplierServiceRequestView view)
    {
        var dto = Fill(new SupplierServiceRequestDetailDto(), view);
        dto.History = (view.History ?? []).Select(h => ServiceRequestHistoryEntryDto.From(h)).ToList();
        return dto;
    }

    public static SupplierTodayDto ToTodayDto(SupplierToday today) => new()
    {
        Date = today.Date,
        TimeZone = Casazen.Core.Utilities.RomeCalendar.TimeZoneId,
        Jobs = today.Jobs.Select(view => ToDto(view)).ToList(),
        NewRequests = today.NewRequests.Select(view => ToDto(view)).ToList(),
        NewRequestsTotal = today.NewRequestsTotal,
        Earnings = new SupplierTodayEarningsDto
        {
            MonthFrom = today.Earnings.MonthFrom,
            MonthTo = today.Earnings.MonthTo,
            MonthAmountCents = today.Earnings.MonthAmountCents,
            MonthJobs = today.Earnings.MonthJobs,
            ToCollectAmountCents = today.Earnings.ToCollectAmountCents,
            ToCollectJobs = today.Earnings.ToCollectJobs,
            Estimated = today.Earnings.IsEstimate,
        },
        AverageResponseMinutes = today.Earnings.AverageResponseMinutes,
    };

    public static SupplierChecklistDto ToChecklistDto(SupplierChecklist checklist) => new()
    {
        ProfileCompletionPercent = checklist.ProfileCompletionPercent,
        ProfileComplete = checklist.ProfileComplete,
        ActiveServices = checklist.ActiveServices,
        HoursConfigured = checklist.HoursConfigured,
        HoursConfiguredAt = checklist.HoursConfiguredAt,
        ShowcasePublished = checklist.ShowcasePublished,
        FirstRequestAnswered = checklist.FirstRequestAnswered,
        PaymentsActive = checklist.PaymentsActive,
    };

    private static T Fill<T>(T dto, SupplierServiceRequestView view)
        where T : SupplierServiceRequestDto
    {
        dto.Id = view.Id;
        dto.RentalContext = view.RentalContext.ToString();
        dto.Status = view.Status.ToString();
        dto.Category = view.Category;
        dto.Urgency = view.Urgency.ToString();
        dto.Notes = view.Notes;
        dto.RejectionReason = view.RejectionReason;
        dto.CreatedAt = view.CreatedAt;
        dto.UpdatedAt = view.UpdatedAt;
        dto.TakenAt = view.TakenAt;
        dto.CompletedAt = view.CompletedAt;
        dto.PaidAt = view.PaidAt;
        dto.PropertyId = view.Location.PropertyId;
        dto.PropertyName = view.Location.PropertyName;
        dto.City = view.Location.City;
        dto.PostalCode = view.Location.PostalCode;
        dto.Address = view.Location.Address;
        dto.Floor = view.Location.Floor;
        dto.AccessNotes = view.Location.AccessNotes;
        dto.ScheduledFor = view.ScheduledFor;
        dto.Stay = view.Stay is null
            ? null
            : new SupplierStayDto { BookingId = view.Stay.BookingId, CheckIn = view.Stay.CheckIn, CheckOut = view.Stay.CheckOut };
        dto.ContactDisclosed = view.ContactDisclosed;
        dto.HostContact = view.HostContact is null
            ? null
            : new SupplierHostContactDto
            {
                Name = view.HostContact.Name,
                Email = view.HostContact.Email,
                Phone = view.HostContact.Phone,
            };

        dto.Source = view.Source;
        dto.ClientId = view.Client.Id;
        dto.ClientName = view.Client.Name;
        dto.ServiceListingId = view.ServiceListingId;
        dto.ServiceName = view.ServiceName;
        dto.ScheduledStart = view.Schedule.StartUtc;
        dto.ScheduledEnd = view.Schedule.EndUtc;
        dto.RespondBy = view.Schedule.RespondBy;
        dto.StartedAt = view.Schedule.StartedAt;
        dto.Price = ServiceRequestDtoParts.ToPriceDto(
            view.Price.EstimatedAmountCents,
            view.Price.QuotedAmountCents,
            view.Price.FinalAmountCents,
            view.Price.FinalAmountNeedsConfirmation,
            view.Price.Lines);
        dto.Proposal = view.Proposal is null
            ? null
            : new ServiceRequestProposalDto
            {
                StartUtc = view.Proposal.StartUtc,
                EndUtc = view.Proposal.EndUtc,
                ProposedAt = view.Proposal.ProposedAt,
                Message = view.Proposal.Message,
            };
        dto.CancelledAt = view.Cancellation?.At;
        dto.CancelledBy = view.Cancellation?.By.ToString();
        dto.CancellationReason = view.Cancellation?.Reason;
        dto.CompletionNotes = view.CompletionNotes;
        // The supplier reads the photos of its own requests through the shared endpoint, the same whatever the context.
        dto.WorkPhotos = ServiceRequestDtoParts.ToPhotoDtos(view.Id, view.WorkPhotos, ServiceRequestDtoParts.ShortRentBasePath);
        return dto;
    }
}
