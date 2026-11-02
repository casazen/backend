using Casazen.Core.Services;
using Casazen.Core.Validation;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.SwissCompliance;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Swiss compliance files of a property in canton Ticino (E08, NF-15, ADR-016): the daily police notification (XML), the OTR
/// overnight report (CSV) and the archive of the receipts the cantonal portal gives back. CasaZen does not transmit anything to
/// the portal: the host downloads the file, submits it there and records the receipt here.
/// </summary>
/// <remarks>
/// <para><b>Routes</b> (all <c>POST</c>, under <c>api/properties/{propertyId}/swiss</c>, JSON in and out except the upload):
/// <c>ticino-notification</c> generates the police XML of one night, <c>otr-report</c> the OTR CSV of one period,
/// <c>upload-receipt</c> (multipart) records a receipt. The files are built on every request from the bookings as they are at
/// that moment and are never stored; the answer carries the whole file as text (<see cref="SwissComplianceFileResponse"/>).</para>
/// <para><b>Who may call.</b> The class asks <c>booking.read</c>; the two files list the guests by name, so they also ask
/// <c>guest.read</c> (as the Alloggiati record file does), and recording a receipt declares a legal communication done, so it asks
/// <c>alloggiati.submit</c> (the owner and the property managers; not a collaborator). Then the property is authorized as a row
/// (TN-3): one of another org is a 404, one the caller's scope does not reach (a collaborator «Solo alcuni» not given it) is a 403,
/// and nothing is generated or stored for either. No feature flag: the services were never behind one.</para>
/// <para><b>Errors</b> are ProblemDetails with a stable <c>code</c> (FD-05): 400 <c>validation_error</c> (a field is missing or is
/// not a date, with <c>errors</c> per field), <c>swiss_period_invalid</c>, <c>swiss_period_too_long</c>,
/// <c>swiss_receipt_type_invalid</c>, <c>swiss_receipt_file_invalid</c>; 401 <c>unauthorized</c>; 403 <c>forbidden</c>;
/// 404 <c>property_not_found</c>; 422 <c>property_not_in_ticino</c> (the property has no Swiss municipality or is in another canton).</para>
/// </remarks>
[ApiController]
[Route("api/properties/{propertyId:guid}/swiss")]
[Authorize(Policy = CasazenPolicies.BookingRead)]
public class SwissComplianceController(
    ITicinoNotificationService ticinoNotifications,
    IOtrTaxService otrReports,
    ISwissComplianceReceiptService receipts,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    ILogger<SwissComplianceController> logger) : ControllerBase
{
    /// <summary>Code of an OTR period whose end is before its start (400).</summary>
    public const string PeriodInvalidCode = "swiss_period_invalid";

    /// <summary>Code of an OTR period longer than <see cref="MaxPeriodDays"/> days (400).</summary>
    public const string PeriodTooLongCode = "swiss_period_too_long";

    /// <summary>Code of a receipt type that is not <c>Police</c> or <c>OTR</c> (400).</summary>
    public const string ReceiptTypeInvalidCode = "swiss_receipt_type_invalid";

    /// <summary>Code of a receipt file that is empty or larger than the limit (400).</summary>
    public const string ReceiptFileInvalidCode = "swiss_receipt_file_invalid";

    /// <summary>Longest OTR period, both ends counted: a year, leap year included.</summary>
    public const int MaxPeriodDays = 366;

    private const string XmlContentType = "application/xml";
    private const string CsvContentType = "text/csv";
    private const long MaxReceiptRequestBytes = SwissComplianceReceipts.MaxFileBytes + 64 * 1024;

    /// <summary>
    /// Generates the police notification of one night for a Ticino property (notifiche.ti.ch, ADR-016): the confirmed stays that
    /// include <c>reportDate</c>. A night with no stay still gives a valid file with an empty list of stays.
    /// </summary>
    /// <param name="propertyId">The property.</param>
    /// <param name="request">Body <c>{ "reportDate": "2026-10-25" }</c> (<see cref="TicinoNotificationRequest"/>).</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <returns>
    /// 200 with <see cref="SwissComplianceFileResponse"/>: <c>fileName</c> (<c>{reference}.xml</c>), <c>contentType</c>
    /// (<c>application/xml</c>), <c>content</c> (the XML), <c>reference</c> (<c>TI-{propertyId}-{yyyyMMdd}</c>) and
    /// <c>fileHash</c> (SHA-256 of <c>content</c>). The answer is never cached (<c>Cache-Control: private, no-store</c>): it holds guest names.
    /// </returns>
    /// <response code="200">The file.</response>
    /// <response code="400"><c>validation_error</c>: <c>reportDate</c> is missing or is not a date.</response>
    /// <response code="403">The caller may not read the bookings or the guests of this property.</response>
    /// <response code="404"><c>property_not_found</c>: no such property in the caller's org.</response>
    /// <response code="422"><c>property_not_in_ticino</c>: the property is not in canton Ticino.</response>
    [HttpPost("ticino-notification")]
    [Authorize(Policy = CasazenPolicies.GuestRead)]
    [ProducesResponseType(typeof(SwissComplianceFileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SwissComplianceFileResponse>> GenerateTicinoNotification(
        Guid propertyId,
        [FromBody] TicinoNotificationRequest request,
        CancellationToken cancellationToken)
    {
        var denied = await AuthorizeAsync(propertyId, BookingOperations.Read, cancellationToken);
        if (denied is not null)
            return denied;

        var reportDate = request.ReportDate!.Value;
        var file = await ticinoNotifications.GenerateDailyNotificationAsync(propertyId, AsDay(reportDate), cancellationToken);

        logger.LogInformation(
            "User {UserId} generated the Ticino police notification of property {PropertyId} for {ReportDate}",
            User.GetUserId(), propertyId, reportDate);
        Response.Headers.CacheControl = "private, no-store";
        return Ok(new SwissComplianceFileResponse(
            propertyId, $"{file.Reference}.xml", XmlContentType, file.XmlContent, file.Reference, file.FileHash));
    }

    /// <summary>
    /// Generates the OTR overnight report of a period for a Ticino property (ADR-016): one CSV row per confirmed stay that
    /// overlaps the period, with the nights that fall inside it. Both ends of the period count: the night that starts on
    /// <c>toDate</c> is included.
    /// </summary>
    /// <param name="propertyId">The property.</param>
    /// <param name="request">Body <c>{ "fromDate": "2026-10-01", "toDate": "2026-10-31" }</c> (<see cref="OtrReportRequest"/>).</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <returns>
    /// 200 with <see cref="SwissComplianceFileResponse"/>: <c>fileName</c> (<c>OTR-{propertyId}-{yyyyMMdd}-{yyyyMMdd}.csv</c>, from
    /// the requested days), <c>contentType</c> (<c>text/csv</c>), <c>content</c> (header row
    /// <c>PropertyId,PropertyName,BookingId,GuestName,CheckIn,CheckOut,Nights,NumberOfGuests</c> and the rows),
    /// <c>reference</c> (the identifier the server gives to the file) and <c>fileHash</c> (SHA-256 of <c>content</c>).
    /// Never cached (<c>Cache-Control: private, no-store</c>): it holds guest names.
    /// </returns>
    /// <response code="200">The file.</response>
    /// <response code="400">
    /// <c>validation_error</c>: <c>fromDate</c> or <c>toDate</c> is missing or is not a date; <c>swiss_period_invalid</c>:
    /// <c>toDate</c> is before <c>fromDate</c>; <c>swiss_period_too_long</c>: more than 366 days.
    /// </response>
    /// <response code="403">The caller may not read the bookings or the guests of this property.</response>
    /// <response code="404"><c>property_not_found</c>: no such property in the caller's org.</response>
    /// <response code="422"><c>property_not_in_ticino</c>: the property is not in canton Ticino.</response>
    [HttpPost("otr-report")]
    [Authorize(Policy = CasazenPolicies.GuestRead)]
    [ProducesResponseType(typeof(SwissComplianceFileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SwissComplianceFileResponse>> GenerateOtrReport(
        Guid propertyId,
        [FromBody] OtrReportRequest request,
        CancellationToken cancellationToken)
    {
        var denied = await AuthorizeAsync(propertyId, BookingOperations.Read, cancellationToken);
        if (denied is not null)
            return denied;

        var from = request.FromDate!.Value;
        var to = request.ToDate!.Value;
        if (to < from || to == DateOnly.MaxValue)
            return this.ApiProblem(StatusCodes.Status400BadRequest, PeriodInvalidCode, "SwissPeriodInvalid");
        if (to.DayNumber - from.DayNumber >= MaxPeriodDays)
            return this.ApiProblem(StatusCodes.Status400BadRequest, PeriodTooLongCode, "SwissPeriodTooLong", MaxPeriodDays);

        // The service counts the nights up to its end bound, exclusive: the last night of the period starts the day before it.
        var file = await otrReports.GeneratePeriodReportAsync(propertyId, AsDay(from), AsDay(to).AddDays(1), cancellationToken);

        logger.LogInformation(
            "User {UserId} generated the OTR report of property {PropertyId} for {FromDate}..{ToDate}",
            User.GetUserId(), propertyId, from, to);
        Response.Headers.CacheControl = "private, no-store";
        return Ok(new SwissComplianceFileResponse(
            propertyId, $"OTR-{propertyId:N}-{from:yyyyMMdd}-{to:yyyyMMdd}.csv", CsvContentType, file.CsvContent, file.Reference, file.FileHash));
    }

    /// <summary>
    /// Records the receipt of a file the host submitted on the cantonal portal (AC-NF15-3): <c>multipart/form-data</c> with
    /// <c>type</c> (<c>Police</c> or <c>OTR</c>, case-insensitive), <c>reportDate</c> (<c>2026-10-25</c>: the night of a police
    /// notification, the first day of an OTR period), <c>reference</c> (up to 200 characters) and <c>file</c> (the receipt, not
    /// empty, up to 10 MB). The file is read to compute its SHA-256 and is not stored; the receipt belongs to the property's org.
    /// Nothing is deduplicated: sending the same receipt twice records two.
    /// </summary>
    /// <param name="propertyId">The property.</param>
    /// <param name="form">The multipart form (<see cref="UploadSwissReceiptForm"/>).</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <returns>
    /// 201 with <see cref="SwissReceiptResponse"/>: <c>id</c>, <c>propertyId</c>, <c>type</c> (<c>Police</c> or <c>OTR</c>),
    /// <c>reportDate</c>, <c>reference</c> (trimmed), <c>fileHash</c> (SHA-256 of the file) and <c>uploadedAt</c>. There is no
    /// <c>Location</c>: the receipts cannot be read back yet.
    /// </returns>
    /// <response code="201">The receipt was recorded.</response>
    /// <response code="400">
    /// <c>validation_error</c>: a field is missing, <c>reportDate</c> is not a date or <c>reference</c> is longer than 200
    /// characters; <c>swiss_receipt_type_invalid</c>: <c>type</c> is not <c>Police</c> or <c>OTR</c>;
    /// <c>swiss_receipt_file_invalid</c>: the file is empty or larger than 10 MB (a request far over the limit is refused earlier by the server).
    /// </response>
    /// <response code="403">The caller may not declare the police communication of this property.</response>
    /// <response code="404"><c>property_not_found</c>: no such property in the caller's org.</response>
    /// <response code="422"><c>property_not_in_ticino</c>: the property is not in canton Ticino.</response>
    [HttpPost("upload-receipt")]
    [Consumes("multipart/form-data")]
    [Authorize(Policy = CasazenPolicies.AlloggiatiSubmit)]
    [RequestSizeLimit(MaxReceiptRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxReceiptRequestBytes)]
    [ProducesResponseType(typeof(SwissReceiptResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SwissReceiptResponse>> UploadReceipt(
        Guid propertyId,
        [FromForm] UploadSwissReceiptForm form,
        CancellationToken cancellationToken)
    {
        var denied = await AuthorizeAsync(propertyId, AlloggiatiOperations.Submit, cancellationToken);
        if (denied is not null)
            return denied;

        if (!EnumNames.TryParseDefined<SwissComplianceFileType>(form.Type, out var type))
            return this.ApiProblem(StatusCodes.Status400BadRequest, ReceiptTypeInvalidCode, "SwissReceiptTypeInvalid");

        var file = form.File!;
        if (file.Length is 0 or > SwissComplianceReceipts.MaxFileBytes)
        {
            return this.ApiProblem(
                StatusCodes.Status400BadRequest, ReceiptFileInvalidCode, "SwissReceiptFileInvalid", SwissComplianceReceipts.MaxFileBytes / (1024 * 1024));
        }

        await using var content = file.OpenReadStream();
        var receipt = await receipts.RecordAsync(
            propertyId, type, AsDay(form.ReportDate!.Value), form.Reference!, content, cancellationToken);

        logger.LogInformation(
            "User {UserId} recorded the Swiss {Type} receipt {ReceiptId} of property {PropertyId}",
            User.GetUserId(), receipt.Type, receipt.Id, propertyId);
        return StatusCode(StatusCodes.Status201Created, SwissReceiptResponse.From(receipt));
    }

    /// <summary>A day from the client as a stay date: midnight UTC of that day (the storage convention of the bookings).</summary>
    private static DateTime AsDay(DateOnly day) => DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);

    /// <summary>TN-3: 404 when the property is not visible (another org), 403 when it is but the caller's scope or permission does not allow it.</summary>
    private async Task<ActionResult?> AuthorizeAsync(
        Guid propertyId,
        HostOperationRequirement operation,
        CancellationToken cancellationToken)
    {
        var property = await hostResources.ForPropertyAsync(propertyId, cancellationToken);
        if (property is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, "property_not_found", "PropertyNotFound");

        if (!await authorizationService.IsAuthorizedAsync(User, property, operation))
        {
            logger.LogWarning(
                "User {UserId} denied {Permission} on the Swiss compliance files of property {PropertyId}",
                User.GetUserId(), operation.PermissionKey, propertyId);
            return Forbid();
        }

        return null;
    }
}
