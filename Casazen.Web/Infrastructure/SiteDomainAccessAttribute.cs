using Casazen.Infrastructure.Data;
using Casazen.Web.Authorization;
using Casazen.Web.Middleware;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// The commercial gate of the domain operations of a connected plugin (S023, PLG-BE-SITEAPI, decision of the coordinator after the review of S022): <c>GET account</c>,
/// <c>PUT domain</c>, <c>POST domain/verify</c> and <c>PUT domain/surfaces</c> need the same module as the console's domain endpoints (<c>OrgDomainController</c>:
/// <c>direct-booking</c>) and the same commercial standing. The disconnection (<c>DELETE api/site/v1/connection</c>) never carries it: a lapsed module must not leave
/// a credential that cannot be revoked.
/// </summary>
/// <remarks>
/// <see cref="CommercialAccessMiddleware"/> cannot do it: it runs before authorization, so the credential of a plugin (scheme <c>SiteToken</c>, authenticated by the
/// policy of the endpoint) is no signed-in caller for it yet. The rule is the one of that middleware (<see cref="CommercialAccessRules"/>) and the answer is its own
/// (403 <c>commercial_module_required</c> with the module, 403 <c>onboarding_required</c>). A resource filter, so it answers before the body is read: the same
/// 403 whatever the body, as in the console.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class SiteDomainAccessAttribute() : TypeFilterAttribute(typeof(SiteDomainAccessFilter))
{
}

public sealed class SiteDomainAccessFilter(AppDbContext db) : IAsyncResourceFilter
{
    public static string? Module { get; } = CommercialAccessMiddleware.ModuleForController("OrgDomain");

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        // Behind the policy of the plugin the principal is always the credential's; the org is its org, never one of the request.
        if (context.HttpContext.User.GetSiteConnection() is { } site)
        {
            var org = await db.Orgs.AsNoTracking().FirstOrDefaultAsync(o => o.Id == site.OrgId, context.HttpContext.RequestAborted);
            switch (CommercialAccessRules.Evaluate(org, Module))
            {
                case CommercialAccessDecision.OnboardingRequired:
                    context.Result = new ObjectResult(
                        ApiProblemDetails.Create(
                            context.HttpContext,
                            StatusCodes.Status403Forbidden,
                            CommercialAccessRules.OnboardingRequiredCode,
                            CommercialAccessRules.OnboardingRequiredMessageKey))
                    {
                        StatusCode = StatusCodes.Status403Forbidden,
                        ContentTypes = { ApiProblemDetails.ContentType },
                    };
                    return;

                case CommercialAccessDecision.ModuleRequired:
                    context.Result = new ObjectResult(new { code = CommercialAccessRules.ModuleRequiredCode, module = Module })
                    {
                        StatusCode = StatusCodes.Status403Forbidden,
                    };
                    return;
            }
        }

        await next();
    }
}
