using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/marketing/import")]
[FeatureGate(FeatureFlags.CrmMarketing)]
[Authorize(Policy = CasazenPolicies.Marketing)]
[Consumes("multipart/form-data")]
[RequestSizeLimit(BulkImportLimits.MaxFileBytes + 65536)]
[RequestFormLimits(MemoryBufferThreshold = BulkImportLimits.MaxFileBytes + 65536, MultipartBodyLengthLimit = BulkImportLimits.MaxFileBytes + 65536)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class MarketingImportController(MarketingGuestImport importer, IOrgContextResolver organizations, IHostScopeResolver scopes) : ControllerBase
{
    [HttpPost("preview")]
    public async Task<ActionResult<MarketingImportPreview>> Preview(IFormFile file, CancellationToken ct)
    {
        if (await ScopeAsync(ct) is null) return Forbid();
        if (file.Length > BulkImportLimits.MaxFileBytes) return StatusCode(StatusCodes.Status413PayloadTooLarge);
        return Ok(MarketingGuestImport.Preview(file.FileName, await ReadAsync(file, ct)));
    }

    [HttpPost]
    public async Task<ActionResult<MarketingImportResult>> Import(IFormFile file, [FromForm] Guid propertyId,
        [FromForm] string mapping, [FromForm] string basis, [FromForm] string source, CancellationToken ct)
    {
        if (await ScopeAsync(ct) is not { } scope) return Forbid();
        if (file.Length > BulkImportLimits.MaxFileBytes) return StatusCode(StatusCodes.Status413PayloadTooLarge);
        Dictionary<string, int>? columns;
        try
        {
            columns = JsonSerializer.Deserialize<Dictionary<string, int>>(mapping);
        }
        catch (JsonException)
        {
            return BadRequest(new { code = "invalid_mapping" });
        }
        if (columns is null) return BadRequest(new { code = "invalid_mapping" });
        return Ok(await importer.ImportAsync(scope, propertyId, file.FileName, await ReadAsync(file, ct), columns, basis, source, ct));
    }

    private async Task<HostScope?> ScopeAsync(CancellationToken ct) =>
        await organizations.GetOrProvisionOrgIdAsync(ct) is { } orgId ? await scopes.ResolveHostScopeAsync(User, orgId, ct) : null;

    private static async Task<byte[]> ReadAsync(IFormFile file, CancellationToken ct)
    {
        var bytes = new byte[file.Length];
        await using var stream = file.OpenReadStream();
        await stream.ReadExactlyAsync(bytes, ct);
        return bytes;
    }
}
