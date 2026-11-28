using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Infrastructure;

public static class ApiProblemControllerExtensions
{
    public static ObjectResult ApiProblem(
        this ControllerBase controller,
        int statusCode,
        string code,
        string messageKey,
        params object[] messageArgs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageKey);

        var problem = ApiProblemDetails.Create(controller.HttpContext, statusCode, code, messageKey, messageArgs);
        return new ObjectResult(problem)
        {
            StatusCode = statusCode,
            ContentTypes = { ApiProblemDetails.ContentType },
        };
    }
}
