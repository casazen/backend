using Casazen.Core.Features;
using Casazen.Core.GuestPortal;
using Casazen.Infrastructure.Email;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.GuestPortal;

/// <summary>
/// The address of the stay link of a booking, for the e-mails and the chat messages that carry it (E50/F01/US03/T03, NF-152, decision A2): the confirmation, «Prepara il tuo arrivo» and the message of
/// the check-in link in the chat of a portal. It is the one place where those messages get the link: <see cref="IGuestPortalLinkService.GetOrCreateAsync"/> then
/// <see cref="IGuestPortalLinkService.BuildUrl"/>, never an address built by a template. The services that send them take it as an optional dependency: without it, or while
/// <c>Features:GuestPortal</c> is off, the message is the one it has always been.
/// </summary>
public interface IGuestStayLinkProvider
{
    /// <summary>
    /// The address of the stay link of the booking (made now when it has none), or <c>null</c>: while <c>Features:GuestPortal</c> is off, when the booking may not have a link (a request, a
    /// cancelled booking, a no-show, a stay over by 30 days), when the public address of the web app is not configured and when the link cannot be made (logged). Never throws except for a
    /// cancellation: a message that cannot carry the link goes out without it, never lost for it.
    /// </summary>
    Task<string?> UrlAsync(Guid orgId, Guid bookingId, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IGuestStayLinkProvider"/>
/// <remarks>The org is named with the booking: a booking that is not the org's gives no link. The logs carry the ids of the booking and of the org, never the address or the token.</remarks>
public sealed class GuestStayLinkProvider(
    IFeatureFlags featureFlags,
    IGuestPortalLinkService links,
    PublicSiteLinks siteLinks,
    ILogger<GuestStayLinkProvider> logger) : IGuestStayLinkProvider
{
    public async Task<string?> UrlAsync(Guid orgId, Guid bookingId, CancellationToken cancellationToken = default)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.GuestPortal))
            return null;

        // No public address, no link: a row is not made for an address that cannot be written.
        if (!siteLinks.IsConfigured)
        {
            logger.LogWarning("The stay link of booking {BookingId} of org {OrgId} was not made for a message: App:PublicSiteBaseUrl is not configured", bookingId, orgId);
            return null;
        }

        try
        {
            var result = await links.GetOrCreateAsync(orgId, bookingId, createdByUserId: null, cancellationToken);
            return result is null ? null : links.BuildUrl(result.Link);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The stay link of booking {BookingId} of org {OrgId} could not be made for a message: it goes without", bookingId, orgId);
            return null;
        }
    }
}

/// <summary>The one way the services read the stay link when it is an optional dependency of theirs.</summary>
public static class GuestStayLinkProviderExtensions
{
    /// <summary>The address of the stay link of the booking, or <c>null</c> when the service was built without a provider (an older test, a tool): the message without it.</summary>
    public static async Task<string?> UrlOrNullAsync(
        this IGuestStayLinkProvider? provider,
        Guid orgId,
        Guid bookingId,
        CancellationToken cancellationToken = default)
    {
        if (provider is null)
            return null;

        return await provider.UrlAsync(orgId, bookingId, cancellationToken);
    }
}
