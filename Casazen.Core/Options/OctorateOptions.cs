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

    public Guid? MasterOrganizationId { get; set; }

    public string? BillingNetworkName { get; set; }

    public const int DefaultStateLifetimeMinutes = 10;

    public const int MaxStateLifetimeMinutes = 60;

    public const int DefaultCheckInMaxAttempts = 5;

    public string? ClientId { get; set; }

    /// <summary>The application's client secret (<c>Octorate__ClientSecret</c>): an environment variable only, never committed.</summary>
    public string? ClientSecret { get; set; }

    public string? ApiBaseUrl { get; set; }

    public string? FrontendBaseUrl { get; set; }

    public string? AuthorizationEndpoint { get; set; }

    /// <summary>
    /// The CasaZen callback page Octorate sends the host back to with the code (<c>Octorate__RedirectUri</c>). Octorate must whitelist
    /// it first (D12: today only <c>https://localhost/</c> is registered); an address it does not know shows an explanation page.
    /// </summary>
    public string? RedirectUri { get; set; }

    public bool AllowProductionApiInTest { get; set; }

    public int StateLifetimeMinutes { get; set; } = DefaultStateLifetimeMinutes;

    public string? OwnerEmailDomain { get; set; }

    /// <summary>The secret in the address of the webhook (<c>Octorate__WebhookSecret</c>, 32+ characters), never committed.</summary>
    public string? WebhookSecret { get; set; }

    public OctorateMappingOptions Mapping { get; set; } = new();

    public OctorateExistingInventoryOptions ExistingInventory { get; set; } = new();

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

    public bool IsValid =>
        CandidateMinScore >= 0 && CandidateMinScore < ProposeMinScore && ProposeMinScore <= 100
        && ProposeMinMargin is >= 0 and <= 100;
}
