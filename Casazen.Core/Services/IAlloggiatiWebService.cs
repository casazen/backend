using Casazen.Core.Authorization;
using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Alloggiati Web communications (art. 109 TULPS). CasaZen does not transmit to the Questura yet (CO-13 builds the record
/// file the host uploads on the portal, not a web service client): on the arrival day a report becomes <see cref="AlloggiatiWebStatus.DaInviareManualmente"/> and
/// the host sends it on the portal, then records it with <see cref="MarkSentManuallyAsync"/>. No method ever sets
/// <see cref="AlloggiatiWebStatus.Inviato"/> without a real receipt.
/// </summary>
public interface IAlloggiatiWebService
{
    /// <summary>
    /// True when every guest of the stay has every field of the Alloggiati record and the order of the guests (head of
    /// family or group before its members) is valid (CO-12). Official codes are not required: they block only the export.
    /// False for an unknown booking.
    /// </summary>
    Task<bool> IsStayDataCompleteAsync(Guid bookingId);

    Task<AlloggiatiStatusInfo> GetStatusAsync(Guid bookingId);

    Task<IReadOnlyList<AlloggiatiSummaryInfo>> GetSummaryAsync(HostScope scope, Guid? propertyId);

    Task<AlloggiatiGuestSummaryInfo> GetGuestSummaryAsync(Guid bookingId);

    /// <summary>
    /// The record file the host uploads on the Alloggiati Web portal (CO-13): one 168-character line per guest of the stay
    /// in record order, built from the data and the imported official codes. It is not a communication: no status
    /// changes, nothing is stored or transmitted. Throws <c>NotFoundException</c> for an unknown booking and
    /// <c>DomainRuleException</c> when the file cannot be built (data incomplete or codes to complete, stay of less than 1
    /// or more than 30 days, a name that cannot be written with the letters the portal accepts).
    /// </summary>
    Task<AlloggiatiRecordFileInfo> BuildRecordFileAsync(Guid bookingId);

    /// <summary>
    /// Idempotency step of the scheduling: makes sure the booking's guest has its report and says whether a job
    /// must be scheduled for it. Returns null when a job is already scheduled, the communication is past the
    /// arrival-day step or the booking is not active, so a second caller never queues the job again.
    /// </summary>
    Task<AlloggiatiReportReservation?> ReserveReportAsync(Guid bookingId);

    Task SetScheduledJobAsync(Guid reportId, string jobId, DateTime scheduledForUtc);

    Task<AlloggiatiProcessOutcome> ProcessScheduledReportAsync(Guid bookingId, Guid guestId);

    Task<AlloggiatiStatusInfo> MarkSentManuallyAsync(Guid bookingId, DateTime sentOn, string? byUserId = null);

    bool IsOverdue(Booking booking, AlloggiatiWebStatus? reportStatus);
}
