using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;

namespace Casazen.Core.Entities;

[Table("ServiceRequests")]
public class ServiceRequest
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The org the request belongs to: the <b>host</b> org for a host's request (short-rent or long-rent context) and the
    /// <b>supplier</b> org for a request born from the supplier's public showcase (<see cref="ServiceRequestRentalContext.Showcase"/>,
    /// SP-10): the data belongs to the supplier, never to a host org.
    /// </summary>
    public Guid OrgId { get; set; }

    /// <summary>
    /// The stay the request is for: set for every short-rent request created since SU-07 (D2), null for long-rent
    /// requests, for older short-rent requests that could not be traced to a single stay and for showcase requests.
    /// </summary>
    public Guid? BookingId { get; set; }

    /// <summary>
    /// Rental context the request was opened in (D2): short-rent (per stay), long-rent (per property) or, since SP-10, the
    /// supplier's public showcase. <c>CK_ServiceRequests_Context</c> ties it to <see cref="PropertyId"/>, <see cref="BookingId"/>,
    /// <see cref="Source"/>, <see cref="CustomerId"/>, <see cref="PublicCode"/> and the <c>Location*</c> columns.
    /// </summary>
    public ServiceRequestRentalContext RentalContext { get; set; } = ServiceRequestRentalContext.ShortRent;

    public Guid? PropertyId { get; set; }

    public Guid SupplierOrgId { get; set; }

    [Required, MaxLength(100)]
    public string Category { get; set; } = string.Empty;

    public ServiceRequestUrgency Urgency { get; set; } = ServiceRequestUrgency.Normal;

    [MaxLength(1000)]
    public string Notes { get; set; } = string.Empty;

    public ServiceRequestStatus Status { get; set; } = ServiceRequestStatus.Richiesto;

    public DateTime? TakenAt { get; set; }

    [MaxLength(255)]
    public string? TakenByUserId { get; set; }

    /// <summary>
    /// True when the request was born already taken (SP-04d): the supplier turned on the automatic acceptance of its regular hosts
    /// (<see cref="SupplierSettings.AutoAcceptRegulars"/>) and the host had at least
    /// <see cref="Casazen.Core.Options.ServiceRequestOptions.RegularHostMinJobs"/> jobs taken with it. Such a request is
    /// <see cref="ServiceRequestStatus.PresoInCarico"/> from its creation, with <see cref="TakenAt"/> equal to it and no
    /// <see cref="TakenByUserId"/>; the history shows the take as made by CasaZen and the supplier's response time leaves it out.
    /// False for every other request, and for every one that exists before SP-04d.
    /// </summary>
    public bool AutoAccepted { get; set; }

    public DateTime? CompletedAt { get; set; }

    public DateTime? PaidAt { get; set; }

    public decimal? HostSurchargePercent { get; set; }

    public bool ChargeToGuest { get; set; }

    [MaxLength(500)]
    public string? RejectionReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ScheduledStartUtc { get; set; }

    public DateTime? ScheduledEndUtc { get; set; }

    public Guid? ServiceListingId { get; set; }

    [MaxLength(ServiceRequestLimits.ServiceNameMaxLength)]
    public string? ServiceNameSnapshot { get; set; }

    [Column(TypeName = "jsonb")]
    public string OptionsJson { get; set; } = "[]";

    public int? EstimatedAmountCents { get; set; }

    public int? QuotedAmountCents { get; set; }

    public int? FinalAmountCents { get; set; }

    [Column(TypeName = "jsonb")]
    public string PriceLinesJson { get; set; } = "[]";

    public bool FinalAmountNeedsConfirmation { get; set; }

    /// <summary>
    /// How the request is paid (SP-15a, decisions D2 and D5), <b>fixed when the supplier takes the request</b>: <c>Online</c> only if
    /// the payments flag was on and the supplier's Stripe account could take charges and payouts at that moment (no payment before
    /// the KYC), <c>Manual</c> otherwise. The requests that exist before SP-15a are <c>Manual</c>. The only change after the take
    /// is the fall back to <c>Manual</c> when the work is completed and nothing can be charged online (see
    /// <c>ISupplierPaymentService.PlanAsync</c>). The payment itself is a <see cref="ServiceRequestPayment"/>.
    /// </summary>
    public ServiceRequestPaymentMode PaymentMode { get; set; } = ServiceRequestPaymentMode.Manual;

    /// <summary>
    /// When the host confirmed a final amount that was above the quote by more than the tolerance (decision D7). The confirmation
    /// clears <see cref="FinalAmountNeedsConfirmation"/> (so every reader sees that nothing is left to confirm) and keeps the trace
    /// here; a request paid inside CasaZen gets its payment at this moment. Null on a request that never needed it.
    /// </summary>
    public DateTime? FinalAmountConfirmedAt { get; set; }

    public ServiceRequestActorParty? PaidBy { get; set; }

    public DateTime? ResponseDueAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CancelledAt { get; set; }

    public ServiceRequestActorParty? CancelledBy { get; set; }

    [MaxLength(ServiceRequestLimits.CancellationReasonMaxLength)]
    public string? CancellationReason { get; set; }

    [MaxLength(ServiceRequestLimits.CompletionNotesMaxLength)]
    public string? CompletionNotes { get; set; }

    [Column(TypeName = "jsonb")]
    public string WorkPhotosJson { get; set; } = "[]";

    public DateTime? LastRemindedAt { get; set; }

    public Guid? GroupId { get; set; }

    public ServiceRequestGroupRole GroupRole { get; set; } = ServiceRequestGroupRole.None;

    public DateTime? BackupAcceptedAt { get; set; }

    [MaxLength(255)]
    public string? BackupAcceptedByUserId { get; set; }

    /// <summary>
    /// The lease contract a long-rent request was made for (LR-03c, LR-08a-FU1), when the landlord made it from the sheet of that contract:
    /// always a lease of the request's own org and property (the service checks it). <c>null</c> on every request made before, on a
    /// long-rent request made for the property alone and on every short-rent and showcase one (<c>CK_ServiceRequests_LeaseContract</c>).
    /// A link and nothing more: no datum of the tenant goes with the request and the supplier's views never carry it (decision LR-D22).
    /// Set to null by the database if the lease is ever deleted for good (a draft is); the request stays one for the property.
    /// </summary>
    public Guid? LeaseContractId { get; set; }

    // ─── The VAT of the cleaning company, for the recharge to the owner (FIX/BE-CLEANING-VAT) ─────────────────────────
    // The answer of the agency to «La ditta di pulizie ha applicato l'IVA su questa fattura?» for a cleaning request, set only through
    // POST api/service-requests/{id}/cleaning-vat. Empty on every request nobody answered (the remembered default of the company may answer for it).
    // Only the host's: the supplier's views never carry it.

    public CleaningSupplierVat? SupplierVat { get; set; }

    [Column(TypeName = "numeric(5,2)")]
    public decimal? SupplierVatRatePercent { get; set; }

    public DateTime? SupplierVatAnsweredAt { get; set; }

    [MaxLength(255)]
    public string? SupplierVatAnsweredByUserId { get; set; }

    public DateTime? SupplierVatAskedAgainAt { get; set; }

    /// <summary>
    /// Start of the time the supplier proposes instead of the requested one (UTC); <c>null</c> while there is no proposal.
    /// The request stays <see cref="ServiceRequestStatus.Richiesto"/> until the host accepts or rejects it.
    /// </summary>
    public DateTime? ProposedStartUtc { get; set; }

    public DateTime? ProposedEndUtc { get; set; }

    public DateTime? ProposedAt { get; set; }

    [MaxLength(255)]
    public string? ProposedByUserId { get; set; }

    [MaxLength(ServiceRequestLimits.ProposalMessageMaxLength)]
    public string? ProposalMessage { get; set; }

    // ─── Quote with the customer's approval (SP-04b) ─────────────────────────────────────────────────────────────────
    // An offer the supplier made on a request still waiting for its answer (Richiesto), like the proposed time: not an eighth status.
    // The columns below are empty unless a quote waits for the customer, who is the host or, for a showcase request, the private
    // customer. One offer at a time: a quote and a proposed time are never both there (CK_ServiceRequests_QuoteOffer).

    public DateTime? QuoteSentAt { get; set; }

    [MaxLength(255)]
    public string? QuoteSentByUserId { get; set; }

    [Column(TypeName = "jsonb")]
    public string QuoteLinesJson { get; set; } = "[]";

    public int? QuoteTotalCents { get; set; }

    [MaxLength(ServiceRequestLimits.QuoteNoteMaxLength)]
    public string? QuoteNote { get; set; }

    /// <summary>
    /// Start of another time the quote offers instead of the request's own (UTC); <c>null</c> when the quote leans on the time the request
    /// already has. The offer does not hold the slot: it is checked again, under the supplier's calendar lock, when the customer accepts.
    /// </summary>
    public DateTime? QuoteScheduledStartUtc { get; set; }

    public DateTime? QuoteScheduledEndUtc { get; set; }

    [Column(TypeName = "jsonb")]
    public string QuoteEventsJson { get; set; } = "[]";

    public ServiceRequestSource Source { get; set; } = ServiceRequestSource.Host;

    public ServiceRequestKind Kind { get; set; } = ServiceRequestKind.Standard;

    [MaxLength(Services.BookingCodes.Length)]
    public string? PublicCode { get; set; }

    public Guid? CustomerId { get; set; }

    /// <summary>ISTAT code (6 digits) of the comune of the work, when the customer chose it from the official list.</summary>
    [MaxLength(6)]
    public string? LocationComuneIstat { get; set; }

    [MaxLength(ShowcaseBookingLimits.CityMaxLength)]
    public string? LocationCity { get; set; }

    [MaxLength(10)]
    public string? LocationPostalCode { get; set; }

    public string? LocationAddress { get; set; }

    public string? LocationFloor { get; set; }

    public string? LocationAccessNotes { get; set; }

    public DateTime? ReminderSentAt { get; set; }

    /// <summary>
    /// When the reminder of the day before was decided for the <b>supplier</b> (SP-16d, the same job <c>service-request-reminders</c>, 18:00
    /// Europe/Rome, any context of the request): once per request. It is set in the save that precedes the queueing, and also when the
    /// reminder is not sent because the supplier turned it off or is suspended, so a request is judged once and not at every run. Requests that
    /// existed before the column have no value and are judged by the same rule: the work must still be ahead.
    /// </summary>
    public DateTime? SupplierReminderSentAt { get; set; }

    /// <summary>
    /// When the reminder at half of the time to answer was queued for the supplier of a request on measure that nobody had answered yet (SP-10c, job
    /// <c>service-request-expiry</c>, <see cref="Suppliers.ShowcaseReplyReminderRules"/>): once per request. It is saved before the mail and the push are
    /// queued, only if nobody touched the request since it was read, so a supplier that answered meanwhile is not reminded. Null on every other request.
    /// </summary>
    public DateTime? ReplyReminderHalfSentAt { get; set; }

    public DateTime? ReplyReminderLastSentAt { get; set; }

    /// <summary>
    /// Optimistic concurrency token (A4-19): mapped to PostgreSQL's <c>xmin</c> system column, which changes with every
    /// update of the row (no column of its own). Two transitions that read the same state cannot both be saved: the
    /// second save fails and the caller gets 409 <c>service_request_state_changed</c>.
    /// </summary>
    public uint Version { get; set; }

    [ForeignKey(nameof(OrgId))]
    public Org Org { get; set; } = null!;

    [ForeignKey(nameof(BookingId))]
    public Booking? Booking { get; set; }

    [ForeignKey(nameof(PropertyId))]
    public Property? Property { get; set; }

    [ForeignKey(nameof(SupplierOrgId))]
    public Org SupplierOrg { get; set; } = null!;

    [ForeignKey(nameof(CustomerId))]
    public ServiceCustomer? Customer { get; set; }

    [ForeignKey(nameof(GroupId))]
    public ServiceRequestGroup? Group { get; set; }
}
