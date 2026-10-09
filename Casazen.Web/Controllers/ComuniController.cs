using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Localization;

namespace Casazen.Web.Controllers;

/// <summary>
/// Search of the official ISTAT comuni list for the comune pickers (SU-04): the property form, the supplier profile, the admin
/// invite. Public and rate limited: the list is open data (ISTAT) with no personal data and no org scope, and the signup of a
/// supplier is anonymous. Nothing is invented while the list is not imported: the answer says so
/// (<see cref="ComuneSearchResponse.DatasetAvailable"/>), it is not an empty list. Runbook: <c>docs/runbooks/comuni-istat.md</c>.
/// </summary>
[ApiController]
[Route("api/comuni")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.PublicComuni)]
public class ComuniController(
    IComuneDirectory directory,
    IStringLocalizer<SharedResources> localizer) : ControllerBase
{
    /// <summary>Matches returned when <c>limit</c> is not given.</summary>
    public const int DefaultLimit = 10;

    /// <summary>Most matches one request returns.</summary>
    public const int MaxLimit = 25;

    /// <summary>Shortest query (after trimming) that is searched: below it nothing is useful and the whole list would match.</summary>
    public const int MinQueryLength = 2;

    /// <summary>Longest query accepted.</summary>
    public const int MaxQueryLength = 100;

    /// <summary>Whether the official list is imported, and which one (source and reference date).</summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(ComuneAvailabilityDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<ComuneAvailabilityDto>> GetStatus(CancellationToken cancellationToken)
    {
        var status = await directory.GetStatusAsync(cancellationToken);
        return Ok(new ComuneAvailabilityDto
        {
            DatasetAvailable = status.Available,
            ReferenceDate = status.Available ? status.LastImport?.ReferenceDate : null,
            SourceVersion = status.Available ? status.LastImport?.SourceVersion : null,
        });
    }

    /// <summary>
    /// Active comuni whose name starts with, then contains, <c>q</c> (accents, case and punctuation ignored; also the name in
    /// the other language), shortest name first. <c>q</c> may also be an ISTAT code or a cadastral code. 400
    /// <c>validation_error</c> when <c>q</c> is shorter than <see cref="MinQueryLength"/> or longer than
    /// <see cref="MaxQueryLength"/>; <c>limit</c> is brought within 1 and <see cref="MaxLimit"/>. While the list is not
    /// imported the answer is 200 with <c>datasetAvailable: false</c> and no items.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(ComuneSearchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<ComuneSearchResponse>> Search(
        [FromQuery] string? q,
        [FromQuery] int limit = DefaultLimit,
        CancellationToken cancellationToken = default)
    {
        if (!await directory.IsAvailableAsync(cancellationToken))
            return Ok(new ComuneSearchResponse { DatasetAvailable = false });

        var query = q?.Trim() ?? string.Empty;
        if (query.Length < MinQueryLength || query.Length > MaxQueryLength)
        {
            ModelState.AddModelError(nameof(q), localizer["ComuneSearchQueryLength", MinQueryLength, MaxQueryLength]);
            return ValidationProblem(ModelState);
        }

        var comuni = await directory.SearchAsync(query, Math.Clamp(limit, 1, MaxLimit), cancellationToken);
        return Ok(new ComuneSearchResponse
        {
            DatasetAvailable = true,
            Items = comuni.Select(ComuneDto.From).ToList(),
        });
    }

    /// <summary>
    /// One comune by its ISTAT code, also one that is no longer in the list (<c>isActive: false</c>): it names the comune a
    /// stored code stands for. 404 when the code is not in the list (or is not six digits).
    /// </summary>
    [HttpGet("{istatCode}")]
    [ProducesResponseType(typeof(ComuneDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<ComuneDto>> GetByIstatCode(string istatCode, CancellationToken cancellationToken)
    {
        var comune = await directory.FindByIstatCodeAsync(istatCode, activeOnly: false, cancellationToken);
        return comune is null
            ? this.ApiProblem(StatusCodes.Status404NotFound, ComuneErrorCodes.IstatUnknown, ComuneErrorCodes.IstatUnknownMessageKey, istatCode)
            : Ok(ComuneDto.From(comune));
    }
}

/// <summary>Answer of <c>GET /api/comuni/status</c>.</summary>
public sealed class ComuneAvailabilityDto
{
    /// <summary>False until the official ISTAT list is imported: the pickers then say so.</summary>
    public bool DatasetAvailable { get; init; }

    /// <summary>Date the imported list is valid at, when there is one.</summary>
    public DateOnly? ReferenceDate { get; init; }

    /// <summary>Source and edition of the imported list.</summary>
    public string? SourceVersion { get; init; }
}
