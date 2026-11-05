using Casazen.Core.Features;
using Casazen.Core.Options;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Configuration;

/// <summary>
/// Startup validation of the <c>Octorate</c> options (E02/F01/US02/T01). With <c>Features:OctorateConnector</c> off nothing is
/// required: no endpoint of the connector answers. With the flag on, the life of a consent state must be 1 to 60 minutes and the
/// addresses that are set must be absolute https; outside Development and Testing the application credentials, the API address, the
/// consent page and the callback address are also required, or the service does not start (docs/runbooks/deploy-checklist.md).
/// </summary>
public sealed class OctorateOptionsValidator(IFeatureFlags featureFlags, IHostEnvironment environment) : IValidateOptions<OctorateOptions>
{
    public ValidateOptionsResult Validate(string? name, OctorateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
            return ValidateOptionsResult.Success;

        var failures = new List<string>();
        if (options.StateLifetimeMinutes is < 1 or > OctorateOptions.MaxStateLifetimeMinutes)
            failures.Add($"Octorate__StateLifetimeMinutes must be 1 to {OctorateOptions.MaxStateLifetimeMinutes} minutes.");

        CheckAddress(failures, "Octorate__ApiBaseUrl", options.ApiBaseUrl);
        CheckAddress(failures, "Octorate__AuthorizationEndpoint", options.AuthorizationEndpoint);
        CheckAddress(failures, "Octorate__RedirectUri", options.RedirectUri);
        CheckAddress(failures, "Octorate__FrontendBaseUrl", options.FrontendBaseUrl);

        if (RequiredConfiguration.IsEnforced(environment))
        {
            Require(failures, "Octorate__ClientId", options.ClientId);
            Require(failures, "Octorate__ClientSecret", options.ClientSecret);
            Require(failures, "Octorate__ApiBaseUrl", options.ApiBaseUrl);
            Require(failures, "Octorate__AuthorizationEndpoint", options.AuthorizationEndpoint);
            Require(failures, "Octorate__RedirectUri", options.RedirectUri);
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Require(List<string> failures, string variable, string? value)
    {
        if (RequiredConfiguration.IsMissing(value))
            failures.Add($"{variable} is missing: Features__OctorateConnector is on (docs/runbooks/deploy-checklist.md).");
    }

    private static void CheckAddress(List<string> failures, string variable, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            failures.Add($"{variable} must be an absolute https address.");
    }
}
