namespace Casazen.Core.Services;

/// <summary>
/// The marketing choices of the guest on the guest page of a booking (E07/F04/US01/T04, NF-90, CRM-03, AC-NF90-2): the guest who proved a
/// booking (<c>api/public/guest/session</c>) reads what they agreed to for e-mail and WhatsApp, gives a consent, withdraws it and asks for the
/// confirmation e-mail again. Every method finds the booking with <see cref="IGuestSessionService.FindBookingAsync"/>, so a booking of another
/// guest, of another site or that does not exist is the same <see cref="Exceptions.NotFoundException"/> as everywhere in the guest area and
/// nothing is read or written for it.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Nothing is ever shown as ticked unless the guest ticked it</b>: the answer carries a state per channel
/// (<see cref="GuestMarketingStates"/>); a soft opt-in is told apart from a consent, and a booking address the guest never consented with is
/// <c>none</c> or <c>service_only</c>.</item>
/// <item><b>An opt-out is final.</b> Turning a channel off suppresses its contacts (the address goes on the suppression list); turning it on again
/// stores the new consent and changes nothing of the suppression (the state stays <c>withdrawn</c>).</item>
/// <item><b>Masked.</b> The answer holds a masked address and a masked number, never the clear value.</item>
/// <item>The logs carry ids, never an address or a number.</item>
/// </list>
/// </remarks>
public interface IGuestMarketingPreferencesService
{
    Task<GuestMarketingPreferences> GetAsync(GuestSessionTicket ticket, string bookingCode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the change the guest asked for and answers the choices as they are then. Turning a channel on records a consent contact (origin and
    /// source <c>GuestPage</c>, the version, the IP and the time) and, for e-mail, queues the double opt-in message; turning it off withdraws it.
    /// A channel the request does not name is left as it is.
    /// </summary>
    /// <param name="ticket">What the session of the guest proves.</param>
    /// <param name="bookingCode">The code of the booking, as the guest wrote it.</param>
    /// <param name="change">What the guest asked for.</param>
    /// <param name="ipAddress">The address of the guest's connection, as evidence of the consent; <c>null</c> when it is not known.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="Exceptions.NotFoundException"><see cref="GuestBookingLookupErrorCodes.NotFound"/>.</exception>
    /// <exception cref="Exceptions.DomainRuleException">422 <see cref="GuestMarketingErrorCodes.ConsentUnavailable"/>, <see cref="GuestMarketingErrorCodes.EmailInvalid"/>, <see cref="GuestMarketingErrorCodes.PhoneInvalid"/>.</exception>
    /// <exception cref="Exceptions.DomainConflictException">409 <see cref="GuestMarketingErrorCodes.ConsentOutdated"/>.</exception>
    Task<GuestMarketingPreferences> SetAsync(
        GuestSessionTicket ticket,
        string bookingCode,
        GuestMarketingChange change,
        string? ipAddress,
        CancellationToken cancellationToken = default);

    Task<MarketingConfirmationSendOutcome> ResendConfirmationAsync(
        GuestSessionTicket ticket,
        string bookingCode,
        CancellationToken cancellationToken = default);
}

public static class GuestMarketingStates
{
    public const string None = "none";

    public const string Pending = "pending";

    public const string Confirmed = "confirmed";

    /// <summary>The address is on soft opt-in (art. 130 c. 4): the guest was told, not asked, and can object.</summary>
    public const string SoftOptIn = "soft_opt_in";

    public const string ServiceOnly = "service_only";

    public const string Withdrawn = "withdrawn";
}

public static class GuestMarketingErrorCodes
{
    public const string ConsentUnavailable = "guest_marketing_consent_unavailable";

    /// <summary>409: the text of the consent changed since the page was loaded: the version sent is not the current one.</summary>
    public const string ConsentOutdated = "guest_marketing_consent_outdated";

    public const string EmailInvalid = "guest_marketing_email_invalid";

    public const string PhoneInvalid = "guest_marketing_phone_invalid";
}

/// <summary>What the guest page shows (<see cref="IGuestMarketingPreferencesService.GetAsync"/>).</summary>
/// <param name="MarketingConsentVersion">The version of the consent text (<c>Gdpr:MarketingConsentVersion</c>), <c>null</c> while it has none: the page then offers no consent.</param>
/// <param name="SoftOptInNoticeVersion">The version of the soft opt-in notice (<c>Gdpr:SoftOptInNoticeVersion</c>), <c>null</c> while there is none.</param>
/// <param name="Email">The e-mail channel.</param>
/// <param name="WhatsApp">The WhatsApp channel.</param>
public sealed record GuestMarketingPreferences(
    string? MarketingConsentVersion,
    string? SoftOptInNoticeVersion,
    GuestMarketingEmailChoice Email,
    GuestMarketingWhatsAppChoice WhatsApp);

public sealed record GuestMarketingEmailChoice(string State, string? MaskedAddress, bool CanResend);

/// <summary>The WhatsApp channel of the guest.</summary>
/// <param name="State">One of <see cref="GuestMarketingStates"/> (never <see cref="GuestMarketingStates.Pending"/>: WhatsApp has no confirmation).</param>
/// <param name="MaskedPhone">The number the choice is about, masked; <c>null</c> when there is none.</param>
public sealed record GuestMarketingWhatsAppChoice(string State, string? MaskedPhone);

public sealed record GuestMarketingChange(
    GuestMarketingEmailChange? Email,
    GuestMarketingWhatsAppChange? WhatsApp,
    string? ConsentVersion,
    string? Language);

public sealed record GuestMarketingEmailChange(bool Enabled, string? Address);

public sealed record GuestMarketingWhatsAppChange(bool Enabled, string? Phone);

public static class MarketingContactMasks
{
    public static string Email(string address)
    {
        ArgumentNullException.ThrowIfNull(address);

        var at = address.LastIndexOf('@');
        if (at <= 0 || at == address.Length - 1)
            return "***";

        var domain = address[(at + 1)..];
        var dot = domain.LastIndexOf('.');
        var name = dot > 0 ? domain[..dot] : domain;
        var tail = dot > 0 ? domain[dot..] : string.Empty;
        return $"{address[0]}***@{name[0]}***{tail}";
    }

    public static string Phone(string phone)
    {
        ArgumentNullException.ThrowIfNull(phone);

        var value = phone.Trim();
        if (value.Length < 9)
            return value.Length < 3 ? "***" : "***" + value[^2..];

        return value[..3] + new string('*', value.Length - 6) + value[^3..];
    }
}
