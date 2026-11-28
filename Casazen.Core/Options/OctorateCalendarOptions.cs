namespace Casazen.Core.Options;

/// <summary>
/// The <c>Octorate:Calendar</c> section (E08/F05/US02/T01, NF-36, D7): how the calendar of a property is sent to the channel manager. Nothing here is
/// required and nothing fails the startup: a value out of range is brought into it by the accessors that the code reads
/// (<see cref="MaxDays"/>, <see cref="RoomDaysPerUnit"/>, <see cref="Reserve"/>, <see cref="Horizon"/>, <see cref="NoMaxStayNights"/>), never an error.
/// </summary>
public sealed class OctorateCalendarOptions
{
    public const string SectionName = "Octorate:Calendar";

    public const int DefaultMaxDaysPerCall = 62;

    public const int DefaultRoomDaysPerQuotaUnit = 15;

    public const int DefaultQuotaReserve = 20;

    public const int DefaultHorizonDays = 365;

    public const int MaxHorizonDays = 730;

    public int MaxDaysPerCall { get; set; } = DefaultMaxDaysPerCall;

    public int RoomDaysPerQuotaUnit { get; set; } = DefaultRoomDaysPerQuotaUnit;

    public int QuotaReserve { get; set; } = DefaultQuotaReserve;

    public OctorateCloseNightsWith CloseNightsWith { get; set; } = OctorateCloseNightsWith.StopSells;

    public int? NoMaxStay { get; set; }

    public int HorizonDays { get; set; } = DefaultHorizonDays;

    /// <summary>
    /// The statuses a finished process of a bulk call shows, for the process and for each of its dates (<c>GET /rest/v1/calendar/process/{id}</c>, documented only in Postman,
    /// decision D7: the examples are <c>NEW</c>, <c>NOT_PROCESSED</c> and <c>ERROR</c>, no status of success), separated by commas. Empty until the sandbox shows them: a chunk is then
    /// never confirmed and stays «accepted by the channel manager». A status that is not listed here (unknown, absent, an interim one) never confirms a chunk.
    /// </summary>
    public string? ProcessSuccessStatuses { get; set; }

    public int MaxDays => MaxDaysPerCall is >= 1 and <= 366 ? MaxDaysPerCall : DefaultMaxDaysPerCall;

    public int RoomDaysPerUnit => RoomDaysPerQuotaUnit >= 1 ? RoomDaysPerQuotaUnit : DefaultRoomDaysPerQuotaUnit;

    public int Reserve => QuotaReserve >= 0 ? QuotaReserve : DefaultQuotaReserve;

    public int Horizon => HorizonDays is >= 1 and <= MaxHorizonDays ? HorizonDays : DefaultHorizonDays;

    public int? NoMaxStayNights => NoMaxStay is >= 1 ? NoMaxStay : null;

    public IReadOnlySet<string> SuccessStatuses =>
        (ProcessSuccessStatuses ?? string.Empty)
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

public enum OctorateCloseNightsWith
{
    StopSells = 0,

    Availability = 1,
}
