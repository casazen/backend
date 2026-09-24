using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Alloggiati Web communications (art. 109 TULPS). CasaZen does not transmit to the Questura yet (the web service
/// client is CO-13): on the arrival day a report becomes <see cref="AlloggiatiWebStatus.DaInviareManualmente"/> and
/// the host sends it on the portal, then records it with <see cref="MarkSentManuallyAsync"/>. No method ever sets
/// <see cref="AlloggiatiWebStatus.Inviato"/> without a real receipt.
/// </summary>
public interface IAlloggiatiWebService
{
    /// <summary>True when the booker's guest record has every field (legacy <c>/api/checkin</c> portal only).</summary>
    Task<bool> ValidateGuestDataAsync(Guid guestId);

    /// <summary>
    /// True when every guest of the stay has every field of the Alloggiati record and the order of the guests (head of
    /// family or group before its members) is valid (CO-12). Official codes are not required: they block only the export.
    /// False for an unknown booking.
    /// </summary>
    Task<bool> IsStayDataCompleteAsync(Guid bookingId);

    /// <summary>Status of the communication of a booking. Throws <c>NotFoundException</c> for an unknown booking.</summary>
    Task<AlloggiatiStatusInfo> GetStatusAsync(Guid bookingId);

    Task<IReadOnlyList<AlloggiatiSummaryInfo>> GetSummaryAsync(Guid orgId, Guid? propertyId);

    /// <summary>
    /// Per-guest data the host copies on the portal, in the order of the record. Throws <c>NotFoundException</c>
    /// for an unknown booking.
    /// </summary>
    Task<AlloggiatiGuestSummaryInfo> GetGuestSummaryAsync(Guid bookingId);

    /// <summary>
    /// Idempotency step of the scheduling: makes sure the booking's guest has its report and says whether a job
    /// must be scheduled for it. Returns null when a job is already scheduled, the communication is past the
    /// arrival-day step or the booking is not active, so a second caller never queues the job again.
    /// </summary>
    Task<AlloggiatiReportReservation?> ReserveReportAsync(Guid bookingId);

    /// <summary>Records the Hangfire job scheduled for a reservation.</summary>
    Task SetScheduledJobAsync(Guid reportId, string jobId, DateTime scheduledForUtc);

    /// <summary>Arrival-day step, run by the scheduled job: see <see cref="AlloggiatiProcessOutcome"/>.</summary>
    Task<AlloggiatiProcessOutcome> ProcessScheduledReportAsync(Guid bookingId, Guid guestId);

    /// <summary>
    /// Records that the host sent the communication on the portal on <paramref name="sentOn"/> (a date between the
    /// check-in date and today in Europe/Rome). Throws <c>DomainRuleException</c> for an invalid date,
    /// <c>DomainConflictException</c> when already sent, <c>NotFoundException</c> for an unknown booking.
    /// </summary>
    Task<AlloggiatiStatusInfo> MarkSentManuallyAsync(Guid bookingId, DateTime sentOn);

    /// <summary>True when the deadline has passed and the communication is not sent (nor declared sent).</summary>
    bool IsOverdue(Booking booking, AlloggiatiWebStatus? reportStatus);
}
