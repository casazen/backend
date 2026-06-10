using System.Security.Claims;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// Request-scoped <see cref="ITenantContext"/> that resolves the caller's <c>OrgId</c> from the
/// authenticated principal (AC7). The <c>OrgId</c> is read from <c>User.OrgId</c> once per request
/// and cached; the client can never supply or widen it.
/// </summary>
/// <remarks>
/// Resolution uses a <b>separate</b> DI scope / <see cref="AppDbContext"/> instance because the
/// caller of <see cref="OrgId"/> is the EF global query filter evaluating on the request's own
/// <see cref="AppDbContext"/>; querying that same context here would be a reentrant operation.
/// The lookup hits <c>Users</c>, which carries no tenant filter, so it cannot recurse.
/// </remarks>
public sealed class TenantContext(
    IHttpContextAccessor httpContextAccessor,
    IServiceScopeFactory scopeFactory) : ITenantContext
{
    private bool _resolved;
    private Guid? _orgId;

    public bool FilterEnabled =>
        httpContextAccessor.HttpContext?.User.Identity?.IsAuthenticated == true;

    public Guid? OrgId
    {
        get
        {
            if (_resolved)
                return _orgId;

            _resolved = true;

            var sub = ResolveSub();
            if (string.IsNullOrWhiteSpace(sub))
            {
                _orgId = null;
                return _orgId;
            }

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            _orgId = db.Users.AsNoTracking()
                .Where(u => u.Id == sub)
                .Select(u => u.OrgId)
                .FirstOrDefault();

            return _orgId;
        }
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
