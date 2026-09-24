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
/// Data of one guest of the stay (<see cref="StayGuest"/>, CO-12) in the order of the Alloggiati Web record
/// (<c>.claude/context/regulations/alloggiati.md</c>, "Tracciato record"): kind, arrival date, days of stay, surname,
/// name, sex, date of birth, comune and province of birth (born in Italy), state of birth, citizenship, and for a single
/// guest or a head of family or group the document type, number and place of issue. Values are the text stored in
/// CasaZen; <see cref="Codes"/> holds the official codes found in the imported tables, never invented ones.
/// </summary>
/// <param name="StayGuestId">Id of the stored row; null for the booker shown before any guest is registered.</param>
/// <param name="IsMinor">Under 18 on the arrival date; null without a date of birth.</param>
/// <param name="MissingFields">Record fields missing or not accepted by the record (camelCase names, <c>AlloggiatiRecordRules.Field*</c>).</param>
/// <param name="CodesToComplete">Record fields whose official code is still to complete: they block only the export.</param>
/// <param name="CompositionIssue">Why the guest does not fit in the order of the stay (<c>member_without_head</c>, <c>head_without_members</c>); null when it fits.</param>
public record AlloggiatiGuestRow(
    Guid? StayGuestId,
    int Position,
    StayGuestType Type,
    bool? IsMinor,
    DateTime ArrivalDate,
    int StayDays,
    string LastName,
    string FirstName,
    Gender? Gender,
    DateTime? DateOfBirth,
    bool? BornInItaly,
    string BirthComune,
    string? BirthProvince,
    string BirthCountry,
    string Citizenship,
    bool RequiresDocument,
    GuestDocumentType? DocumentType,
    string DocumentNumber,
    string DocumentIssuePlace,
    AlloggiatiRowCodes Codes,
    IReadOnlyList<string> MissingFields,
    IReadOnlyList<string> CodesToComplete,
    string? CompositionIssue);

/// <summary>
/// Official codes of a guest's record line, from the imported Alloggiati tables (null when still to complete or not
/// part of the line). <see cref="DocumentTypeDescription"/> is the official description of the document type code.
/// </summary>
public record AlloggiatiRowCodes(
    string? Type,
    string? BirthComune,
    string? BirthCountry,
    string? Citizenship,
    string? DocumentType,
    string? DocumentTypeDescription,
    string? DocumentIssuePlace);

/// <param name="StayExceedsMaxDays">True when the stay is longer than the 30 days the portal accepts on one schedina.</param>
/// <param name="DeclaredGuests">Guests declared on the booking; <paramref name="Guests"/> are the ones registered.</param>
/// <param name="Guests">One row per guest, in record order (head of family or group before its members).</param>
/// <param name="DataComplete">Every guest has every field of the record and the order of the guests is valid.</param>
/// <param name="ExportReady">Data complete and every official code found: the record can be exported (CO-13).</param>
/// <param name="MissingCodeTables">Official tables not imported yet: their codes cannot be completed.</param>
public record AlloggiatiGuestSummaryInfo(
    Guid BookingId,
    AlloggiatiWebStatus Status,
    DateTime ArrivalDate,
    int StayDays,
    bool StayExceedsMaxDays,
    int DeclaredGuests,
    IReadOnlyList<AlloggiatiGuestRow> Guests,
    bool DataComplete,
    bool ExportReady,
    IReadOnlyList<AlloggiatiCodeTable> MissingCodeTables);

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
