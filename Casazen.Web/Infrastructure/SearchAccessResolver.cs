using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Search;
using Casazen.Web.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace Casazen.Web.Infrastructure;

public sealed record SearchAccess(HostSearchAccess? Host, Guid? SupplierOrgId)
{
    public bool IsEmpty => Host is null && SupplierOrgId is null;
}

/// <summary>
/// Decides which groups of the global search the caller may read (UI-13a): the same policies as the endpoints that list the same
/// objects (<see cref="CasazenPolicies.BookingRead"/>, <see cref="CasazenPolicies.GuestRead"/>, the property read of each rental
/// context, <see cref="CasazenPolicies.LeaseRead"/>, <see cref="CasazenPolicies.Supplier"/>), evaluated for the caller, and the reach
/// of the caller on the properties of its org (<see cref="IHostScopeResolver"/>, from the database, never widened). The search service
/// reads only what this says.
/// </summary>
public interface ISearchAccessResolver
{
    Task<SearchAccess> ResolveAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default);
}

public sealed class SearchAccessResolver(
    IAuthorizationService authorization,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    ISupplierOrgContextResolver supplierOrgContextResolver) : ISearchAccessResolver
{
    public async Task<SearchAccess> ResolveAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        var shortRentProperties = await CanAsync(user, CasazenPolicies.PropertyRead);
        var longRentProperties = await CanAsync(user, CasazenPolicies.LongRentPropertyRead);
        var bookings = await CanAsync(user, CasazenPolicies.BookingRead);
        var guests = await CanAsync(user, CasazenPolicies.GuestRead);
        var leases = await CanAsync(user, CasazenPolicies.LeaseRead);

        HostSearchAccess? host = null;
        if (shortRentProperties || longRentProperties || bookings || guests || leases)
        {
            // The org is looked up only for a caller with a host permission: a supplier-only account has none, and its search
            // would otherwise log "no org yet" at every pause in typing.
            if (await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken) is { } orgId
                && await hostScopeResolver.ResolveHostScopeAsync(user, orgId, cancellationToken) is { } scope)
            {
                host = new HostSearchAccess(scope, shortRentProperties, longRentProperties, bookings, guests, leases);
            }
        }

        Guid? supplierOrgId = null;
        if (await CanAsync(user, CasazenPolicies.Supplier))
            supplierOrgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);

        return new SearchAccess(host, supplierOrgId);
    }

    private async Task<bool> CanAsync(ClaimsPrincipal user, string policy) =>
        (await authorization.AuthorizeAsync(user, policy)).Succeeded;
}
