using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email;

namespace Casazen.Web.DTOs.Crm;

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
    [MaxLength(255)]
    public string? Email { get; set; }

    [MaxLength(40)]
    public string? Phone { get; set; }

    public bool ConsentEmail { get; set; }

    public bool ConsentWhatsApp { get; set; }

    public bool AdultConfirmed { get; set; }

    [MaxLength(100)]
    public string? ConsentVersion { get; set; }

    [MaxLength(10)]
    public string? Language { get; set; }

    public WelcomeConsentInput ToInput() =>
        new(Email, Phone, ConsentEmail, ConsentWhatsApp, AdultConfirmed, ConsentVersion, Language);
}

public sealed record WelcomeConsentResponse(bool ConfirmationSent);
