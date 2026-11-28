using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

[ApiController]
[Authorize(Policy = CasazenPolicies.PropertyRead)]
[Route("api/properties/{propertyId:guid}/institutional-routes")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class DirectPortalRoutesController(
    DirectPortalRoutes routes, IHostResourceLookup resources, IAuthorizationService authorization,
    AppDbContext db, IDirectAlloggiatiAccount policeAccount, IAlloggiatiDelegationService delegation,
    IConfiguration configuration, IWebHostEnvironment environment) : ControllerBase
{
    public sealed record CutoverRequest(bool PreviousSendersDisabled, bool PeriodReconciled, string? DelegationVersion);
    public sealed record RoutesView(DirectPortalDates EffectiveFrom, bool AlloggiatiAvailable, bool IstatAvailable, bool IstatSupported, string DelegationVersion);

    [HttpGet]
    public async Task<ActionResult<RoutesView>> Get(Guid propertyId, CancellationToken ct)
    {
        var resource = await resources.ForPropertyAsync(propertyId, ct);
        if (resource is null) return NotFound();
        if (!await authorization.IsAuthorizedAsync(User, resource, PropertyOperations.Read)) return Forbid();
        return Ok(await ViewAsync(resource.OrgId, propertyId, ct));
    }

    [HttpPut("{service}")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    public async Task<ActionResult<RoutesView>> Activate(Guid propertyId, string service, CutoverRequest request, CancellationToken ct)
    {
        var resource = await resources.ForPropertyAsync(propertyId, ct);
        if (resource is null) return NotFound();
        if (!await authorization.IsAuthorizedAsync(User, resource, PropertyOperations.Write)) return Forbid();
        if (User.GetUserId() is not { Length: > 0 } userId) return Unauthorized();
        if (service is not ("alloggiati" or "istat")) return BadRequest();

        var view = await ViewAsync(resource.OrgId, propertyId, ct);
        var police = service == "alloggiati";
        if ((police ? view.EffectiveFrom.Alloggiati : view.EffectiveFrom.Istat).HasValue) return Ok(view);
        if (!request.PreviousSendersDisabled || !request.PeriodReconciled)
            return Conflict(new { code = "cutover_not_confirmed" });
        if (!(police ? view.AlloggiatiAvailable : view.IstatAvailable))
            return UnprocessableEntity(new { code = "direct_route_unavailable" });

        if (police && request.DelegationVersion != AlloggiatiDelegationTexts.CurrentVersion)
            return UnprocessableEntity(new { code = "alloggiati_delegation_required" });

        async Task CheckAndGrantAsync(CancellationToken token)
        {
            if (!police) return;
            if (!await policeAccount.VerifyAsync(propertyId, token))
                throw new Casazen.Core.Exceptions.DomainRuleException("direct_account_unverified", "SenderCasaZenNotAvailable");
            await delegation.GrantAsync(resource.OrgId, userId, AlloggiatiDelegationTexts.CurrentVersion, token);
        }
        var dates = await routes.ActivateAsync(resource.OrgId, propertyId, police, userId, CheckAndGrantAsync, ct);
        return Ok(view with { EffectiveFrom = dates });
    }

    private async Task<RoutesView> ViewAsync(Guid orgId, Guid propertyId, CancellationToken ct)
    {
        var property = await db.Properties.AsNoTracking().Where(p => p.Id == propertyId && p.OrgId == orgId)
            .Select(p => new { p.Country, p.ComuneIstatCode }).SingleAsync(ct);
        var italy = string.Equals(property.Country, "IT", StringComparison.OrdinalIgnoreCase);
        var police = italy && environment.IsProduction() && configuration.GetValue<bool>("Alloggiati:DirectEnabled")
            && await db.PropertyQuesturaCredentials.AnyAsync(r => r.OrgId == orgId && r.PropertyId == propertyId, ct);
        var istatSupported = italy && property.ComuneIstatCode is { Length: 6 } code && code.StartsWith("013", StringComparison.Ordinal)
            && environment.IsProduction() && configuration.GetValue<bool>("Ross1000:Enabled");
        var istat = istatSupported && await db.PropertyIstatCredentials.AnyAsync(r => r.OrgId == orgId && r.PropertyId == propertyId && r.StructureCode != null, ct);
        return new(await routes.ReadAsync(orgId, propertyId, ct), police, istat, istatSupported, AlloggiatiDelegationTexts.CurrentVersion);
    }
}
