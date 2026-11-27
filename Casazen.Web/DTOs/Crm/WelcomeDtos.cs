using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email;

namespace Casazen.Web.DTOs.Crm;

/// <summary>
/// The welcome link of a property as the host sees it (E07/F04/US01/T03): whether the property has an active link, the address of the welcome
/// page to print as a QR code, and when the link was made. A property that has none answers <c>active: false</c> and nothing else.
/// </summary>
/// <param name="Active">The property has an active link.</param>
/// <param name="Url">The address of the welcome page (<c>/benvenuto/{token}</c>), <c>null</c> without a link or when the public address of the web app is not configured.</param>
/// <param name="CreatedAt">When the active link was made (UTC).</param>
public sealed record WelcomeLinkDto(bool Active, string? Url, DateTime? CreatedAt)
{
    public static WelcomeLinkDto From(PropertyWelcomeLink? link, PublicSiteLinks siteLinks)
    {
        ArgumentNullException.ThrowIfNull(siteLinks);
        if (link is null)
            return new WelcomeLinkDto(false, null, null);

        return new WelcomeLinkDto(true, siteLinks.IsConfigured ? siteLinks.Welcome(link.Token) : null, link.CreatedAt);
    }
}

/// <summary>What the public welcome page shows: no personal data (E07/F04/US01/T03).</summary>
/// <param name="OrgName">The public name of the org.</param>
/// <param name="PropertyName">The name of the property.</param>
/// <param name="MarketingConsentVersion">The version of the consent text, <c>null</c> while it has none (the page then offers no consent).</param>
public sealed record WelcomePageDto(string OrgName, string PropertyName, string? MarketingConsentVersion)
{
    public static WelcomePageDto From(WelcomePage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return new WelcomePageDto(page.OrgName, page.PropertyName, page.MarketingConsentVersion);
    }
}

/// <summary>
/// What the guest typed and ticked on the welcome page (<c>POST api/public/welcome/{token}/consent</c>). Every box is <c>false</c> unless the
/// guest ticked it: nothing is preselected and a missing member is a box that was not ticked.
/// </summary>
public sealed class WelcomeConsentRequest
{
    /// <summary>The e-mail address; used only when <see cref="ConsentEmail"/> is ticked.</summary>
    [MaxLength(255)]
    public string? Email { get; set; }

    /// <summary>The phone number; used only when <see cref="ConsentWhatsApp"/> is ticked.</summary>
    [MaxLength(40)]
    public string? Phone { get; set; }

    /// <summary>Offers by e-mail.</summary>
    public bool ConsentEmail { get; set; }

    /// <summary>Offers on WhatsApp.</summary>
    public bool ConsentWhatsApp { get; set; }

    /// <summary>The guest declares to be of age. Required: 422 <c>welcome_adult_required</c> without it.</summary>
    public bool AdultConfirmed { get; set; }

    /// <summary>The version of the consent text the page showed (the <c>marketingConsentVersion</c> of the page).</summary>
    [MaxLength(100)]
    public string? ConsentVersion { get; set; }

    /// <summary>The language of the page, <c>it</c> or <c>en</c>: the language of the confirmation e-mail.</summary>
    [MaxLength(10)]
    public string? Language { get; set; }

    public WelcomeConsentInput ToInput() =>
        new(Email, Phone, ConsentEmail, ConsentWhatsApp, AdultConfirmed, ConsentVersion, Language);
}

/// <summary>
/// The answer of the consent call. <see cref="ConfirmationSent"/> is <c>true</c> whatever was stored and whatever was sent (a new address, a known
/// one, a confirmed one, one that opted out): the page cannot be used to learn who is a customer, and cannot lift an opt-out.
/// </summary>
/// <param name="ConfirmationSent">Always <c>true</c>.</param>
public sealed record WelcomeConsentResponse(bool ConfirmationSent);
