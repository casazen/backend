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

    /// <summary>The attempts of a check-in write (a guest, a state of the stay) before one "To do" is raised (E03/F01/US01/T03).</summary>
    public const int DefaultCheckInMaxAttempts = 5;

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

    /// <summary>The thresholds of the matcher of the listings (<c>Octorate:Mapping</c>, E02/F01/US02/T03).</summary>
    public OctorateMappingOptions Mapping { get; set; } = new();

    /// <summary>
    /// Whether the tourist tax of each guest (<c>cityTaxPrice</c>, <c>cityTaxExemption</c>, <c>excludeCityTax</c>) is written to Octorate with
    /// the check-in (<c>Octorate__WriteCityTaxValues</c>, E03/F01/US01/T03, AC-NF46-5). Off by default: CasaZen manages the tax (S18), and the
    /// write is justified only when Octorate issues the documents (PO-05) and uses the values (D20, Q-004). Even on, only a confirmed record
    /// that is not stale travels.
    /// </summary>
    public bool WriteCityTaxValues { get; set; }

    /// <summary>
    /// The attempts of a check-in write before the retry job stops and one "To do" is raised (<c>Octorate__CheckInMaxAttempts</c>, 1 to 20;
    /// default <see cref="DefaultCheckInMaxAttempts"/>).
    /// </summary>
    public int CheckInMaxAttempts { get; set; } = DefaultCheckInMaxAttempts;

    /// <summary>
    /// Whether the guests of a property whose sender is CasaZen or the host (not the channel manager) are sent to Octorate as drafts
    /// (<c>Octorate__SendGuestsWhenSenderIsNotChannelManager</c>, E03/F01/US01/T03). Off by default and until the product owner confirms how
    /// <c>draft</c> behaves on a real property (PO-22, D31): the sandbox ignores it, so a draft could still be transmitted to the police.
    /// </summary>
    public bool SendGuestsWhenSenderIsNotChannelManager { get; set; }

    /// <summary>The life of a consent state, brought into 1 to <see cref="MaxStateLifetimeMinutes"/> minutes.</summary>
    public TimeSpan StateLifetime =>
        TimeSpan.FromMinutes(StateLifetimeMinutes is >= 1 and <= MaxStateLifetimeMinutes ? StateLifetimeMinutes : DefaultStateLifetimeMinutes);
}

/// <summary>
/// The thresholds that turn the score of the matcher (0 to 100) into a proposal (<c>Octorate:Mapping</c>, E02/F01/US02/T03): a single
/// winner with at least <see cref="ProposeMinScore"/> and <see cref="ProposeMinMargin"/> points over the second is proposed; a best
/// score from <see cref="CandidateMinScore"/> up, or a tie, is ambiguous and a person chooses; below it there is no candidate and the
/// listing is offered for import. Defaults proposed by the plan, to calibrate on the sandbox network.
/// </summary>
public sealed class OctorateMappingOptions
{
    public const int DefaultProposeMinScore = 85;
    public const int DefaultProposeMinMargin = 15;
    public const int DefaultCandidateMinScore = 50;

    public int ProposeMinScore { get; set; } = DefaultProposeMinScore;

    public int ProposeMinMargin { get; set; } = DefaultProposeMinMargin;

    public int CandidateMinScore { get; set; } = DefaultCandidateMinScore;

    /// <summary>True when 0 &lt;= candidate &lt; propose &lt;= 100 and the margin is 0 to 100.</summary>
    public bool IsValid =>
        CandidateMinScore >= 0 && CandidateMinScore < ProposeMinScore && ProposeMinScore <= 100
        && ProposeMinMargin is >= 0 and <= 100;
}
