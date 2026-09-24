using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Email;

/// <summary>
/// Builds the links of the web app placed in emails and the public URLs of its pages (SEO canonical, sitemap), always
/// from <c>App:PublicSiteBaseUrl</c> (decision D3). When the value is missing or invalid it throws
/// <see cref="EmailConfigurationException"/>: no fallback domain.
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
    /// Absolute URL of a public page of the web app (<paramref name="path"/> starts with <c>/</c>), e.g. a sitemap
    /// entry. Throws <see cref="EmailConfigurationException"/> when the public URL is not configured.
    /// </summary>
    public string PublicPage(string path) => Build(EnsureAbsolutePath(path));

    /// <summary>
    /// Like <see cref="PublicPage"/>, but <c>null</c> when the public URL is not configured (only possible in
    /// Development/Testing: elsewhere the startup fails). Used for the canonical URL of a page served anyway.
    /// </summary>
    public string? TryPublicPage(string path)
    {
        var absolutePath = EnsureAbsolutePath(path);
        return PublicSiteOptions.TryGetBaseUri(options.Value.PublicSiteBaseUrl, out var baseUri)
            ? baseUri.AbsoluteUri.TrimEnd('/') + absolutePath
            : null;
    }

    private static string EnsureAbsolutePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return path.StartsWith('/')
            ? path
            : throw new ArgumentException("The path of a public page must start with '/'.", nameof(path));
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

    /// <summary>
    /// "Le mie prenotazioni" of the org's booking site (BK-10): the guest finds a booking there with its code and email.
    /// The checkout outcome page (BK-07) needs the checkout token, which only the guest's browser has, so emails link here.
    /// </summary>
    public string GuestBookings(string orgSlug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orgSlug);
        return Build($"/book/{Uri.EscapeDataString(orgSlug)}/my-bookings");
    }

    /// <summary>Host console: detail page of one booking (BK-10).</summary>
    public string HostBooking(Guid bookingId) => Build($"/app/short-rent/bookings/{bookingId:D}");

    /// <summary>
    /// Plan page of the host console (route <c>/app/short-rent/settings/plan</c> of the web app): the page Stripe
    /// Checkout and the billing portal return to (PL-11, A1-31).
    /// </summary>
    public const string BillingPagePath = "/app/short-rent/settings/plan";

    /// <summary>Default return page of a paid Stripe Checkout: the plan page with <c>?checkout=success</c>.</summary>
    public string BillingCheckoutSuccess() => Build(BillingPagePath + "?checkout=success");

    /// <summary>Default return page of an abandoned Stripe Checkout: the plan page with <c>?checkout=cancel</c>.</summary>
    public string BillingCheckoutCancel() => Build(BillingPagePath + "?checkout=cancel");

    /// <summary>Return page of the Stripe billing portal: the plan page.</summary>
    public string BillingPortalReturn() => Build(BillingPagePath);

    /// <summary>
    /// True when <paramref name="url"/> is an absolute URL of the public web app: same scheme, host and port as
    /// <c>App:PublicSiteBaseUrl</c>, without user info. The allow-list of the return URLs a client may send to Stripe
    /// (PL-11, A1-31): after the payment the browser never lands on another site. False when the public URL is not
    /// configured.
    /// </summary>
    public bool IsOnPublicSite(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !PublicSiteOptions.TryGetBaseUri(options.Value.PublicSiteBaseUrl, out var baseUri)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var candidate))
            return false;

        return string.Equals(candidate.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.IdnHost, baseUri.IdnHost, StringComparison.OrdinalIgnoreCase)
            && candidate.Port == baseUri.Port
            && string.IsNullOrEmpty(candidate.UserInfo);
    }

    private string Build(string pathAndQuery) => BaseUrl() + pathAndQuery;

    private string BaseUrl()
    {
        if (!PublicSiteOptions.TryGetBaseUri(options.Value.PublicSiteBaseUrl, out var baseUri))
        {
            throw new EmailConfigurationException(
                "App:PublicSiteBaseUrl is missing or invalid: public links cannot be built.");
        }

        return baseUri.AbsoluteUri.TrimEnd('/');
    }
}
