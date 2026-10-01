using System.Security.Claims;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// Writer side of the request's <see cref="ITenantContext"/> (A1-20): resolved asynchronously once per
/// request by <see cref="TenantResolutionMiddleware"/>, then updated by the org resolver when it provisions
/// or finds the caller's org, so the next queries of the same request are scoped to that org.
/// </summary>
public interface IRequestTenantContext : ITenantContext
{
    /// <summary>
    /// Reads the caller's <c>OrgId</c> and active flag from <c>Users</c> once per request, in one query (idempotent).
    /// Anonymous requests resolve nothing. Called by <see cref="TenantResolutionMiddleware"/> before authorization and
    /// controllers.
    /// </summary>
    Task ResolveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// True when the caller's <c>Users</c> row exists and was deactivated (<c>IsActive = false</c>, PL-03): the request is
    /// refused by <see cref="Casazen.Web.Middleware.InactiveAccountMiddleware"/>. False for anonymous requests and for a first access (no row).
    /// </summary>
    bool IsCallerInactive { get; }

    /// <summary>
    /// Sets the caller's org after it has been provisioned or linked in this request. The org of a request can
    /// only go from none to one: a different org than the one already resolved is refused.
    /// </summary>
    void SetOrgId(Guid orgId);
}

/// <summary>
/// Request-scoped <see cref="ITenantContext"/> that resolves the caller's <c>OrgId</c> from the
/// authenticated principal (AC7). The <c>OrgId</c> is read from <c>User.OrgId</c> once per request;
/// the client can never supply or widen it.
/// </summary>
/// <remarks>
/// <para>
/// The EF global query filter reads <see cref="OrgId"/> synchronously, so the value is loaded beforehand,
/// asynchronously, by <see cref="TenantResolutionMiddleware"/> (A1-20: no blocking query inside the
/// pipeline). Read before that, it is <c>null</c>: fail-closed, the filter matches nothing.
/// </para>
/// <para>
/// Resolution uses a <b>separate</b> DI scope / <see cref="AppDbContext"/> instance because the request's
/// own <see cref="AppDbContext"/> depends on this service. The lookup hits <c>Users</c>, which carries no
/// tenant filter, so it cannot recurse.
/// </para>
/// </remarks>
public sealed class TenantContext(
    IHttpContextAccessor httpContextAccessor,
    IServiceScopeFactory scopeFactory,
    ILogger<TenantContext> logger) : IRequestTenantContext
{
    private bool _resolved;
    private bool _unresolvedReadLogged;
    private bool _callerInactive;
    private Guid? _orgId;

    public bool FilterEnabled =>
        httpContextAccessor.HttpContext?.User.Identity?.IsAuthenticated == true;

    public Guid? OrgId
    {
        get
        {
            if (!_resolved && FilterEnabled && !_unresolvedReadLogged)
            {
                _unresolvedReadLogged = true;
                logger.LogWarning(
                    "Tenant OrgId read before {Middleware} resolved it: tenant queries match nothing",
                    nameof(TenantResolutionMiddleware));
            }

            return _orgId;
        }
    }

    public bool IsCallerInactive => _callerInactive;

    public async Task ResolveAsync(CancellationToken cancellationToken = default)
    {
        if (_resolved || !FilterEnabled)
            return;

        var sub = ResolveSub();
        Guid? orgId = null;
        if (!string.IsNullOrWhiteSpace(sub))
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // One read per request for both the tenant and the active flag (PL-03 reuses it, A1-04).
            // A1-40: User.OrgId can point at the caller's own Supplier org (linked at supplier registration,
            // before ever completing the host onboarding). Only a Host-type org scopes the tenant filter — a
            // supplier account must never read or write host data through it; the join resolves to no org,
            // exactly like a brand-new user (fail-closed), same as OrgContextResolver/OrgService.
            var row = await db.Users.AsNoTracking()
                .Where(u => u.Id == sub)
                .Select(u => new
                {
                    OrgId = u.OrgId != null && u.Org!.OrgType == OrgType.Host ? u.OrgId : null,
                    u.IsActive,
                })
                .FirstOrDefaultAsync(cancellationToken);
            orgId = row?.OrgId;
            _callerInactive = row is { IsActive: false };
        }

        // SetOrgId may have run while the query was in flight; never overwrite it with an older value.
        if (_resolved)
            return;

        _orgId = orgId;
        _resolved = true;
    }

    public void SetOrgId(Guid orgId)
    {
        if (_orgId is Guid current && current != orgId)
        {
            throw new InvalidOperationException(
                $"The tenant of this request is already org {current}; it cannot change to org {orgId}.");
        }

        _orgId = orgId;
        _resolved = true;
    }

    private string? ResolveSub()
    {
        var user = httpContextAccessor.HttpContext?.User;
        if (user is null)
            return null;

        return user.FindFirstValue("sub")
            ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.FindFirstValue("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier");
    }
}

/// <summary>
/// Resolves the request's tenant (<see cref="IRequestTenantContext.ResolveAsync"/>) right after authentication
/// and before authorization, so policies, controllers and the EF tenant filter all read the same, already
/// loaded, <c>OrgId</c> (A1-20).
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IRequestTenantContext tenantContext)
    {
        await tenantContext.ResolveAsync(context.RequestAborted);
        await next(context);
    }
}

public static class TenantResolutionMiddlewareExtensions
{
    /// <summary>Adds <see cref="TenantResolutionMiddleware"/>: after <c>UseAuthentication</c>, before <c>UseAuthorization</c>.</summary>
    public static IApplicationBuilder UseTenantResolution(this IApplicationBuilder app) =>
        app.UseMiddleware<TenantResolutionMiddleware>();
}
