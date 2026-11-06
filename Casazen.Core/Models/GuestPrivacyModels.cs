using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

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

    /// <summary>
    /// The per-channel marketing consents of the check-in (NF-05, <see cref="MarketingConsentRecord"/>), oldest first: the channels ticked,
    /// the marketing e-mail, whether it was confirmed, the version of the text, the time and the IP. E07/F06/US02/T01 (NF-95, NF-20).
    /// </summary>
    public IReadOnlyList<GuestExportMarketingChannel> MarketingChannels { get; init; } = [];

    /// <summary>
    /// The profile the CRM keeps for the guest (<see cref="GuestCrmProfile"/>), or <c>null</c> when it has none. Never the keyed hashes, the
    /// proposals of merge or the data of another guest (see <see cref="GuestExportCrmProfile.SharedWithOtherGuests"/>).
    /// </summary>
    public GuestExportCrmProfile? CrmProfile { get; init; }

    /// <summary>
    /// The contacts of the guest in the CRM (<see cref="GuestContact"/>): e-mail, phone and WhatsApp, with where each came from, whether it was
    /// confirmed, the legal basis, the version of the text, the time and whether the person asked not to be written to.
    /// </summary>
    public IReadOnlyList<GuestExportContact> Contacts { get; init; } = [];

    /// <summary>The messages the guest exchanged with the host on its bookings (<see cref="GuestMessage"/>), oldest first.</summary>
    public IReadOnlyList<GuestExportMessage> Messages { get; init; } = [];
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
    IReadOnlyList<GuestExportPayment> Payments,
    string? ExpectedArrivalTime = null);

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

/// <summary>One per-channel marketing consent of the check-in (<see cref="MarketingConsentRecord"/>), as the guest gave it.</summary>
/// <param name="BookingId">The booking the consent was given with.</param>
/// <param name="ConsentEmail">The guest ticked the e-mail channel.</param>
/// <param name="ConsentWhatsApp">The guest ticked the WhatsApp channel.</param>
/// <param name="MarketingEmail">The address the guest gave for marketing (may differ from the one of the booking).</param>
/// <param name="EmailConfirmed">The guest confirmed the address with the link of the double opt-in.</param>
/// <param name="ConsentTextVersion">Version of the text of the consent the guest saw.</param>
/// <param name="ConsentTimestamp">When the guest gave it (UTC).</param>
/// <param name="IpAddress">The address of the guest's connection when it was given.</param>
public sealed record GuestExportMarketingChannel(
    Guid BookingId,
    bool ConsentEmail,
    bool ConsentWhatsApp,
    string MarketingEmail,
    bool EmailConfirmed,
    string ConsentTextVersion,
    DateTime ConsentTimestamp,
    string IpAddress);

/// <summary>
/// The CRM profile of the guest (E07/F06/US02/T01): language, country, tags and, when the profile is the guest's alone, the aggregates of
/// its stays. A profile that other guests are linked to (a merge of duplicates) adds their stays to its aggregates, so the aggregates are
/// not exported then (<c>null</c>): the stays of the guest are in <see cref="GuestDataExport.Bookings"/>.
/// </summary>
/// <param name="Id">The profile.</param>
/// <param name="Language">The language of the messages to the guest, empty when not known.</param>
/// <param name="Country">The country of the guest as the CRM holds it.</param>
/// <param name="Tags">The tags the hosts gave the profile.</param>
/// <param name="SharedWithOtherGuests">Other guests of the org are linked to the same profile.</param>
/// <param name="TotalStays">Stays of the profile; <c>null</c> when the profile is shared.</param>
/// <param name="TotalSpend">Amount spent on those stays; <c>null</c> when the profile is shared.</param>
/// <param name="FirstStayAt">First stay; <c>null</c> when the profile is shared.</param>
/// <param name="LastStayAt">Latest stay; <c>null</c> when the profile is shared.</param>
public sealed record GuestExportCrmProfile(
    Guid Id,
    string Language,
    string Country,
    IReadOnlyList<string> Tags,
    bool SharedWithOtherGuests,
    int? TotalStays,
    decimal? TotalSpend,
    DateTime? FirstStayAt,
    DateTime? LastStayAt)
{
    /// <summary>
    /// What the CRM still holds in the profiles that were merged into this one (the tombstones of a merge of duplicates, chains included): the same
    /// set the erasure deletes with the profile. <b>Only when the profile is the guest's alone</b>: when other guests share it, what a tombstone holds
    /// cannot be told from their data (the erasure empties it for the same reason), so it is not in the file. Never the keyed hashes.
    /// </summary>
    public IReadOnlyList<GuestExportMergedProfile> MergedProfiles { get; init; } = [];
}

/// <summary>What the CRM still holds in a profile that was merged into the guest's profile (a tombstone): language, country and tags.</summary>
/// <param name="Language">The language the profile had.</param>
/// <param name="Country">The country the profile had.</param>
/// <param name="Tags">The tags the hosts gave the profile.</param>
public sealed record GuestExportMergedProfile(string Language, string Country, IReadOnlyList<string> Tags);

/// <summary>One contact of the guest in the CRM (<see cref="GuestContact"/>), decrypted: the guest's own address or number.</summary>
public sealed record GuestExportContact(
    GuestContactChannel Channel,
    string Value,
    string? DisplayName,
    string Language,
    GuestContactOrigin Origin,
    GuestContactConfirmation Confirmation,
    DateTime? ConfirmedAt,
    GuestContactLegalBasis LegalBasis,
    string BasisVersion,
    DateTime? BasisRecordedAt,
    GuestConsentSource? ConsentSource,
    string? ConsentIpAddress,
    bool IsSuppressed,
    DateTime? SuppressedAt,
    string? SuppressedSource,
    DateTime CreatedAt);

/// <summary>A message of the guest's conversation with the host on one booking (<see cref="GuestMessage"/>).</summary>
public sealed record GuestExportMessage(Guid BookingId, GuestMessageSender Sender, string Text, DateTime CreatedAt);

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
