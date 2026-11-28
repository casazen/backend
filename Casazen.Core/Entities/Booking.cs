using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Bookings;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

[Table("Bookings")]
public class Booking : ITenantOwned
{
    public const int OctorateReservationIdMaxLength = 50;

    public const string OctorateReservationIndexName = "UIX_Bookings_OrgId_OctorateReservationId";

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [ForeignKey("Property")]
    public Guid PropertyId { get; set; }
    public virtual Property Property { get; set; } = null!;

    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    [ForeignKey("Guest")]
    public Guid GuestId { get; set; }
    public virtual Guest Guest { get; set; } = null!;

    [Required]
    public DateTime CheckInDate { get; set; }

    [Required]
    public DateTime CheckOutDate { get; set; }

    /// <summary>
    /// Real arrival instant (UTC), recorded when the host registers the check-in. The Alloggiati Web term
    /// (24 hours, or 6 for short stays) runs from here; without it, from the start of the check-in day in Europe/Rome.
    /// </summary>
    public DateTime? ArrivedAt { get; set; }

    /// <summary>
    /// When the booking became confirmed (UTC): a payment that settled, the host's confirmation or acceptance, or a booking
    /// that is confirmed from the start (entered by the host, an OTA stay). Cleared when a cancelled hold is confirmed again.
    /// Null for a booking that was never confirmed, and for every booking confirmed before SR-05a, which was never backfilled
    /// (SR-D17): its confirmation is on the timeline without a time.
    /// </summary>
    public DateTime? ConfirmedAt { get; set; }

    public DateTime? CancelledAt { get; set; }

    public DateTime? CheckedOutAt { get; set; }

    public DateTime? NoShowAt { get; set; }

    public int NumberOfGuests { get; set; }

    [Required]
    public BookingStatus Status { get; set; } = BookingStatus.Pending;

    [Required]
    public BookingSource Source { get; set; } = BookingSource.Direct;

    [MaxLength(500)]
    public string ExternalId { get; set; } = string.Empty;

    public Guid? ICalFeedId { get; set; }

    [MaxLength(PropertyICalFeed.LabelMaxLength)]
    public string? ChannelLabel { get; set; }

    /// <summary>
    /// Where the guest of a booking came from (E27/F01/US01/T01, NF-123): the <c>source</c> of the link the guest booked from (<c>agency-site</c> for a link
    /// the agency copied from CasaZen) or <c>octorate-booking-engine</c> for a reservation of the booking engine of the agency that the import made. With
    /// <see cref="Source"/> it gives the origin of the booking (<see cref="BookingOrigins.Of(BookingSource, string?)"/>); <see cref="Source"/> itself is never
    /// changed by it. A lower-case token, at most <see cref="BookingAttribution.MaxSourceLength"/> characters, never personal data
    /// (<see cref="BookingAttribution.Sanitize"/>); <c>null</c> for every booking that came from no link, and for every booking made before it.
    /// </summary>
    [MaxLength(BookingAttribution.MaxSourceLength)]
    public string? AttributionSource { get; set; }

    [MaxLength(BookingAttribution.MaxMediumLength)]
    public string? AttributionMedium { get; set; }

    [MaxLength(BookingAttribution.MaxCampaignLength)]
    public string? AttributionCampaign { get; set; }

    /// <summary>The host name of the site the guest came from (e.g. <c>www.agenzia.it</c>), never the address of a page: no scheme, credentials, port, path or query.</summary>
    [MaxLength(BookingAttribution.MaxReferrerHostLength)]
    public string? AttributionReferrerHost { get; set; }

    public OtaStayReviewReason? OtaReviewReason { get; set; }

    public DateTime? OtaReviewRaisedAt { get; set; }

    /// <summary>
    /// The id of the reservation on Octorate (E02/F02/US01/T01, ADR-033-R1): the key of the import, unique within the org
    /// (<see cref="OctorateReservationIndexName"/>). <c>null</c> for every booking that does not come from the channel manager and was
    /// not sent to it. <see cref="ExternalId"/> keeps its own meaning (the reference on the channel) and is never used for it.
    /// </summary>
    [MaxLength(OctorateReservationIdMaxLength)]
    public string? OctorateReservationId { get; set; }

    /// <summary>
    /// The <c>updateTime</c> of the Octorate reservation last applied to this booking (UTC). A read of the same reservation with an older
    /// or equal time changes nothing, so two reads that reach the database in swapped order never undo the newer one. <c>null</c> while
    /// the booking has no Octorate reservation, or Octorate gave no time.
    /// </summary>
    public DateTime? OctorateUpdatedAt { get; set; }

    /// <summary>
    /// What the channel kept as commission on an imported booking: the sum of the <c>COMMISSION</c> lines of the price breakdown of the
    /// reservation; <c>null</c> when it has none (never derived from another figure, ADR-033-R2: a screen says "not available", not zero).
    /// Read-only after the import (ADR-033-R3).
    /// </summary>
    [Precision(10, 2)]
    public decimal? OtaCommission { get; set; }

    /// <summary>The net the host receives; <c>null</c> until Octorate documents a field for it (never computed here). Read-only after the import.</summary>
    [Precision(10, 2)]
    public decimal? OtaNetAmount { get; set; }

    [Precision(10, 2)]
    public decimal? OtaPaidAmount { get; set; }

    /// <summary>What is still to pay; <c>null</c> until Octorate documents a field for it (never computed here). Read-only after the import.</summary>
    [Precision(10, 2)]
    public decimal? OtaBalanceAmount { get; set; }

    /// <summary>
    /// Who collects the payment of a booking that came from a channel (E08/F03/US03/T02, NF-77): <c>true</c> when the channel (the company) collects it,
    /// <c>false</c> when the host does; <c>null</c> when Octorate does not say (<c>companyCollect</c> absent or <c>NONE</c>, as in the sandbox samples) and for
    /// every booking that does not come from a channel. Read-only after the import, like the figures above (ADR-033-R3): only the import writes it.
    /// </summary>
    public bool? OtaCompanyCollect { get; set; }

    public bool? OtaCommissionIncludedInPayments { get; set; }

    [Precision(18, 2)]
    public decimal BasePrice { get; set; }

    [Precision(18, 2)]
    public decimal CleaningFee { get; set; }

    [Precision(18, 2)]
    public decimal TouristTax { get; set; }

    [Precision(18, 2)]
    public decimal TotalPrice { get; set; }

    public bool PriceOverridden { get; set; }

    [MaxLength(Services.ManualBookingRules.PriceNoteMaxLength)]
    public string? PriceOverrideNote { get; set; }

    [Precision(18, 2)]
    public decimal TouristTaxAmount { get; set; }

    public int NumberOfAdults { get; set; }

    public int NumberOfChildren { get; set; }

    [MaxLength(1000)]
    public string SpecialRequests { get; set; } = string.Empty;

    /// <summary>
    /// When the guest of a direct booking thinks they will arrive (DB-03b): <c>HH:mm</c>, or <c>late</c> for after the check-in window;
    /// <c>null</c> when they did not say, and for every booking that does not come from the public checkout. A note for the host
    /// ("arrivo previsto alle…"), never a rule (<see cref="Services.ArrivalTimeRules"/>). Cleared with the guest's other free-text data when
    /// the guest is erased.
    /// </summary>
    [MaxLength(Services.ArrivalTimeRules.MaxLength)]
    public string? ExpectedArrivalTime { get; set; }

    /// <summary>
    /// Plate of the car the guests arrive with («Numero autovettura» of the Ticino police file, NF-132, casazen/backend#979); null when not given.
    /// Optional, asked only where the property is in canton Ticino; stored upper case without spaces (<see cref="Regulatory.VehiclePlates"/>,
    /// at most <see cref="Regulatory.VehiclePlates.MaxLength"/> characters, checked on input). Encrypted at rest (<c>EncryptedColumns</c>): the
    /// column is <c>text</c>. Cleared with the guest's data when the guest is erased or the Alloggiati data retention runs.
    /// </summary>
    public string? VehiclePlate { get; set; }

    [Required]
    public PaymentOption PaymentOption { get; set; } = PaymentOption.Immediate;

    /// <summary>
    /// Last day (Europe/Rome) of the full refund: from the property's cancellation policy when it has one, otherwise
    /// check-in − 7 days (<see cref="Services.DirectBookingPaymentRules.FreeRefundDeadline"/>). Also the day the
    /// deferred payment is charged.
    /// </summary>
    public DateTime? FreeRefundDeadline { get; set; }

    [MaxLength(255)]
    public string? StripeSetupIntentId { get; set; }

    [MaxLength(255)]
    public string? StripePaymentMethodId { get; set; }

    [MaxLength(255)]
    public string? StripeCustomerId { get; set; }

    public int DeferredChargeAttempts { get; set; }

    public DateTime? DeferredChargeLastAttemptOn { get; set; }

    /// <summary>
    /// When the deferred charge failed in a way the guest must fix (authentication required, card declined): the guest
    /// and the host were emailed once, the guest with a link to pay on the outcome page. The automatic cancellation counts
    /// <c>DirectBooking:DeferredChargeCancelAfterDays</c> from this instant's Rome day. Cleared when the payment succeeds.
    /// </summary>
    public DateTime? DeferredChargeFailedAt { get; set; }

    public DateTime? CheckoutWizardStartedAt { get; set; }

    public BookingCancellationReason? CancellationReason { get; set; }

    [MaxLength(500)]
    public string? CancellationNote { get; set; }

    public DateTime? GuestEmailVerifiedAt { get; set; }

    [MaxLength(64)]
    public string? GuestEmailVerificationTokenHash { get; set; }

    public DateTime? RequestExpiresAt { get; set; }

    [MaxLength(64)]
    public string? CheckoutTokenHash { get; set; }

    public HostPaymentArrangement? HostPaymentArrangement { get; set; }

    public DateTime? PaymentLinkSentAt { get; set; }

    [MaxLength(64)]
    public string? GuestCancelTokenHash { get; set; }

    public DateTime? GuestCancelTokenExpiresAt { get; set; }

    /// <summary>
    /// Booking code of the guest (BK-11, A3-10): shown in the confirmation email and on the checkout outcome page, and
    /// asked with the guest's email by "Le mie prenotazioni". Random, readable, unique per org, never the booking id
    /// (<see cref="Services.BookingCodes"/>). Stored without separator, shown as <c>XXXXX-XXXXX</c>.
    /// </summary>
    [Required]
    [MaxLength(Services.BookingCodes.Length)]
    public string BookingCode { get; set; } = Services.BookingCodes.New();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public virtual ICollection<Payment> Payments { get; set; } = new List<Payment>();
    public virtual ICollection<AlloggiatiWebReport> AlloggiatiWebReports { get; set; } = new List<AlloggiatiWebReport>();

    /// <summary>Guests staying, one per line of the Alloggiati communication (CO-12), ordered by <see cref="StayGuest.Position"/>.</summary>
    public virtual ICollection<StayGuest> StayGuests { get; set; } = new List<StayGuest>();
}

public enum BookingStatus
{
    Pending,
    Confirmed,
    CheckedIn,
    CheckedOut,
    Cancelled
}

public enum BookingSource
{
    Direct,
    Airbnb,
    BookingCom,
    Expedia,
    Vrbo,
    TripAdvisor,
    Agoda,
    Local,

    Manual,

    OtherChannel = 9,
}

public enum BookingCancellationReason
{
    CheckoutHoldExpired = 1,

    DatesUnavailableAtPayment = 2,

    OnSiteRequestDeclined = 3,

    OnSiteRequestExpired = 4,

    OnSiteEmailNotConfirmed = 5,

    DeferredPaymentNotCompleted = 6,

    GuestRequested = 7,
}

public enum PaymentOption
{
    Immediate,

    OnCancellationDeadline,

    /// <summary>
    /// Pay at the property, no online payment. Never confirmed at once (decision D5, BK-06): the booking stays
    /// <see cref="BookingStatus.Pending"/> as a request that the guest confirms by email and the host accepts or
    /// declines (<see cref="Services.OnSiteRequests"/>).
    /// </summary>
    OnSite
}
