using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Email;

/// <summary>
/// Builds the links of the web app placed in emails, always from <c>App:PublicSiteBaseUrl</c> (decision D3). When the
/// value is missing or invalid it throws <see cref="EmailConfigurationException"/>: no fallback domain.
/// </summary>
public sealed class PublicSiteLinks(IOptions<PublicSiteOptions> options)
{
    public bool IsConfigured => PublicSiteOptions.TryGetBaseUri(options.Value.PublicSiteBaseUrl, out _);

    /// <summary>Throws <see cref="EmailConfigurationException"/> when links cannot be built.</summary>
    public void EnsureConfigured() => _ = BaseUrl();

    /// <summary>Supplier console inbox.</summary>
    public string SupplierInbox() => Build("/app/supplier/inbox");

    /// <summary>Guest self check-in page for a raw session token.</summary>
    public string GuestCheckIn(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return Build($"/checkin/{Uri.EscapeDataString(token)}");
    }

    /// <summary>
    /// Supplier registration page of the web app for an invite (SU-01). Only the token is in the link: the page reads
    /// email and comune from the API, so no personal data ends up in URLs and logs.
    /// </summary>
    public string SupplierInviteSignup(string inviteToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inviteToken);
        return Build($"/register?inviteToken={Uri.EscapeDataString(inviteToken)}");
    }

    /// <summary>
    /// Page of the booking site where the guest confirms the email of a "pay at the property" request (BK-06). Only the
    /// booking id and the random token are in the link, no personal data.
    /// </summary>
    public string OnSiteRequestConfirmation(string orgSlug, Guid bookingId, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orgSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return Build(
            $"/book/{Uri.EscapeDataString(orgSlug)}/requests/{bookingId:D}/confirm?token={Uri.EscapeDataString(token)}");
    }

    /// <summary>Host console: the "pay at the property" requests to accept or decline (BK-06).</summary>
    public string HostBookingRequests() => Build("/app/short-rent/bookings?view=requests");

    private string Build(string pathAndQuery) => BaseUrl() + pathAndQuery;

    private string BaseUrl()
    {
        if (!PublicSiteOptions.TryGetBaseUri(options.Value.PublicSiteBaseUrl, out var baseUri))
        {
            throw new EmailConfigurationException(
                "App:PublicSiteBaseUrl is missing or invalid: email links cannot be built.");
        }

        return baseUri.AbsoluteUri.TrimEnd('/');
    }
}
