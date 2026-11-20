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
    /// <summary>Code of a BFS number the directory does not have (404).</summary>
    public const string MunicipalityUnknownCode = "swiss_municipality_unknown";

    /// <summary>
    /// The localities whose postal code starts with <paramref name="query"/> (one to four digits, <c>CH-</c> ignored) or whose name contains it
    /// (case, accents and extra spaces ignored), exact matches first: <c>6900</c> gives Lugano, Massagno and Paradiso, <c>locarn</c> gives Locarno.
    /// Each item is <c>{ postalCode, name, municipalityCode, municipalityName, cantonCode }</c>, one for each municipality a locality lies in; at
    /// most <paramref name="limit"/> items (default and most 20). A query that matches nothing, or cannot match (blank, one letter, five digits),
    /// gives an empty list. 400 <c>validation_error</c> when the query is longer than 100 characters.
    /// </summary>
    [HttpGet("localities")]
    [ProducesResponseType(typeof(IReadOnlyList<SwissLocalityDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<SwissLocalityDto>>> SearchLocalities(
        [FromQuery, StringLength(ISwissPlacesDirectory.MaxQueryLength, ErrorMessage = "SwissPlacesQueryTooLong")] string? query,
        [FromQuery] int limit = ISwissPlacesDirectory.MaxLimit,
        CancellationToken cancellationToken = default)
    {
        var matches = await directory.SearchLocalitiesAsync(query, limit, cancellationToken);
        return Ok(matches.Select(SwissLocalityDto.From).ToList());
    }

    /// <summary>
    /// One municipality by its BFS number (<c>5192</c> is Lugano): <c>{ municipalityCode, name, cantonCode }</c>. 404
    /// <c>swiss_municipality_unknown</c> when the directory has no such number (or it is not a number).
    /// </summary>
    [HttpGet("municipalities/{bfs}")]
    [ProducesResponseType(typeof(SwissMunicipalityDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SwissMunicipalityDto>> GetMunicipality(string bfs, CancellationToken cancellationToken)
    {
        var municipality = await directory.FindMunicipalityAsync(bfs, cancellationToken);
        return municipality is null
            ? this.ApiProblem(StatusCodes.Status404NotFound, MunicipalityUnknownCode, "SwissMunicipalityUnknown")
            : Ok(SwissMunicipalityDto.From(municipality));
    }
}

/// <summary>One locality of <c>GET /api/swiss/localities</c>: its postal code and name, and the municipality (BFS number, name, canton) it lies in.</summary>
public sealed class SwissLocalityDto
{
    /// <summary>The four digits of the postal code.</summary>
    public string PostalCode { get; init; } = string.Empty;

    /// <summary>The name of the locality as the official directory writes it.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The BFS number of the municipality, as a text of digits (<c>"5192"</c>): the value of <c>swissMunicipalityCode</c> on a property.</summary>
    public string MunicipalityCode { get; init; } = string.Empty;

    /// <summary>The official name of the municipality.</summary>
    public string MunicipalityName { get; init; } = string.Empty;

    /// <summary>The two letters of the canton (<c>TI</c>).</summary>
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

/// <summary>One municipality of <c>GET /api/swiss/municipalities/{bfs}</c>.</summary>
public sealed class SwissMunicipalityDto
{
    /// <summary>The BFS number, as a text of digits (<c>"5192"</c>).</summary>
    public string MunicipalityCode { get; init; } = string.Empty;

    /// <summary>The official name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The two letters of the canton (<c>TI</c>).</summary>
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
