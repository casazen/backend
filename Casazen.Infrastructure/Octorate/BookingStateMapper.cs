using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// What an Octorate reservation becomes in CasaZen, and the other way (E02/F02/US01/T01, ADR-031, ADR-033): the state, the channel, the
/// stay dates, the guests and the amounts of an imported booking, and the operation CasaZen sends when its own state changes. Pure: no
/// database, no clock, no call.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>State.</b> <c>WAITING</c> is <see cref="BookingStatus.Pending"/>, <c>CONFIRMED</c> is <see cref="BookingStatus.Confirmed"/>, <c>CANCELLED</c>
/// is <see cref="BookingStatus.Cancelled"/>. A booking already <see cref="BookingStatus.CheckedIn"/> or <see cref="BookingStatus.CheckedOut"/> is
/// never moved back: check-in and check-out are CasaZen's states, Octorate sends none.</item>
/// <item><b>Channel.</b> The portal code of the reservation (<c>channelId</c>) is the first key, its name (<c>channelName</c>) the second; the
/// booking engine of the agency (<c>270</c>) is <see cref="BookingSource.Direct"/>, so it counts as direct for the fiscal documents and the
/// marketing (BKG-01.5). A portal that <see cref="BookingSource"/> has no value for is <see cref="BookingSource.OtherChannel"/>, with the name
/// in <see cref="Booking.ChannelLabel"/>: a wrong value is never reused. The source is the per-channel state of a booking.</item>
/// <item><b>Dates.</b> <c>checkin</c> and <c>checkout</c> are UTC instants: they are put on the clock of the property
/// (<see cref="TimezoneHelper.ConvertUtcToLocal"/>) and only the local calendar date is kept, as every stay date is (midnight UTC of the date).</item>
/// <item><b>Amounts.</b> Rounded to the cent and bounded to what the columns hold; <c>null</c> stays <c>null</c>, it is never turned into zero. They
/// are taken only from a reservation that states the currency of the property: with another currency, or none, no amount is imported.</item>
/// </list>
/// </remarks>
public sealed class BookingStateMapper
{
    /// <summary>The channel code of the booking engine of the agency's own site (sandbox, 2026-11-04).</summary>
    public const string BookingEngineChannelId = "270";

    /// <summary>Largest amount the columns of the OTA figures hold (<c>numeric(10,2)</c>) and the stay figures are taken at.</summary>
    private const decimal AmountLimit = 100_000_000m;

    private static readonly IReadOnlyDictionary<string, BookingSource> SourceByChannelId =
        new Dictionary<string, BookingSource>(StringComparer.Ordinal)
        {
            ["142"] = BookingSource.BookingCom,
            ["274"] = BookingSource.Airbnb,
            ["235"] = BookingSource.Vrbo,
            ["4"] = BookingSource.Expedia,
            [BookingEngineChannelId] = BookingSource.Direct,
        };

    private static readonly (string Fragment, BookingSource Source)[] SourceByChannelName =
    [
        ("booking.com", BookingSource.BookingCom),
        ("airbnb", BookingSource.Airbnb),
        ("vrbo", BookingSource.Vrbo),
        ("homeaway", BookingSource.Vrbo),
        ("expedia", BookingSource.Expedia),
        ("agoda", BookingSource.Agoda),
        ("tripadvisor", BookingSource.TripAdvisor),
        ("octorate", BookingSource.Direct),
    ];

    /// <summary>
    /// The CasaZen state of a reservation whose Octorate state is <paramref name="remote"/>, for a booking that is
    /// <paramref name="current"/> now (<c>null</c> for a booking not made yet). A stay in the house or over is not moved back. A booking
    /// CasaZen owns (<paramref name="casazenOwned"/>: entered by the host, or paid through CasaZen) that CasaZen cancelled is not brought
    /// back to life by a read of Octorate, which may be older than the cancellation that has yet to reach it.
    /// </summary>
    public BookingStatus ToBookingStatus(OctorateBookingStatus remote, BookingStatus? current, bool casazenOwned = false)
    {
        if (current is BookingStatus.CheckedIn or BookingStatus.CheckedOut)
            return current.Value;

        var mapped = remote switch
        {
            OctorateBookingStatus.Waiting => BookingStatus.Pending,
            OctorateBookingStatus.Confirmed => BookingStatus.Confirmed,
            OctorateBookingStatus.Cancelled => BookingStatus.Cancelled,
            _ => throw new ArgumentOutOfRangeException(nameof(remote), remote, "Not an Octorate reservation status."),
        };

        return casazenOwned && current == BookingStatus.Cancelled ? BookingStatus.Cancelled : mapped;
    }

    /// <summary>The source of a booking that arrives from the channel with this code and name (see the remarks of the class).</summary>
    public BookingSource ToSource(string? channelId, string? channelName)
    {
        if (channelId is not null && SourceByChannelId.TryGetValue(channelId.Trim(), out var byId))
            return byId;

        if (!string.IsNullOrWhiteSpace(channelName))
        {
            foreach (var (fragment, source) in SourceByChannelName)
            {
                if (channelName.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                    return source;
            }
        }

        return BookingSource.OtherChannel;
    }

    /// <summary>
    /// The name to show next to the source of a booking from a portal that has no source of its own; <c>null</c> for every other source and
    /// when Octorate gave no name.
    /// </summary>
    public string? ToChannelLabel(BookingSource source, string? channelName)
    {
        if (source != BookingSource.OtherChannel || string.IsNullOrWhiteSpace(channelName))
            return null;

        var name = channelName.Trim();
        return name.Length <= PropertyICalFeed.LabelMaxLength ? name : name[..PropertyICalFeed.LabelMaxLength];
    }

    /// <summary>
    /// The calendar date (midnight UTC, the storage convention of the stay dates) of a <c>checkin</c> or <c>checkout</c> of Octorate on the
    /// clock of <paramref name="timezoneId"/> (<see cref="Property.Timezone"/>): an instant is converted to the local date; a plain date is
    /// already one. <c>null</c> when the text is empty or not a date. An unknown time zone counts as Europe/Rome.
    /// </summary>
    public DateTime? ToStayDate(string? text, string? timezoneId)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var value = text.Trim();
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var plain))
            return DateTime.SpecifyKind(plain.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);

        // A text without an offset is UTC, as Octorate writes its instants.
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant))
            return null;

        var zone = !string.IsNullOrWhiteSpace(timezoneId) && TimezoneHelper.IsValidTimezone(timezoneId) ? timezoneId : RomeCalendar.TimeZoneId;
        var local = TimezoneHelper.ConvertUtcToLocal(instant.UtcDateTime, zone);
        return DateTime.SpecifyKind(local.Date, DateTimeKind.Utc);
    }

    /// <summary>
    /// The guests of a reservation: all of them (at least one), the adults (at least one) and the minors. Octorate's <c>totalGuest</c> decides
    /// the total; without it the adults, children and infants it gives are added up.
    /// </summary>
    public (int Guests, int Adults, int Children) ToGuests(OctorateReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);

        var minors = Math.Max(0, reservation.Children ?? 0) + Math.Max(0, reservation.Infants ?? 0);
        var guests = reservation.TotalGuests is > 0
            ? reservation.TotalGuests.Value
            : Math.Max(1, Math.Max(0, reservation.Adults ?? 0) + minors);
        var children = Math.Min(minors, guests - 1);
        return (guests, guests - children, children);
    }

    /// <summary>The amount to the cent; <c>null</c> when there is none or it is beyond what a column holds (it is then "not available", not a guess).</summary>
    public decimal? ToMoney(decimal? amount)
    {
        if (amount is null || Math.Abs(amount.Value) >= AmountLimit)
            return null;

        return decimal.Round(amount.Value, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// The four OTA figures of a booking from the reservation (ADR-033-R2, sandbox and OpenAPI 2026-11-04): the commission is the sum of the
    /// <c>COMMISSION</c> lines of the breakdown, <c>null</c> without one; the paid amount is <c>totalPaid</c>; the net and the balance have no
    /// documented source and stay <c>null</c>, never derived from <c>roomGross</c>. All <c>null</c> when the reservation does not state the
    /// currency of the property (<see cref="InCurrencyOf"/>): the figures would be in another unit, or in an unknown one.
    /// </summary>
    public OtaAmounts ToOtaAmounts(OctorateReservation reservation, string propertyCurrency)
    {
        ArgumentNullException.ThrowIfNull(reservation);

        if (!InCurrencyOf(reservation, propertyCurrency))
            return OtaAmounts.None;

        return new OtaAmounts(
            Commission: ToMoney(reservation.Commission),
            Net: null,
            Paid: ToMoney(reservation.TotalPaid),
            Balance: null);
    }

    /// <summary>
    /// The price figures of the stay (lodging, total, tourist tax) from the reservation: the room gross, the total gross and the tourist tax as
    /// Octorate gives them. A figure it does not give is <c>null</c>, never zero (a zero would read as a price), and every figure is <c>null</c>
    /// when the reservation does not state the currency of the property (<see cref="InCurrencyOf"/>): the caller writes none of them.
    /// </summary>
    public StayPrices ToStayPrices(OctorateReservation reservation, string propertyCurrency)
    {
        ArgumentNullException.ThrowIfNull(reservation);

        if (!InCurrencyOf(reservation, propertyCurrency))
            return StayPrices.None;

        var total = ToMoney(reservation.TotalGross) ?? ToMoney(reservation.RoomGross);
        var lodging = ToMoney(reservation.RoomGross) ?? total;
        return new StayPrices(lodging, total, ToMoney(reservation.TouristTax));
    }

    /// <summary>
    /// True when the reservation states the currency of the property (EUR or CHF). A reservation that states none is not taken for being in it: its
    /// amounts are in an unknown unit, and an amount of an unknown unit is not imported as if it were in the currency of the property.
    /// </summary>
    public bool InCurrencyOf(OctorateReservation reservation, string propertyCurrency)
    {
        ArgumentNullException.ThrowIfNull(reservation);

        return reservation.Currency is not null
               && string.Equals(reservation.Currency, StayCurrency.Normalize(propertyCurrency), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The operation CasaZen sends to Octorate when a booking becomes <paramref name="newStatus"/> (outbound): the arrival and the departure
    /// with the check-in status operation, a cancellation with <c>DELETE</c> (a status patch to <c>CANCELLED</c> answers 400);
    /// <see cref="OctorateStateChange.None"/> for a state Octorate has no operation for.
    /// </summary>
    public OctorateStateChange ToOutbound(BookingStatus newStatus) => newStatus switch
    {
        BookingStatus.CheckedIn => OctorateStateChange.CheckInEnd,
        BookingStatus.CheckedOut => OctorateStateChange.CheckOutEnd,
        BookingStatus.Cancelled => OctorateStateChange.Cancel,
        _ => OctorateStateChange.None,
    };
}

/// <summary>The operation CasaZen sends to Octorate for a new state of a booking (<see cref="BookingStateMapper.ToOutbound"/>).</summary>
public enum OctorateStateChange
{
    /// <summary>Octorate has no operation for the state: nothing is sent.</summary>
    None = 0,

    /// <summary><c>PUT /rest/v1/checkin/{acc}/{id}?status=CHECKIN_END</c>.</summary>
    CheckInEnd = 1,

    /// <summary><c>PUT /rest/v1/checkin/{acc}/{id}?status=CHECKOUT_END</c>.</summary>
    CheckOutEnd = 2,

    /// <summary><c>DELETE /rest/v1/reservation/{acc}/{id}</c>.</summary>
    Cancel = 3,
}

/// <summary>The four OTA figures of an imported booking; each <c>null</c> when Octorate does not give it.</summary>
public readonly record struct OtaAmounts(decimal? Commission, decimal? Net, decimal? Paid, decimal? Balance)
{
    /// <summary>No figure.</summary>
    public static OtaAmounts None => default;
}

/// <summary>The price figures of the stay of an imported booking; each <c>null</c> when Octorate does not give it (or not in the currency of the property): a zero would read as a price.</summary>
/// <param name="Lodging">The room (<c>BasePrice</c>).</param>
/// <param name="Total">The total of the reservation (<c>TotalPrice</c>).</param>
/// <param name="TouristTax">The tourist tax, as Octorate gives it.</param>
public readonly record struct StayPrices(decimal? Lodging, decimal? Total, decimal? TouristTax)
{
    /// <summary>No figure.</summary>
    public static StayPrices None => default;
}
