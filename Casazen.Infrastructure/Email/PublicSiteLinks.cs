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
    /// Payments page of the host console (route <c>/app/short-rent/settings/payments</c> of the web app), where the Stripe
    /// Connect onboarding starts and ends (BK-09, A3-42).
    /// </summary>
    public const string ConnectPaymentsPagePath = "/app/short-rent/settings/payments";

    /// <summary><c>return_url</c> of the Connect Account Link: the payments page with <c>?stripe_return=1</c> (BK-09).</summary>
    public string ConnectOnboardingReturn() => Build(ConnectPaymentsPagePath + "?stripe_return=1");

    /// <summary>
    /// <c>refresh_url</c> of the Connect Account Link (expired or already used link): the payments page with
    /// <c>?stripe_refresh=1</c>, from which the host starts a new link (BK-09).
    /// </summary>
    public string ConnectOnboardingRefresh() => Build(ConnectPaymentsPagePath + "?stripe_refresh=1");

    /// <summary>
    /// "Le mie prenotazioni" of the org's booking site (BK-10, BK-11): the guest finds a booking there with its code and
    /// email. The checkout outcome page (BK-07) needs the checkout token, which only the guest's browser has, so emails
    /// link here. With <paramref name="bookingCode"/> the page opens with the code filled in (<c>?code=</c>) and still asks
    /// for the email before showing anything.
    /// </summary>
    public string GuestBookings(string orgSlug, string? bookingCode = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orgSlug);
        var path = $"/book/{Uri.EscapeDataString(orgSlug)}/my-bookings";
        return string.IsNullOrWhiteSpace(bookingCode)
            ? Build(path)
            : Build($"{path}?code={Uri.EscapeDataString(bookingCode)}");
    }

    /// <summary>
    /// Checkout outcome page of the booking site (BK-07) with a checkout token: the guest of a failed deferred charge pays
    /// there (BK-08). Only the booking id and the random token are in the link, no personal data.
    /// </summary>
    public string CheckoutOutcome(string orgSlug, Guid bookingId, string checkoutToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orgSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkoutToken);
        return Build(
            $"/book/{Uri.EscapeDataString(orgSlug)}/booking/{bookingId:D}?token={Uri.EscapeDataString(checkoutToken)}");
    }

    /// <summary>Host console: detail page of one booking (BK-10).</summary>
    public string HostBooking(Guid bookingId) => Build($"/app/short-rent/bookings/{bookingId:D}");

    /// <summary>
    /// Plan page of the host console (route <c>/app/short-rent/settings/plan</c> of the web app): the default page Stripe
    /// Checkout and the billing portal return to (PL-11, A1-31) when the client names no page.
    /// </summary>
    public const string BillingPagePath = "/app/short-rent/settings/plan";

    /// <summary>
    /// Allow-list of the pages Stripe may send the browser back to (PL-16, A1-36): the plan and billing pages of the web
    /// app in each rental context, so a long-term landlord comes back to its own shell. Exact paths, no query string.
    /// </summary>
    public static readonly IReadOnlySet<string> BillingReturnPagePaths = new HashSet<string>(StringComparer.Ordinal)
    {
        BillingPagePath,
        "/app/short-rent/settings/billing",
        "/app/long-rent/settings/plan",
        "/app/long-rent/settings/billing",
    };

    /// <summary>True when <paramref name="path"/> is exactly one of <see cref="BillingReturnPagePaths"/>.</summary>
    public static bool IsBillingReturnPagePath(string? path) =>
        path is not null && BillingReturnPagePaths.Contains(path);

    /// <summary>
    /// Return page of a paid Stripe Checkout: <paramref name="returnPath"/> (one of <see cref="BillingReturnPagePaths"/>)
    /// or the default plan page, with <c>?checkout=success</c>.
    /// </summary>
    public string BillingCheckoutSuccess(string? returnPath = null) => Build(BillingPage(returnPath) + "?checkout=success");

    /// <summary>Return page of an abandoned Stripe Checkout, like <see cref="BillingCheckoutSuccess"/> with <c>?checkout=cancel</c>.</summary>
    public string BillingCheckoutCancel(string? returnPath = null) => Build(BillingPage(returnPath) + "?checkout=cancel");

    /// <summary>Return page of the Stripe billing portal: <paramref name="returnPath"/> or the default plan page.</summary>
    public string BillingPortalReturn(string? returnPath = null) => Build(BillingPage(returnPath));

    /// <summary>
    /// True when <paramref name="url"/> is a page Stripe may return to: an absolute URL of the public web app
    /// (<see cref="IsOnPublicSite"/>) whose path is one of <see cref="BillingReturnPagePaths"/>; any query string is kept
    /// (e.g. <c>?checkout=success&amp;session_id={CHECKOUT_SESSION_ID}</c>).
    /// </summary>
    public bool IsBillingReturnUrl(string? url) =>
        IsOnPublicSite(url)
        && Uri.TryCreate(url!.Trim(), UriKind.Absolute, out var candidate)
        && IsBillingReturnPagePath(candidate.AbsolutePath);

    private static string BillingPage(string? returnPath)
    {
        if (string.IsNullOrWhiteSpace(returnPath))
            return BillingPagePath;

        return IsBillingReturnPagePath(returnPath)
            ? returnPath
            : throw new ArgumentException("The billing return page is not in the allow-list.", nameof(returnPath));
    }

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
