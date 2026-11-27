using Casazen.Core.Authorization;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Onboarding;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// The import of the data that changes from one property to the next (comune and region, the Alloggiati Web room numbers, the lock code) from a CSV
/// or an Excel file, with the check of every row (NF-105, ONB-02, E10/F02/US01/T02). Stateless, in two calls: the same endpoint validates and
/// answers each row, and without <c>dryRun</c> writes only the valid ones.
/// </summary>
/// <remarks>
/// <para><b>Policy.</b> <see cref="CasazenPolicies.OrgBillingAdmin"/>: an org-level decision. The properties a row names are looked up in the
/// caller's org and among what it reaches (<c>HostScope</c>, AM-03); one that is not is <c>property_not_found</c>, whoever's it is.</para>
/// <para><b>The file</b> is read from memory and never stored: <c>.csv</c> or <c>.xlsx</c> in the multipart field <c>file</c>, at most 2 MB (the
/// multipart body is buffered in memory up to its size, so it never spills to a temporary file on disk) and 1,000 rows. <b>The lock code</b> of a row
/// is stored encrypted and is never answered or logged. Every answer is private and never cached.</para>
/// <para>Behind <c>Features:OctorateOnboarding</c>: 404 while it is off, before authentication.</para>
/// </remarks>
[ApiController]
[Route("api/onboarding/bulk/import")]
[FeatureGate(FeatureFlags.OctorateOnboarding)]
[Authorize(Policy = CasazenPolicies.OrgBillingAdmin)]
[EnableRateLimiting(RateLimitPolicies.OnboardingBulkImport)]
[PrivateAnswer]
public sealed class OnboardingBulkImportController(
    IOnboardingBulkImportService import,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    /// <summary>The largest request: the file and a margin for the other parts of the multipart body.</summary>
    private const int MaxRequestBytes = BulkImportLimits.MaxFileBytes + (256 * 1024);

    /// <summary>
    /// Checks the file (<c>dryRun=true</c>, the default: nothing is written) or imports it (<c>dryRun=false</c>: validates again and writes ONLY the
    /// valid rows, each in its own unit of work; an invalid row never blocks a valid one). The answer is the same: one entry for each row with its
    /// status, its errors by column and <c>lockCodeProvided</c>, plus a summary and the columns the import does not know.
    /// </summary>
    /// <response code="200">One entry per row.</response>
    /// <response code="400"><c>import_file_missing</c>: no file in the field <c>file</c>.</response>
    /// <response code="413"><c>import_file_too_large</c>: more than 2 MB, or an Excel file that unzips to more than 20 MB.</response>
    /// <response code="415"><c>import_file_type_unsupported</c>: not a <c>.csv</c> or an <c>.xlsx</c> (a <c>.xls</c>, an <c>.xlsm</c>, another type).</response>
    /// <response code="422"><c>import_too_many_rows</c> (more than 1,000), <c>import_file_unreadable</c>, <c>import_file_empty</c>, <c>import_columns_unrecognized</c>, <c>comuni_dataset_unavailable</c>.</response>
    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MemoryBufferThreshold = MaxRequestBytes, MultipartBodyLengthLimit = MaxRequestBytes)]
    [ProducesResponseType(typeof(BulkImportResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<BulkImportResultDto>> Import(
        IFormFile? file,
        [FromQuery] bool dryRun = true,
        CancellationToken cancellationToken = default)
    {
        var userId = User.GetUserId();
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        var scope = orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
        if (scope is null || string.IsNullOrWhiteSpace(userId))
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        if (file is null || file.Length == 0)
            return this.ApiProblem(StatusCodes.Status400BadRequest, BulkImportCodes.FileMissing, "OnboardingImportFileMissing");

        // Judged on the size the request declares, before a byte of it is copied.
        if (file.Length > BulkImportLimits.MaxFileBytes)
        {
            return this.ApiProblem(
                StatusCodes.Status413PayloadTooLarge,
                BulkImportCodes.FileTooLarge,
                "OnboardingImportFileTooLarge",
                BulkImportLimits.MaxFileBytes / (1024 * 1024));
        }

        var content = new byte[file.Length];
        await using (var stream = file.OpenReadStream())
            await stream.ReadExactlyAsync(content, cancellationToken);

        var result = await import.ImportAsync(scope, userId, file.FileName, content, dryRun, cancellationToken);
        return Ok(BulkImportResultDto.From(result));
    }
}
