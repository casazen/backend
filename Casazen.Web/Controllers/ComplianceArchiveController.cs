using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Compliance;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Controllers;

/// <summary>
/// The archive of the proofs CasaZen holds for the compliance obligations, the log of what was sent, the coverage report and the audit exports
/// (E03/F02/US03/T01, NF-47, NF-52, CHK-08, CHK-12 point 8). It lists, hands out and exports only what CasaZen owns: the files a host uploaded
/// (the receipt of an Alloggiati communication, the proof of an ISTAT communication, the receipt of a tourist tax declaration or payment). It
/// holds no Octorate screen, no attestation of a user and no audit of a handoff: where an outcome lives only in the Cassetto Compliance of Octorate the
/// report says so (<c>ExternalVerificationRequired</c>, the handoff topic) and proves nothing.
/// </summary>
/// <remarks>
/// <para><b>Policy and reach.</b> The class asks <see cref="CasazenPolicies.BookingRead"/> (the owner, the administrators, the property managers, the
/// accountant and the collaborator hold it), and every query is of the org of the caller and of the properties its <c>HostScope</c> reaches (AM-03): a
/// proof, a stay or a property of another org, or one outside the reach of a collaborator «Solo alcuni», answers 404 exactly like one that does not
/// exist. Uploading asks <see cref="CasazenPolicies.AlloggiatiSubmit"/> and, for a tax proof, <see cref="CasazenPolicies.PaymentWrite"/> too: the
/// receipt of a tax declaration or payment is a record of money.</para>
/// <para><b>Money.</b> Reading those receipts asks <see cref="CasazenPolicies.PaymentRead"/> too (the owner, the administrators, the property managers
/// and the accountant hold it; the collaborator never does, AM-03). Whoever lacks it does not get a 403, which would tell that they exist: the
/// receipts of a tax declaration or payment are simply not in the list, its total, the ZIP and its index, and the download of one is the 404 of a
/// proof that does not exist. The coverage report (a state per obligation, no file and no amount) is not a record of money and is not cut.</para>
/// <para><b>Files.</b> A download answers with a redirect to a short-lived signed address of the private bucket, never a public one; where the storage
/// cannot sign (development) it streams the file. The exports are built on request and never stored; <c>Cache-Control: private, no-store</c> on
/// every answer that carries a proof or a report.</para>
/// <para><b>The report</b> (<c>report</c>, <c>report/export</c>) is the traffic light's: behind <c>Features:ComplianceTrafficLight</c> (404 while it is
/// off) and decided by the same rules over a window of days. The period of an export or a report is both days included, at most 24 months
/// (400 <c>report_invalid_period</c>, 422 <c>report_period_too_long</c>).</para>
/// </remarks>
[ApiController]
[Authorize(Policy = CasazenPolicies.BookingRead)]
public sealed class ComplianceArchiveController(
    IComplianceArchiveService archive,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    IAuthorizationService authorizationService,
    IOptions<ComplianceOptions> options) : ControllerBase
{
    /// <summary>Code of a <c>format</c> that is not <c>pdf</c> or <c>csv</c> (400).</summary>
    public const string InvalidFormatCode = "compliance_report_invalid_format";

    /// <summary>
    /// The largest upload request: the largest file the option allows and a margin for the other fields. The multipart body is buffered in memory
    /// up to this size, so a proof is never spilled to a temporary file on disk.
    /// </summary>
    private const int MaxRequestBytes = (ComplianceArchiveOptions.MaxUploadMegabytes * 1024 * 1024) + (64 * 1024);

    /// <summary>
    /// The proofs held, newest first, one page of them (<c>from</c> and <c>to</c> are days, both included, of the day or month a proof is about).
    /// 404 for a property the caller does not reach. The receipts of a tax declaration or payment are in it, and in its total, only for who holds
    /// <c>payment.read</c>.
    /// </summary>
    [HttpGet("api/compliance/archive")]
    [ProducesResponseType(typeof(ComplianceArchivePageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> List(
        [FromQuery] Guid? propertyId,
        [FromQuery(Name = "from")] DateOnly? fromDay,
        [FromQuery(Name = "to")] DateOnly? toDay,
        [FromQuery] ManualProofKind? kind,
        CancellationToken cancellationToken,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = ComplianceArchiveLimits.DefaultPageSize)
    {
        if (fromDay is { } first && toDay is { } last && first > last)
            return this.ApiProblem(StatusCodes.Status400BadRequest, ReportRequestChecks.InvalidPeriodCode, "ReportInvalidPeriod");
        if (await GetScopeAsync(cancellationToken) is not { } scope)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var result = await archive.ListAsync(
            scope,
            new ComplianceArchiveQuery(propertyId, fromDay, toDay, kind, page, pageSize, await CanReadMoneyAsync()),
            cancellationToken);
        Response.Headers.CacheControl = "private, no-store";
        return Ok(ComplianceArchivePageDto.From(result, options.Value.Archive.PortalUrl));
    }

    /// <summary>The proofs of a stay (the receipts of its arrival day) and the log of what was sent for it, by whom: CasaZen, the channel manager or the host.</summary>
    [HttpGet("api/bookings/{bookingId:guid}/compliance")]
    [ProducesResponseType(typeof(ComplianceBookingComplianceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetBookingCompliance(Guid bookingId, CancellationToken cancellationToken)
    {
        if (await GetScopeAsync(cancellationToken) is not { } scope)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var view = await archive.GetBookingComplianceAsync(scope, bookingId, cancellationToken);
        Response.Headers.CacheControl = "private, no-store";
        return Ok(ComplianceBookingComplianceDto.From(view));
    }

    /// <summary>
    /// A proof, from the private bucket: a redirect (302) to a short-lived signed address, never a public one; the file itself where the storage
    /// cannot sign. 404 for a proof of another org, one outside the caller's reach, one that does not exist and, without <c>payment.read</c>, the
    /// receipt of a tax declaration or payment.
    /// </summary>
    [HttpGet("api/compliance/archive/{id:guid}/download")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Download(Guid id, CancellationToken cancellationToken)
    {
        if (await GetScopeAsync(cancellationToken) is not { } scope)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var download = await archive.OpenDownloadAsync(scope, id, await CanReadMoneyAsync(), cancellationToken);
        Response.Headers.CacheControl = "private, no-store";
        return download.SignedUrl is { } signed
            ? Redirect(signed.AbsoluteUri)
            : File(download.Content!, download.ContentType, download.FileName);
    }

    /// <summary>
    /// A ZIP of every proof of the period (<c>from</c> and <c>to</c>, both included, at most 24 months) and an <c>index.csv</c>, built now and
    /// never stored; with the traffic light on it also carries <c>rapporto-copertura.csv</c>, the coverage of the period. 422
    /// <c>compliance_export_too_large</c> when the period holds more than one export carries. The receipts of a tax declaration or payment are in the
    /// ZIP, its index and its count only for who holds <c>payment.read</c>.
    /// </summary>
    [HttpGet("api/compliance/archive/export")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Export(
        [FromQuery(Name = "from")] DateOnly? fromDay,
        [FromQuery(Name = "to")] DateOnly? toDay,
        [FromQuery] Guid? propertyId,
        CancellationToken cancellationToken)
    {
        if (this.CheckPeriod(fromDay, toDay, out var from, out var to) is { } periodProblem)
            return periodProblem;
        if (await GetScopeAsync(cancellationToken) is not { } scope)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var export = await archive.ExportAsync(
            scope, new ComplianceArchiveExportQuery(from, to, propertyId, await CanReadMoneyAsync()), cancellationToken);
        Response.Headers.CacheControl = "private, no-store";
        return File(export.Content, "application/zip", export.FileName);
    }

    /// <summary>
    /// The obligations expected for every stay and month of the caller's reach in the period, with the proof CasaZen holds for each, the incomplete ones
    /// included. Behind the traffic light (404 while it is off).
    /// </summary>
    [HttpGet("api/compliance/report")]
    [FeatureGate(FeatureFlags.ComplianceTrafficLight)]
    [ProducesResponseType(typeof(ComplianceCoverageReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> GetReport(
        [FromQuery(Name = "from")] DateOnly? fromDay,
        [FromQuery(Name = "to")] DateOnly? toDay,
        [FromQuery] Guid? propertyId,
        CancellationToken cancellationToken)
    {
        if (this.CheckPeriod(fromDay, toDay, out var from, out var to) is { } periodProblem)
            return periodProblem;
        if (await GetScopeAsync(cancellationToken) is not { } scope)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var report = await archive.GetCoverageReportAsync(scope, new ComplianceReportQuery(from, to, propertyId), cancellationToken);
        Response.Headers.CacheControl = "private, no-store";
        return Ok(ComplianceCoverageReportDto.Of(report));
    }

    /// <summary>
    /// The report as a PDF or a CSV (<c>format</c>, <c>pdf</c> by default), built now and never stored. A row held only by Octorate says «Da verificare
    /// nel Cassetto Compliance» with source «Octorate» and no outcome; the document never claims to be issued by an authority.
    /// </summary>
    [HttpGet("api/compliance/report/export")]
    [FeatureGate(FeatureFlags.ComplianceTrafficLight)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ExportReport(
        [FromQuery(Name = "from")] DateOnly? fromDay,
        [FromQuery(Name = "to")] DateOnly? toDay,
        [FromQuery] Guid? propertyId,
        [FromQuery] string? format,
        CancellationToken cancellationToken)
    {
        if (!TryReadFormat(format, out var reportFormat))
            return this.ApiProblem(StatusCodes.Status400BadRequest, InvalidFormatCode, "ComplianceReportInvalidFormat");
        if (this.CheckPeriod(fromDay, toDay, out var from, out var to) is { } periodProblem)
            return periodProblem;
        if (await GetScopeAsync(cancellationToken) is not { } scope)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var file = await archive.ExportReportAsync(scope, new ComplianceReportQuery(from, to, propertyId), reportFormat, cancellationToken);
        Response.Headers.CacheControl = "private, no-store";
        return File(file.Content, file.ContentType, file.FileName);
    }

    /// <summary>
    /// Uploads a proof: <c>multipart/form-data</c> with the property, the kind, the day and the file (a PDF, a JPEG or a PNG of at most
    /// <c>Compliance:Archive:UploadMaxMegabytes</c>, judged on its content). Asks <c>alloggiati.submit</c>, and <c>payment.write</c> too for a tax proof. 201 with the
    /// proof listed in the archive. 404 for a property the caller does not reach; 413, 415 and 422 for a file or a day that is not acceptable.
    /// </summary>
    [HttpPost("api/compliance/archive/proofs")]
    [Consumes("multipart/form-data")]
    [Authorize(Policy = CasazenPolicies.AlloggiatiSubmit)]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MemoryBufferThreshold = MaxRequestBytes, MultipartBodyLengthLimit = MaxRequestBytes)]
    [ProducesResponseType(typeof(ComplianceArchiveEntryDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Upload([FromForm] UploadComplianceProofForm form, CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not { Length: > 0 } userId)
            return Unauthorized();

        // A tax proof is a record of money: the receipt of a declaration or of a payment needs the permission that records a payment too.
        if (IsTaxProof(form.Kind!.Value)
            && !(await authorizationService.AuthorizeAsync(User, CasazenPolicies.PaymentWrite)).Succeeded)
        {
            return Forbid();
        }

        if (await GetScopeAsync(cancellationToken) is not { } scope)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        // No file chosen is an empty part with no name (a plain HTML form); an empty file that has a name is a proof, and an invalid one.
        if (form.File is not { } sent || (sent.Length == 0 && string.IsNullOrEmpty(sent.FileName)))
        {
            return this.ApiProblem(
                StatusCodes.Status422UnprocessableEntity, ComplianceArchiveErrorCodes.ProofFileInvalid, "ComplianceProofFileInvalid");
        }

        await using var content = sent.OpenReadStream();
        var entry = await archive.UploadProofAsync(
            scope,
            new UploadComplianceProofRequest(form.PropertyId!.Value, form.Kind!.Value, form.Day!.Value, userId),
            content,
            cancellationToken);
        return StatusCode(StatusCodes.Status201Created, ComplianceArchiveEntryDto.From(entry));
    }

    /// <summary>True for the receipts of the tourist tax (declaration and payment): the money kinds (<see cref="ComplianceArchiveRules.MoneyKinds"/>).</summary>
    public static bool IsTaxProof(ManualProofKind kind) => ComplianceArchiveRules.IsMoneyKind(kind);

    /// <summary>
    /// Whether the caller may read money (<c>payment.read</c>): the owner, the administrators, the property managers and the accountant; never the
    /// collaborator. It decides whether the receipts of a tax declaration or payment are part of what it reads (AM-03).
    /// </summary>
    private async Task<bool> CanReadMoneyAsync() =>
        (await authorizationService.AuthorizeAsync(User, CasazenPolicies.PaymentRead)).Succeeded;

    /// <summary><c>pdf</c> (the default) or <c>csv</c>, whatever the case; false for anything else.</summary>
    private static bool TryReadFormat(string? format, out ComplianceReportFormat reportFormat)
    {
        reportFormat = ComplianceReportFormat.Pdf;
        if (string.IsNullOrWhiteSpace(format) || string.Equals(format.Trim(), "pdf", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.Equals(format.Trim(), "csv", StringComparison.OrdinalIgnoreCase))
            return false;

        reportFormat = ComplianceReportFormat.Csv;
        return true;
    }

    /// <summary>The caller's reach (AM-03): the whole org for the roles that reach it, only the properties it was given otherwise; null without an org.</summary>
    private async Task<HostScope?> GetScopeAsync(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        return orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
    }
}
