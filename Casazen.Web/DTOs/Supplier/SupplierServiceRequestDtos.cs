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

    /// <summary>
    /// How the request is paid (SP-15a), fixed when the supplier took it: <c>Online</c> (inside CasaZen, with the commission;
    /// the supplier asks for the payment with <c>POST api/supplier/requests/{id}/payment-request</c>) or <c>Manual</c> (outside,
    /// the host or the supplier records it). <c>Manual</c> for every earlier request.
    /// </summary>
    public string PaymentMode { get; set; } = nameof(Casazen.Core.Entities.Enums.ServiceRequestPaymentMode.Manual);

    /// <summary>
    /// The request was <b>accepted automatically</b> (SP-04d): the supplier turned on the acceptance of its regular hosts, so the request was
    /// born <c>PresoInCarico</c> with <see cref="TakenAt"/> equal to <see cref="CreatedAt"/>, and no member of the supplier took it. The
    /// supplier can still cancel it, with a reason, until the work starts. <c>false</c> for every other request.
    /// </summary>
    public bool AutoAccepted { get; set; }

    // ─── Phase and payment summary (SP-04e) ───

    /// <summary>
    /// Where the request is for the console, derived once by the server so the frontend repeats no rule (<see cref="SupplierRequestPhases"/>):
    /// <c>to_answer</c> (the supplier has to answer), <c>proposal_pending</c> (it proposed another time and waits for the customer),
    /// <c>scheduled</c>, <c>in_progress</c>, <c>completed</c> (done, not paid), <c>paid</c>, <c>rejected</c>, <c>cancelled</c>. <b>An open set</b>:
    /// <c>quote_pending</c> (SP-04b) and the phases of the showcase's trattative and of the held payments will join it, and <c>unknown</c>
    /// stands for a status this version does not know; show a generic wording for a value you do not know. <see cref="Status"/> is unchanged.
    /// </summary>
    public string Phase { get; set; } = SupplierRequestPhases.Unknown;

    /// <summary>
    /// The payment of a completed job (see <see cref="SupplierRequestPaymentDto"/>): <b>null</b> unless the request has a payment record or is
    /// <c>Completato</c>. The same object in the inbox, in <c>today</c> and in the detail.
    /// </summary>
    public SupplierRequestPaymentDto? Payment { get; set; }
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

    /// <summary>
    /// <c>Host</c>, <c>Supplier</c>, <c>System</c> (the automatic cancellation, or the take of a request accepted automatically, SP-04d:
    /// <c>status</c> <c>PresoInCarico</c> with this actor is "Accettata in automatico") or <c>Customer</c> (a private customer of the
    /// public showcase, SP-10).
    /// </summary>
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

    /// <summary>
    /// The jobs of today (taken, in progress, completed or paid), the first in time first; with <c>?days=</c> (SP-04e) also the ones of the
    /// next days, up to <see cref="DateTo"/>.
    /// </summary>
    public IEnumerable<SupplierServiceRequestDto> Jobs { get; set; } = [];

    /// <summary>The first new requests waiting for the answer, the one to answer first on top.</summary>
    public IEnumerable<SupplierServiceRequestDto> NewRequests { get; set; } = [];

    /// <summary>How many requests wait for the supplier's answer.</summary>
    public int NewRequestsTotal { get; set; }

    public SupplierTodayEarningsDto Earnings { get; set; } = new();

    /// <summary>
    /// Average minutes from the arrival of a request to its take (<c>takenAt − createdAt</c>) over the requests taken in the
    /// last 90 days; <c>null</c> when there are none. The requests accepted automatically (<c>autoAccepted</c>, SP-04d) are left out.
    /// </summary>
    public int? AverageResponseMinutes { get; set; }

    /// <summary>How many days <see cref="Jobs"/> cover from <see cref="Date"/> (SP-04e, <c>?days=</c> from 1 to 7, 1 by default).</summary>
    public int Days { get; set; } = 1;

    /// <summary>The last day <see cref="Jobs"/> cover (<c>YYYY-MM-DD</c>, Europe/Rome): <see cref="Date"/> plus <see cref="Days"/> minus one.</summary>
    public DateOnly DateTo { get; set; }

    /// <summary>
    /// How many jobs fall on those days (SP-04e): more than the length of <see cref="Jobs"/> when the list was cut at 50 (the first in
    /// time are the ones kept).
    /// </summary>
    public int JobsTotal { get; set; }
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
        Days = today.Days,
        DateTo = today.LastDate,
        TimeZone = Casazen.Core.Utilities.RomeCalendar.TimeZoneId,
        Jobs = today.Jobs.Select(view => ToDto(view)).ToList(),
        JobsTotal = today.JobsTotal,
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
        dto.PaymentMode = view.PaymentMode.ToString();
        dto.AutoAccepted = view.AutoAccepted;
        dto.Phase = view.Phase;
        dto.Payment = view.Payment is null ? null : SupplierRequestPaymentDto.From(view.Payment);
        return dto;
    }
}

/// <summary>
/// Where the payment of a completed job stands and what the supplier may do about it (SP-04e). It exists when the request has a payment
/// record or is <c>Completato</c>; a completed job paid outside CasaZen has no record yet (<see cref="Id"/>, <see cref="Status"/> and the
/// commission fields are then null and <see cref="AmountCents"/> is the total the supplier declared). The commission is the supplier's own
/// figure: the payer never sees it. No text may promise a payout date.
/// </summary>
public class SupplierRequestPaymentDto
{
    /// <summary>The payment record; null while the job has none.</summary>
    public Guid? Id { get; set; }

    /// <summary>
    /// The status of the record: <c>Requested</c>, <c>Processing</c>, <c>Paid</c>, <c>Failed</c>, <c>PartiallyRefunded</c>, <c>Refunded</c> or
    /// <c>NeedsReview</c>; null while there is no record. <b>An open set</b>: the held payments of SP-15c will add values.
    /// </summary>
    public string? Status { get; set; }

    /// <summary>What the payer pays, gross, in cents: the record's amount, else the total the supplier declared.</summary>
    public int? AmountCents { get; set; }

    /// <summary>The commission percentage the record was created with (a snapshot, never recomputed); null while there is no record.</summary>
    public decimal? CommissionPercent { get; set; }

    /// <summary>The commission CasaZen keeps, in cents (0 when none is charged, as for a payment recorded as received outside CasaZen); null while there is no record.</summary>
    public int? CommissionCents { get; set; }

    /// <summary>What the supplier receives before Stripe's own fees, in cents; null while there is no record.</summary>
    public int? NetCents { get; set; }

    public DateTime? PaidAt { get; set; }

    /// <summary><c>Stripe</c> or <c>Offline</c>; null until it is paid.</summary>
    public string? PaidVia { get; set; }

    /// <summary>When the payer was first asked; null while nothing was sent.</summary>
    public DateTime? RequestedAt { get; set; }

    /// <summary>When the payer was last sent a link; the supplier can ask again a day later.</summary>
    public DateTime? LastSentAt { get; set; }

    /// <summary>Emails with a link sent so far (the request and its reminders).</summary>
    public int SentCount { get; set; }

    /// <summary>The payer is late: the payment was flagged late (<c>SupplierPayments:LateAfterDays</c> after the first request) and is still owed.</summary>
    public bool Late { get; set; }

    /// <summary>The final amount is above the quote by more than the tolerance and the host has not confirmed it yet (decision D7).</summary>
    public bool NeedsAmountConfirmation { get; set; }

    /// <summary>
    /// The supplier may ask the payer for the payment, or remind them, now: <c>POST api/supplier/requests/{id}/payment-request</c> would not
    /// refuse. False while the flag <c>SupplierOnlinePayments</c> is off.
    /// </summary>
    public bool CanRequest { get; set; }

    /// <summary>
    /// The supplier may record that it was paid outside CasaZen now: <c>POST api/supplier/requests/{id}/payment/offline</c> would not refuse
    /// (a reason is mandatory for a request paid inside CasaZen). Also with the flag off.
    /// </summary>
    public bool CanRecordOffline { get; set; }

    internal static SupplierRequestPaymentDto From(SupplierJobPayment payment) => new()
    {
        Id = payment.Id,
        Status = payment.Status?.ToString(),
        AmountCents = payment.AmountCents,
        CommissionPercent = payment.CommissionPercent,
        CommissionCents = payment.CommissionCents,
        NetCents = payment.NetCents,
        PaidAt = payment.PaidAt,
        PaidVia = payment.PaidVia?.ToString(),
        RequestedAt = payment.RequestedAt,
        LastSentAt = payment.LastSentAt,
        SentCount = payment.SentCount,
        Late = payment.Late,
        NeedsAmountConfirmation = payment.NeedsAmountConfirmation,
        CanRequest = payment.CanRequest,
        CanRecordOffline = payment.CanRecordOffline,
    };
}

/// <summary>
/// Body of <c>GET api/supplier/dashboard/overview?period=</c> (SP-04e): the three numbers of the console home (how often the supplier
/// says yes, how long it takes to answer, what comes in) for a period and for the same number of Europe/Rome days just before it, so the
/// console can say "better" or "worse" without a calculation of its own. Definitions: <c>docs/runbooks/suppliers.md</c> § 27.
/// </summary>
public class SupplierOverviewDto
{
    /// <summary><c>current-month</c> (the 1st to today), <c>last-30-days</c> or <c>last-3-months</c>: what was asked.</summary>
    public string Period { get; set; } = string.Empty;

    /// <summary>The time zone of the days: <c>Europe/Rome</c>.</summary>
    public string TimeZone { get; set; } = string.Empty;

    /// <summary>
    /// The supplier has money through CasaZen to show: the flag <c>SupplierOnlinePayments</c> is on <b>and</b> it has at least one payment.
    /// When false the money figures are 0 and the console talks about the jobs completed ("Lavori completati"), never about "Incassi".
    /// </summary>
    public bool OnlinePayments { get; set; }

    /// <summary>The period asked for.</summary>
    public SupplierOverviewPeriodDto Current { get; set; } = new();

    /// <summary>The same number of days just before <see cref="Current"/> (adjacent and of the same length).</summary>
    public SupplierOverviewPeriodDto Previous { get; set; } = new();
}

/// <summary>
/// The figures of one period of the overview. A figure that does not exist (a rate when nobody was answered, an average when nothing was
/// taken) is <c>null</c>, never 0: the console shows a dash.
/// </summary>
public class SupplierOverviewPeriodDto
{
    /// <summary>First day (<c>YYYY-MM-DD</c>, Europe/Rome, included).</summary>
    public DateOnly From { get; set; }

    /// <summary>Last day (<c>YYYY-MM-DD</c>, Europe/Rome, included).</summary>
    public DateOnly To { get; set; }

    /// <summary>Requests that arrived in the period, whatever became of them.</summary>
    public int Received { get; set; }

    /// <summary>Requests the supplier took in the period (a request cancelled after the take is still one).</summary>
    public int Accepted { get; set; }

    /// <summary>Requests the supplier refused in the period.</summary>
    public int Rejected { get; set; }

    /// <summary>Accepted over accepted plus rejected, from 0 to 1 with four decimals; null when the supplier answered none.</summary>
    public double? AcceptanceRate { get; set; }

    /// <summary>
    /// Average minutes from the arrival of a request to its take (<c>takenAt − createdAt</c>) over the requests taken in the period; null when
    /// none was. The same figure as <c>GET api/supplier/today</c>, for the period; the public showcase shows the median instead. The
    /// requests accepted automatically (<c>autoAccepted</c>, SP-04d) are left out of it, and count in <see cref="Accepted"/>.
    /// </summary>
    public int? AverageResponseMinutes { get; set; }

    /// <summary>Jobs completed in the period (by completion date; a paid one counts as completed).</summary>
    public int CompletedJobs { get; set; }

    /// <summary>The net, in cents, of the payments paid in the period (by payment date); 0 unless <see cref="SupplierOverviewDto.OnlinePayments"/>.</summary>
    public long PaidNetCents { get; set; }

    /// <summary>The gross, in cents, of the payments paid in the period; 0 unless <see cref="SupplierOverviewDto.OnlinePayments"/>.</summary>
    public long PaidGrossCents { get; set; }

    /// <summary>The commission CasaZen kept on them, in cents; 0 unless <see cref="SupplierOverviewDto.OnlinePayments"/>.</summary>
    public long CommissionCents { get; set; }

    /// <summary>
    /// What is still to be collected <b>now</b>, in cents: the total the supplier declared for a job paid outside CasaZen, the net of the
    /// payment for one paid inside it. Only in <c>current</c>; null in <c>previous</c>.
    /// </summary>
    public long? ToCollectCents { get; set; }

    /// <summary>How many jobs are still to be collected now. Only in <c>current</c>; null in <c>previous</c>.</summary>
    public int? ToCollectJobs { get; set; }
}

/// <summary>
/// Body of <c>GET api/supplier/inbox/counts</c> (SP-04e): the numbers on the chips of the list and on the menu, computed with the same
/// rules as the tabs of <c>GET api/supplier/inbox</c> so a number and its list cannot disagree.
/// </summary>
public class SupplierInboxCountsDto
{
    /// <summary>Tab <c>da-rispondere</c>: new requests the supplier still has to answer.</summary>
    public int ToAnswer { get; set; }

    /// <summary>Tab <c>in-attesa</c>: requests the supplier answered with a proposed time the customer has not answered. <c>nuove</c> holds <c>toAnswer</c> plus this.</summary>
    public int AwaitingCustomer { get; set; }

    /// <summary>Tab <c>programmate</c>: taken or in progress.</summary>
    public int Scheduled { get; set; }

    /// <summary>Tab <c>da-incassare</c>: completed and not paid yet.</summary>
    public int ToCollect { get; set; }

    /// <summary>Tab <c>archivio</c>: paid, rejected or cancelled.</summary>
    public int Archive { get; set; }

    /// <summary>Payments asked for that are late and still owed.</summary>
    public int PaymentsLate { get; set; }
}

/// <summary>Maps the read models of the console home (SP-04e) to their API DTOs.</summary>
public static class SupplierReadModelsMapper
{
    public static SupplierOverviewDto ToDto(SupplierOverview overview) => new()
    {
        Period = SupplierKpiPeriods.OverviewToken(overview.Period),
        TimeZone = Casazen.Core.Utilities.RomeCalendar.TimeZoneId,
        OnlinePayments = overview.OnlinePayments,
        Current = ToDto(overview.Current),
        Previous = ToDto(overview.Previous),
    };

    public static SupplierInboxCountsDto ToDto(SupplierInboxCounts counts) => new()
    {
        ToAnswer = counts.ToAnswer,
        AwaitingCustomer = counts.AwaitingCustomer,
        Scheduled = counts.Scheduled,
        ToCollect = counts.ToCollect,
        Archive = counts.Archive,
        PaymentsLate = counts.PaymentsLate,
    };

    private static SupplierOverviewPeriodDto ToDto(SupplierOverviewPeriod period) => new()
    {
        From = period.From,
        To = period.To,
        Received = period.Received,
        Accepted = period.Accepted,
        Rejected = period.Rejected,
        AcceptanceRate = period.AcceptanceRate,
        AverageResponseMinutes = period.AverageResponseMinutes,
        CompletedJobs = period.CompletedJobs,
        PaidNetCents = period.PaidNetCents,
        PaidGrossCents = period.PaidGrossCents,
        CommissionCents = period.CommissionCents,
        ToCollectCents = period.ToCollectCents,
        ToCollectJobs = period.ToCollectJobs,
    };
}
