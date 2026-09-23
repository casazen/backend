using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// Error responses for controllers in the API's single error shape (<see cref="ApiProblemDetails"/>).
/// </summary>
/// <example>
/// <code>
/// if (await slugTaken)
///     return this.ApiProblem(StatusCodes.Status409Conflict, "duplicate_property_slug", "PropertySlugTaken");
/// </code>
/// Add <c>PropertySlugTaken</c> to both <c>Resources/SharedResources.resx</c> (Italian) and
/// <c>SharedResources.en.resx</c>. For field errors use <c>ModelState.AddModelError(field, localizer[key])</c> and
/// <c>ValidationProblem(ModelState)</c>; services throw <c>DomainRuleException</c>, <c>DomainConflictException</c>
/// or <c>NotFoundException</c> instead, which the error middleware turns into the same shape.
/// </example>
public static class ApiProblemControllerExtensions
{
    /// <summary>
    /// ProblemDetails with status <paramref name="statusCode"/>, extension <c>code</c> = <paramref name="code"/>
    /// and <c>detail</c> = the localized <paramref name="messageKey"/> of <c>SharedResources</c>, formatted with
    /// <paramref name="messageArgs"/>.
    /// </summary>
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
