using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Alloggiati;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Casazen.Web.Controllers;

/// <summary>
/// Import of the official Alloggiati Web code tables (CO-12). The admin downloads each table from the "Area Download
/// Tabelle" of the Alloggiati portal and uploads it here; the import replaces the whole table (all or nothing).
/// File format and steps: <c>docs/runbooks/alloggiati.md</c>, "Tabelle codici".
/// </summary>
[ApiController]
[Route("api/admin/alloggiati/code-tables")]
[Authorize(Policy = CasazenPolicies.AdminOnly)]
public class AdminAlloggiatiCodeTablesController(
    IAlloggiatiCodeTableService codeTableService,
    IStringLocalizer<SharedResources> localizer,
    ILogger<AdminAlloggiatiCodeTablesController> logger) : ControllerBase
{
    /// <summary>Code of an import rejected because of the file content (422, with <c>lines</c>).</summary>
    public const string ImportInvalidCode = "alloggiati_code_import_invalid";

    /// <summary>Code of an unknown table name (404).</summary>
    public const string TableUnknownCode = "alloggiati_code_table_unknown";

    private const long MaxRequestBytes = AlloggiatiCodeTableService.MaxFileBytes + 64 * 1024;

    /// <summary>Rows and last import of every table.</summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<AlloggiatiCodeTableStatusDto>>> GetStatus()
    {
        var status = await codeTableService.GetStatusAsync(HttpContext.RequestAborted);
        return Ok(status.Select(AlloggiatiCodeTableStatusDto.From));
    }

    /// <summary>
    /// Imports an official table (<c>Comuni</c>, <c>Stati</c>, <c>Documenti</c>, <c>TipiAlloggiato</c>) from a
    /// multipart upload: <c>file</c> (CSV/TXT) and <c>sourceVersion</c> (e.g. the download date). A file with any
    /// invalid line is rejected as a whole: 422 <c>alloggiati_code_import_invalid</c> with <c>lines</c>.
    /// </summary>
    [HttpPost("{table}")]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
    public async Task<IActionResult> Import(string table, IFormFile? file, [FromForm] string? sourceVersion)
    {
        if (int.TryParse(table, out _)
            || !Enum.TryParse<AlloggiatiCodeTable>(table, ignoreCase: true, out var codeTable)
            || !Enum.IsDefined(codeTable))
        {
            return this.ApiProblem(StatusCodes.Status404NotFound, TableUnknownCode, "AlloggiatiCodeTableUnknown");
        }

        if (file is null || file.Length == 0)
            ModelState.AddModelError("file", localizer["CheckInFieldRequired"]);
        if (string.IsNullOrWhiteSpace(sourceVersion))
            ModelState.AddModelError(nameof(sourceVersion), localizer["CheckInFieldRequired"]);
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        await using var stream = file!.OpenReadStream();
        var result = await codeTableService.ImportAsync(
            codeTable,
            stream,
            file.FileName,
            sourceVersion!,
            User.GetUserId() ?? "admin",
            HttpContext.RequestAborted);

        if (!result.Success)
        {
            var problem = ApiProblemDetails.Create(
                HttpContext, StatusCodes.Status422UnprocessableEntity, ImportInvalidCode, "AlloggiatiCodeImportInvalid", []);
            problem.Extensions["lines"] = result.Errors.Select(e => new { line = e.Line, error = e.Error }).ToList();
            return new ObjectResult(problem)
            {
                StatusCode = StatusCodes.Status422UnprocessableEntity,
                ContentTypes = { ApiProblemDetails.ContentType },
            };
        }

        logger.LogInformation("Alloggiati table {Table} imported by an admin: {RowCount} codes", codeTable, result.RowCount);
        return Ok(new { table = codeTable.ToString(), rowCount = result.RowCount, importId = result.ImportId });
    }
}
