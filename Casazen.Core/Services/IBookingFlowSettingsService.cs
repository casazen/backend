using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// The steps of the stays of an organization, automatic or by hand (casazen/backend#978, <see cref="BookingFlowSettings"/>): read by the jobs and services that do
/// a step by themselves, changed by the organization's administrators (<c>api/settings/booking-flow</c>).
/// </summary>
public interface IBookingFlowSettingsService
{
    /// <summary>The settings in force for <paramref name="orgId"/>: its row, or the defaults (<see cref="BookingFlowSettingsView.IsDefault"/>) while it has none.</summary>
    Task<BookingFlowSettingsView> GetAsync(Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>The settings in force for each of <paramref name="orgIds"/> (the defaults for an org with no row): one read for a job that works on many orgs.</summary>
    Task<IReadOnlyDictionary<Guid, BookingFlowSettingsView>> GetManyAsync(IReadOnlyCollection<Guid> orgIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the settings of <paramref name="orgId"/>: a member left out keeps its value. 422 <see cref="BookingFlowErrorCodes.DaysOutOfRange"/> (days not 0 to
    /// <see cref="BookingFlowSettingsView.MaxCheckInLinkDaysBefore"/>) or <see cref="BookingFlowErrorCodes.ValueInvalid"/> (a mode or a trigger that does not exist), and
    /// nothing is saved. The same values again write nothing.
    /// </summary>
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
    /// <summary>CasaZen sends the check-in link by itself.</summary>
    public bool SendsCheckInLink => CheckInLinkMode == BookingFlowMode.Automatic;

    /// <summary>CasaZen registers the arrival by itself.</summary>
    public bool RegistersArrival => ArrivalMode == BookingFlowMode.Automatic;

    /// <summary>CasaZen registers the departure by itself.</summary>
    public bool RegistersDeparture => DepartureMode == BookingFlowMode.Automatic;

    /// <summary>CasaZen declares the property ready at the check-out.</summary>
    public bool DeclaresReadyAtCheckOut =>
        PropertyReadyMode == BookingFlowMode.Automatic && PropertyReadyTrigger == PropertyReadyTrigger.CheckOut;

    /// <summary>CasaZen declares the property ready when the cleaning of the stay is done.</summary>
    public bool DeclaresReadyWhenCleaningDone =>
        PropertyReadyMode == BookingFlowMode.Automatic && PropertyReadyTrigger == PropertyReadyTrigger.CleaningDone;
}

/// <summary>A change of the settings: a <c>null</c> member keeps its value.</summary>
public sealed record BookingFlowSettingsUpdate(
    BookingFlowMode? CheckInLinkMode = null,
    int? CheckInLinkDaysBefore = null,
    BookingFlowMode? ArrivalMode = null,
    BookingFlowMode? DepartureMode = null,
    BookingFlowMode? PropertyReadyMode = null,
    PropertyReadyTrigger? PropertyReadyTrigger = null);

/// <summary>The rules of the steps of a stay that need no database, in one place (casazen/backend#978).</summary>
public static class BookingFlowRules
{
    /// <summary>
    /// How far back the hourly job looks for an arrival or a departure to register: a stay older than this is left to the host, so that switching the automation on (or a
    /// job that was down) never closes a backlog of old stays at once.
    /// </summary>
    public const int AutomationLookbackDays = 3;

    /// <summary>The departure time when the property says none: the time a cleaning starts when nothing else says one.</summary>
    public static readonly TimeOnly FallbackCheckOutTime = new(10, 0);

    /// <summary>The defaults of an org that never saved its settings: every step automatic, the link <paramref name="checkInLinkDaysBefore"/> days before the arrival.</summary>
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

/// <summary>A step CasaZen did in the host's place, as the host is told of it (push and bell, <c>PushTypes.BookingFlowStep</c>).</summary>
public enum BookingFlowAutomaticStep
{
    /// <summary>The arrival was registered once the arrival day was over.</summary>
    Arrival = 1,

    /// <summary>The departure was registered at the check-out time.</summary>
    Departure = 2,

    /// <summary>The property was declared ready for the next guest: the cleaning is done.</summary>
    PropertyReady = 3,
}

/// <summary>The stable codes of the errors of the settings of the steps of a stay.</summary>
public static class BookingFlowErrorCodes
{
    /// <summary>The days before the arrival are not 0 to the life of a link.</summary>
    public const string DaysOutOfRange = "booking_flow_days_out_of_range";

    /// <summary>A mode or a trigger that does not exist.</summary>
    public const string ValueInvalid = "booking_flow_value_invalid";
}
