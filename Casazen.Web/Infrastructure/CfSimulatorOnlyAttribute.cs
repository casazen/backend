using Casazen.Core.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// Puts a controller behind the simulator of ConcordatoFacile (CF-SIM): unless <c>ConcordatoFacile:FormMode</c> is <c>Simulated</c> the
/// endpoint answers 404 like a route that does not exist (the empty 404 gets the standard problem body from
/// <see cref="Middleware.ErrorHandlingMiddleware"/>), so with the real service, which is the default and the only mode of production,
/// nothing of the simulator can be reached. It also answers 404 when the environment is Production, whatever the configuration says
/// (the startup refuses that configuration first, <see cref="ConcordatoFacileOptionsValidator"/>). It reads the options at every request,
/// not when the application is built.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class CfSimulatorOnlyAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var services = context.HttpContext.RequestServices;
        var simulated = services.GetRequiredService<IOptions<ConcordatoFacileOptions>>().Value.FormMode == ConcordatoFacileFormMode.Simulated;
        var production = services.GetRequiredService<IHostEnvironment>().IsProduction();
        if (!simulated || production)
            context.Result = new NotFoundResult();
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}
