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
    public string Currency { get; set; } = "EUR";
    public string Status { get; set; } = string.Empty;

    /// <summary>Reason the host wrote when cancelling (host only).</summary>
    public string? CancellationNote { get; set; }
    public string Source { get; set; } = string.Empty;
    public string SpecialRequests { get; set; } = string.Empty;

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
