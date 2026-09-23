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

    /// <summary>Supplier registration page of the web app, pre-filled from the invite.</summary>
    public string SupplierInviteSignup(Guid inviteId, string email, string comuneCode) =>
        Build(
            $"/register?inviteToken={inviteId}&email={Uri.EscapeDataString(email)}&comune={Uri.EscapeDataString(comuneCode)}");

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
