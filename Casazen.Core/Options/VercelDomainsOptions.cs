namespace Casazen.Core.Options;

/// <summary>
/// Access to the Vercel REST API for the custom domains of the hosts' booking sites (BK-17, A3-25): the platform adds a
/// verified custom domain to the Vercel project (so Vercel serves it and issues its certificate), reads whether Vercel
/// accepts it, and removes it when the host drops it. Section <c>Vercel</c>, Railway variables <c>Vercel__ApiToken</c>,
/// <c>Vercel__ProjectId</c>, <c>Vercel__TeamId</c>. The values are the product owner's (decision D9, runbook
/// <c>docs/runbooks/seo-domain.md</c> section 10); nothing here is a secret or a default domain.
/// </summary>
public sealed class VercelDomainsOptions
{
    public const string SectionName = "Vercel";

    /// <summary>
    /// Vercel access token. The narrowest one that works: a token scoped to the single web app project (the runbook says how
    /// to create it). Secret: only ever read from the environment, never logged, never returned.
    /// </summary>
    public string? ApiToken { get; set; }

    /// <summary>Id (<c>prj_…</c>) or name of the Vercel project that serves the web app (<c>Vercel__ProjectId</c>).</summary>
    public string? ProjectId { get; set; }

    /// <summary>
    /// Id (<c>team_…</c>) of the team that owns the project, sent as <c>teamId</c>; empty for a project of a personal account
    /// or when the token is scoped to the team.
    /// </summary>
    public string? TeamId { get; set; }

    /// <summary>Base URL of the Vercel REST API (https). The documented API host of the provider, overridden only by tests.</summary>
    public string ApiBaseUrl { get; set; } = "https://api.vercel.com";

    /// <summary>Seconds one call to the API may take before it is treated as unavailable.</summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// The token and the project are both set: the platform can activate custom domains. When this is false the settings page
    /// says so honestly, no domain is reported as active, and the health check names the missing variables (D9).
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiToken) && !string.IsNullOrWhiteSpace(ProjectId);

    /// <summary>Names of the Railway variables that are missing (never their values).</summary>
    public IReadOnlyList<string> MissingVariables() =>
    [
        .. string.IsNullOrWhiteSpace(ApiToken) ? new[] { "Vercel__ApiToken" } : [],
        .. string.IsNullOrWhiteSpace(ProjectId) ? new[] { "Vercel__ProjectId" } : [],
    ];
}
