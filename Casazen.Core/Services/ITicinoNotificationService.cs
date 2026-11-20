using Casazen.Core.Regulatory;

namespace Casazen.Core.Services;

/// <summary>
/// The notification of the guests of a property in canton Ticino to the cantonal police (notifiche.ti.ch; casazen/backend#979): every
/// guest within 24 hours of the arrival. The portal has no API: CasaZen writes the file of an arrival day in the official format
/// (<see cref="TicinoPoliceFile"/>), the host uploads it on the portal («Notifiche → Invio») and either records the receipt
/// (<see cref="ISwissComplianceReceiptService"/>, type <c>Police</c>, report date = the arrival day) or declares the day uploaded without one
/// (<see cref="DeclareDayAsync"/>, E30/F02/US02/T01, decision D10). A day is <b>closed</b> once a receipt of it is recorded or a declaration of it exists: <see cref="ListPendingDaysAsync"/>
/// is the one place that says it, so the alerts and the cockpit never read the receipts or the declarations themselves.
/// </summary>
public interface ITicinoNotificationService
{
    /// <summary>How many days back the days still to notify are looked for.</summary>
    public const int PendingLookbackDays = 14;

    /// <summary>
    /// One arrival day of a Ticino property: the stays that arrive on it, the rows of the file and what the incomplete guests miss, the
    /// receipts recorded for it, who declared it uploaded and when, how many times its file was generated and the other days still to notify.
    /// Throws <c>property_not_found</c> or <c>property_not_in_ticino</c>.
    /// </summary>
    Task<TicinoNotificationDay> GetDayAsync(Guid propertyId, DateOnly day, CancellationToken cancellationToken = default);

    /// <summary>
    /// The file of an arrival day for the portal, rebuilt from the check-in data of that moment (never stored); every generation writes one
    /// <c>SwissNotificationFileEvent</c> (no personal data). Throws
    /// <c>property_not_found</c>, <c>property_not_in_ticino</c>, <c>ticino_structure_id_missing</c> (the property has no identifier),
    /// <c>ticino_notification_empty</c> (no guest of the day has complete data), <c>ticino_xml_not_available</c> (a row does not fit the
    /// XSD: a postcode that is not a number or a country of residence that is not known; the CSV has no such limit) or
    /// <c>ticino_day_already_declared</c> (<see cref="Casazen.Core.Exceptions.DomainConflictException"/>, 409: the day is closed by a declaration or a receipt and the portal may create duplicates, so
    /// the file is generated again only when <see cref="TicinoFileRequest.ConfirmRegenerate"/> says the person confirmed).
    /// </summary>
    Task<TicinoNotificationFile> GenerateFileAsync(
        Guid propertyId,
        DateOnly day,
        TicinoPoliceFileFormat format,
        TicinoFileRequest? request = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The arrival days still to notify (stays arrived, no receipt recorded and no declaration) of the Ticino properties of an org, or of every org when
    /// <paramref name="orgId"/> is <c>null</c>, from <see cref="PendingLookbackDays"/> days before <paramref name="today"/> up to it, oldest
    /// first. <paramref name="propertyIds"/> limits the properties read (the caller's reach); <c>null</c> reads them all. <b>The one rule of a closed day</b>: a day with a
    /// <c>Police</c> receipt or a declaration of the notification to the Ticino police (the rows without a regime code) is not listed.
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
    /// owes a notification: another org's booking, a pending or cancelled one, a no-show) or <c>ticino_not_a_group</c> (not a group of six or more). Every sheet
    /// writes one <c>SwissNotificationFileEvent</c> of format <c>groupSheet</c> (the arrival day of the stay, the members, the hash of the sheet; no personal data).
    /// A sheet is kept by the host and never uploaded as a notification, so <see cref="TicinoFileRequest.ConfirmRegenerate"/> does not apply to it.
    /// </summary>
    Task<TicinoGroupSheet> GetGroupSheetAsync(
        Guid propertyId,
        Guid bookingId,
        TicinoFileRequest? request = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Declares an arrival day of a Ticino property as uploaded to the police portal, with or without the receipt (E30/F02/US02/T01, NF-128 AC1, decision D10): the day is closed
    /// (<see cref="ListPendingDaysAsync"/> no longer lists it) and shows who declared it and when. <b>Idempotent</b>: a day declared already answers the declaration that exists
    /// (<see cref="TicinoDeclaration.Created"/> false), declared by whoever did it first, and nothing is told again; only a <paramref name="receiptId"/> the declaration does not have yet is
    /// attached to it. A created declaration is told live to the hosts of the property (push, bell and open pages: <c>ticino-notification-declared</c>).
    /// Throws <c>property_not_found</c>, <c>property_not_in_ticino</c>, <c>ticino_declaration_day_in_future</c> (a day that has not started in Switzerland cannot be declared: it would
    /// silence the alerts of an arrival that has not happened) or <c>ticino_declaration_receipt_invalid</c> (the receipt is not a <c>Police</c> receipt of this property and day).
    /// </summary>
    /// <param name="propertyId">The property.</param>
    /// <param name="day">The arrival day.</param>
    /// <param name="userId">The person who declares (<c>User.Id</c>, the Auth0 subject): the web layer says it, the services never read the caller.</param>
    /// <param name="receiptId">A <c>Police</c> receipt of the property and the day to attach (optional).</param>
    /// <param name="cancellationToken">Aborts the call.</param>
    Task<TicinoDeclaration> DeclareDayAsync(
        Guid propertyId,
        DateOnly day,
        string userId,
        Guid? receiptId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Undoes the declaration of an arrival day (E30/F02/US02/T01): the row is deleted, so the day is to notify again unless a receipt of it is recorded (a receipt still closes
    /// the day). <b>Idempotent</b>: <c>false</c>, and nothing told, when the day was not declared. The undo of a declaration is told live to the hosts of the property like the declaration.
    /// Throws <c>property_not_found</c> or <c>property_not_in_ticino</c>.
    /// </summary>
    /// <param name="propertyId">The property.</param>
    /// <param name="day">The arrival day.</param>
    /// <param name="userId">The person who undoes it (<c>User.Id</c>): written in the log of the application, never in the row (it is deleted).</param>
    /// <param name="cancellationToken">Aborts the call.</param>
    /// <returns><c>true</c> when a declaration was removed.</returns>
    Task<bool> UndeclareDayAsync(Guid propertyId, DateOnly day, string userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Who asks for a file of the Ticino police and what they confirmed (<see cref="ITicinoNotificationService.GenerateFileAsync"/>,
/// <see cref="ITicinoNotificationService.GetGroupSheetAsync"/>).
/// </summary>
/// <param name="UserId">The person who asks (<c>User.Id</c>, the Auth0 subject), written on the audit row of the file; <c>null</c> when no person asks.</param>
/// <param name="ConfirmRegenerate">
/// The person confirmed that the file of a day already declared (or with a receipt) is generated again, knowing that the portal may create duplicates: without it such a day answers
/// 409 <c>ticino_day_already_declared</c>. Not read for a group sheet.
/// </param>
public sealed record TicinoFileRequest(string? UserId = null, bool ConfirmRegenerate = false);

/// <summary>A declaration of an arrival day (<see cref="ITicinoNotificationService.DeclareDayAsync"/>).</summary>
/// <param name="PropertyId">The property.</param>
/// <param name="Day">The arrival day.</param>
/// <param name="DeclaredAtUtc">When it was declared (UTC): the instant of the first declaration, whoever repeats it.</param>
/// <param name="DeclaredBy">The display name of the person who declared it (first and last name of the user); <c>null</c> when the account no longer exists.</param>
/// <param name="ReceiptId">The receipt attached to the declaration; <c>null</c> when there is none.</param>
/// <param name="Created">True when this call recorded the declaration, false when the day was declared already.</param>
public sealed record TicinoDeclaration(Guid PropertyId, DateOnly Day, DateTime DeclaredAtUtc, string? DeclaredBy, Guid? ReceiptId, bool Created);

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
/// <param name="DeclaredAtUtc">When a person declared the day uploaded (E30/F02/US02/T01), UTC; <c>null</c> while nobody did.</param>
/// <param name="DeclaredBy">The display name of the person who declared it (first and last name of the user); <c>null</c> while nobody did, and when the account no longer exists.</param>
/// <param name="FileGeneratedAtUtc">When the file of the day (the CSV or the XML) was generated last, UTC; <c>null</c> when it never was.</param>
/// <param name="FileGeneratedCount">How many times the file of the day (the CSV or the XML) was generated.</param>
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
    int TransliteratedCount = 0,
    DateTime? DeclaredAtUtc = null,
    string? DeclaredBy = null,
    DateTime? FileGeneratedAtUtc = null,
    int FileGeneratedCount = 0)
{
    /// <summary>
    /// The day is closed: a person declared it uploaded or a receipt of the portal is recorded (the rule of <see cref="ITicinoNotificationService.ListPendingDaysAsync"/>). Generating
    /// its file again asks for a confirmation (<see cref="TicinoFileRequest.ConfirmRegenerate"/>), because the portal may create duplicates.
    /// </summary>
    public bool AlreadyDeclared => DeclaredAtUtc is not null || ReceiptCount > 0;
}

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
