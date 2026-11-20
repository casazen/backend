using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Core.Validation;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.SwissCompliance;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Swiss compliance files of a property in canton Ticino (E08, NF-15, ADR-016): the police notification of the arrivals of a day (the official CSV or XML of notifiche.ti.ch, casazen/backend#979), the OTR
/// overnight report (CSV) and the archive of the receipts the cantonal portal gives back. CasaZen does not transmit anything to
/// the portal: the host downloads the file, submits it there and records the receipt here.
/// </summary>
/// <remarks>
/// <para><b>Routes</b> (all <c>POST</c>, under <c>api/properties/{propertyId}/swiss</c>, JSON in and out except the upload):
/// <c>ticino-notification</c> generates the police XML of one night, <c>otr-report</c> the OTR CSV of one period,
/// <c>upload-receipt</c> (multipart) records a receipt. The files are built on every request from the bookings as they are at
/// that moment and are never stored; the answer carries the whole file as text (<see cref="SwissComplianceFileResponse"/>).</para>
/// <para><b>Declaration of a day</b> (E30/F02/US02/T01, NF-128, decision D10): <c>POST ticino-notification/declare</c> says the day was uploaded to the portal, with or without the receipt
/// (idempotent), and <c>DELETE ticino-notification/declare?arrivalDay=</c> undoes it. A declared day is closed like a day with a receipt (the one rule of
/// <c>ITicinoNotificationService.ListPendingDaysAsync</c>); the file of a closed day is generated again only with <c>confirmRegenerate: true</c> (the portal may create duplicates), and
/// every generation of a file or a group sheet leaves an audit row with no personal data. The other hosts of the property learn a declaration or its undo live
/// (<c>ticino-notification-declared</c>: push, bell, open pages).</para>
/// <para><b>Who may call.</b> The class asks <c>booking.read</c>; the two files list the guests by name, so they also ask
/// <c>guest.read</c> (as the Alloggiati record file does), and recording a receipt or declaring a day declares a legal communication done, so they ask
/// <c>alloggiati.submit</c> (the owner and the property managers; not a collaborator). Then the property is authorized as a row
/// (TN-3): one of another org is a 404, one the caller's scope does not reach (a collaborator «Solo alcuni» not given it) is a 403,
/// and nothing is generated, declared or stored for either. No feature flag: the services were never behind one.</para>
/// <para><b>Errors</b> are ProblemDetails with a stable <c>code</c> (FD-05): 400 <c>validation_error</c> (a field is missing or is
/// not a date, with <c>errors</c> per field), <c>swiss_period_invalid</c>, <c>swiss_period_too_long</c>,
/// <c>swiss_receipt_type_invalid</c>, <c>swiss_receipt_file_invalid</c>; 401 <c>unauthorized</c>; 403 <c>forbidden</c>;
/// 404 <c>property_not_found</c>; 409 <c>ticino_day_already_declared</c> (the file of a closed day without <c>confirmRegenerate</c>); 422 <c>property_not_in_ticino</c> (the property has no
/// Swiss municipality or is in another canton), <c>ticino_declaration_day_in_future</c>, <c>ticino_declaration_receipt_invalid</c>.</para>
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

    /// <summary>Code of a receipt type that is not <c>Police</c>, <c>OTR</c>, <c>ParentalAuthorization</c> or <c>GroupSheet</c> (400).</summary>
    public const string ReceiptTypeInvalidCode = "swiss_receipt_type_invalid";

    /// <summary>Code of a receipt file that is empty or larger than the limit (400).</summary>
    public const string ReceiptFileInvalidCode = "swiss_receipt_file_invalid";

    /// <summary>
    /// Code of a document kept five years (<c>Police</c>, <c>ParentalAuthorization</c>, <c>GroupSheet</c>) whose report date is already past that time (400): the daily
    /// retention job would delete it the night it is recorded.
    /// </summary>
    public const string ReceiptDateTooOldCode = "swiss_receipt_date_too_old";

    /// <summary>Longest OTR period, both ends counted: a year, leap year included.</summary>
    /// <summary>Code of a police file format that is not <c>csv</c> or <c>xml</c> (400).</summary>
    public const string TicinoFileFormatInvalidCode = "ticino_file_format_invalid";

    public const int MaxPeriodDays = 366;

    private const string CsvContentType = "text/csv";
    private const long MaxReceiptRequestBytes = SwissComplianceReceipts.MaxFileBytes + 64 * 1024;

    /// <summary>Lists recorded Swiss receipts, including legacy hash-only evidence without a retained original.</summary>
    [HttpGet("receipts")]
    [Authorize(Policy = CasazenPolicies.GuestRead)]
    [ProducesResponseType(typeof(SwissReceiptPageResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<SwissReceiptPageResponse>> ListReceipts(
        Guid propertyId, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken cancellationToken = default)
    {
        var denied = await AuthorizeAsync(propertyId, BookingOperations.Read, cancellationToken);
        if (denied is not null)
            return denied;
        var result = await receipts.ListAsync(propertyId, page, pageSize, cancellationToken);
        Response.Headers.CacheControl = "private, no-store";
        return Ok(SwissReceiptPageResponse.From(result));
    }

    /// <summary>Downloads the verified original from private storage after the same tenant and property-scope checks.</summary>
    [HttpGet("receipts/{receiptId:guid}/file")]
    [Authorize(Policy = CasazenPolicies.GuestRead)]
    public async Task<ActionResult> DownloadReceipt(Guid propertyId, Guid receiptId, CancellationToken cancellationToken = default)
    {
        var denied = await AuthorizeAsync(propertyId, BookingOperations.Read, cancellationToken);
        if (denied is not null)
            return denied;
        var file = await receipts.OpenFileAsync(propertyId, receiptId, cancellationToken);
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(file.Content, file.ContentType, file.FileName);
    }

    /// <summary>
    /// One arrival day of the police notification of a Ticino property (notifiche.ti.ch, casazen/backend#979): who arrives, who is in the
    /// file and what the others miss, whether the receipt of the portal is recorded, who declared the day uploaded and when (<c>declaredAt</c>,
    /// <c>declaredBy</c>), how many times its file was generated (<c>fileGeneratedCount</c>, <c>fileGeneratedAt</c>), whether the day is closed
    /// (<c>alreadyDeclared</c>: declared or with a receipt) and the days still to notify. Without <c>date</c> it
    /// is the oldest day still to notify (of the last 14), or today when none is.
    /// </summary>
    /// <param name="propertyId">The property.</param>
    /// <param name="date">The arrival day (<c>yyyy-MM-dd</c>, Europe/Zurich); optional.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="200"><see cref="TicinoNotificationDayResponse"/>; never cached: it holds guest names.</response>
    /// <response code="403">The caller may not read the bookings or the guests of this property.</response>
    /// <response code="404"><c>property_not_found</c>: no such property in the caller's org.</response>
    /// <response code="422"><c>property_not_in_ticino</c>: the property is not in canton Ticino.</response>
    [HttpGet("ticino-notification")]
    [Authorize(Policy = CasazenPolicies.GuestRead)]
    [ProducesResponseType(typeof(TicinoNotificationDayResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<TicinoNotificationDayResponse>> GetTicinoNotificationDay(
        Guid propertyId,
        [FromQuery] DateOnly? date,
        CancellationToken cancellationToken)
    {
        var denied = await AuthorizeAsync(propertyId, BookingOperations.Read, cancellationToken);
        if (denied is not null)
            return denied;

        var day = await ticinoNotifications.GetDayAsync(propertyId, date ?? TimeProvider.System.TodayInRomeAsDateOnly(), cancellationToken);
        if (date is null && day.PendingDays.Count > 0 && day.PendingDays[0].Day != day.Day)
            day = await ticinoNotifications.GetDayAsync(propertyId, day.PendingDays[0].Day, cancellationToken);

        Response.Headers.CacheControl = "private, no-store";
        return Ok(TicinoNotificationDayResponse.From(day));
    }

    /// <summary>
    /// The file of an arrival day for the Ticino police portal (casazen/backend#979), in the official format the host uploads at
    /// «Notifiche → Invio»: <c>csv</c> (default; Windows-1252 like the official model, or UTF-8 by <c>GuestNotifications:TicinoCsvEncoding</c>, the
    /// <c>charset</c> of the content type says which) or <c>xml</c> (the official XSD). It holds the guests of the stays whose data are complete
    /// (<c>GET</c> lists the others with what they miss); it is rebuilt on every request and never stored.
    /// </summary>
    /// <param name="propertyId">The property.</param>
    /// <param name="request">Body <c>{ "reportDate": "2026-11-19", "format": "csv", "confirmRegenerate": false }</c> (<see cref="TicinoNotificationRequest"/>).</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="200">The file (attachment), never cached: it holds guest data. Every generation leaves one audit row (format, rows, hash; no personal data).</response>
    /// <response code="400"><c>validation_error</c>: <c>reportDate</c> is missing; <c>ticino_file_format_invalid</c>: the format is not <c>csv</c> or <c>xml</c>.</response>
    /// <response code="403">The caller may not read the bookings or the guests of this property.</response>
    /// <response code="404"><c>property_not_found</c>: no such property in the caller's org.</response>
    /// <response code="409">
    /// <c>ticino_day_already_declared</c>: the day is declared uploaded (or has a receipt of the portal) and the portal may create duplicates: the same request with
    /// <c>confirmRegenerate: true</c> generates the file again.
    /// </response>
    /// <response code="422">
    /// <c>property_not_in_ticino</c>; <c>ticino_structure_id_missing</c>: the property has no identifier of the portal;
    /// <c>ticino_notification_empty</c>: no guest of the day has complete data; <c>ticino_xml_not_available</c>: use the CSV.
    /// </response>
    [HttpPost("ticino-notification")]
    [Authorize(Policy = CasazenPolicies.GuestRead)]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult> GenerateTicinoNotification(
        Guid propertyId,
        [FromBody] TicinoNotificationRequest request,
        CancellationToken cancellationToken)
    {
        var denied = await AuthorizeAsync(propertyId, BookingOperations.Read, cancellationToken);
        if (denied is not null)
            return denied;

        if (!TicinoNotificationRequest.TryParseFormat(request.Format, out var format))
            return this.ApiProblem(StatusCodes.Status400BadRequest, TicinoFileFormatInvalidCode, "TicinoFileFormatInvalid");

        var reportDate = request.ReportDate!.Value;
        var file = await ticinoNotifications.GenerateFileAsync(
            propertyId, reportDate, format, new TicinoFileRequest(User.GetUserId(), request.ConfirmRegenerate), cancellationToken);

        logger.LogInformation(
            "User {UserId} generated the Ticino police file ({Format}, {Rows} rows) of property {PropertyId} for {ReportDate} (regeneration confirmed: {Confirmed})",
            User.GetUserId(), format, file.Rows, propertyId, reportDate, request.ConfirmRegenerate);
        Response.Headers.CacheControl = "private, no-store";
        return File(file.Content, file.ContentType, file.FileName);
    }

    /// <summary>
    /// Declares an arrival day of a Ticino property as uploaded to the police portal, with or without the receipt (E30/F02/US02/T01, NF-128, decision D10): the day is closed like a day
    /// with a receipt (it is no longer one of the days to notify, so the alerts and the cockpit stop listing it) and shows who declared it and when. <b>Idempotent</b>: a day declared already
    /// answers the declaration that exists (<c>created: false</c>), whoever repeats the call; a <c>receiptId</c> the declaration does not have yet is attached to it. The other hosts of the
    /// property are told live when a declaration is created (<c>ticino-notification-declared</c>). The answer holds no guest data.
    /// </summary>
    /// <param name="propertyId">The property.</param>
    /// <param name="request">Body <c>{ "arrivalDay": "2026-11-19", "receiptId": null }</c> (<see cref="DeclareTicinoDayRequest"/>).</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="200"><see cref="TicinoDeclarationResponse"/>, never cached.</response>
    /// <response code="400"><c>validation_error</c>: <c>arrivalDay</c> is missing or is not a date.</response>
    /// <response code="401"><c>unauthorized</c>: the token has no subject.</response>
    /// <response code="403">The caller may not declare the police communication of this property (<c>alloggiati.submit</c>, or the property is out of their reach).</response>
    /// <response code="404"><c>property_not_found</c>: no such property in the caller's org.</response>
    /// <response code="422">
    /// <c>property_not_in_ticino</c>; <c>ticino_declaration_day_in_future</c>: the day has not started in Switzerland; <c>ticino_declaration_receipt_invalid</c>: the receipt is not a
    /// <c>Police</c> receipt of this property and day.
    /// </response>
    [HttpPost("ticino-notification/declare")]
    [Authorize(Policy = CasazenPolicies.AlloggiatiSubmit)]
    [ProducesResponseType(typeof(TicinoDeclarationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<TicinoDeclarationResponse>> DeclareTicinoDay(
        Guid propertyId,
        [FromBody] DeclareTicinoDayRequest request,
        CancellationToken cancellationToken)
    {
        var denied = await AuthorizeAsync(propertyId, AlloggiatiOperations.Submit, cancellationToken);
        if (denied is not null)
            return denied;

        if (User.GetUserId() is not { Length: > 0 } userId)
            return Unauthorized();

        var arrivalDay = request.ArrivalDay!.Value;
        var declaration = await ticinoNotifications.DeclareDayAsync(propertyId, arrivalDay, userId, request.ReceiptId, cancellationToken);

        logger.LogInformation(
            "User {UserId} declared the Ticino police notification of property {PropertyId} for {ArrivalDay} (created: {Created})",
            userId, propertyId, arrivalDay, declaration.Created);
        Response.Headers.CacheControl = "private, no-store";
        return Ok(TicinoDeclarationResponse.From(declaration));
    }

    /// <summary>
    /// Undoes the declaration of an arrival day (E30/F02/US02/T01): by any person of the same org with the same permission as declaring. The day is to notify again unless a receipt of the
    /// portal is recorded for it (a receipt still closes the day). <b>Idempotent</b>: 204 also when the day was not declared. The other hosts of the property are told live when a
    /// declaration was removed.
    /// </summary>
    /// <param name="propertyId">The property.</param>
    /// <param name="arrivalDay">The arrival day (<c>yyyy-MM-dd</c>).</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="204">Nothing is declared for the day any more.</response>
    /// <response code="400"><c>validation_error</c>: <c>arrivalDay</c> is missing or is not a date.</response>
    /// <response code="401"><c>unauthorized</c>: the token has no subject.</response>
    /// <response code="403">The caller may not declare the police communication of this property (<c>alloggiati.submit</c>, or the property is out of their reach).</response>
    /// <response code="404"><c>property_not_found</c>: no such property in the caller's org.</response>
    /// <response code="422"><c>property_not_in_ticino</c>.</response>
    [HttpDelete("ticino-notification/declare")]
    [Authorize(Policy = CasazenPolicies.AlloggiatiSubmit)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult> UndeclareTicinoDay(Guid propertyId, [FromQuery] DateOnly? arrivalDay, CancellationToken cancellationToken)
    {
        var denied = await AuthorizeAsync(propertyId, AlloggiatiOperations.Submit, cancellationToken);
        if (denied is not null)
            return denied;

        if (User.GetUserId() is not { Length: > 0 } userId)
            return Unauthorized();
        if (arrivalDay is not { } day)
            return this.ApiProblem(StatusCodes.Status400BadRequest, "validation_error", "ValidationError");

        var removed = await ticinoNotifications.UndeclareDayAsync(propertyId, day, userId, cancellationToken);

        logger.LogInformation(
            "User {UserId} undid the Ticino police declaration of property {PropertyId} for {ArrivalDay} (removed: {Removed})",
            userId, propertyId, day, removed);
        return NoContent();
    }

    /// <summary>
    /// The sheet of a group of six or more persons of a Ticino property (decision D11, casazen/backend#979): the first name, the last name, the birth date
    /// and the nationality (ISO alpha-3) of every member, the leader first, which the host keeps five years at the disposal of the police and of the
    /// regional tourism organizations (or files in the archive as type <c>GroupSheet</c>). The file of the day holds only the leader's row, with the other
    /// members counted on it (<c>kind: group</c>). CSV in UTF-8 with its byte order mark, semicolons; rebuilt from the check-in on every request and never
    /// stored.
    /// </summary>
    /// <param name="propertyId">The property.</param>
    /// <param name="bookingId">The stay of the group.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="200">The sheet (attachment), never cached: it holds the personal data of the members.</response>
    /// <response code="400"><c>validation_error</c>: <c>bookingId</c> is missing.</response>
    /// <response code="403">The caller may not read the guests of this property.</response>
    /// <response code="404">
    /// <c>property_not_found</c>: no such property in the caller's org; <c>booking_not_found</c>: no stay of this property with that id that owes a
    /// notification (another org's, a pending or cancelled booking, a no-show).
    /// </response>
    /// <response code="422"><c>property_not_in_ticino</c>; <c>ticino_not_a_group</c>: the stay is not a group of six or more.</response>
    [HttpGet("ticino-notification/group-sheet")]
    [Authorize(Policy = CasazenPolicies.GuestRead)]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult> DownloadGroupSheet(Guid propertyId, [FromQuery] Guid? bookingId, CancellationToken cancellationToken)
    {
        var denied = await AuthorizeAsync(propertyId, BookingOperations.Read, cancellationToken);
        if (denied is not null)
            return denied;

        if (bookingId is not { } booking || booking == Guid.Empty)
            return this.ApiProblem(StatusCodes.Status400BadRequest, "validation_error", "ValidationError");

        var sheet = await ticinoNotifications.GetGroupSheetAsync(propertyId, booking, new TicinoFileRequest(User.GetUserId()), cancellationToken);

        logger.LogInformation(
            "User {UserId} downloaded the group sheet ({Members} members) of booking {BookingId} of property {PropertyId}",
            User.GetUserId(), sheet.Members, booking, propertyId);
        Response.Headers.CacheControl = "private, no-store";
        return File(sheet.Content, sheet.ContentType, sheet.FileName);
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
    /// Records the receipt of a file the host submitted on the cantonal portal (AC-NF15-3), or a document the Ticino rules keep five years (decision
    /// D11): <c>multipart/form-data</c> with <c>type</c> (<c>Police</c>, <c>OTR</c>, <c>ParentalAuthorization</c> or <c>GroupSheet</c>,
    /// case-insensitive), <c>reportDate</c> (<c>2026-10-25</c>: the arrival day of a police notification, a parents' authorization or a group sheet, the
    /// first day of an OTR period), <c>reference</c> (up to 200 characters) and <c>file</c> (the document, not empty, up to 10 MB). The original is
    /// encrypted in private storage and hashed; the document belongs to the property's org. Nothing is deduplicated: sending the same receipt twice
    /// records two. The police receipts, the parents' authorizations and the group sheets are deleted five years after their report date (NF-128).
    /// </summary>
    /// <param name="propertyId">The property.</param>
    /// <param name="form">The multipart form (<see cref="UploadSwissReceiptForm"/>).</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <returns>
    /// 201 with <see cref="SwissReceiptResponse"/>: <c>id</c>, <c>propertyId</c>, <c>type</c> (<c>Police</c> or <c>OTR</c>),
    /// <c>reportDate</c>, <c>reference</c> (trimmed), <c>fileHash</c> (SHA-256 of the file) and <c>uploadedAt</c>. There is no
    /// <c>Location</c>; metadata and originals are available through the receipts endpoints.
    /// </returns>
    /// <response code="201">The receipt was recorded.</response>
    /// <response code="400">
    /// <c>validation_error</c>: a field is missing, <c>reportDate</c> is not a date or <c>reference</c> is longer than 200
    /// characters; <c>swiss_receipt_type_invalid</c>: <c>type</c> is not <c>Police</c>, <c>OTR</c>, <c>ParentalAuthorization</c> or <c>GroupSheet</c>;
    /// <c>swiss_receipt_file_invalid</c>: the file is empty or larger than 10 MB (a request far over the limit is refused earlier by the server);
    /// <c>swiss_receipt_date_too_old</c>: a <c>Police</c>, <c>ParentalAuthorization</c> or <c>GroupSheet</c> document whose <c>reportDate</c> is more than five years
    /// ago, which the daily retention job would delete the night it is recorded (an OTR report is never refused for its date).
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

        // NF-128: a document of a type kept five years with a date already past that time would be deleted (file and row) by the daily retention job the night
        // it is recorded, with a count in the log: refused here, so a legal proof is never destroyed in silence by a mistyped date.
        if (SwissComplianceReceipts.IsPastRetention(type, form.ReportDate!.Value, TimeProvider.System.TodayInRomeAsDateOnly()))
        {
            return this.ApiProblem(
                StatusCodes.Status400BadRequest, ReceiptDateTooOldCode, "SwissReceiptDateTooOld", SwissComplianceReceipts.PoliceRetentionYears);
        }

        var file = form.File!;
        if (file.Length is 0 or > SwissComplianceReceipts.MaxFileBytes)
        {
            return this.ApiProblem(
                StatusCodes.Status400BadRequest, ReceiptFileInvalidCode, "SwissReceiptFileInvalid", SwissComplianceReceipts.MaxFileBytes / (1024 * 1024));
        }

        await using var content = file.OpenReadStream();
        var receipt = await receipts.RecordAsync(
            propertyId, type, AsDay(form.ReportDate!.Value), form.Reference!, content, file.FileName, file.ContentType, cancellationToken);

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
