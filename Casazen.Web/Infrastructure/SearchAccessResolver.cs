using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Search;
using Casazen.Web.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace Casazen.Web.Infrastructure;

/// <summary>What the caller may search (UI-13a): as a host (its scope and the permissions), as a supplier (its supplier org), or nothing.</summary>
/// <param name="Host">The host side, null when the caller holds none of the host permissions of the search or has no reach on its org.</param>
/// <param name="SupplierOrgId">The supplier org whose inbox is searched, null when the caller does not work as a supplier.</param>
public sealed record SearchAccess(HostSearchAccess? Host, Guid? SupplierOrgId)
{
    /// <summary>True when there is nothing the caller may search.</summary>
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
    /// <summary>The access of <paramref name="user"/>; never throws for a user with no permission (the answer is empty).</summary>
    Task<SearchAccess> ResolveAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ISearchAccessResolver" />
public sealed class SearchAccessResolver(
    IAuthorizationService authorization,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    ISupplierOrgContextResolver supplierOrgContextResolver) : ISearchAccessResolver
{
    /// <inheritdoc />
    public async Task<SearchAccess> ResolveAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        // Each permission counts in the context that grants it: property.read of the short-rent context says nothing about the
        // long-rent properties (CasazenPolicies). The decisions come from one authorization snapshot, cached for a few seconds.
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
