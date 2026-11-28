using System.ComponentModel.DataAnnotations;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Crm;

/// <summary>
/// The marketing choices of the guest of a booking, as the guest page shows them (E07/F04/US01/T04, NF-90): the versions of the texts and, for
/// each channel, a state, the value masked, and whether the confirmation e-mail can be asked for again. Nothing is ever shown as ticked unless the
/// guest ticked it: the page reads <c>state</c>, it does not guess.
/// </summary>
/// <param name="MarketingConsentVersion">The version of the consent text, <c>null</c> while it has none (the page then offers no consent).</param>
/// <param name="SoftOptInNoticeVersion">The version of the soft opt-in notice, <c>null</c> while there is none.</param>
/// <param name="Email">The e-mail channel.</param>
/// <param name="Whatsapp">The WhatsApp channel.</param>
public sealed record GuestMarketingDto(
    string? MarketingConsentVersion,
    string? SoftOptInNoticeVersion,
    GuestMarketingEmailDto Email,
    GuestMarketingWhatsappDto Whatsapp)
{
    public static GuestMarketingDto From(GuestMarketingPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        return new GuestMarketingDto(
            preferences.MarketingConsentVersion,
            preferences.SoftOptInNoticeVersion,
            new GuestMarketingEmailDto(preferences.Email.State, preferences.Email.MaskedAddress, preferences.Email.CanResend),
            new GuestMarketingWhatsappDto(preferences.WhatsApp.State, preferences.WhatsApp.MaskedPhone));
    }
}

public sealed record GuestMarketingEmailDto(string State, string? MaskedAddress, bool CanResend);

public sealed record GuestMarketingWhatsappDto(string State, string? MaskedPhone);

public sealed class UpdateGuestMarketingRequest
{
    public GuestMarketingEmailRequest? Email { get; set; }

    public GuestMarketingWhatsappRequest? Whatsapp { get; set; }

    [MaxLength(100)]
    public string? ConsentVersion { get; set; }

    [MaxLength(10)]
    public string? Language { get; set; }

    public GuestMarketingChange ToChange() =>
        new(
            Email is { Enabled: { } emailEnabled } ? new GuestMarketingEmailChange(emailEnabled, Email.Address) : null,
            Whatsapp is { Enabled: { } whatsappEnabled } ? new GuestMarketingWhatsAppChange(whatsappEnabled, Whatsapp.Phone) : null,
            ConsentVersion,
            Language);
}

public sealed class GuestMarketingEmailRequest
{
    /// <summary>
    /// <c>true</c> to consent, <c>false</c> to withdraw. <b>Required</b>: a body without it is a 400, because the opt-out it would be taken for is
    /// final (a suppression is never lifted) and a forgotten member must not cause one.
    /// </summary>
    [Required(ErrorMessage = "GuestMarketingEnabledRequired")]
    public bool? Enabled { get; set; }

    [MaxLength(255)]
    public string? Address { get; set; }
}

public sealed class GuestMarketingWhatsappRequest
{
    [Required(ErrorMessage = "GuestMarketingEnabledRequired")]
    public bool? Enabled { get; set; }

    [MaxLength(40)]
    public string? Phone { get; set; }
}

public sealed record GuestMarketingResendResponse(bool Sent);
