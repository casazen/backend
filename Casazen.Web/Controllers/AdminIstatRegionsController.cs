using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Compliance;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The ISTAT region table (E21/F01/US01/T01, decision O6): for each region and autonomous province the system of its portal, the deadline, the
/// statistical fields it requires and whether the channel manager covers it. Platform data, the same for every org, written <b>only by a platform
/// administrator</b> (<see cref="CasazenPolicies.AdminOnly"/>): no org member, whatever its role, reads or changes it here. Every save is stamped with the
/// time and the administrator and leaves a line in the audit trail of the regulatory reference data. A row never exists without its https source, and a
/// value nobody verified stays «da verificare»: this endpoint is the only way a region becomes covered.
/// </summary>
[ApiController]
[Route("api/admin/istat-regions")]
[Authorize(Policy = CasazenPolicies.AdminOnly)]
public sealed class AdminIstatRegionsController(IIstatRegionService regions) : ControllerBase
{
    /// <summary>The whole table: the regions in the order of their ISTAT code, the autonomous provinces in place of Trentino-Alto Adige.</summary>
    /// <response code="200">The rows.</response>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<IstatRegionRuleDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<IstatRegionRuleDto>>> List(CancellationToken cancellationToken)
    {
        var rows = await regions.ListAsync(cancellationToken);
        return Ok(rows.Select(IstatRegionRuleDto.From).ToList());
    }

    /// <summary>
    /// Saves the row of a region or autonomous province, by its territory: the system, the portal, the deadline, the required statistical fields, the
    /// coverage, the source and the notes replace what the row held, the row is stamped as verified (at <c>verifiedAt</c>, now when left out) and one line
    /// of the audit trail says who changed what. It never creates a row.
    /// </summary>
    /// <response code="200">The row after the change.</response>
    /// <response code="404"><c>istat_region_not_found</c>: no such territory in the table.</response>
    /// <response code="422"><c>istat_region_source_invalid</c>, <c>istat_region_portal_invalid</c>, <c>istat_region_deadline_invalid</c>, <c>istat_region_fields_invalid</c>.</response>
    [HttpPut]
    [ProducesResponseType(typeof(IstatRegionRuleDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<IstatRegionRuleDto>> Update([FromBody] UpdateIstatRegionRequest request, CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not { Length: > 0 } userId)
            return Unauthorized();

        var rule = await regions.UpdateAsync(request.ToUpdate(), userId, cancellationToken);
        return Ok(IstatRegionRuleDto.From(rule));
    }
}
