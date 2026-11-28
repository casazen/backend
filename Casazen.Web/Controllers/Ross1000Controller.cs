using Casazen.Core.Services;
using Casazen.Infrastructure.Ross1000;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[PrivateAnswer]
[Route("api/properties/{propertyId:guid}/ross1000/{day}")]
[Authorize(Policy = CasazenPolicies.PropertyWrite)]
[Authorize(Policy = CasazenPolicies.GuestRead)]
public sealed class Ross1000Controller(IHostResourceLookup hosts, IAuthorizationService auth, Ross1000DayService service) : ControllerBase
{
    public sealed record SendRequest(Ross1000Capacity Capacity, string Hash);

    [HttpPost("preview")]
    public Task<ActionResult<Ross1000Preview>> Preview(Guid propertyId, DateOnly day, Ross1000Capacity capacity, CancellationToken ct) =>
        Run(propertyId, day, capacity, null, ct);

    [HttpPost("send")]
    public Task<ActionResult<Ross1000Preview>> Send(Guid propertyId, DateOnly day, SendRequest request, CancellationToken ct) =>
        Run(propertyId, day, request.Capacity, request.Hash, ct);

    private async Task<ActionResult<Ross1000Preview>> Run(Guid propertyId, DateOnly day, Ross1000Capacity capacity, string? hash, CancellationToken ct)
    {
        var resource = await hosts.ForPropertyAsync(propertyId, ct);
        if (resource == null) return NotFound();
        if (!await auth.IsAuthorizedAsync(User, resource, PropertyOperations.Write)) return Forbid();
        try
        {
            return Ok(await service.RunAsync(resource.OrgId, propertyId, day, capacity, hash, ct));
        }
        catch (Ross1000Problem problem)
        {
            return Conflict(new { code = problem.Message });
        }
    }
}
