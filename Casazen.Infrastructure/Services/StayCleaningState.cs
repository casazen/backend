using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Where the cleaning of a stay stands, read the same way by the check-out, the booking flow and the cockpit (casazen/backend#978): a supplier's request of the stay in the
/// cleaning category, or a staff activity of type Cleaning on the booking (what the automation of the cleaning at check-out creates, and what the host asks by hand).
/// Every read names the org and ignores the tenant filter, so a job with no tenant reads the same rows.
/// </summary>
internal static class StayCleaningState
{
    /// <summary>A cleaning of the stay is planned: a request that was not declined or cancelled, or a staff activity that was not cancelled.</summary>
    internal static async Task<bool> IsPlannedAsync(AppDbContext db, Guid orgId, Guid bookingId, CancellationToken cancellationToken) =>
        await db.ServiceRequests
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .AnyAsync(
                r => r.OrgId == orgId
                     && r.BookingId == bookingId
                     && r.Category == ServiceCategories.Cleaning
                     && r.Status != ServiceRequestStatus.Annullato
                     && r.Status != ServiceRequestStatus.Rifiutato,
                cancellationToken)
        || await db.InternalActivities
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .AnyAsync(
                a => a.OrgId == orgId
                     && a.BookingId == bookingId
                     && a.Action == null
                     && a.ActivityType == InternalActivity.CleaningActivityType
                     && a.Status != InternalActivity.StatusCancelled,
                cancellationToken);

    /// <summary>A cleaning of the stay is done: the supplier completed the request (or it is paid), or the staff member completed the activity.</summary>
    internal static async Task<bool> IsDoneAsync(AppDbContext db, Guid orgId, Guid bookingId, CancellationToken cancellationToken) =>
        await db.ServiceRequests
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .AnyAsync(
                r => r.OrgId == orgId
                     && r.BookingId == bookingId
                     && r.Category == ServiceCategories.Cleaning
                     && (r.Status == ServiceRequestStatus.Completato || r.Status == ServiceRequestStatus.Pagato),
                cancellationToken)
        || await db.InternalActivities
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .AnyAsync(
                a => a.OrgId == orgId
                     && a.BookingId == bookingId
                     && a.Action == null
                     && a.ActivityType == InternalActivity.CleaningActivityType
                     && a.Status == InternalActivity.StatusCompleted,
                cancellationToken);
}
