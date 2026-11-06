namespace Casazen.Core.Services;

/// <summary>
/// The public welcome page of a property (E07/F04/US01/T03, NF-90, CRM-03, AC-NF90-3): a guest who scans the QR code in the apartment reads
/// what the page is about and, if they want, leaves an e-mail address and/or a phone number with a per-channel consent. Anonymous: the token of
/// the link is the only thing the caller proves, and an unknown token and a revoked one are the same <see cref="Exceptions.NotFoundException"/>
/// (<see cref="WelcomeConsentErrorCodes.LinkInvalid"/>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Adults only</b> (the guest declares it; minors are excluded at the check-in too), <b>nothing preselected</b>, at least one channel,
/// a valid address for each ticked channel, and the text of the consent must have a configured version and the version sent must be the
/// current one.</item>
/// <item><b>What it stores</b>: a lead profile (<see cref="IGuestMatchingService.FindOrCreateLeadProfileAsync"/>, the property of the link added to
/// its properties) and a consent contact for each ticked channel (origin <c>WelcomePage</c>, source <c>WelcomePage</c>, the version, the IP and
/// the time), then the double opt-in e-mail. No guest is created.</item>
/// <item><b>It reveals nothing and lifts nothing</b>: the caller is told the same whether the address is new, known, already confirmed or
/// suppressed, and a suppressed contact stays suppressed (the consent is stored, no message is sent).</item>
/// <item>The logs carry ids, never an address or a number.</item>
/// </list>
/// </remarks>
public interface IWelcomeConsentService
{
    /// <summary>
    /// What the page shows, with no personal data: the org, the property and the version of the consent text. <c>null</c> when the token is unknown
    /// or revoked, or the property was deleted or its org is not active.
    /// </summary>
    Task<WelcomePage?> GetPageAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>Records the consent the guest ticked and queues the double opt-in e-mail. Returns nothing: the answer never depends on what was stored.</summary>
    /// <param name="token">The token of the link.</param>
    /// <param name="input">What the guest typed and ticked.</param>
    /// <param name="ipAddress">The address of the guest's connection, as evidence of the consent; <c>null</c> when it is not known.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="Exceptions.NotFoundException"><see cref="WelcomeConsentErrorCodes.LinkInvalid"/>.</exception>
    /// <exception cref="Exceptions.DomainRuleException">422: the adult declaration, the channel, the e-mail address or the phone number, or the consent text, is missing or not valid.</exception>
    /// <exception cref="Exceptions.DomainConflictException">409 <see cref="WelcomeConsentErrorCodes.ConsentOutdated"/>: the text the guest read is not the current one.</exception>
    Task SubmitAsync(string token, WelcomeConsentInput input, string? ipAddress, CancellationToken cancellationToken = default);
}

/// <summary>The public page of a welcome link (<see cref="IWelcomeConsentService.GetPageAsync"/>): nothing personal.</summary>
/// <param name="OrgName">The public name of the org that runs the property.</param>
/// <param name="PropertyName">The name of the property.</param>
/// <param name="MarketingConsentVersion">The version of the consent text (<c>Gdpr:MarketingConsentVersion</c>), <c>null</c> while it has none: the page then offers no consent.</param>
public sealed record WelcomePage(string OrgName, string PropertyName, string? MarketingConsentVersion);

/// <summary>What the guest typed and ticked on the welcome page.</summary>
/// <param name="Email">The e-mail address, as typed; used only when <paramref name="ConsentEmail"/> is ticked.</param>
/// <param name="Phone">The phone number, as typed; used only when <paramref name="ConsentWhatsApp"/> is ticked.</param>
/// <param name="ConsentEmail">The guest ticked offers by e-mail. Never preselected.</param>
/// <param name="ConsentWhatsApp">The guest ticked offers on WhatsApp. Never preselected.</param>
/// <param name="AdultConfirmed">The guest declared to be of age. Must be <c>true</c>.</param>
/// <param name="ConsentVersion">The version of the consent text the page showed.</param>
/// <param name="Language">The language of the page (<c>it</c>, <c>en</c>); Italian for anything else.</param>
public sealed record WelcomeConsentInput(
    string? Email,
    string? Phone,
    bool ConsentEmail,
    bool ConsentWhatsApp,
    bool AdultConfirmed,
    string? ConsentVersion,
    string? Language);

/// <summary>Stable <c>code</c> values of the welcome page errors: the frontend translates them.</summary>
public static class WelcomeConsentErrorCodes
{
    /// <summary>404: the token is unknown or revoked (one answer for both, so a printed code that was replaced tells nothing).</summary>
    public const string LinkInvalid = "welcome_link_invalid";

    /// <summary>422: the guest did not declare to be of age.</summary>
    public const string AdultRequired = "welcome_adult_required";

    /// <summary>422: no channel was ticked.</summary>
    public const string ChannelRequired = "welcome_channel_required";

    /// <summary>422: the e-mail address is missing or is not a plain address.</summary>
    public const string EmailInvalid = "welcome_email_invalid";

    /// <summary>422: the phone number is missing or is not a number that can be stored.</summary>
    public const string PhoneInvalid = "welcome_phone_invalid";

    /// <summary>
    /// 422: the consent cannot be collected now: the text has no configured version (<c>Gdpr:MarketingConsentVersion</c>) or the CRM cannot store a
    /// contact in this environment (no <c>Crm:ContactIndexKey</c>). Nothing is recorded.
    /// </summary>
    public const string ConsentUnavailable = "welcome_consent_unavailable";

    /// <summary>409: the text of the consent changed since the page was loaded: the version sent is not the current one.</summary>
    public const string ConsentOutdated = "welcome_consent_outdated";
}
