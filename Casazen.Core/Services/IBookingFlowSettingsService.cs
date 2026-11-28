using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public interface IBookingFlowSettingsService
{
    Task<BookingFlowSettingsView> GetAsync(Guid orgId, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, BookingFlowSettingsView>> GetManyAsync(IReadOnlyCollection<Guid> orgIds, CancellationToken cancellationToken = default);

    Task<BookingFlowSettingsView> UpdateAsync(Guid orgId, BookingFlowSettingsUpdate update, string? actorUserId, CancellationToken cancellationToken = default);
}

/// <summary>The settings in force for an organization.</summary>
/// <param name="CheckInLinkMode">The online check-in link.</param>
/// <param name="CheckInLinkDaysBefore">Days before the arrival the link goes, when automatic.</param>
/// <param name="MaxCheckInLinkDaysBefore">The most days allowed: the life of a link (<c>CheckIn:SessionLifetimeDays</c>), so that it is still open on the arrival day.</param>
/// <param name="ArrivalMode">The arrival.</param>
/// <param name="DepartureMode">The departure.</param>
/// <param name="PropertyReadyMode">The property ready for the next guest.</param>
/// <param name="PropertyReadyTrigger">When CasaZen declares it ready.</param>
/// <param name="IsDefault">The org never saved its settings: these are the defaults.</param>
public sealed record BookingFlowSettingsView(
    BookingFlowMode CheckInLinkMode,
    int CheckInLinkDaysBefore,
    int MaxCheckInLinkDaysBefore,
    BookingFlowMode ArrivalMode,
    BookingFlowMode DepartureMode,
    BookingFlowMode PropertyReadyMode,
    PropertyReadyTrigger PropertyReadyTrigger,
    bool IsDefault)
{
    public bool SendsCheckInLink => CheckInLinkMode == BookingFlowMode.Automatic;

    public bool RegistersArrival => ArrivalMode == BookingFlowMode.Automatic;

    public bool RegistersDeparture => DepartureMode == BookingFlowMode.Automatic;

    public bool DeclaresReadyAtCheckOut =>
        PropertyReadyMode == BookingFlowMode.Automatic && PropertyReadyTrigger == PropertyReadyTrigger.CheckOut;

    public bool DeclaresReadyWhenCleaningDone =>
        PropertyReadyMode == BookingFlowMode.Automatic && PropertyReadyTrigger == PropertyReadyTrigger.CleaningDone;
}

public sealed record BookingFlowSettingsUpdate(
    BookingFlowMode? CheckInLinkMode = null,
    int? CheckInLinkDaysBefore = null,
    BookingFlowMode? ArrivalMode = null,
    BookingFlowMode? DepartureMode = null,
    BookingFlowMode? PropertyReadyMode = null,
    PropertyReadyTrigger? PropertyReadyTrigger = null);

public static class BookingFlowRules
{
    /// <summary>
    /// How far back the hourly job looks for an arrival or a departure to register: a stay older than this is left to the host, so that switching the automation on (or a
    /// job that was down) never closes a backlog of old stays at once.
    /// </summary>
    public const int AutomationLookbackDays = 3;

    public static readonly TimeOnly FallbackCheckOutTime = new(10, 0);

    public static BookingFlowSettingsView Defaults(int checkInLinkDaysBefore, int maxCheckInLinkDaysBefore) => new(
        BookingFlowMode.Automatic,
        Math.Clamp(checkInLinkDaysBefore, 0, Math.Max(0, maxCheckInLinkDaysBefore)),
        Math.Max(0, maxCheckInLinkDaysBefore),
        BookingFlowMode.Automatic,
        BookingFlowMode.Automatic,
        BookingFlowMode.Automatic,
        PropertyReadyTrigger.CleaningDone,
        IsDefault: true);
}

public enum BookingFlowAutomaticStep
{
    Arrival = 1,

    Departure = 2,

    PropertyReady = 3,
}

public static class BookingFlowErrorCodes
{
    public const string DaysOutOfRange = "booking_flow_days_out_of_range";

    public const string ValueInvalid = "booking_flow_value_invalid";
}
