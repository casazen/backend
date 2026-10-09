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

    public Guid OrgId { get; set; }

    /// <summary>
    /// The stay the request is for: set for every short-rent request created since SU-07 (D2), null for long-rent
    /// requests and for older short-rent requests that could not be traced to a single stay.
    /// </summary>
    public Guid? BookingId { get; set; }

    /// <summary>Rental context the request was opened in (D2): short-rent (per stay) or long-rent (per property).</summary>
    public ServiceRequestRentalContext RentalContext { get; set; } = ServiceRequestRentalContext.ShortRent;

    public Guid PropertyId { get; set; }

    public Guid SupplierOrgId { get; set; }

    [Required, MaxLength(100)]
    public string Category { get; set; } = string.Empty;

    public ServiceRequestUrgency Urgency { get; set; } = ServiceRequestUrgency.Normal;

    /// <summary>
    /// The host's notes for the supplier. Written by the host when it creates the request and never rewritten by the
    /// supplier: what the supplier leaves when it completes the work is <see cref="CompletionNotes"/>.
    /// </summary>
    [MaxLength(1000)]
    public string Notes { get; set; } = string.Empty;

    public ServiceRequestStatus Status { get; set; } = ServiceRequestStatus.Richiesto;

    public DateTime? TakenAt { get; set; }

    [MaxLength(255)]
    public string? TakenByUserId { get; set; }

    public DateTime? CompletedAt { get; set; }

    public DateTime? PaidAt { get; set; }

    public bool ChargeToGuest { get; set; }

    [MaxLength(500)]
    public string? RejectionReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // ─── Schedule and price (SP-04) ──────────────────────────────────────────────────────────────────────────────────
    // Every column below is new in SP-04 and empty on the requests that exist before it: they stay "da concordare" (no
    // backfill). The day of such a request is still the check-out day of its stay.

    /// <summary>
    /// First instant of the work (UTC). <c>null</c> is "da concordare": the time is agreed outside CasaZen, as before
    /// SP-04. Set by the host when it picks a slot of the supplier's agenda, or by the supplier when it takes the request or
    /// when the host accepts the time the supplier proposed. Together with <see cref="ScheduledEndUtc"/> it is the interval
    /// the slot planner keeps free (<c>SupplierOccupancy.TimedRequest</c>).
    /// </summary>
    public DateTime? ScheduledStartUtc { get; set; }

    /// <summary>Instant the work ends (UTC), after <see cref="ScheduledStartUtc"/>; both are set or both are null.</summary>
    public DateTime? ScheduledEndUtc { get; set; }

    /// <summary>
    /// The catalog service of the supplier the request is for (<see cref="SupplierServiceListing"/>), or null (a request
    /// by category only). Set to null by the database if the service is ever hard-deleted; the catalog deletes softly.
    /// </summary>
    public Guid? ServiceListingId { get; set; }

    /// <summary>The name of the service at the moment of the request: later renames or deletions do not rewrite history.</summary>
    [MaxLength(ServiceRequestLimits.ServiceNameMaxLength)]
    public string? ServiceNameSnapshot { get; set; }

    /// <summary>
    /// JSON array of the options (supplements) the customer picked, as a snapshot of the catalog at the time of the request.
    /// Written by the request flows that take options (the showcase booking, SP-10); empty for a host's request.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string OptionsJson { get; set; } = "[]";

    /// <summary>
    /// The price known when the request was made, in cents of euro: the "from" price of a per-job service of the catalog.
    /// <c>null</c> when nothing was known (on quote, per hour, no service): the price is agreed with the supplier.
    /// </summary>
    public int? EstimatedAmountCents { get; set; }

    /// <summary>The price the supplier committed to when it took the request (cents of euro); <c>null</c> while it did not.</summary>
    public int? QuotedAmountCents { get; set; }

    /// <summary>
    /// The total the supplier asks when it completes the work (cents of euro): what <see cref="PriceLinesJson"/> adds up to.
    /// Until the payments of SP-15 exist it is the supplier's declaration, not a payment (the earnings are "estimated").
    /// </summary>
    public int? FinalAmountCents { get; set; }

    /// <summary>
    /// JSON array of <see cref="ServiceRequestPriceLine"/> (<c>kind</c>, <c>label</c>, <c>amountCents</c>) that explain
    /// <see cref="FinalAmountCents"/>: the agreed price and the extras. Empty until the work is completed.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string PriceLinesJson { get; set; } = "[]";

    /// <summary>
    /// Decision D7: the final amount is above the price the customer was quoted by more than the tolerance (20 % by default),
    /// so the customer has to confirm it. SP-04 records the fact; the host's confirmation (SP-15a, <c>POST api/service-requests/{id}/final-amount/confirm</c>) clears it and keeps the trace in <see cref="FinalAmountConfirmedAt"/>.
    /// </summary>
    public bool FinalAmountNeedsConfirmation { get; set; }

    // ─── Payment (SP-15a) ────────────────────────────────────────────────────────────────────────────────────────────

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

    /// <summary>
    /// Who moved the request to <see cref="ServiceRequestStatus.Pagato"/> (SP-15a): the host marking it paid by hand, or the
    /// supplier recording a payment received outside CasaZen (decision D5). The history of the request shows it. <c>null</c> on a
    /// request that is not paid and on the ones paid before SP-15a (the host did).
    /// </summary>
    public ServiceRequestActorParty? PaidBy { get; set; }

    // ─── Lifecycle (SP-04) ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The instant after which a request nobody answered is cancelled by the <c>service-request-auto-cancel</c> job
    /// (decision D8: creation + 120 minutes for a host's request). <c>null</c> on the requests that exist before SP-04 and
    /// on the ones that are not waiting for an answer anymore. Only used while the feature flag
    /// <c>SupplierRequestAutoCancel</c> is on.
    /// </summary>
    public DateTime? ResponseDueAt { get; set; }

    /// <summary>When the supplier started the work (<see cref="ServiceRequestStatus.InCorso"/>).</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>When the request was cancelled (<see cref="ServiceRequestStatus.Annullato"/>).</summary>
    public DateTime? CancelledAt { get; set; }

    /// <summary>Who cancelled it: the host, the supplier (before the start) or CasaZen (no answer in time).</summary>
    public ServiceRequestActorParty? CancelledBy { get; set; }

    /// <summary>
    /// Why it was cancelled: the text the host or the supplier wrote, or the code <c>NoResponse</c>
    /// (<see cref="ServiceRequestCancellationReasons.NoResponse"/>) of the automatic cancellation.
    /// </summary>
    [MaxLength(ServiceRequestLimits.CancellationReasonMaxLength)]
    public string? CancellationReason { get; set; }

    /// <summary>What the supplier wrote for the host when it completed the work (separate from <see cref="Notes"/>).</summary>
    [MaxLength(ServiceRequestLimits.CompletionNotesMaxLength)]
    public string? CompletionNotes { get; set; }

    /// <summary>
    /// JSON array of <see cref="ServiceRequestPhoto"/> (<c>id</c>, <c>key</c>, <c>uploadedAt</c>): the photos of the work,
    /// kept in the <b>private</b> bucket of <c>IFileStorage</c> and read only through an authenticated endpoint.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string WorkPhotosJson { get; set; } = "[]";

    /// <summary>When the host last reminded the supplier to answer (at most one reminder every 6 hours).</summary>
    public DateTime? LastRemindedAt { get; set; }

    // ─── Another time proposed by the supplier (SP-04) ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Start of the time the supplier proposes instead of the requested one (UTC); <c>null</c> while there is no proposal.
    /// The request stays <see cref="ServiceRequestStatus.Richiesto"/> until the host accepts or rejects it.
    /// </summary>
    public DateTime? ProposedStartUtc { get; set; }

    /// <summary>End of the proposed time (UTC); set and cleared together with <see cref="ProposedStartUtc"/>.</summary>
    public DateTime? ProposedEndUtc { get; set; }

    /// <summary>When the supplier made the proposal.</summary>
    public DateTime? ProposedAt { get; set; }

    /// <summary>The supplier member who made it: the one recorded as having taken the request if the host accepts.</summary>
    [MaxLength(255)]
    public string? ProposedByUserId { get; set; }

    /// <summary>An optional message to the host.</summary>
    [MaxLength(ServiceRequestLimits.ProposalMessageMaxLength)]
    public string? ProposalMessage { get; set; }

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
    public Property Property { get; set; } = null!;

    [ForeignKey(nameof(SupplierOrgId))]
    public Org SupplierOrg { get; set; } = null!;
}
