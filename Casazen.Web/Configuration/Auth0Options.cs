using Microsoft.Extensions.Options;

namespace Casazen.Web.Configuration;

/// <summary>
/// Auth0 settings (section <c>Auth0</c>, Railway <c>Auth0__*</c>). <see cref="Domain"/> and <see cref="Audience"/>
/// validate every JWT: without them no authenticated request can succeed, so they are checked at startup outside
/// Development and Testing (<see cref="Auth0OptionsValidator"/>). The Management API client (M2M) is optional: without
/// it only the role sync is off, reported as <c>degraded</c> by the health check. Runbook <c>docs/runbooks/auth0.md</c>.
/// </summary>
public sealed class Auth0Options
{
    public const string SectionName = "Auth0";

    /// <summary>Login domain of the tenant, host only (e.g. <c>tenant.eu.auth0.com</c>): the JWT issuer is <c>https://{Domain}/</c>.</summary>
    public string? Domain { get; set; }

    /// <summary>API identifier expected in the <c>aud</c> claim.</summary>
    public string? Audience { get; set; }

    public string? ManagementClientId { get; set; }

    public string? ManagementClientSecret { get; set; }

    /// <summary>Deprecated static Management API token (FD-14): read only when no M2M client is configured.</summary>
    public string? ManagementApiToken { get; set; }

    public bool HasManagementClient =>
        !RequiredConfiguration.IsMissing(ManagementClientId) && !RequiredConfiguration.IsMissing(ManagementClientSecret);

    public bool UsesLegacyManagementToken =>
        !HasManagementClient && !RequiredConfiguration.IsMissing(ManagementApiToken);
}

/// <summary>Validates the settings JWT validation depends on (registered with <c>ValidateOnStart</c> where required).</summary>
public sealed class Auth0OptionsValidator : IValidateOptions<Auth0Options>
{
    public ValidateOptionsResult Validate(string? name, Auth0Options options)
    {
        var errors = GetErrors(options);
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    public static IReadOnlyList<string> GetErrors(Auth0Options options)
    {
        var errors = new List<string>();

        if (RequiredConfiguration.IsMissing(options.Domain))
        {
            errors.Add("Auth0__Domain is missing or a placeholder: set the login domain of the Auth0 tenant (e.g. tenant.eu.auth0.com).");
        }
        else if (options.Domain!.Contains("://", StringComparison.Ordinal) || options.Domain.Contains('/'))
        {
            errors.Add("Auth0__Domain must be the tenant host only (e.g. tenant.eu.auth0.com), without https:// or a path.");
        }

        if (RequiredConfiguration.IsMissing(options.Audience))
            errors.Add("Auth0__Audience is missing or a placeholder: set the identifier of the Auth0 API (e.g. https://casazen-api).");

        return errors;
    }
}
