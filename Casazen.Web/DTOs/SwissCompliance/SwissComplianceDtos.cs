using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.SwissCompliance;

/// <summary>
/// Body of <c>POST /api/properties/{propertyId}/swiss/ticino-notification</c> (E08, ADR-016): the night to notify to the
/// Ticino police.
/// </summary>
public sealed class TicinoNotificationRequest
{
    /// <summary>
    /// The night the notification is about, a calendar day (<c>yyyy-MM-dd</c>, for instance <c>2026-10-25</c>) in Switzerland (Europe/Zurich), no time and no
    /// time zone. The file lists the confirmed stays that include that night: check-in on that day or before, check-out after it.
    /// </summary>
    [Required(ErrorMessage = "SwissReportDateRequired")]
    public DateOnly? ReportDate { get; set; }
}

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
    /// <summary>What was submitted: <c>Police</c> (the police notification) or <c>OTR</c> (the overnight report); case-insensitive.</summary>
    [Required(ErrorMessage = "SwissReceiptTypeRequired")]
    public string? Type { get; set; }

    /// <summary>
    /// The day the submission is about, a calendar day in ISO format (<c>yyyy-MM-dd</c>, for instance <c>2026-10-25</c>): the night of a police notification, the first day
    /// (<c>fromDate</c>) of the period of an OTR report.
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
    /// The receipt file, any type, not empty, at most 10 MB. It is read to compute its SHA-256 and then discarded: the host
    /// keeps the original.
    /// </summary>
    [Required(ErrorMessage = "SwissReceiptFileRequired")]
    public IFormFile? File { get; set; }
}

/// <summary>
/// A receipt recorded by <c>POST /api/properties/{propertyId}/swiss/upload-receipt</c>.
/// </summary>
/// <param name="Id">Id of the receipt.</param>
/// <param name="PropertyId">The property of the request.</param>
/// <param name="Type"><c>Police</c> or <c>OTR</c>.</param>
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
