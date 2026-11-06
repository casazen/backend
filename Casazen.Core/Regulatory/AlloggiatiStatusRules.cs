using Casazen.Core.Entities;

namespace Casazen.Core.Regulatory;

/// <summary>
/// How an Alloggiati Web status reads for the host (CO-11, decision D6). CasaZen does not transmit (no web service client), so
/// from the arrival day on every communication not sent is "to send manually", whether or not the arrival-day job
/// has already run: a booking never looks done without a receipt or the host's declaration.
/// </summary>
public static class AlloggiatiStatusRules
{
    /// <summary>
    /// The statuses of a booking that owes a police communication: confirmed, checked in or checked out. A request, a hold and a cancelled
    /// booking owe none. The one definition of the Alloggiati job, the lists, the alerts and the check-in sent to the channel manager.
    /// </summary>
    public static readonly BookingStatus[] ActiveBookingStatuses =
    [
        BookingStatus.Confirmed,
        BookingStatus.CheckedIn,
        BookingStatus.CheckedOut,
    ];

    /// <summary>
    /// True when the booking owes a police communication: a status of <see cref="ActiveBookingStatuses"/> and no no-show mark (NF-45: the
    /// guests of a no-show never came, there is nobody to communicate). Queries repeat it as
    /// <c>ActiveBookingStatuses.Contains(b.Status) &amp;&amp; b.NoShowAt == null</c>.
    /// </summary>
    public static bool IsDue(Booking booking)
    {
        ArgumentNullException.ThrowIfNull(booking);
        return ActiveBookingStatuses.Contains(booking.Status) && booking.NoShowAt is null;
    }

    /// <summary>
    /// The report that stands for a booking: the one of the booking's guest, or else the latest report of the booking (the same
    /// guest slot, CO-11). The Home (SR-03b) and the summary of the Alloggiati page read the same one.
    /// </summary>
    public static AlloggiatiWebReport? SlotReport(IReadOnlyCollection<AlloggiatiWebReport> reports, Guid guestId) =>
        reports.FirstOrDefault(r => r.GuestId == guestId)
        ?? reports.OrderByDescending(r => r.UpdatedAt).FirstOrDefault();

    /// <summary>Sent with a receipt, or declared sent by the host.</summary>
    public static bool IsSent(AlloggiatiWebStatus status) =>
        status is AlloggiatiWebStatus.Inviato or AlloggiatiWebStatus.InviatoManualmente;

    /// <summary>Error or rejection: the host must act.</summary>
    public static bool IsFailure(AlloggiatiWebStatus status) =>
        status is AlloggiatiWebStatus.Errore or AlloggiatiWebStatus.Rifiutato;

    /// <summary>
    /// Status shown for a booking: the stored one once past <see cref="AlloggiatiWebStatus.DaInviare"/>; otherwise
    /// (no report yet, or job not run yet) <see cref="AlloggiatiWebStatus.DaInviare"/> before the arrival day and
    /// <see cref="AlloggiatiWebStatus.DaInviareManualmente"/> from the arrival day in Europe/Rome.
    /// </summary>
    public static AlloggiatiWebStatus Effective(AlloggiatiWebStatus? stored, DateTime checkInDate, DateTime todayInRome)
    {
        if (stored is { } status && status != AlloggiatiWebStatus.DaInviare)
            return status;

        return AlloggiatiTerms.IsArrivalDayReached(checkInDate, todayInRome)
            ? AlloggiatiWebStatus.DaInviareManualmente
            : AlloggiatiWebStatus.DaInviare;
    }
}
