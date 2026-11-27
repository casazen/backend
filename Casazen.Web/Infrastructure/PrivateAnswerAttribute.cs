using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Casazen.Web.Infrastructure;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class PrivateAnswerAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context) => Apply(context.HttpContext.Response);

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }

    public static void Apply(HttpResponse response)
    {
        response.Headers["X-Robots-Tag"] = "noindex";
        response.Headers.CacheControl = "no-store";
    }
}
