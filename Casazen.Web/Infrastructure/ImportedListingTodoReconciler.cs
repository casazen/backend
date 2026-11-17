using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Infrastructure;

/// <summary>Imported listings stay to review until the property has actually been activated, including old reminders.</summary>
public sealed class ImportedListingTodoReconciler(AppDbContext db, IInternalActivityService activities)
{
    public async Task ReconcileAsync(HostScope scope, CancellationToken ct)
    {
        var reviewed = await db.InternalActivities.AsNoTracking()
            .Where(a => a.OrgId == scope.OrgId && a.Action == ActivityAction.CheckImportedListing
                        && a.EntityType == "Property" && a.EntityId == a.PropertyId
                        && a.CompletedAt == null && a.Property != null && a.Property.OrgId == scope.OrgId
                        && a.Property.IsActive)
            .InScope(scope)
            .Select(a => a.Id)
            .ToListAsync(ct);

        foreach (var id in reviewed)
            await activities.CloseAsync(id, InternalActivity.ClosedBySystem, ct);
    }
}
