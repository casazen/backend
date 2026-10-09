using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// Marks every answer of the controller (or action) it is on as private: <c>X-Robots-Tag: noindex</c> and
/// <c>Cache-Control: no-store</c>. A resource filter, so it runs before model binding and the answers MVC gives on its own (the 400
/// of a body that is refused, the 415 of one that is not JSON) carry the headers like the ones the action gives; the 429 of the rate
/// limiter, which answers before MVC, reads the same attribute from the endpoint (<see cref="Apply"/>), so every answer of these
/// endpoints is the same on this point. The answers of a customer's own area belong to one customer.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class PrivateAnswerAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context) => Apply(context.HttpContext.Response);

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }

    /// <summary>Puts the two headers on <paramref name="response"/>.</summary>
    public static void Apply(HttpResponse response)
    {
        response.Headers["X-Robots-Tag"] = "noindex";
        response.Headers.CacheControl = "no-store";
    }
}
