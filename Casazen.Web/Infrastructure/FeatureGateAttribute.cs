namespace Casazen.Web.Infrastructure;

/// <summary>
/// Puts a controller or an action behind a feature flag (<see cref="Casazen.Core.Features.FeatureFlags"/>): while the
/// flag is off the endpoint answers 404 like a route that does not exist, before authentication, authorization, rate
/// limiting and model binding (<see cref="Middleware.FeatureGateMiddleware"/>). Several attributes: every flag must be on.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class FeatureGateAttribute(string flag) : Attribute
{
    public string Flag { get; } = flag;
}
