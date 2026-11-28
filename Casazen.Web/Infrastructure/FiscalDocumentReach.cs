using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace Casazen.Web.Infrastructure;

internal static class FiscalDocumentReach
{
    public static async Task<FiscalDocument?> ReachableAsync(
        ClaimsPrincipal user,
        Guid id,
        HostOperationRequirement operation,
        IFiscalDocumentService documents,
        IHostResourceLookup hostResources,
        IAuthorizationService authorizationService,
        IOrgContextResolver orgContextResolver,
        IHostScopeResolver hostScopeResolver,
        CancellationToken ct)
    {
        var document = await documents.GetAsync(id, ct);
        if (document is null)
            return null;

        if (document.BookingId is { } bookingId)
            return await CanOnBookingAsync(user, bookingId, operation, hostResources, authorizationService, ct) ? document : null;

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(ct);
        return orgId is not null
               && document.OrgId == orgId.Value
               && await hostScopeResolver.ResolveHostScopeAsync(user, orgId.Value, ct) is { IsOrgWide: true }
            ? document
            : null;
    }

    public static async Task<bool> CanOnBookingAsync(
        ClaimsPrincipal user,
        Guid bookingId,
        HostOperationRequirement operation,
        IHostResourceLookup hostResources,
        IAuthorizationService authorizationService,
        CancellationToken ct)
    {
        var booking = await hostResources.ForBookingAsync(bookingId, ct);
        return booking is not null && await authorizationService.IsAuthorizedAsync(user, booking, operation);
    }
}
