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
/// The owner portfolio of an agency in the onboarding (E10/F02/US02/T01, NF-109, ONB-06): the import of the owners and of their shares of the
/// properties from a CSV or an Excel file, with the check of every row, and the readiness of every owner and property for the owner accounting and
/// for the portal. Stateless, in two calls for the import: the same endpoint validates and answers each row, and without <c>dryRun</c> writes only
/// the valid ones. <b>It never sends an invitation</b>: that is a separate, explicit act with its own preview and confirmation
/// (<see cref="OwnerInvitationBatchController"/>).
/// </summary>
/// <remarks>
/// <para><b>Policies.</b> The import is <see cref="CasazenPolicies.AgencyOwnerManage"/> (the owner and the administrators of an agency): it writes
/// fiscal and bank data of the owners. The readiness is <see cref="CasazenPolicies.AgencyOwnerView"/>: it answers codes and field names, never a
/// value. Both look at the caller's reach (<c>HostScope</c>, AM-03): a property of another org, one outside the reach and one that does not
/// exist are the same answer.</para>
/// <para><b>The file</b> is read from memory and never stored: <c>.csv</c> or <c>.xlsx</c> in the multipart field <c>file</c>, at most 2 MB and 1,000
/// rows, the same limits and the same safe reading as the import of the per-property data. Every answer is private and never cached.</para>
/// <para>Behind <c>Features:OctorateOnboarding</c>: 404 while it is off, before authentication.</para>
/// </remarks>
[ApiController]
[Route("api/onboarding/owners")]
[FeatureGate(FeatureFlags.OctorateOnboarding)]
[PrivateAnswer]
public sealed class OwnerOnboardingController(
    IOwnerOnboardingService owners,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    /// <summary>The largest request: the file and a margin for the other parts of the multipart body.</summary>
    private const int MaxRequestBytes = BulkImportLimits.MaxFileBytes + (256 * 1024);

    /// <summary>
    /// Checks the file (<c>dryRun=true</c>, the default: nothing is written) or imports it (<c>dryRun=false</c>: validates again and writes ONLY the
    /// valid rows, in one unit of work). The answer is the same: one entry for each row with its status, its errors by column, what it does with the
    /// owner and with the ownership, plus a summary, the fingerprint of the file, the columns the import does not know and the properties whose
    /// shares would be wrong. Sending the same file twice changes nothing the second time.
    /// </summary>
    /// <response code="200">One entry per row.</response>
    /// <response code="400"><c>import_file_missing</c>: no file in the field <c>file</c>.</response>
    /// <response code="413"><c>import_file_too_large</c>: more than 2 MB, or an Excel file that unzips to more than 20 MB.</response>
    /// <response code="415"><c>import_file_type_unsupported</c>: not a <c>.csv</c> or an <c>.xlsx</c>.</response>
    /// <response code="422"><c>import_too_many_rows</c> (more than 1,000), <c>import_file_unreadable</c>, <c>import_file_empty</c>, <c>import_columns_unrecognized</c> (no column that identifies the owner).</response>
    [HttpPost("import")]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerManage)]
    [EnableRateLimiting(RateLimitPolicies.OnboardingBulkImport)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MemoryBufferThreshold = MaxRequestBytes, MultipartBodyLengthLimit = MaxRequestBytes)]
    [ProducesResponseType(typeof(OwnerImportResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OwnerImportResultDto>> Import(
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

        var result = await owners.ImportAsync(scope, userId, file.FileName, content, dryRun, cancellationToken);
        return Ok(OwnerImportResultDto.From(result));
    }

    /// <summary>
    /// For every owner and every property inside the caller's reach: what is missing for the registry, the ownership (the shares of each property add
    /// up to exactly 100% on every day), the mandate, the fiscal profile, the payment data and the portal, each fact by its code and the field it is
    /// about. <c>summary.financialReady</c> is false until the owner accounting can start; it never blocks, and is never blocked by, the OTA readiness.
    /// </summary>
    [HttpGet("readiness")]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerView)]
    [ProducesResponseType(typeof(OwnerReadinessDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<OwnerReadinessDto>> GetReadiness(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        var scope = orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
        if (scope is null)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        return Ok(OwnerReadinessDto.From(await owners.GetReadinessAsync(scope, cancellationToken)));
    }
}
