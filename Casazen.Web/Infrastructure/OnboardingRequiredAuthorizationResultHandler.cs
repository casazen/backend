using System.Text.Json;
using Casazen.Core.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// Turns a policy refused because the host onboarding is not complete (a failure reason
/// <see cref="HostOnboarding.RequiredCode"/>, PL-02) into 403 ProblemDetails with code <c>onboarding_required</c>.
/// Every other outcome goes through the default handler (challenge, generic 403, next).
/// </summary>
public sealed class OnboardingRequiredAuthorizationResultHandler(
    ILogger<OnboardingRequiredAuthorizationResultHandler> logger) : IAuthorizationMiddlewareResultHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden && IsOnboardingRequired(authorizeResult.AuthorizationFailure))
        {
            logger.LogInformation(
                "Host access withheld until the onboarding is complete: {Method} {Route}. TraceId={TraceId}",
                context.Request.Method,
                (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(no route)",
                ApiProblemDetails.GetTraceId(context));

            var problem = ApiProblemDetails.Create(
                context,
                StatusCodes.Status403Forbidden,
                ProblemCodes.OnboardingRequired,
                "OnboardingRequired");
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(problem, problem.GetType(), JsonOptions, ApiProblemDetails.ContentType);
            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }

    /// <summary>True when a requirement was refused by the host onboarding gate.</summary>
    public static bool IsOnboardingRequired(AuthorizationFailure? failure) =>
        failure is not null &&
        failure.FailureReasons.Any(r => string.Equals(r.Message, HostOnboarding.RequiredCode, StringComparison.Ordinal));
}
