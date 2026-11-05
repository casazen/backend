namespace Casazen.Core.Options;

/// <summary>
/// The <c>Octorate</c> section (ADR-023, E02/F01/US02/T01). Five keys are environment variables that already exist on test and
/// production (S20): <see cref="ClientId"/>, <see cref="ClientSecret"/>, <see cref="ApiBaseUrl"/>, <see cref="FrontendBaseUrl"/> and
/// <see cref="AuthorizationEndpoint"/>; the names are kept exactly, a different one would leave the environments without a value.
/// Every address comes from configuration, never from code. With <c>Features:OctorateConnector</c> on, outside Development and Testing,
/// the options are validated at startup (<c>OctorateOptionsValidator</c>).
/// </summary>
public sealed class OctorateOptions
{
    public const string SectionName = "Octorate";

    /// <summary>Minutes a consent state lives when the setting is not given.</summary>
    public const int DefaultStateLifetimeMinutes = 10;

    /// <summary>The longest life a consent state may be given.</summary>
    public const int MaxStateLifetimeMinutes = 60;

    /// <summary>The application's client id (<c>Octorate__ClientId</c>): the code exchange, the refresh, the partner login.</summary>
    public string? ClientId { get; set; }

    /// <summary>The application's client secret (<c>Octorate__ClientSecret</c>): an environment variable only, never committed.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>The API address (<c>Octorate__ApiBaseUrl</c>, <c>https://api.octorate.com/connect</c>); the calls add <c>/rest/v1/...</c>.</summary>
    public string? ApiBaseUrl { get; set; }

    /// <summary>The Octorate login and panel address (<c>Octorate__FrontendBaseUrl</c>), also read by the panel steps of E10.</summary>
    public string? FrontendBaseUrl { get; set; }

    /// <summary>The consent page (<c>Octorate__AuthorizationEndpoint</c>, <c>.../octobook/identity/oauth.xhtml</c>).</summary>
    public string? AuthorizationEndpoint { get; set; }

    /// <summary>
    /// The CasaZen callback page Octorate sends the host back to with the code (<c>Octorate__RedirectUri</c>). Octorate must whitelist
    /// it first (D12: today only <c>https://localhost/</c> is registered); an address it does not know shows an explanation page.
    /// </summary>
    public string? RedirectUri { get; set; }

    /// <summary>Minutes a consent state is valid (1 to <see cref="MaxStateLifetimeMinutes"/>).</summary>
    public int StateLifetimeMinutes { get; set; } = DefaultStateLifetimeMinutes;

    /// <summary>
    /// The domain of the CasaZen address given as the alternative e-mail when CasaZen creates a property on Octorate (PO-19), read by
    /// E02/F03/US01/T06. No default in code.
    /// </summary>
    public string? OwnerEmailDomain { get; set; }

    /// <summary>The secret in the address of the webhook (<c>Octorate__WebhookSecret</c>, 32+ characters), never committed.</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>The life of a consent state, brought into 1 to <see cref="MaxStateLifetimeMinutes"/> minutes.</summary>
    public TimeSpan StateLifetime =>
        TimeSpan.FromMinutes(StateLifetimeMinutes is >= 1 and <= MaxStateLifetimeMinutes ? StateLifetimeMinutes : DefaultStateLifetimeMinutes);
}
