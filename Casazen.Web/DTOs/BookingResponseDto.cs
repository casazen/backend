using Casazen.Core.Bookings;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

public class BookingResponseDto
{
    public Guid Id { get; set; }

    public string BookingCode { get; set; } = string.Empty;

    public Guid PropertyId { get; set; }
    public string? PropertyName { get; set; }
    public DateTime CheckInDate { get; set; }
    public DateTime CheckOutDate { get; set; }
    public int NumberOfGuests { get; set; }

    public int NumberOfAdults { get; set; }
    public int NumberOfChildren { get; set; }

    public decimal TotalPrice { get; set; }

    public decimal BasePrice { get; set; }

    public decimal CleaningFee { get; set; }

    public decimal TouristTax { get; set; }

    public bool PriceOverridden { get; set; }

    public string? PriceOverrideNote { get; set; }

    public string Currency { get; set; } = StayCurrency.Default;
    public string Status { get; set; } = string.Empty;

    public string? CancellationNote { get; set; }
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// The channel of a booking that came from a channel through the channel manager (E02/F02/US01/T01, E08/F03/US03/T02, ADR-033-R9, read-only):
    /// <c>Airbnb</c>, <c>BookingCom</c>, <c>Expedia</c>, <c>Vrbo</c>, <c>TripAdvisor</c>, <c>Agoda</c> or <c>OtherChannel</c> (the name is in
    /// <see cref="ChannelLabel"/>). <c>null</c> for every other booking: one the host entered, one made on the booking site (CasaZen sends it to Octorate,
    /// where it has a reservation too, but it does not come from a channel), a stay from an iCal feed, a reservation of the booking engine of the agency
    /// (it counts as direct). This is what the "OTA" badge reads.
    /// </summary>
    public string? OtaSource { get; set; }

    public string Origin { get; set; } = string.Empty;

    public string? OriginChannel { get; set; }

    public string SpecialRequests { get; set; } = string.Empty;

    public string? ExpectedArrivalTime { get; set; }

    public string PaymentOption { get; set; } = string.Empty;

    public string? OnSiteRequestState { get; set; }

    public DateTime? RequestExpiresAt { get; set; }

    public Guid? IcalFeedId { get; set; }

    public string? ChannelLabel { get; set; }

    public string? OtaReviewReason { get; set; }

    public DateTime? OtaReviewRaisedAt { get; set; }

    public OtaChannelBlockDto? ChannelBlock { get; set; }

    public BookingGuestDto Guest { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public DateTime? ConfirmedAt { get; set; }

    public DateTime? CancelledAt { get; set; }

    public DateTime? ArrivedAt { get; set; }

    public DateTime? CheckedOutAt { get; set; }

    /// <summary>
    /// Whether the host may change the dates and the guests of the booking, and if not why: the rule of <c>PUT /api/bookings/{id}</c>
    /// (<see cref="BookingEditRules"/>), so the client shows "Modifica" or the explanation without a rule of its own. The notes of a
    /// booking that is not cancelled can always be changed.
    /// </summary>
    public BookingEditabilityDto CanEdit { get; set; } = new();

    public BookingChangeOptionsDto ChangeOptions { get; set; } = new();
}

public sealed class BookingEditabilityDto
{
    public bool Allowed { get; init; }

    public BookingEditLock? Reason { get; init; }

    public static BookingEditabilityDto From(BookingEditability editability) => new()
    {
        Allowed = editability.Allowed,
        Reason = editability.Reason,
    };
}

public class BookingDetailDto : BookingResponseDto
{
    public string PaymentState { get; set; } = string.Empty;

    public decimal? PaidAmount { get; set; }

    /// <summary>
    /// The amounts of the channel of a booking that came from a channel (E02/F02/US01/T01, E08/F03/US03/T02, ADR-033-R8, read-only): commission, net, paid
    /// and balance, each <c>null</c> when Octorate does not give it ("not available", never zero), exactly as stored. They are also there for a reservation
    /// of the booking engine of the agency that the import made (it counts as direct, but its money is Octorate's). <c>null</c> for any other booking,
    /// one CasaZen made and sent to Octorate included, and for a caller without <c>payment.read</c> (as <see cref="PaidAmount"/>).
    /// </summary>
    public OtaAmountsDto? OtaAmounts { get; set; }

    public BookingCollectionDto? Collection { get; set; }

    /// <summary>
    /// Where each figure of the booking comes from (E08/F03/US03/T02, NF-77): Stripe for a booking of the booking site, the channel manager for a booking of a
    /// channel (or a reservation of the booking engine of the agency), the host for what was entered by hand. <c>null</c> for a caller without
    /// <c>payment.read</c> (as <see cref="PaidAmount"/>).
    /// </summary>
    public BookingAmountSourcesDto? AmountSources { get; set; }

    public BookingOctorateSyncDto? OctorateSync { get; set; }

    /// <summary>
    /// Every guest of the stay has every field of the Alloggiati record, whoever entered them; <c>null</c> for a booking that has no
    /// communication (a request, a hold, a cancelled booking).
    /// </summary>
    public bool? GuestDataComplete { get; set; }
}

public sealed class OtaAmountsDto
{
    public decimal? Commission { get; init; }

    public decimal? Net { get; init; }

    public decimal? Paid { get; init; }

    public decimal? Balance { get; init; }

    public string Currency { get; init; } = StayCurrency.Default;
}

public class BookingListItemDto : BookingResponseDto
{
    public string Channel { get; set; } = string.Empty;

    public string PaymentState { get; set; } = string.Empty;

    public decimal? PaidAmount { get; set; }

    /// <summary>
    /// Where the Alloggiati Web communication stands (<c>DaInviare</c>, <c>DaInviareManualmente</c>, <c>Inviato</c>,
    /// <c>InviatoManualmente</c>, <c>Errore</c>, <c>Rifiutato</c>); <c>null</c> for a booking that has none (pending, cancelled).
    /// </summary>
    public string? AlloggiatiStatus { get; set; }

    public DateTime? RespondBy { get; set; }
}

public class BookingCountsDto
{
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
