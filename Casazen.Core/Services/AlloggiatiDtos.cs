using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <param name="ConfirmationNumber">Reference of the real receipt; only with <see cref="AlloggiatiWebStatus.Inviato"/>.</param>
/// <param name="ErrorCode">Stable error code; only with <see cref="AlloggiatiWebStatus.Errore"/> or <see cref="AlloggiatiWebStatus.Rifiutato"/>.</param>
/// <param name="ReportedAt">When it was sent (receipt) or declared sent by the host; null while not sent.</param>
/// <param name="DeadlineAt">Legal deadline (UTC): arrival + 24 hours, or + 6 hours for a short stay.</param>
public record AlloggiatiStatusInfo(
    Guid BookingId,
    AlloggiatiWebStatus Status,
    string? ConfirmationNumber,
    string? ErrorCode,
    DateTime? ReportedAt,
    DateTime DeadlineAt,
    bool IsShortStay,
    double HoursUntilDeadline,
    bool IsOverdue,
    bool DataComplete);

public record AlloggiatiSummaryInfo(
    Guid BookingId,
    string GuestName,
    string PropertyName,
    DateTime CheckInDate,
    AlloggiatiWebStatus Status,
    bool DataComplete,
    bool IsOverdue,
    double HoursUntilDeadline,
    DateTime DeadlineAt,
    bool IsShortStay);

/// <summary>
/// Kind of guest ("tipo alloggiato") as a label, never a table code: with one guest per booking the registered
/// guest is either a single guest or the head of the family/group the others belong to (CO-12 models them).
/// </summary>
public enum AlloggiatiGuestKind
{
    SingleGuest,
    HeadOfFamilyOrGroup,
}

/// <summary>
/// Data of one guest in the order of the Alloggiati Web record (<c>.claude/context/regulations/alloggiati.md</c>,
/// "Tracciato record"): kind, arrival date, days of stay, surname, name, sex, date of birth, place of birth,
/// citizenship, document type, document number, place of issue. Values are the text stored in CasaZen: no
/// table code is derived or invented.
/// </summary>
/// <param name="MissingFields">Names of the fields the record needs and CasaZen does not have (camelCase property names of this record).</param>
public record AlloggiatiGuestRow(
    Guid GuestId,
    AlloggiatiGuestKind Kind,
    DateTime ArrivalDate,
    int StayDays,
    string LastName,
    string FirstName,
    Gender? Gender,
    DateTime? DateOfBirth,
    string PlaceOfBirth,
    string Citizenship,
    GuestDocumentType? DocumentType,
    string DocumentNumber,
    string DocumentIssuePlace,
    IReadOnlyList<string> MissingFields);

/// <param name="StayExceedsMaxDays">True when the stay is longer than the 30 days the portal accepts on one schedina.</param>
/// <param name="DeclaredGuests">Guests declared on the booking; the ones beyond <paramref name="Guests"/> are not registered in CasaZen.</param>
public record AlloggiatiGuestSummaryInfo(
    Guid BookingId,
    AlloggiatiWebStatus Status,
    DateTime ArrivalDate,
    int StayDays,
    bool StayExceedsMaxDays,
    int DeclaredGuests,
    IReadOnlyList<AlloggiatiGuestRow> Guests);

/// <summary>
/// A report that needs a job on the arrival day: returned by <see cref="IAlloggiatiWebService.ReserveReportAsync"/>
/// only when there is something to schedule, so a second call does not queue the job again.
/// </summary>
/// <param name="RunAtUtc">When the job must run: the start of the arrival day in Europe/Rome, or now if already reached.</param>
/// <param name="PreviousJobId">Job this one replaces (to delete), when the report was scheduled before.</param>
public record AlloggiatiReportReservation(
    Guid ReportId,
    Guid BookingId,
    Guid GuestId,
    DateTime RunAtUtc,
    string? PreviousJobId);

/// <summary>What the arrival-day job did with a report.</summary>
public enum AlloggiatiProcessOutcome
{
    /// <summary>Report moved to <see cref="AlloggiatiWebStatus.DaInviareManualmente"/>: the host must send it.</summary>
    MarkedForManualSubmission,

    /// <summary>The report is past the arrival-day step (manual, sent, rejected): nothing to do.</summary>
    AlreadyHandled,

    /// <summary>The arrival day has not started yet in Europe/Rome (the check-in date moved): schedule again.</summary>
    NotYetDue,

    /// <summary>No report for this booking and guest (job queued before CO-11): reserve and schedule one.</summary>
    NotReserved,

    /// <summary>The booking now points at another guest record: the job of that guest handles it.</summary>
    Superseded,

    /// <summary>The booking is pending or cancelled: nothing is due.</summary>
    BookingInactive,

    /// <summary>The booking no longer exists.</summary>
    BookingNotFound,
}
