using System.Globalization;
using System.Text.Json;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The mirror of a booking on Octorate as the booking answer says it (E08/F03/US03/T02, NF-74, NF-77): the state of the last operation CasaZen sent,
/// what it was, when Octorate last confirmed one, why it failed (a stable code, never a text of Octorate), whether the host may ask for it again
/// and, only in the <see cref="OctorateSyncState.Conflict"/> state, the dates and guests Octorate holds. No personal data.
/// </summary>
/// <param name="State">Where the mirror stands.</param>
/// <param name="LastOperation">The last operation sent for the booking.</param>
/// <param name="LastSyncedAt">When Octorate last confirmed an operation (UTC); <c>null</c> until it did.</param>
/// <param name="ErrorCode">A code of <see cref="OctorateSyncErrorCodes"/> when the last operation failed; <c>null</c> otherwise.</param>
/// <param name="CanRetry">The state is one the host can ask again (<c>Failed</c> or <c>Conflict</c>) and the connector is on; the caller adds its own permission.</param>
/// <param name="OctorateVersion">What Octorate holds after a refusal; only for a <c>Conflict</c>, and <c>null</c> when it was not kept or cannot be read.</param>
public sealed record BookingSyncStatus(
    OctorateSyncState State,
    OctorateSyncOperation LastOperation,
    DateTime? LastSyncedAt,
    string? ErrorCode,
    bool CanRetry,
    OctorateVersionSnapshot? OctorateVersion);

/// <summary>Reads the mirror of a booking on Octorate for the booking answer (E08/F03/US03/T02).</summary>
public interface IBookingOctorateSyncReader
{
    /// <summary>
    /// The mirror of the booking <paramref name="bookingId"/> of the org <paramref name="orgId"/>; <c>null</c> when the booking has no row, which means
    /// that it is not mirrored (a booking of another org has none either: the org is part of the question). The caller has already checked that the
    /// user may read the booking.
    /// </summary>
    Task<BookingSyncStatus?> GetAsync(Guid orgId, Guid bookingId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The dates and the number of guests of a reservation as Octorate holds them (<c>OctorateBookingSync.OctorateVersionJson</c>, read again after a 409): what
/// a host compares with their booking before they choose which version stays. The stored form is a JSON object, <c>{"checkIn":"2026-11-20","checkOut":
/// "2026-11-22","guests":2}</c> (stay dates as <c>yyyy-MM-dd</c>, a member that is not known is left out), written by <see cref="ToJson"/> and read by
/// <see cref="Parse"/>. Never a name, a contact or an amount.
/// </summary>
/// <param name="CheckIn">The arrival day; <c>null</c> when it was not kept.</param>
/// <param name="CheckOut">The departure day; <c>null</c> when it was not kept.</param>
/// <param name="Guests">All the guests; <c>null</c> when it was not kept.</param>
public sealed record OctorateVersionSnapshot(DateOnly? CheckIn, DateOnly? CheckOut, int? Guests)
{
    private const string DateFormat = "yyyy-MM-dd";

    private static readonly string[] CheckInNames = ["checkIn", "checkInDate"];
    private static readonly string[] CheckOutNames = ["checkOut", "checkOutDate"];
    private static readonly string[] GuestsNames = ["guests", "numberOfGuests", "totalGuests", "totalGuest"];

    /// <summary>The stored form of the snapshot: only the members that are known.</summary>
    public string ToJson()
    {
        var members = new Dictionary<string, object>(StringComparer.Ordinal);
        if (CheckIn is { } checkIn)
            members["checkIn"] = checkIn.ToString(DateFormat, CultureInfo.InvariantCulture);

        if (CheckOut is { } checkOut)
            members["checkOut"] = checkOut.ToString(DateFormat, CultureInfo.InvariantCulture);

        if (Guests is { } guests)
            members["guests"] = guests;

        return JsonSerializer.Serialize(members);
    }

    /// <summary>
    /// The snapshot stored in <paramref name="json"/>, or <c>null</c> when there is none or nothing in it can be read. The member names are read in any
    /// case; a day is only a plain <c>yyyy-MM-dd</c> date (a time is not guessed into a stay day), the guests a whole number of at least one. A member
    /// that cannot be read is left <c>null</c>, never invented.
    /// </summary>
    public static OctorateVersionSnapshot? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var root = document.RootElement;
            var checkIn = DayOf(Member(root, CheckInNames));
            var checkOut = DayOf(Member(root, CheckOutNames));
            var guests = CountOf(Member(root, GuestsNames));
            return checkIn is null && checkOut is null && guests is null ? null : new OctorateVersionSnapshot(checkIn, checkOut, guests);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? Member(JsonElement root, string[] names)
    {
        foreach (var name in names)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    return property.Value;
            }
        }

        return null;
    }

    private static DateOnly? DayOf(JsonElement? value)
    {
        if (value is not { ValueKind: JsonValueKind.String } text)
            return null;

        return DateOnly.TryParseExact(text.GetString(), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;
    }

    private static int? CountOf(JsonElement? value)
    {
        if (value is not { } element)
            return null;

        var count = element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(element.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) => number,
            _ => 0,
        };
        return count >= 1 ? count : null;
    }
}
