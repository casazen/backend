using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.SwissCompliance;

/// <summary>
/// Body of <c>POST /api/properties/{propertyId}/swiss/ticino-notification</c> (casazen/backend#979): the arrival day whose guests go to the
/// Ticino police, and the format of the file.
/// </summary>
public sealed class TicinoNotificationRequest
{
    /// <summary>
    /// The arrival day, a calendar day (<c>yyyy-MM-dd</c>, for instance <c>2026-11-19</c>) in Switzerland (Europe/Zurich), no time and no time
    /// zone. The file lists the guests of the stays that arrive on that day.
    /// </summary>
    [Required(ErrorMessage = "SwissReportDateRequired")]
    public DateOnly? ReportDate { get; set; }

    /// <summary><c>csv</c> (default, the official CSV model) or <c>xml</c> (the official XSD), case-insensitive.</summary>
    public string? Format { get; set; }

    /// <summary>
    /// <c>true</c> when the person confirmed that the file of a day that is already declared (or has a receipt of the portal) is generated again, knowing that the portal may create
    /// duplicates (E30/F02/US02/T01, NF-128). Without it such a day answers 409 <c>ticino_day_already_declared</c>; a day that is not declared is never asked. Default <c>false</c>.
    /// </summary>
    public bool ConfirmRegenerate { get; set; }

    /// <summary>The format of <paramref name="value"/>: CSV when blank; false when it is neither <c>csv</c> nor <c>xml</c>.</summary>
    public static bool TryParseFormat(string? value, out TicinoPoliceFileFormat format)
    {
        format = TicinoPoliceFileFormat.Csv;
        if (string.IsNullOrWhiteSpace(value))
            return true;
        return Enum.TryParse(value.Trim(), ignoreCase: true, out format) && Enum.IsDefined(format);
    }
}

/// <summary>
/// Answer of <c>GET /api/properties/{propertyId}/swiss/ticino-notification</c> (casazen/backend#979): one arrival day of a Ticino property.
/// </summary>
/// <param name="PropertyId">The property.</param>
/// <param name="Day">The arrival day.</param>
/// <param name="StructureId">The «Numero identificativo LEAR / NO-LEAR» of the property; <c>null</c> until entered (no file without it).</param>
/// <param name="StayCount">The stays that arrive on the day.</param>
/// <param name="Rows">The persons in the file of the day, with the minors counted on them.</param>
/// <param name="Issues">The guests (or stays, <c>position</c> null) left out of the file, with the codes of what they miss: <c>firstName</c>,
/// <c>lastName</c>, <c>birthDate</c>, <c>nationality</c>, <c>documentType</c>, <c>guests</c> (fewer guests recorded than the booking counts).</param>
/// <param name="XmlAvailable">The XML can be written too (every row has a numeric postcode and a known country of residence).</param>
/// <param name="ReceiptCount">Receipts of the portal recorded for the day (type <c>Police</c>): one or more means the day is notified.</param>
/// <param name="DueAt">Deadline (UTC): 24 hours from the first recorded arrival, or the start of the next day.</param>
/// <param name="Reference">The reference of the file of the day, to record with its receipt.</param>
/// <param name="PortalUrl">Where the host uploads the file.</param>
/// <param name="PendingDays">The days of the property still to notify (of the last 14), oldest first.</param>
/// <param name="Warnings">
/// What the file alone does not settle, per stay (decision D11): <c>parental_authorization_needed</c> (minors without an adult: the written
/// authorization of the parents, to file in the archive as <c>ParentalAuthorization</c>), <c>group_sheet_incomplete</c> (a member of a group of six or
/// more misses a name or a nationality on the sheet). A stay may be in the file and still have a warning.
/// </param>
/// <param name="CsvCharset">The character set of the CSV (<c>GuestNotifications:TicinoCsvEncoding</c>): <c>windows-1252</c> (default) or <c>utf-8</c>.</param>
/// <param name="TransliteratedCount">
/// The characters of the rows that <paramref name="CsvCharset"/> cannot hold (NF-126): the CSV writes them without their accent, or as <c>?</c> as the
/// last resort. More than zero: the page says that some names are written without a letter they have, before the host uploads the file.
/// </param>
/// <param name="DeclaredAt">When a person of the org declared the day uploaded to the portal (E30/F02/US02/T01), UTC; <c>null</c> while nobody did.</param>
/// <param name="DeclaredBy">The display name (first and last name) of the person who declared it; <c>null</c> while nobody did, and when the account no longer exists.</param>
/// <param name="FileGeneratedAt">When the file of the day (CSV or XML) was generated last, UTC; <c>null</c> when it never was.</param>
/// <param name="FileGeneratedCount">How many times the file of the day (CSV or XML) was generated: more than one is the risk of duplicates on the portal.</param>
/// <param name="AlreadyDeclared">
/// The day is closed: it is declared (<paramref name="DeclaredAt"/> is set) or has a receipt (<paramref name="ReceiptCount"/> is more than zero), which is also the rule that takes it off the
/// days to notify. Generating its file again needs <c>confirmRegenerate: true</c> (409 <c>ticino_day_already_declared</c> otherwise).
/// </param>
public sealed record TicinoNotificationDayResponse(
    Guid PropertyId,
    DateOnly Day,
    string? StructureId,
    int StayCount,
    IReadOnlyList<TicinoNotificationRowResponse> Rows,
    IReadOnlyList<TicinoNotificationIssueResponse> Issues,
    bool XmlAvailable,
    int ReceiptCount,
    DateTime DueAt,
    string Reference,
    string PortalUrl,
    IReadOnlyList<TicinoPendingDayResponse> PendingDays,
    IReadOnlyList<TicinoStayWarningResponse> Warnings,
    string CsvCharset = "windows-1252",
    int TransliteratedCount = 0,
    DateTime? DeclaredAt = null,
    string? DeclaredBy = null,
    DateTime? FileGeneratedAt = null,
    int FileGeneratedCount = 0,
    bool AlreadyDeclared = false)
{
    public static TicinoNotificationDayResponse From(TicinoNotificationDay day)
    {
        ArgumentNullException.ThrowIfNull(day);
        return new TicinoNotificationDayResponse(
            day.PropertyId,
            day.Day,
            day.StructureId,
            day.StayCount,
            day.Plan.Registrations.Select(TicinoNotificationRowResponse.From).ToList(),
            day.Plan.Issues.Select(i => new TicinoNotificationIssueResponse(i.BookingId, i.Position, i.GuestName, i.Missing)).ToList(),
            day.Plan.XmlAvailable,
            day.ReceiptCount,
            day.DueAtUtc,
            $"TI-{day.PropertyId:N}-{day.Day:yyyyMMdd}",
            TicinoPoliceFile.PortalUrl,
            day.PendingDays.Select(p => new TicinoPendingDayResponse(p.Day, p.StayCount, p.DueAtUtc)).ToList(),
            day.Plan.Warnings.Select(w => new TicinoStayWarningResponse(w.BookingId, w.Code)).ToList(),
            day.CsvCharset,
            day.TransliteratedCount,
            day.DeclaredAtUtc,
            day.DeclaredBy,
            day.FileGeneratedAtUtc,
            day.FileGeneratedCount,
            day.AlreadyDeclared);
    }
}

/// <summary>
/// Body of <c>POST /api/properties/{propertyId}/swiss/ticino-notification/declare</c> (E30/F02/US02/T01, NF-128, decision D10): the arrival day the host uploaded to the police portal, and
/// optionally the receipt of the portal they recorded for it.
/// </summary>
public sealed class DeclareTicinoDayRequest
{
    /// <summary>The arrival day, a calendar day (<c>yyyy-MM-dd</c>) in Switzerland (Europe/Zurich), not after today.</summary>
    [Required(ErrorMessage = "SwissArrivalDayRequired")]
    public DateOnly? ArrivalDay { get; set; }

    /// <summary>
    /// A <c>Police</c> receipt of this property whose report date is the arrival day (the id <c>POST …/upload-receipt</c> gave back): optional, the declaration closes the day without it. A
    /// receipt of another property, of another day or of another type is refused (422 <c>ticino_declaration_receipt_invalid</c>).
    /// </summary>
    public Guid? ReceiptId { get; set; }
}

/// <summary>
/// The declaration of an arrival day, answer of <c>POST …/ticino-notification/declare</c> (E30/F02/US02/T01): the same answer on every repeat of the call. No guest data: the day response
/// (<see cref="TicinoNotificationDayResponse"/>) carries the guests and asks <c>guest.read</c>; this one only says that the day is declared, by whom and when.
/// </summary>
/// <param name="PropertyId">The property.</param>
/// <param name="ArrivalDay">The arrival day.</param>
/// <param name="DeclaredAt">When it was declared (UTC): the instant of the first declaration, whoever repeats the call.</param>
/// <param name="DeclaredBy">The display name (first and last name) of the person who declared it; <c>null</c> when the account no longer exists.</param>
/// <param name="ReceiptId">The receipt attached to the declaration; <c>null</c> when there is none.</param>
/// <param name="Created">
/// <c>true</c> when this call recorded the declaration (the other hosts were told); <c>false</c> when the day was declared already, by this person or by a co-host: the answer is then the
/// declaration that was there.
/// </param>
public sealed record TicinoDeclarationResponse(Guid PropertyId, DateOnly ArrivalDay, DateTime DeclaredAt, string? DeclaredBy, Guid? ReceiptId, bool Created)
{
    public static TicinoDeclarationResponse From(TicinoDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        return new TicinoDeclarationResponse(
            declaration.PropertyId, declaration.Day, declaration.DeclaredAtUtc, declaration.DeclaredBy, declaration.ReceiptId, declaration.Created);
    }
}

/// <summary>
/// A row of the file: the guest and the accompanying minors counted on them (14 to 17, under 14), or the leader of a group of six or more with every
/// other member counted on the row (14 or more, under 14).
/// </summary>
/// <param name="BookingId">The stay.</param>
/// <param name="GuestName">The notified person.</param>
/// <param name="PersonsOver14">«Mag14».</param>
/// <param name="PersonsUnder14">«Min14».</param>
/// <param name="Kind"><c>guest</c>, or <c>group</c> for the leader of a group of six or more (its sheet: <c>GET …/ticino-notification/group-sheet?bookingId=</c>).</param>
/// <param name="GroupSize">The persons of the group, the leader included; 0 for a guest.</param>
public sealed record TicinoNotificationRowResponse(
    Guid BookingId, string GuestName, int PersonsOver14, int PersonsUnder14, string Kind = TicinoNotificationRowResponse.GuestKind, int GroupSize = 0)
{
    /// <summary>The <see cref="Kind"/> of the row of a guest.</summary>
    public const string GuestKind = "guest";

    /// <summary>The <see cref="Kind"/> of the row of the leader of a group of six or more.</summary>
    public const string GroupKind = "group";

    public static TicinoNotificationRowResponse From(TicinoRegistration row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new TicinoNotificationRowResponse(
            row.BookingId,
            $"{row.FirstName} {row.LastName}",
            row.PersonsOver14,
            row.PersonsUnder14,
            row.Kind == TicinoRowKind.Group ? GroupKind : GuestKind,
            row.GroupSize);
    }
}

/// <summary>A warning about a stay of the day (the codes of <c>TicinoWarnings</c>, the clients translate them).</summary>
public sealed record TicinoStayWarningResponse(Guid BookingId, string Code);

/// <summary>A guest left out of the file and what they miss (the codes of <c>TicinoMissing</c>).</summary>
public sealed record TicinoNotificationIssueResponse(Guid BookingId, int? Position, string GuestName, IReadOnlyList<string> Missing);

/// <summary>An arrival day still to notify.</summary>
public sealed record TicinoPendingDayResponse(DateOnly Day, int StayCount, DateTime DueAt);

/// <summary>
/// Body of <c>POST /api/properties/{propertyId}/swiss/otr-report</c> (E08, ADR-016): the period of the overnight report.
/// </summary>
public sealed class OtrReportRequest
{
    /// <summary>First day of the period, a calendar day (<c>2026-10-01</c>); the night that starts on it is counted.</summary>
    [Required(ErrorMessage = "SwissPeriodFromRequired")]
    public DateOnly? FromDate { get; set; }

    /// <summary>
    /// Last day of the period, <b>inclusive</b> (<c>2026-10-31</c> for the month of October): the night that starts on it is
    /// counted. Not before <see cref="FromDate"/>, at most 366 days from it (both ends counted).
    /// </summary>
    [Required(ErrorMessage = "SwissPeriodToRequired")]
    public DateOnly? ToDate { get; set; }
}

/// <summary>
/// Query of <c>GET /api/properties/{propertyId}/swiss/guest-register</c> and of <c>GET …/guest-register/info</c> (E31/F01/US01/T01, NF-130): the period of the guest register, by arrival day,
/// and the format of the file.
/// </summary>
public sealed class GuestRegisterQuery
{
    /// <summary>The csv of the register: the default and the only format.</summary>
    public const string CsvFormat = "csv";

    /// <summary>First day of the period, a calendar day (<c>2026-01-01</c>): the stays that arrive on it are listed.</summary>
    [Required(ErrorMessage = "SwissPeriodFromRequired")]
    public DateOnly? From { get; set; }

    /// <summary>
    /// Last day of the period, <b>inclusive</b> (<c>2026-03-31</c>): the stays that arrive on it are listed. Not before <see cref="From"/>, at most 366 days from it (both ends counted).
    /// </summary>
    [Required(ErrorMessage = "SwissPeriodToRequired")]
    public DateOnly? To { get; set; }

    /// <summary><c>csv</c> (default, and the only one), case-insensitive. Read by the file; the info does not need it.</summary>
    public string? Format { get; set; }

    /// <summary>The format asked is the csv: blank (the default) or <c>csv</c> in any case.</summary>
    public static bool IsCsv(string? value) =>
        string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), CsvFormat, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// What the guest register of a period holds, answer of <c>GET …/guest-register/info</c> (E31/F01/US01/T01, NF-130): the screen reads it before the download. No guest and no value of the
/// file: counts, a number of years and a code.
/// </summary>
/// <param name="PropertyId">The property.</param>
/// <param name="From">The first day of the period.</param>
/// <param name="To">The last day of the period, inclusive.</param>
/// <param name="RowCount">The lines of the register, the header not counted: the guests, adults and minors, of the stays that arrived in the period.</param>
/// <param name="RetentionYears">How many years the regime of the property asks the host to keep the register, the same value as the header <c>X-Retention-Years</c> of the file; <c>null</c> when the sources do not say.</param>
/// <param name="RegimeCode">The code of the regime of the property: <c>CH-TI</c>, <c>CH-ZH</c>... or <c>CH-UNKNOWN</c> while its municipality is not known.</param>
public sealed record GuestRegisterInfoResponse(Guid PropertyId, DateOnly From, DateOnly To, int RowCount, int? RetentionYears, string RegimeCode)
{
    public static GuestRegisterInfoResponse Of(GuestRegisterInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return new GuestRegisterInfoResponse(info.PropertyId, info.From, info.To, info.RowCount, info.RetentionYears, info.RegimeCode);
    }
}

/// <summary>
/// A Swiss compliance file the server generated, answer of <c>ticino-notification</c> and <c>otr-report</c>. Nothing is stored:
/// the same request generates the file again from the bookings as they are at that moment, and a new <see cref="FileHash"/> if
/// they changed. The frontend turns <see cref="Content"/> into a download (for instance
/// <c>new Blob([content], { type: contentType })</c> named <see cref="FileName"/>).
/// </summary>
/// <param name="PropertyId">The property of the request.</param>
/// <param name="FileName">Suggested name of the downloaded file (<c>.xml</c> for the police notification, <c>.csv</c> for the OTR report).</param>
/// <param name="ContentType"><c>application/xml</c> or <c>text/csv</c>.</param>
/// <param name="Content">The whole file, UTF-8 text.</param>
/// <param name="Reference">Identifier the server gives to the file (for instance <c>TI-{propertyId}-{yyyyMMdd}</c> for a police notification).</param>
/// <param name="FileHash">SHA-256 of <paramref name="Content"/> as UTF-8 bytes, lowercase hexadecimal (64 characters).</param>
public sealed record SwissComplianceFileResponse(
    Guid PropertyId,
    string FileName,
    string ContentType,
    string Content,
    string Reference,
    string FileHash);

/// <summary>
/// Multipart form of <c>POST /api/properties/{propertyId}/swiss/upload-receipt</c> (E08, ADR-016): the receipt the cantonal
/// portal gave back for a file the host submitted, with the reference of the submission.
/// </summary>
public sealed class UploadSwissReceiptForm
{
    /// <summary>
    /// What the document is, case-insensitive: <c>Police</c> (the receipt of the police notification), <c>OTR</c> (the overnight report),
    /// <c>ParentalAuthorization</c> (the written authorization of the parents of a minor staying without an adult) or <c>GroupSheet</c> (the sheet of a
    /// group of six or more): the last two are the documents the Ticino rules keep five years (decision D11).
    /// </summary>
    [Required(ErrorMessage = "SwissReceiptTypeRequired")]
    public string? Type { get; set; }

    /// <summary>
    /// The day the document is about, a calendar day in ISO format (<c>yyyy-MM-dd</c>, for instance <c>2026-10-25</c>): the arrival day of a police
    /// notification, of a group sheet or of a parents' authorization, the first day (<c>fromDate</c>) of the period of an OTR report. The five years of
    /// the retention of the police documents run from it.
    /// </summary>
    [Required(ErrorMessage = "SwissReportDateRequired")]
    public DateOnly? ReportDate { get; set; }

    /// <summary>
    /// Reference of the submission: the confirmation number the portal showed or a reference of the host's own. At most
    /// 200 characters; surrounding spaces are removed.
    /// </summary>
    [Required(ErrorMessage = "SwissReceiptReferenceRequired")]
    [MaxLength(SwissComplianceReceipts.MaxReferenceLength, ErrorMessage = "SwissReceiptReferenceTooLong")]
    public string? Reference { get; set; }

    /// <summary>
    /// The receipt file, any type, not empty, at most 10 MB. Its original bytes are encrypted and retained in private storage.
    /// </summary>
    [Required(ErrorMessage = "SwissReceiptFileRequired")]
    public IFormFile? File { get; set; }
}

/// <summary>
/// A receipt recorded by <c>POST /api/properties/{propertyId}/swiss/upload-receipt</c>.
/// </summary>
/// <param name="Id">Id of the receipt.</param>
/// <param name="PropertyId">The property of the request.</param>
/// <param name="Type"><c>Police</c>, <c>OTR</c>, <c>ParentalAuthorization</c> or <c>GroupSheet</c>.</param>
/// <param name="ReportDate">The day the submission is about, as sent.</param>
/// <param name="Reference">The reference of the submission, trimmed.</param>
/// <param name="FileHash">SHA-256 of the receipt file, lowercase hexadecimal (64 characters).</param>
/// <param name="UploadedAt">When the receipt was recorded (UTC instant).</param>
public sealed record SwissReceiptResponse(
    Guid Id,
    Guid PropertyId,
    string Type,
    DateOnly ReportDate,
    string Reference,
    string FileHash,
    DateTime UploadedAt)
{
    public static SwissReceiptResponse From(SwissComplianceReceipt receipt) => new(
        receipt.Id,
        receipt.PropertyId,
        receipt.Type,
        DateOnly.FromDateTime(receipt.ReportDate),
        receipt.Reference,
        receipt.UploadedFileHash,
        receipt.UploadedAt);
}

/// <summary>No storage key or public URL is exposed; HasFile distinguishes retained originals from legacy hash-only rows.</summary>
public sealed record SwissReceiptArchiveItem(SwissReceiptResponse Receipt, bool HasFile);
public sealed record SwissReceiptPageResponse(IReadOnlyList<SwissReceiptArchiveItem> Items, int Page, int PageSize, int TotalCount, bool IsTicinoProperty)
{
    public static SwissReceiptPageResponse From(SwissComplianceReceiptPage page) => new(
        page.Items.Select(item => new SwissReceiptArchiveItem(SwissReceiptResponse.From(item.Receipt), item.HasFile)).ToList(),
        page.Page, page.PageSize, page.TotalCount, page.IsTicinoProperty);
}
