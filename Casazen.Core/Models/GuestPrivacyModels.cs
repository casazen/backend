using Casazen.Core.Entities;

namespace Casazen.Core.Models;

/// <summary>
/// Export of the personal data of a guest (art. 15 access, art. 20 portability; CO-15, A5-13), as JSON. Versioned by
/// <see cref="SchemaVersion"/>: a field is renamed or removed only with a new version. The identity document is decrypted
/// only here, for the guest or the authorized host (<c>guest.read</c> on the guest's org).
/// </summary>
public sealed record GuestDataExport
{
    /// <summary>Version of this layout; the first export (a flat dictionary, before CO-15) was version 1.</summary>
    public const string CurrentSchemaVersion = "casazen.guest-data-export/2";

    public string SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required DateTime ExportedAt { get; init; }
    public required GuestExportSubject Subject { get; init; }
    public required GuestExportBirth Birth { get; init; }
    public required GuestExportDocument Document { get; init; }
    public required IReadOnlyList<GuestExportBooking> Bookings { get; init; }

    /// <summary>
    /// Every guest registered for the booker's stays (Alloggiati Web records, CO-12): the booker's own row
    /// (<see cref="GuestExportStayGuest.IsBooker"/>) and the companions the booker (or the host) entered for them.
    /// </summary>
    public required IReadOnlyList<GuestExportStayGuest> StayGuests { get; init; }

    public required IReadOnlyList<GuestExportCheckInSession> CheckInSessions { get; init; }
    public required IReadOnlyList<GuestExportAlloggiatiCommunication> AlloggiatiCommunications { get; init; }
    public required GuestExportConsents Consents { get; init; }
    public required GuestExportProcessing Processing { get; init; }
}

public sealed record GuestExportSubject(
    Guid Id,
    string FirstName,
    string LastName,
    string Email,
    string PhoneNumber,
    string Address,
    string City,
    string PostalCode,
    string Country,
    Gender? Gender,
    string Notes,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record GuestExportBirth(DateOnly? DateOfBirth, string PlaceOfBirth, string Nationality);

public sealed record GuestExportDocument(
    GuestDocumentType? Type,
    string Number,
    DateOnly? IssueDate,
    DateOnly? ExpiryDate,
    string IssuingPlace,
    bool HasScan);

public sealed record GuestExportBooking(
    Guid Id,
    string BookingCode,
    string PropertyName,
    DateOnly CheckInDate,
    DateOnly CheckOutDate,
    BookingStatus Status,
    BookingSource Source,
    int NumberOfGuests,
    int NumberOfAdults,
    int NumberOfChildren,
    decimal TotalPrice,
    decimal TouristTaxAmount,
    string SpecialRequests,
    DateTime CreatedAt,
    IReadOnlyList<GuestExportPayment> Payments);

public sealed record GuestExportPayment(
    decimal Amount,
    decimal RefundedAmount,
    PaymentStatus Status,
    PaymentMethod Method,
    DateTime? ProcessedAt,
    DateTime CreatedAt);

public sealed record GuestExportStayGuest(
    Guid BookingId,
    int Position,
    bool IsBooker,
    StayGuestType Type,
    string FirstName,
    string LastName,
    Gender? Gender,
    DateOnly? DateOfBirth,
    bool? BornInItaly,
    string? BirthComuneCode,
    string BirthComuneName,
    string? BirthProvince,
    string? BirthCountryCode,
    string BirthCountryName,
    string? CitizenshipCode,
    string CitizenshipName,
    GuestDocumentType? DocumentType,
    string? DocumentTypeCode,
    string DocumentNumber,
    string? DocumentIssuePlaceCode,
    string DocumentIssuePlaceName,
    StayGuestDataSource DataSource,
    DateTime? AnonymizedAt);

public sealed record GuestExportCheckInSession(
    Guid Id,
    Guid BookingId,
    GuestCheckInSessionStatus Status,
    DateTime CreatedAt,
    DateTime? SentAt,
    DateTime? CompletedAt,
    DateTime ExpiresAt);

public sealed record GuestExportAlloggiatiCommunication(Guid BookingId, AlloggiatiWebStatus Status, DateTime? ReportedAt);

/// <param name="PrivacyNoticeVersion">Version stored on the guest record (direct booking checkout, before CO-15 also the check-in).</param>
/// <param name="History">Every event of <see cref="GuestConsentRecord"/>, oldest first, IP included.</param>
public sealed record GuestExportConsents(
    string PrivacyNoticeVersion,
    DateTime? PrivacyNoticeDate,
    string ConsentIpAddress,
    bool MarketingConsent,
    DateTime? MarketingConsentDate,
    IReadOnlyList<GuestExportConsentEvent> History);

public sealed record GuestExportConsentEvent(
    GuestConsentPurpose Purpose,
    GuestConsentAction Action,
    string Version,
    GuestConsentSource Source,
    DateTime RecordedAt,
    string? IpAddress,
    string? Note);

public sealed record GuestExportProcessing(
    string Purpose,
    DateTime? AlloggiatiDataErasedAt,
    DateTime? AnonymizedAt,
    bool IsDeleted,
    DateTime? DeletedAt,
    IReadOnlyList<GuestRetentionScheduleItem> Retention);

/// <summary>
/// Retention of one category of the guest's data (CO-15), computed on read from <c>Gdpr:Retention</c>.
/// </summary>
/// <param name="Configured">False when the period or its source is missing: the job deletes nothing of this category.</param>
/// <param name="ReferenceDate">Start of the period (latest check-out, creation date without bookings, date of the consent); null when it does not apply.</param>
/// <param name="DueDate">Day from which the job applies the period; null when not configured or without a reference date.</param>
/// <param name="AppliedAt">When the job last applied it to this guest (audit), or null.</param>
public sealed record GuestRetentionScheduleItem(
    GuestDataCategory Category,
    bool Configured,
    int? Years,
    int? Months,
    int? Days,
    string? Source,
    DateTime? ReferenceDate,
    DateTime? DueDate,
    DateTime? AppliedAt);

/// <summary>What the host's GDPR tab shows for a guest (CO-15): consents with their versions, retention, status. No IP.</summary>
public sealed record GuestPrivacySummary(
    Guid GuestId,
    GuestMarketingConsentState Marketing,
    GuestPrivacyNoticeState PrivacyNotice,
    IReadOnlyList<GuestConsentHistoryItem> ConsentHistory,
    IReadOnlyList<GuestRetentionScheduleItem> Retention,
    DateTime? AnonymizedAt,
    DateTime? AlloggiatiDataErasedAt,
    bool IsDeleted,
    DateTime? DeletedAt,
    bool HasDocumentScan,
    bool HasOpenBookings);

/// <param name="Version">Version of the text of the consent in force (latest grant), empty when given before CO-15.</param>
public sealed record GuestMarketingConsentState(bool Granted, DateTime? Since, string Version);

/// <summary>Latest privacy notice presented (check-in portal) or stored on the record (direct booking checkout).</summary>
public sealed record GuestPrivacyNoticeState(string Version, DateTime? PresentedAt);

/// <summary>A consent event as the host sees it: no IP address.</summary>
public sealed record GuestConsentHistoryItem(
    GuestConsentPurpose Purpose,
    GuestConsentAction Action,
    string Version,
    GuestConsentSource Source,
    DateTime RecordedAt,
    string? Note);

/// <summary>Outcome of one run of the retention job (CO-15).</summary>
public sealed record GuestRetentionRunResult(IReadOnlyList<GuestRetentionCategoryResult> Categories);

/// <param name="Configured">False: nothing of the category was deleted (see the warning in the logs).</param>
/// <param name="Guests">Guest records changed.</param>
/// <param name="StayGuests">Guests of the stays anonymized.</param>
/// <param name="FilesDeleted">Objects deleted from the private storage.</param>
public sealed record GuestRetentionCategoryResult(
    GuestDataCategory Category,
    bool Configured,
    int Guests,
    int StayGuests,
    int FilesDeleted);
