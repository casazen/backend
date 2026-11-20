using Casazen.Core.GuestPortal;
using Casazen.Infrastructure.GuestPortal;

namespace Casazen.Web.Extensions;

/// <summary>
/// The one place where the guest portal (epics E50-E60, casazen/backend#987) is registered: <c>Program.cs</c> calls <see cref="AddGuestPortal"/> once.
/// Nothing here is conditional: what is not ready yet sits behind <c>Features:GuestPortal</c> (and its two companion flags) where it is used, the
/// endpoints, not by being left out of DI.
/// </summary>
public static class GuestPortalServiceCollectionExtensions
{
    /// <summary>The services of the guest portal.</summary>
    public static IServiceCollection AddGuestPortal(this IServiceCollection services)
    {
        // E50/F01/US01/T01 (NF-152): the stay link of a booking, made on request, regenerated and revoked by the host, resolved from its token.
        services.AddScoped<IGuestPortalLinkService, GuestPortalLinkService>();

        return services;
    }
}
