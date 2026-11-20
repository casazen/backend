using Casazen.Core.Regulatory;

namespace Casazen.Core.Services;

/// <summary>
/// The notification of the guests of a property in canton Ticino to the cantonal police (notifiche.ti.ch; casazen/backend#979): every
/// guest within 24 hours of the arrival. The portal has no API: CasaZen writes the file of an arrival day in the official format
/// (<see cref="TicinoPoliceFile"/>), the host uploads it on the portal («Notifiche → Invio») and records the receipt
/// (<see cref="ISwissComplianceReceiptService"/>, type <c>Police</c>, report date = the arrival day). A day is notified once a receipt of it
/// is recorded.
/// </summary>
public interface ITicinoNotificationService
{
    /// <summary>How many days back the days still to notify are looked for.</summary>
    public const int PendingLookbackDays = 14;

    /// <summary>
    /// One arrival day of a Ticino property: the stays that arrive on it, the rows of the file and what the incomplete guests miss, the
    /// receipts recorded for it and the other days still to notify. Throws <c>property_not_found</c> or <c>property_not_in_ticino</c>.
    /// </summary>
    Task<TicinoNotificationDay> GetDayAsync(Guid propertyId, DateOnly day, CancellationToken cancellationToken = default);

    /// <summary>
    /// The file of an arrival day for the portal, rebuilt from the check-in data of that moment (never stored). Throws
    /// <c>property_not_found</c>, <c>property_not_in_ticino</c>, <c>ticino_structure_id_missing</c> (the property has no identifier),
    /// <c>ticino_notification_empty</c> (no guest of the day has complete data) or <c>ticino_xml_not_available</c> (a row does not fit the
    /// XSD: a postcode that is not a number or a country of residence that is not known; the CSV has no such limit).
    /// </summary>
    Task<TicinoNotificationFile> GenerateFileAsync(
        Guid propertyId,
        DateOnly day,
        TicinoPoliceFileFormat format,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The arrival days still to notify (stays arrived, no receipt recorded) of the Ticino properties of an org, or of every org when
    /// <paramref name="orgId"/> is <c>null</c>, from <see cref="PendingLookbackDays"/> days before <paramref name="today"/> up to it, oldest
    /// first. <paramref name="propertyIds"/> limits the properties read (the caller's reach); <c>null</c> reads them all.
    /// </summary>
    Task<IReadOnlyList<TicinoPendingDay>> ListPendingDaysAsync(
        Guid? orgId,
        IReadOnlyCollection<Guid>? propertyIds,
        DateOnly today,
        CancellationToken cancellationToken = default);

    /// <summary>Whether a property is in canton Ticino (country CH, municipality of canton TI).</summary>
    Task<bool> IsPropertyInTicinoAsync(Guid propertyId);

    /// <summary>
    /// The sheet of the group of a stay of a Ticino property (decision D11, NF-126): the first name, the last name, the birth date and the nationality of
    /// every member, the leader first (<see cref="TicinoPoliceFile.WriteGroupSheet"/>), rebuilt from the check-in on every request and never stored. Throws
    /// <c>property_not_found</c>, <c>property_not_in_ticino</c>, <c>booking_not_found</c> (no stay of that property, of the org of the property, that
    /// owes a notification: another org's booking, a pending or cancelled one, a no-show) or <c>ticino_not_a_group</c> (not a group of six or more).
    /// </summary>
    Task<TicinoGroupSheet> GetGroupSheetAsync(Guid propertyId, Guid bookingId, CancellationToken cancellationToken = default);
}

/// <summary>The sheet of a group (<see cref="ITicinoNotificationService.GetGroupSheetAsync"/>).</summary>
/// <param name="Content">The CSV, UTF-8 with its byte order mark.</param>
/// <param name="FileName">Suggested name, e.g. <c>scheda-gruppo-ti-2026-11-19-ABCDE-12345.csv</c> (the arrival day and the code of the booking).</param>
/// <param name="ContentType"><c>text/csv; charset=utf-8</c>.</param>
/// <param name="Members">The persons on the sheet, the leader included.</param>
public sealed record TicinoGroupSheet(byte[] Content, string FileName, string ContentType, int Members);

/// <summary>An arrival day of a Ticino property (<see cref="ITicinoNotificationService.GetDayAsync"/>).</summary>
/// <param name="PropertyId">The property.</param>
/// <param name="Day">The arrival day.</param>
/// <param name="StructureId">The identifier of the structure on the portal; <c>null</c> until the host enters it (no file without it).</param>
/// <param name="StayCount">The stays that arrive on the day.</param>
/// <param name="Plan">The rows of the file and what the incomplete guests miss.</param>
/// <param name="ReceiptCount">The receipts of the portal recorded for the day: one or more means the day is notified.</param>
/// <param name="DueAtUtc">The deadline: 24 hours from the first recorded arrival, or the start of the next day when none is recorded.</param>
/// <param name="PendingDays">The days of the property still to notify, this one included when it is.</param>
/// <param name="CsvCharset">The character set the CSV of the day is written in (<c>GuestNotifications:TicinoCsvEncoding</c>): <c>windows-1252</c> or <c>utf-8</c>.</param>
/// <param name="TransliteratedCount">
/// The characters of the rows of the day that <paramref name="CsvCharset"/> cannot hold: the CSV writes them without their accent, or as <c>?</c>
/// (NF-126 AC3). Zero when every name is written as typed.
/// </param>
public sealed record TicinoNotificationDay(
    Guid PropertyId,
    DateOnly Day,
    string? StructureId,
    int StayCount,
    TicinoDayPlan Plan,
    int ReceiptCount,
    DateTime DueAtUtc,
    IReadOnlyList<TicinoPendingDay> PendingDays,
    string CsvCharset = "windows-1252",
    int TransliteratedCount = 0);

/// <summary>A file for the portal (<see cref="ITicinoNotificationService.GenerateFileAsync"/>).</summary>
/// <param name="Content">The bytes (Windows-1252, or UTF-8 by configuration, for the CSV; UTF-8 for the XML).</param>
/// <param name="FileName">Suggested name, e.g. <c>notifiche-ti-2026-11-19.csv</c>.</param>
/// <param name="ContentType"><c>text/csv; charset=windows-1252</c> (or <c>charset=utf-8</c>) or <c>application/xml</c>.</param>
/// <param name="Reference">Identifier of the file, <c>TI-{propertyId}-{yyyyMMdd}</c>.</param>
/// <param name="FileHash">SHA-256 of <paramref name="Content"/>, lowercase hexadecimal.</param>
/// <param name="Rows">The notified persons in the file.</param>
/// <param name="TransliteratedCount">The characters of a CSV its character set cannot hold, written without their accent or as <c>?</c>; 0 for the XML.</param>
public sealed record TicinoNotificationFile(
    byte[] Content, string FileName, string ContentType, string Reference, string FileHash, int Rows, int TransliteratedCount = 0);

/// <summary>An arrival day still to notify.</summary>
public sealed record TicinoPendingDay(Guid OrgId, Guid PropertyId, string PropertyName, DateOnly Day, int StayCount, DateTime DueAtUtc);
