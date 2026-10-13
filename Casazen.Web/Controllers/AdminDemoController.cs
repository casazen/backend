using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Demo environment for a non-production installation: invented but format-valid CIN, documents and checklist, and a
/// reset of the showcase template. Production answers 404 (<c>demo_unavailable</c>).
/// </summary>
[ApiController]
[Route("api/admin/demo")]
[Authorize(Policy = "AdminOnly")]
public class AdminDemoController(IDemoEnvironmentService demo) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<DemoEnvironmentStatus>> GetStatus(CancellationToken cancellationToken) =>
        Ok(await demo.GetStatusAsync(cancellationToken));

    [HttpPut("enabled")]
    public async Task<ActionResult<DemoEnvironmentStatus>> SetEnabled(
        [FromBody] SetDemoEnabledRequest request, CancellationToken cancellationToken) =>
        Ok(await demo.SetEnabledAsync(request.Enabled, ActorUserId(), cancellationToken));

    [HttpGet("orgs")]
    public async Task<ActionResult<IReadOnlyList<DemoOrgListItem>>> Orgs(CancellationToken cancellationToken) =>
        Ok(await demo.ListOrgsAsync(cancellationToken));

    [HttpPost("orgs/{orgId:guid}/compliance")]
    public async Task<ActionResult<DemoComplianceResult>> ApplyCompliance(Guid orgId, CancellationToken cancellationToken) =>
        Ok(await demo.ApplyTestComplianceAsync(orgId, cancellationToken));

    [HttpPost("orgs/{orgId:guid}/reset")]
    public async Task<ActionResult<DemoResetResult>> Reset(
        Guid orgId, [FromBody] ResetDemoRequest request, CancellationToken cancellationToken) =>
        Ok(await demo.ResetAsync(orgId, request.Confirmation, request.RemoveUnpublished, ActorUserId(), cancellationToken));

    private string ActorUserId() =>
        User.GetUserId() ?? throw new UnauthorizedAccessException("Admin without subject claim");
}

public sealed class SetDemoEnabledRequest
{
    public bool Enabled { get; set; }
}

public sealed class ResetDemoRequest
{
    public string? Confirmation { get; set; }

    public bool RemoveUnpublished { get; set; }
}
