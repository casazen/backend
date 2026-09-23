using Casazen.Core.Services;
using Casazen.Web.DTOs.Compliance;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Compliance summary cockpit (US-019 / #295 AC10).
/// </summary>
[ApiController]
[Route("api/compliance")]
[Authorize(Policy = "PropertyOwner")]
public class ComplianceController(
    IComplianceWizardService complianceWizardService,
    IOrgContextResolver orgContextResolver) : ControllerBase
{
    [HttpGet("summary")]
    [Authorize(Policy = "RequireContext:short-rent:booking.read")]
    [ProducesResponseType(typeof(ComplianceSummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ComplianceSummaryDto>> GetSummary(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return Unauthorized();

        var summary = await complianceWizardService.GetSummaryAsync(orgId.Value, cancellationToken);
        return Ok(MapSummary(summary));
    }

    private static ComplianceSummaryDto MapSummary(ComplianceSummaryResult summary) => new()
    {
        PropertiesPending = MapSection(summary.PropertiesPending),
        GuestCheckInsIncomplete = MapSection(summary.GuestCheckInsIncomplete),
        CheckoutsDue = MapSection(summary.CheckoutsDue),
        AlloggiatiFailures = MapSection(summary.AlloggiatiFailures),
    };

    private static ComplianceSummarySectionDto MapSection(ComplianceSummarySection section) => new()
    {
        Count = section.Count,
        Items = section.Items.Select(i => new ComplianceSummaryItemDto
        {
            Id = i.Id,
            Label = i.Label,
            RouteLink = i.RouteLink,
        }),
    };
}
