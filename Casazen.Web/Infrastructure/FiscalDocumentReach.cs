using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// Who reaches a fiscal document, the same for the controllers that read or change it (<c>FiscalDocumentsController</c>, <c>FiscalDocumentSdiController</c>):
/// through its booking, or the whole org for a document without a booking.
/// </summary>
internal static class FiscalDocumentReach
{
    /// <summary>The document when the caller reaches it: through its booking, or the whole org for a document without a booking.</summary>
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

    /// <summary>The caller may do <paramref name="operation"/> on the booking: the booking exists in its org and the resource check allows it.</summary>
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
