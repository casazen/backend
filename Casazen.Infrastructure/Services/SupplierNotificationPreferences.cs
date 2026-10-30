using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// <b>The one place</b> that decides whether an informative notice goes to a supplier (SP-16d): it reads the supplier's switches
/// (<c>SupplierSettings.NotifyNewRequests</c>, <c>NotifyDayBeforeReminder</c>, <c>NotifyPaymentMarked</c>, written by <c>PUT api/supplier/settings</c>)
/// and every path that sends one of the three kinds (<see cref="SupplierNotificationKind"/>) asks it first, so a switch cannot be forgotten by a path
/// written later: <c>SupplierNotifyingPathsArchitectureTests</c> lists those paths and fails when one does not go through here.
/// </summary>
/// <remarks>
/// The operational notices (a cancellation, the answer to a proposed time or to a quote, an expiry, a payment that failed or was refunded, a reminder
/// from the host) do not ask: the supplier has to act on them, and no switch hides them. A supplier that has not saved its settings has no row
/// (<c>SupplierSettings</c> is lazy) and gets the defaults, which are all on (<see cref="SupplierNotificationRules"/>). The table is keyed by the
/// supplier org and not tenant-filtered, so the org is always an explicit predicate.
/// </remarks>
public static class SupplierNotificationPreferences
{
    /// <summary>True when the supplier <paramref name="supplierOrgId"/> wants the notice of <paramref name="kind"/>.</summary>
    public static async Task<bool> AllowsAsync(
        AppDbContext db,
        Guid supplierOrgId,
        SupplierNotificationKind kind,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var settings = await db.SupplierSettings
            .AsNoTracking()
            .Where(s => s.OrgId == supplierOrgId)
            .FirstOrDefaultAsync(cancellationToken);
        return SupplierNotificationRules.Allows(settings, kind);
    }
}
