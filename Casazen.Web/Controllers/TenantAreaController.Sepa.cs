using Casazen.Core.Services;
using Microsoft.AspNetCore.Mvc;
namespace Casazen.Web.Controllers;

public sealed partial class TenantAreaController
{
    [HttpGet("contracts/{leaseId:guid}/sepa")]
    public async Task<IActionResult> GetSepa(Guid leaseId, [FromServices] ITenantRentSepaService sepa, CancellationToken ct)
    {
        var access = await RequireAccessAsync(leaseId, ct);
        if (access.Failure is not null) return access.Failure;
        return Ok(await sepa.GetAsync(access.Lease!.OrgId, leaseId, access.UserId!, ct));
    }
    [HttpPost("contracts/{leaseId:guid}/sepa/setup")]
    public async Task<IActionResult> SetupSepa(Guid leaseId, [FromServices] ITenantRentSepaService sepa, CancellationToken ct)
    {
        var access = await RequireAccessAsync(leaseId, ct);
        if (access.Failure is not null) return access.Failure;
        return Ok(await sepa.SetupAsync(access.Lease!.OrgId, leaseId, access.UserId!, access.Email!, ct));
    }
    [HttpPut("contracts/{leaseId:guid}/sepa/automatic")]
    public async Task<IActionResult> AutomaticSepa(Guid leaseId, [FromBody] SepaAutomaticRequest request,
        [FromServices] ITenantRentSepaService sepa, CancellationToken ct)
    {
        var access = await RequireAccessAsync(leaseId, ct);
        if (access.Failure is not null) return access.Failure;
        return Ok(await sepa.SetAutomaticAsync(access.Lease!.OrgId, leaseId, access.UserId!, request.Enabled, ct));
    }
    [HttpPost("contracts/{leaseId:guid}/dues/{dueId:guid}/sepa/payment-session")]
    public async Task<IActionResult> PaySepa(Guid leaseId, Guid dueId, [FromServices] ITenantRentSepaService sepa, CancellationToken ct)
    {
        var access = await RequireAccessAsync(leaseId, ct);
        if (access.Failure is not null) return access.Failure;
        return Ok(await sepa.PayAsync(access.Lease!.OrgId, leaseId, dueId, ct, access.UserId));
    }
}
public sealed record SepaAutomaticRequest(bool Enabled);
