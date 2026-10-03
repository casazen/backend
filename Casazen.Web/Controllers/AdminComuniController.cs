using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Casazen.Web.Controllers;

/// <summary>
/// Import of the official ISTAT comuni list (SU-04, "Elenco dei comuni italiani"), the same pattern as the Alloggiati code
/// tables (CO-12): the admin uploads the file; nothing is written by hand and nothing ships with the code except the seed
/// file of the deploy. File format and steps: <c>docs/runbooks/comuni-istat.md</c>.
/// </summary>
[ApiController]
[Route("api/admin/comuni")]
[Authorize(Policy = CasazenPolicies.AdminOnly)]
public class AdminComuniController(
    IComuneDirectory directory,
    IComuneImportService importService,
    ISupplierPilotComuni pilotComuni,
    IStringLocalizer<SharedResources> localizer,
    ILogger<AdminComuniController> logger) : ControllerBase
{
    /// <summary>Code of an import rejected because of the file content (422, with <c>lines</c>).</summary>
    public const string ImportInvalidCode = "comuni_import_invalid";

    private const long MaxRequestBytes = ComuneImportService.MaxFileBytes + 64 * 1024;

    /// <summary>Rows of the list, last import (file, version, reference date, SHA-256) and the pilot comuni that are not in it.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ComuneDatasetStatusDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ComuneDatasetStatusDto>> GetStatus(CancellationToken cancellationToken)
    {
        var status = await directory.GetStatusAsync(cancellationToken);
        return Ok(new ComuneDatasetStatusDto
        {
            Available = status.Available,
            TotalRows = status.TotalRows,
            ActiveRows = status.ActiveRows,
            LastImport = status.LastImport is null ? null : ComuneImportDto.From(status.LastImport),
            InvalidPilotComuni = status.Available ? await pilotComuni.GetInvalidConfiguredCodesAsync(cancellationToken) : [],
        });
    }

    /// <summary>
    /// Imports a file of the official list from a multipart upload: <c>file</c> (CSV), <c>sourceVersion</c> (source and
    /// edition, e.g. "ISTAT, Elenco dei comuni italiani, aggiornato al 21/02/2026"), <c>referenceDate</c>
    /// (<c>yyyy-MM-dd</c>, the "aggiornato al" date) and optionally <c>partial=true</c> (the file is a part of the list: the
    /// comuni it does not have are not deactivated). A file with any invalid line is rejected as a whole: 422
    /// <c>comuni_import_invalid</c> with <c>lines</c>. The same file again changes nothing.
    /// </summary>
    [HttpPost("import")]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
    [ProducesResponseType(typeof(ComuneImportResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Import(
        IFormFile? file,
        [FromForm] string? sourceVersion,
        [FromForm] string? referenceDate,
        [FromForm] bool partial = false)
    {
        if (file is null || file.Length == 0)
            ModelState.AddModelError("file", localizer["ComuniImportFieldRequired"]);
        if (string.IsNullOrWhiteSpace(sourceVersion))
            ModelState.AddModelError(nameof(sourceVersion), localizer["ComuniImportFieldRequired"]);
        if (!DateOnly.TryParseExact(referenceDate?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var reference))
            ModelState.AddModelError(nameof(referenceDate), localizer["ComuniImportReferenceDateInvalid"]);
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        await using var stream = file!.OpenReadStream();
        var result = await importService.ImportAsync(
            new ComuneImportRequest(file.FileName, sourceVersion!, reference, User.GetUserId() ?? "admin", ComuneImportOrigin.AdminUpload, partial),
            stream,
            HttpContext.RequestAborted);

        if (!result.Success)
        {
            var problem = ApiProblemDetails.Create(
                HttpContext, StatusCodes.Status422UnprocessableEntity, ImportInvalidCode, "ComuniImportInvalid", []);
            problem.Extensions["lines"] = result.Errors.Select(e => new { line = e.Line, error = e.Error }).ToList();
            return new ObjectResult(problem)
            {
                StatusCode = StatusCodes.Status422UnprocessableEntity,
                ContentTypes = { ApiProblemDetails.ContentType },
            };
        }

        logger.LogInformation(
            "Comuni list imported by an admin: {Rows} rows ({Inserted} new, {Updated} changed, {Unchanged} unchanged, {Deactivated} deactivated)",
            result.Rows, result.Inserted, result.Updated, result.Unchanged, result.Deactivated);
        return Ok(new ComuneImportResultDto
        {
            ImportId = result.ImportId!.Value,
            Rows = result.Rows,
            Inserted = result.Inserted,
            Updated = result.Updated,
            Unchanged = result.Unchanged,
            Deactivated = result.Deactivated,
        });
    }
}

/// <summary>Outcome of an accepted import.</summary>
public sealed class ComuneImportResultDto
{
    public Guid ImportId { get; init; }
    public int Rows { get; init; }
    public int Inserted { get; init; }
    public int Updated { get; init; }
    public int Unchanged { get; init; }
    public int Deactivated { get; init; }
}
