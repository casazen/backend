using System.Globalization;
using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The document archive (NF-99, REP-04): every document the caller may read in one place, filtered, searched, downloaded one by one and
/// exported as a ZIP for a period. The door is the read of the property core in either rental context (<see cref="CasazenPolicies.SharedPropertyRead"/>:
/// every host reader passes, a long-term landlord with no short-rent context included); each type of document is then opened by the permission
/// of the area it belongs to (<see cref="DocumentArchiveAccessResolver"/>: <c>property.read</c> for the property documents, <c>lease.read</c> for
/// the leases, <c>payment.read</c> for the tourist tax receipts, <c>guest.read</c> for the compliance proofs), so the Accountant reads the tax,
/// property and lease documents and never a compliance proof. Everything works inside the org of the caller and the reach of its
/// <see cref="HostScope"/>, decided in SQL: another org's document, or one of a property outside the scope, is a 404, never a 403.
/// <para>
/// <c>GET api/documents</c> takes <c>type</c> (repeatable: <c>propertyDocument</c>, <c>contract</c>, <c>touristTax</c>, ...), <c>from</c> and
/// <c>to</c> (<c>yyyy-MM-dd</c>, Europe/Rome days), <c>propertyId</c>, <c>bookingId</c>, <c>q</c>, <c>page</c> and <c>pageSize</c> (1 to 100);
/// 400 <c>archive_invalid_type</c>, <c>archive_invalid_period</c> and <c>archive_invalid_page</c> for a word the archive does not know.
/// <c>GET api/documents/types</c> answers the types the caller may read with their count for the same filters except the type.
/// <c>GET api/documents/{type}/{id}/download</c> streams one file (<c>Cache-Control: private, no-store</c>): 403 <c>archive_type_forbidden</c>,
/// 404 <c>archive_document_not_found</c> or <c>archive_document_has_no_file</c>. <c>GET api/documents/export</c> takes <c>from</c> and <c>to</c>
/// (required, at most 24 months: 400 <c>report_invalid_period</c>, 422 <c>report_period_too_long</c>), and optional <c>type</c> and
/// <c>propertyId</c>, and streams <c>casazen-documenti-{from}-{to}.zip</c>; beyond the caps 422 <c>archive_export_too_large</c> with the number
/// of documents found, before the first byte. Each export and each download of a compliance proof writes one line in the log with the ids of
/// the org and of the user and the counts, never a document name.
/// </para>
/// </summary>
[ApiController]
[Route("api/documents")]
[Authorize(Policy = CasazenPolicies.SharedPropertyRead)]
public class DocumentArchiveController(
    IDocumentArchiveService archive,
    IEnumerable<IDocumentArchiveSource> sources,
    IContextAuthorizationService contextAuthorization,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    ILogger<DocumentArchiveController> logger) : ControllerBase
{
    /// <summary>Code of a <c>type</c> that is not the key of a kind of document.</summary>
    public const string InvalidTypeCode = "archive_invalid_type";

    /// <summary>Code of days that are out of order or out of the supported years.</summary>
    public const string InvalidPeriodCode = "archive_invalid_period";

    /// <summary>Code of a <c>page</c> below 1 or a <c>pageSize</c> outside 1 to <see cref="DocumentArchiveQuery.MaxPageSize"/>.</summary>
    public const string InvalidPageCode = "archive_invalid_page";

    /// <summary>The documents of the caller, a page at a time, newest day first.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(DocumentArchivePageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(
        [FromQuery(Name = "type")] string[]? types,
        [FromQuery(Name = "from")] DateOnly? fromDay,
        [FromQuery(Name = "to")] DateOnly? toDay,
        [FromQuery] Guid? propertyId,
        [FromQuery] Guid? bookingId,
        [FromQuery(Name = "q")] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DocumentArchiveQuery.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        if (page < 1 || pageSize is < 1 or > DocumentArchiveQuery.MaxPageSize)
            return this.ApiProblem(StatusCodes.Status400BadRequest, InvalidPageCode, "ArchiveInvalidPage", DocumentArchiveQuery.MaxPageSize);
        if (ReadFilters(types, fromDay, toDay, propertyId, bookingId, search, page, pageSize, out var query) is { } problem)
            return problem;
        if (await ResolveCallerAsync(cancellationToken) is not { } caller)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var result = await archive.ListAsync(caller.Scope, caller.Access, query, cancellationToken);
        return Ok(DocumentArchivePageDto.From(result));
    }

    /// <summary>The types the caller may read, each with how many documents match the filters (the type filter left out).</summary>
    [HttpGet("types")]
    [ProducesResponseType(typeof(DocumentArchiveTypesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetTypes(
        [FromQuery(Name = "from")] DateOnly? fromDay,
        [FromQuery(Name = "to")] DateOnly? toDay,
        [FromQuery] Guid? propertyId,
        [FromQuery] Guid? bookingId,
        [FromQuery(Name = "q")] string? search,
        CancellationToken cancellationToken = default)
    {
        if (ReadFilters(null, fromDay, toDay, propertyId, bookingId, search, 1, DocumentArchiveQuery.DefaultPageSize, out var query) is { } problem)
            return problem;
        if (await ResolveCallerAsync(cancellationToken) is not { } caller)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var counts = await archive.CountByTypeAsync(caller.Scope, caller.Access, query, cancellationToken);
        return Ok(DocumentArchiveTypesDto.From(counts));
    }

    /// <summary>One file, from the private bucket, as the original: its name and its content type.</summary>
    [HttpGet("{type}/{id:guid}/download")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Download(string type, Guid id, CancellationToken cancellationToken)
    {
        if (!ArchiveDocumentTypes.TryParse(type, out var documentType))
            return this.ApiProblem(StatusCodes.Status400BadRequest, InvalidTypeCode, "ArchiveInvalidType");
        if (await ResolveCallerAsync(cancellationToken) is not { } caller)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var file = await archive.OpenAsync(caller.Scope, caller.Access, documentType, id, cancellationToken);

        // A compliance proof carries data about guests: who opened one is in the log, by id, with no name of a document.
        if (ArchiveDocumentTypes.IsComplianceProof(documentType))
        {
            logger.LogInformation(
                "Document archive: a compliance proof was downloaded. OrgId={OrgId} UserId={UserId} Type={Type}",
                caller.Scope.OrgId,
                caller.UserId,
                documentType);
        }

        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(file.Content, file.ContentType, file.FileName);
    }

    /// <summary>
    /// The documents of a period as a ZIP, streamed from the storage to the response: a folder for each type and <c>indice.csv</c>. It holds
    /// exactly what the list shows to the same caller for the same days (and the optional type and property).
    /// </summary>
    [HttpGet("export")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Export(
        [FromQuery(Name = "from")] DateOnly? fromDay,
        [FromQuery(Name = "to")] DateOnly? toDay,
        [FromQuery(Name = "type")] string[]? types,
        [FromQuery] Guid? propertyId,
        CancellationToken cancellationToken)
    {
        if (this.CheckPeriod(fromDay, toDay, out var first, out var last) is { } periodProblem)
            return periodProblem;
        if (!TryReadTypes(types, out var kinds))
            return this.ApiProblem(StatusCodes.Status400BadRequest, InvalidTypeCode, "ArchiveInvalidType");
        if (await ResolveCallerAsync(cancellationToken) is not { } caller)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        // The headers of the ZIP are set when the first byte goes out, and only for a 200: the service refuses an export beyond the caps before
        // it writes anything, and that answer is a problem (422), which must not carry the name of a file to save.
        Response.Headers.CacheControl = "private, no-store";
        ZipDownloadHeaders.WhenTheResponseStarts(Response, ExportFileName(first, last));

        var query = new DocumentArchiveQuery(kinds, first, last, propertyId, null, null, 1, DocumentArchiveQuery.MaxPageSize);
        var result = await archive.ExportAsync(caller.Scope, caller.Access, query, Response.Body, cancellationToken);

        logger.LogInformation(
            "Document archive exported. OrgId={OrgId} UserId={UserId} Documents={Documents} Files={Files} Bytes={Bytes}",
            caller.Scope.OrgId,
            caller.UserId,
            result.Documents,
            result.Files,
            result.Bytes);
        return new EmptyResult();
    }

    /// <summary>The name of the ZIP of a period: <c>casazen-documenti-20260101-20261231.zip</c>.</summary>
    public static string ExportFileName(DateOnly from, DateOnly to) =>
        $"casazen-documenti-{from.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}-{to.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.zip";

    // The words of a list or a count, before anything is read: the types, then the days. The problem to answer, or null with the query.
    private ObjectResult? ReadFilters(
        string[]? types,
        DateOnly? fromDay,
        DateOnly? toDay,
        Guid? propertyId,
        Guid? bookingId,
        string? search,
        int page,
        int pageSize,
        out DocumentArchiveQuery query)
    {
        query = new DocumentArchiveQuery(null, null, null, null, null, null, 1, DocumentArchiveQuery.DefaultPageSize);
        if (!TryReadTypes(types, out var kinds))
            return this.ApiProblem(StatusCodes.Status400BadRequest, InvalidTypeCode, "ArchiveInvalidType");
        if (!IsValidPeriod(fromDay, toDay))
            return this.ApiProblem(StatusCodes.Status400BadRequest, InvalidPeriodCode, "ArchiveInvalidPeriod");

        query = new DocumentArchiveQuery(kinds, fromDay, toDay, propertyId, bookingId, search, page, pageSize);
        return null;
    }

    /// <summary>No <c>type</c> is every type; each one must be the key of a kind of document (a blank one is not).</summary>
    private static bool TryReadTypes(string[]? types, out IReadOnlyCollection<ArchiveDocumentType>? kinds)
    {
        kinds = null;
        if (types is null or { Length: 0 })
            return true;

        var parsed = new HashSet<ArchiveDocumentType>();
        foreach (var key in types)
        {
            if (!ArchiveDocumentTypes.TryParse(key, out var kind))
                return false;

            parsed.Add(kind);
        }

        kinds = parsed;
        return true;
    }

    /// <summary>Each day in the years the dashboard supports and, when there are two, the first not after the last.</summary>
    private static bool IsValidPeriod(DateOnly? fromDay, DateOnly? toDay) =>
        (fromDay is null || IsSupportedYear(fromDay.Value))
        && (toDay is null || IsSupportedYear(toDay.Value))
        && (fromDay is null || toDay is null || fromDay.Value <= toDay.Value);

    private static bool IsSupportedYear(DateOnly day) =>
        day.Year is >= HostDashboardPeriod.MinCustomYear and <= HostDashboardPeriod.MaxCustomYear;

    private sealed record Caller(HostScope Scope, DocumentArchiveAccess Access, string UserId);

    /// <summary>The caller's reach and the types it may read; null without a user, an org or a scope (never widened to the whole org).</summary>
    private async Task<Caller?> ResolveCallerAsync(CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not { } userId)
            return null;

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null || await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken) is not { } scope)
            return null;

        var access = await DocumentArchiveAccessResolver.ResolveAsync(sources, contextAuthorization, userId, cancellationToken);
        return new Caller(scope, access, userId);
    }
}
