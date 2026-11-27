using System.ComponentModel.DataAnnotations;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/admin/suppliers/identity-reviews")]
[Authorize(Policy = CasazenPolicies.AdminOnly)]
[PrivateAnswer]
public sealed class AdminSupplierIdentityReviewsController(ISupplierService suppliers) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SupplierIdentityReview>>> List([FromQuery, Range(1, 1000000)] int page = 1,
        CancellationToken ct = default) => Ok(await suppliers.ListIdentityReviewsAsync(page, ct));

    [HttpPost("{id:guid}/decision")]
    public async Task<IActionResult> Decide(Guid id, Decision request, CancellationToken ct)
    {
        var actor = User.GetUserId();
        if (actor == null) return Unauthorized();
        await suppliers.ResolveIdentityReviewAsync(id, request.Action, request.SupplierOrgId, actor, ct);
        return NoContent();
    }

    public sealed record Decision([Required, MaxLength(12)] string Action, Guid? SupplierOrgId);
}
