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

/// <summary>The e-mail channel: <c>state</c> is one of <c>none</c>, <c>pending</c>, <c>confirmed</c>, <c>soft_opt_in</c>, <c>service_only</c>, <c>withdrawn</c>.</summary>
public sealed record GuestMarketingEmailDto(string State, string? MaskedAddress, bool CanResend);

/// <summary>The WhatsApp channel: <c>state</c> as for e-mail, never <c>pending</c> (there is no confirmation).</summary>
public sealed record GuestMarketingWhatsappDto(string State, string? MaskedPhone);

/// <summary>What the guest changes on the guest page (<c>PUT api/public/guest/bookings/{code}/marketing</c>): a channel that is left out stays as it is.</summary>
public sealed class UpdateGuestMarketingRequest
{
    /// <summary>Turn e-mail on or off.</summary>
    public GuestMarketingEmailRequest? Email { get; set; }

    /// <summary>Turn WhatsApp on or off.</summary>
    public GuestMarketingWhatsappRequest? Whatsapp { get; set; }

    /// <summary>The version of the consent text the page showed; needed to turn a channel on.</summary>
    [MaxLength(100)]
    public string? ConsentVersion { get; set; }

    /// <summary>The language of the page, <c>it</c> or <c>en</c>.</summary>
    [MaxLength(10)]
    public string? Language { get; set; }

    public GuestMarketingChange ToChange() =>
        new(
            Email is null ? null : new GuestMarketingEmailChange(Email.Enabled, Email.Address),
            Whatsapp is null ? null : new GuestMarketingWhatsAppChange(Whatsapp.Enabled, Whatsapp.Phone),
            ConsentVersion,
            Language);
}

/// <summary>The e-mail part of a change.</summary>
public sealed class GuestMarketingEmailRequest
{
    /// <summary><c>true</c> to consent, <c>false</c> to withdraw. A missing member is <c>false</c>: nothing is ever consented by default.</summary>
    public bool Enabled { get; set; }

    /// <summary>The address to consent with; needed when the booking has an OTA forward address or none, else the address of the booking is used.</summary>
    [MaxLength(255)]
    public string? Address { get; set; }
}

/// <summary>The WhatsApp part of a change.</summary>
public sealed class GuestMarketingWhatsappRequest
{
    /// <summary><c>true</c> to consent, <c>false</c> to withdraw.</summary>
    public bool Enabled { get; set; }

    /// <summary>The number to consent with; the phone of the booking is used when it is left out.</summary>
    [MaxLength(40)]
    public string? Phone { get; set; }
}

/// <summary>The answer of the resend: whether this call queued the e-mail (<c>false</c>: nothing was waiting for a confirmation, or the limits did not allow another message).</summary>
/// <param name="Sent"><c>true</c> when the confirmation e-mail was queued by this call.</param>
public sealed record GuestMarketingResendResponse(bool Sent);
