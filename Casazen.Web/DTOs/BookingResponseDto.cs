using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

public class BookingResponseDto
{
    public Guid Id { get; set; }

    /// <summary>
    /// The code the guest knows, as shown to people (<c>XXXXX-XXXXX</c>): in the confirmation email, on the checkout outcome page
    /// and in "Le mie prenotazioni". What the host reads out on the phone and searches by (SR-03); never the booking id.
    /// </summary>
    public string BookingCode { get; set; } = string.Empty;

    public Guid PropertyId { get; set; }
    public string? PropertyName { get; set; }
    public DateTime CheckInDate { get; set; }
    public DateTime CheckOutDate { get; set; }
    public int NumberOfGuests { get; set; }

    /// <summary>Adults and minors among <see cref="NumberOfGuests"/> (every guest an adult when the split is unknown).</summary>
    public int NumberOfAdults { get; set; }
    public int NumberOfChildren { get; set; }

    public decimal TotalPrice { get; set; }

    /// <summary>Lodging plus <see cref="CleaningFee"/>, tourist tax excluded.</summary>
    public decimal BasePrice { get; set; }

    /// <summary>Cleaning fee included in <see cref="BasePrice"/>: the lodging is <c>BasePrice - CleaningFee</c>.</summary>
    public decimal CleaningFee { get; set; }

    public decimal TouristTax { get; set; }

    /// <summary>
    /// The host entered a total other than the quote's when they created the booking by hand (SR-06a): <see cref="TotalPrice"/> is the
    /// price they agreed with the guest, <see cref="TouristTax"/> stays the tax of the quote.
    /// </summary>
    public bool PriceOverridden { get; set; }

    /// <summary>The host's note on that price (at most 80 characters); <c>null</c> when there is none.</summary>
    public string? PriceOverrideNote { get; set; }

    public string Currency { get; set; } = "EUR";
    public string Status { get; set; } = string.Empty;

    /// <summary>Reason the host wrote when cancelling (host only).</summary>
    public string? CancellationNote { get; set; }
    public string Source { get; set; } = string.Empty;
    public string SpecialRequests { get; set; } = string.Empty;

    /// <summary>
    /// When the guest said they would arrive (DB-03b), from the public checkout: <c>HH:mm</c>, or <c>late</c> for after the check-in window;
    /// <c>null</c> when they did not say and for the bookings that do not come from the checkout. The note "arrivo previsto alle…".
    /// </summary>
    public string? ExpectedArrivalTime { get; set; }

    /// <summary>Immediate, OnCancellationDeadline or OnSite.</summary>
    public string PaymentOption { get; set; } = string.Empty;

    /// <summary>
    /// Open "pay at the property" request (BK-06, D5): <c>AwaitingGuestEmail</c> or <c>AwaitingHostApproval</c>;
    /// <c>null</c> for every other booking.
    /// </summary>
    public string? OnSiteRequestState { get; set; }

    /// <summary>Deadline of an open "pay at the property" request (the host's answer once the email is confirmed).</summary>
    public DateTime? RequestExpiresAt { get; set; }

    /// <summary>
    /// OTA stay created by the host from an iCal block (CO-21): the import feed it came from, kept when the calendar is
    /// disconnected. Null for every other booking.
    /// </summary>
    public Guid? IcalFeedId { get; set; }

    /// <summary>Label of that feed when the stay was created (e.g. "camera 2"), shown next to the source.</summary>
    public string? ChannelLabel { get; set; }

    /// <summary>
    /// OTA stay "da verificare" (CO-21): <c>BlockRemoved</c> (the reservation left the channel's calendar) or
    /// <c>BlockDatesChanged</c> (other dates on the channel). Null when there is nothing to check, and for a cancelled stay.
    /// </summary>
    public string? OtaReviewReason { get; set; }

    /// <summary>When the iCal sync found the change (UTC).</summary>
    public DateTime? OtaReviewRaisedAt { get; set; }

    /// <summary>
    /// The linked block with its dates on the channel (booking detail only, <c>GET /api/bookings/{id}</c>); null when the
    /// stay has no block (not from iCal, or the block left the feed).
    /// </summary>
    public OtaChannelBlockDto? ChannelBlock { get; set; }

    public BookingGuestDto Guest { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// When the booking became confirmed (UTC, SR-05a). <c>null</c> while it is not confirmed and for a booking confirmed before the
    /// time was kept (nothing is backfilled: its timeline shows the confirmation without a time).
    /// </summary>
    public DateTime? ConfirmedAt { get; set; }

    /// <summary>When the booking was cancelled (UTC); <c>null</c> while it is not, and for one cancelled before the time was kept.</summary>
    public DateTime? CancelledAt { get; set; }

    /// <summary>When the host registered the arrival (UTC); <c>null</c> before, and when it was registered after the check-in day.</summary>
    public DateTime? ArrivedAt { get; set; }

    /// <summary>When the host checked the stay out (UTC); <c>null</c> before, and for a stay checked out before the time was kept.</summary>
    public DateTime? CheckedOutAt { get; set; }

    /// <summary>
    /// Whether the host may change the dates and the guests of the booking, and if not why: the rule of <c>PUT /api/bookings/{id}</c>
    /// (<see cref="BookingEditRules"/>), so the client shows "Modifica" or the explanation without a rule of its own. The notes of a
    /// booking that is not cancelled can always be changed.
    /// </summary>
    public BookingEditabilityDto CanEdit { get; set; } = new();
}

/// <summary>Whether the dates and guests of a booking may be changed (SR-05a), and why not.</summary>
public sealed class BookingEditabilityDto
{
    /// <summary>True when the dates and the guests may be changed.</summary>
    public bool Allowed { get; init; }

    /// <summary>
    /// Why not: <c>Cancelled</c> (the booking is cancelled), <c>Started</c> (the guest has arrived or left) or <c>SourceLocked</c> (it comes
    /// from the booking site or a channel, with price and payment settled there: it is cancelled or made again, not edited). <c>null</c>
    /// when <see cref="Allowed"/>.
    /// </summary>
    public BookingEditLock? Reason { get; init; }

    public static BookingEditabilityDto From(BookingEditability editability) => new()
    {
        Allowed = editability.Allowed,
        Reason = editability.Reason,
    };
}

/// <summary>
/// The booking of <c>GET /api/bookings/{id}</c> (SR-05a): the booking, as everywhere, and the state of what is tied to it. Only the detail
/// reads these (they cost a query), so they are not on the booking of the other endpoints; the lists have their own (<see cref="BookingListItemDto"/>).
/// </summary>
public class BookingDetailDto : BookingResponseDto
{
    /// <summary>How much of the total came in: <c>None</c>, <c>Partial</c>, <c>Paid</c> or <c>Refunded</c> (a failed or open payment is nothing).</summary>
    public string PaymentState { get; set; } = string.Empty;

    /// <summary>
    /// The money that came in, in euros: the payments collected minus what was refunded. <c>null</c> for a caller who may not read
    /// payments (the collaborator): the state is still there.
    /// </summary>
    public decimal? PaidAmount { get; set; }

    /// <summary>
    /// Every guest of the stay has every field of the Alloggiati record, whoever entered them; <c>null</c> for a booking that has no
    /// communication (a request, a hold, a cancelled booking).
    /// </summary>
    public bool? GuestDataComplete { get; set; }
}

/// <summary>
/// A row of the booking list (SR-03b, <c>GET /api/bookings</c> and <c>GET /api/bookings/search</c>): the booking, as everywhere, and
/// what a list says besides it. Only the lists read these, so they are not on the booking of the other endpoints.
/// </summary>
public class BookingListItemDto : BookingResponseDto
{
    /// <summary>
    /// The channel the booking came from, in the words of the channel filter: <c>Direct</c> (the booking site), <c>Manual</c> (entered
    /// by the host), <c>Airbnb</c>, <c>BookingCom</c> or <c>Other</c>. The exact one is <see cref="BookingResponseDto.Source"/>.
    /// </summary>
    public string Channel { get; set; } = string.Empty;

    /// <summary>How much of the total came in: <c>None</c>, <c>Partial</c>, <c>Paid</c> or <c>Refunded</c> (a failed or open payment is nothing).</summary>
    public string PaymentState { get; set; } = string.Empty;

    /// <summary>
    /// The money that came in, in euros: the payments collected minus what was refunded. <c>null</c> for a caller who may not read
    /// payments (the collaborator): the state is still there.
    /// </summary>
    public decimal? PaidAmount { get; set; }

    /// <summary>
    /// Where the Alloggiati Web communication stands (<c>DaInviare</c>, <c>DaInviareManualmente</c>, <c>Inviato</c>,
    /// <c>InviatoManualmente</c>, <c>Errore</c>, <c>Rifiutato</c>); <c>null</c> for a booking that has none (pending, cancelled).
    /// </summary>
    public string? AlloggiatiStatus { get; set; }

    /// <summary>Until when the host may answer a "pay at the property" request (UTC); <c>null</c> for any other booking.</summary>
    public DateTime? RespondBy { get; set; }
}

/// <summary>The bookings in each chip of the list (SR-03b, <c>GET /api/bookings/counts</c>).</summary>
public class BookingCountsDto
{
    /// <summary>Every booking that satisfies the filters.</summary>
    public int All { get; set; }

    public int ToConfirm { get; set; }

    public int Upcoming { get; set; }

    public int InProgress { get; set; }

    public int Completed { get; set; }

    public int Cancelled { get; set; }

    public static BookingCountsDto From(Casazen.Core.Services.BookingCounts counts) => new()
    {
        All = counts.All,
        ToConfirm = counts.ToConfirm,
        Upcoming = counts.Upcoming,
        InProgress = counts.InProgress,
        Completed = counts.Completed,
        Cancelled = counts.Cancelled,
    };
}

/// <summary>The iCal block of an OTA stay (CO-21): its dates as the channel publishes them (check-out day excluded).</summary>
public class OtaChannelBlockDto
{
    public Guid Id { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }

    public static OtaChannelBlockDto From(Casazen.Core.Entities.CalendarBlock block) => new()
    {
        Id = block.Id,
        StartDate = block.StartUtc.Date,
        EndDate = block.EndUtc.Date,
    };
}

public class BookingGuestDto
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
}
