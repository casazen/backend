using System.ComponentModel.DataAnnotations;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The official directory of the Swiss localities and municipalities (swisstopo, E29/F01/US01/T01, NF-124) for the locality picker of the property
/// form: the search by postal code or name and one municipality by its BFS number. Reference data with no personal data and no org scope, read by
/// whoever may read properties (<see cref="CasazenPolicies.SharedPropertyRead"/>, as the property form): never public. Source and refresh of the
/// directory: <c>docs/compliance/swiss-municipalities.md</c>.
/// </summary>
[ApiController]
[Route("api/swiss")]
[Authorize(Policy = CasazenPolicies.SharedPropertyRead)]
public class SwissPlacesController(ISwissPlacesDirectory directory) : ControllerBase
{
    public const string MunicipalityUnknownCode = SwissPlaceErrorCodes.Unknown;

    /// <summary>
    /// The localities whose postal code starts with <paramref name="query"/> (one to four digits, <c>CH-</c> ignored) or whose name contains it
    /// (case, accents and extra spaces ignored), exact matches first: <c>6900</c> gives Lugano, Massagno and Paradiso, <c>locarn</c> gives Locarno.
    /// Each item is <c>{ postalCode, name, municipalityCode, municipalityName, cantonCode }</c>, one for each municipality a locality lies in; at
    /// most <paramref name="limit"/> items (default and most 20). A query that matches nothing, or cannot match (blank, one letter, five digits),
    /// gives an empty list. 400 <c>validation_error</c> when the query is longer than 100 characters; 422 <c>swiss_places_unavailable</c> when the
    /// directory is not loaded (the load at startup failed or was turned off): an empty list means «no such place» only for a directory that is there.
    /// </summary>
    [HttpGet("localities")]
    [ProducesResponseType(typeof(IReadOnlyList<SwissLocalityDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<IReadOnlyList<SwissLocalityDto>>> SearchLocalities(
        [FromQuery, StringLength(ISwissPlacesDirectory.MaxQueryLength, ErrorMessage = "SwissPlacesQueryTooLong")] string? query,
        [FromQuery] int limit = ISwissPlacesDirectory.MaxLimit,
        CancellationToken cancellationToken = default)
    {
        var matches = await directory.SearchLocalitiesAsync(query, limit, cancellationToken);
        return Ok(matches.Select(SwissLocalityDto.From).ToList());
    }

    [HttpGet("municipalities/{bfs}")]
    [ProducesResponseType(typeof(SwissMunicipalityDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SwissMunicipalityDto>> GetMunicipality(string bfs, CancellationToken cancellationToken)
    {
        var municipality = await directory.FindMunicipalityAsync(bfs, cancellationToken);
        return municipality is null
            ? this.ApiProblem(StatusCodes.Status404NotFound, MunicipalityUnknownCode, SwissPlaceErrorCodes.UnknownMessageKey)
            : Ok(SwissMunicipalityDto.From(municipality));
    }
}

public sealed class SwissLocalityDto
{
    public string PostalCode { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string MunicipalityCode { get; init; } = string.Empty;

    public string MunicipalityName { get; init; } = string.Empty;

    public string CantonCode { get; init; } = string.Empty;

    public static SwissLocalityDto From(SwissLocalityMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);
        return new SwissLocalityDto
        {
            PostalCode = match.PostalCode,
            Name = match.Name,
            MunicipalityCode = match.MunicipalityCode,
            MunicipalityName = match.MunicipalityName,
            CantonCode = match.CantonCode,
        };
    }
}

public sealed class SwissMunicipalityDto
{
    public string MunicipalityCode { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string CantonCode { get; init; } = string.Empty;

    public static SwissMunicipalityDto From(SwissMunicipalityInfo municipality)
    {
        ArgumentNullException.ThrowIfNull(municipality);
        return new SwissMunicipalityDto
        {
            MunicipalityCode = municipality.MunicipalityCode,
            Name = municipality.Name,
            CantonCode = municipality.CantonCode,
        };
    }
}
