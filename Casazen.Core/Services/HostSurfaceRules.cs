using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// What an org's host serves besides the booking site (E40/F01/US01, NF-140..NF-142, NF-144): one rule, one place. The resolver
/// (<c>resolve-host</c>), the domain settings, the CORS of the owner portal and the links of the e-mails all call <see cref="Evaluate"/>, so
/// they cannot disagree (the same idea as <c>PublicHostResolver</c>, the single decision point of «does this host serve an org's site»).
/// </summary>
/// <remarks>
/// Only a served custom domain carries surfaces: the Auth0 hosts of the owner sign-in are registered per custom domain, so a platform
/// subdomain (<see cref="PublicHostMode.CasazenSubdomain"/>) never gets any. The plan follows the domain's: the caller passes
/// <see cref="PublicHostMode.CustomDomain"/> only for a verified domain the org pays for (the one the resolver serves).
/// </remarks>
public static class HostSurfaceRules
{
    /// <param name="org">The org, with its stored choices (<see cref="Org.GuestAreaOnOwnDomain"/>, <see cref="Org.OwnerPortalOnOwnDomain"/>, <see cref="Org.UseOwnDomainForLinks"/>, <see cref="Org.OwnerLoginHostState"/>).</param>
    /// <param name="servedMode">How the org's host is really served: <see cref="PublicHostMode.CustomDomain"/> only for a verified custom domain that is paid for.</param>
    /// <param name="whiteLabelHost">The flag <c>WhiteLabelHost</c>.</param>
    /// <param name="siteEmbed">The flag <c>SiteEmbed</c>.</param>
    public static HostSurfaces Evaluate(Org org, PublicHostMode servedMode, bool whiteLabelHost, bool siteEmbed)
    {
        ArgumentNullException.ThrowIfNull(org);

        var ownDomain = whiteLabelHost && servedMode == PublicHostMode.CustomDomain;
        var guestArea = ownDomain && org.GuestAreaOnOwnDomain;
        var ownerPortal = ownDomain && org.OwnerPortalOnOwnDomain;
        var ownerLoginReady = ownerPortal && org.OwnerLoginHostState == OwnerLoginHostState.Ready;
        var useOwnDomainForLinks = ownDomain && org.UseOwnDomainForLinks;

        // An owner link moves only where the owner can sign in: never to a page that says «not yet enabled».
        return new HostSurfaces(
            GuestArea: guestArea,
            OwnerPortal: ownerPortal,
            OwnerLoginReady: ownerLoginReady,
            UseOwnDomainForLinks: useOwnDomainForLinks,
            GuestLinksOnOwnDomain: useOwnDomainForLinks && guestArea,
            OwnerLinksOnOwnDomain: useOwnDomainForLinks && ownerLoginReady,
            EmbedEnabled: siteEmbed);
    }
}

/// <summary>The effective surfaces of an org's host (<see cref="HostSurfaceRules.Evaluate"/>).</summary>
/// <param name="GuestArea">The guest area (check-in, booking area) is served on the host.</param>
/// <param name="OwnerPortal">The owner portal is served on the host.</param>
/// <param name="OwnerLoginReady">The owner sign-in works on the host: the owner portal is on and the host is registered in Auth0.</param>
/// <param name="UseOwnDomainForLinks">The agency's «use my domain for links» is in effect (the domain is served and the flag is on).</param>
/// <param name="GuestLinksOnOwnDomain">The guest links (check-in, booking area, booking confirmation) are built on the host.</param>
/// <param name="OwnerLinksOnOwnDomain">The owner links (invitations, conversations) are built on the host.</param>
/// <param name="EmbedEnabled">The embeddable pages are on (the flag <c>SiteEmbed</c>).</param>
public sealed record HostSurfaces(
    bool GuestArea,
    bool OwnerPortal,
    bool OwnerLoginReady,
    bool UseOwnDomainForLinks,
    bool GuestLinksOnOwnDomain,
    bool OwnerLinksOnOwnDomain,
    bool EmbedEnabled);
