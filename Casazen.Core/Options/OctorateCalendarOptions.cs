namespace Casazen.Core.Options;

/// <summary>
/// The <c>Octorate:Calendar</c> section (E08/F05/US02/T01, NF-36, D7): how the calendar of a property is sent to the channel manager. Nothing here is
/// required and nothing fails the startup: a value out of range is brought into it by the accessors that the code reads
/// (<see cref="MaxDays"/>, <see cref="RoomDaysPerUnit"/>, <see cref="Reserve"/>, <see cref="Horizon"/>, <see cref="NoMaxStayNights"/>), never an error.
/// </summary>
public sealed class OctorateCalendarOptions
{
    public const string SectionName = "Octorate:Calendar";

    /// <summary>The days of one call when <see cref="MaxDaysPerCall"/> is out of range.</summary>
    public const int DefaultMaxDaysPerCall = 62;

    /// <summary>The room-days of one unit of quota when <see cref="RoomDaysPerQuotaUnit"/> is out of range (decision D7).</summary>
    public const int DefaultRoomDaysPerQuotaUnit = 15;

    /// <summary>The units of quota kept free for the other calls when <see cref="QuotaReserve"/> is out of range.</summary>
    public const int DefaultQuotaReserve = 20;

    /// <summary>The days of a publication when <see cref="HorizonDays"/> is out of range.</summary>
    public const int DefaultHorizonDays = 365;

    /// <summary>The longest horizon of a publication: two years, what Octorate documents as the full update of a calendar.</summary>
    public const int MaxHorizonDays = 730;

    /// <summary>The most days one call to the channel manager covers (to calibrate on the sandbox).</summary>
    public int MaxDaysPerCall { get; set; } = DefaultMaxDaysPerCall;

    /// <summary>How many room-days the channel manager counts as one unit of its quota (decision D7: 15).</summary>
    public int RoomDaysPerQuotaUnit { get; set; } = DefaultRoomDaysPerQuotaUnit;

    /// <summary>The units of quota the calendar push leaves free for the other calls of CasaZen.</summary>
    public int QuotaReserve { get; set; } = DefaultQuotaReserve;

    /// <summary>The value that closes a night on the channel manager (<c>StopSells</c> by default; <c>Availability</c> writes 0 units).</summary>
    public OctorateCloseNightsWith CloseNightsWith { get; set; } = OctorateCloseNightsWith.StopSells;

    /// <summary>
    /// The value of <c>maxstay</c> that means «no maximum» on the channel manager. Empty until it is checked on the sandbox: a night with no maximum
    /// then leaves the field out.
    /// </summary>
    public int? NoMaxStay { get; set; }

    /// <summary>How many days from today a publication of a listing sends (<see cref="DefaultHorizonDays"/> by default).</summary>
    public int HorizonDays { get; set; } = DefaultHorizonDays;

    /// <summary>
    /// The statuses a finished process of a bulk call shows, for the process and for each of its dates (<c>GET /rest/v1/calendar/process/{id}</c>, documented only in Postman,
    /// decision D7: the examples are <c>NEW</c>, <c>NOT_PROCESSED</c> and <c>ERROR</c>, no status of success), separated by commas. Empty until the sandbox shows them: a chunk is then
    /// never confirmed and stays «accepted by the channel manager». A status that is not listed here (unknown, absent, an interim one) never confirms a chunk.
    /// </summary>
    public string? ProcessSuccessStatuses { get; set; }

    /// <summary><see cref="MaxDaysPerCall"/>, or its default when it is not between 1 and 366.</summary>
    public int MaxDays => MaxDaysPerCall is >= 1 and <= 366 ? MaxDaysPerCall : DefaultMaxDaysPerCall;

    /// <summary><see cref="RoomDaysPerQuotaUnit"/>, or its default when it is below 1.</summary>
    public int RoomDaysPerUnit => RoomDaysPerQuotaUnit >= 1 ? RoomDaysPerQuotaUnit : DefaultRoomDaysPerQuotaUnit;

    /// <summary><see cref="QuotaReserve"/>, or its default when it is negative.</summary>
    public int Reserve => QuotaReserve >= 0 ? QuotaReserve : DefaultQuotaReserve;

    /// <summary><see cref="HorizonDays"/>, or its default when it is not between 1 and <see cref="MaxHorizonDays"/>.</summary>
    public int Horizon => HorizonDays is >= 1 and <= MaxHorizonDays ? HorizonDays : DefaultHorizonDays;

    /// <summary><see cref="NoMaxStay"/> when it is a positive number of nights; <c>null</c> when it is empty or not usable.</summary>
    public int? NoMaxStayNights => NoMaxStay is >= 1 ? NoMaxStay : null;

    /// <summary>The statuses of <see cref="ProcessSuccessStatuses"/>, trimmed and compared without regard to case; empty when none is configured.</summary>
    public IReadOnlySet<string> SuccessStatuses =>
        (ProcessSuccessStatuses ?? string.Empty)
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

/// <summary>What closes a night on the channel manager (<see cref="OctorateCalendarOptions.CloseNightsWith"/>).</summary>
public enum OctorateCloseNightsWith
{
    /// <summary>The night is stopped for sale (<c>stopSells</c>): the default, the way the sandbox calendar closes a night.</summary>
    StopSells = 0,

    /// <summary>The night has no unit available (<c>availability</c> 0).</summary>
    Availability = 1,
}
