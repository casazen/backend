using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IOwnerOnboardingService" />
/// <remarks>
/// <para><b>Where things are.</b> The import is in <c>OwnerOnboardingService.Import.cs</c> (its row rules in <c>OwnerImportRows.cs</c>); the
/// readiness in <c>OwnerOnboardingService.Readiness.cs</c>, on the pure rules of <see cref="OwnerReadinessRules"/> and
/// <see cref="OwnerOwnershipTimeline"/>. This file holds what both share: the clock and the owners inside the reach of the caller.</para>
/// <para><b>Reach.</b> The properties come from the org of the scope and from what the scope reaches (<c>InScope</c>). An owner is inside the
/// reach of an org-wide scope when it is an owner of the org; inside a partial one only when it holds a period on a property the scope reaches.
/// The ownership periods are read through their property, so a period on a property outside the reach is never seen, counted or changed.</para>
/// <para><b>Logs</b> carry the org and counts, never a name, an identifier, an address, an IBAN or a cell of the file.</para>
/// </remarks>
public sealed partial class OwnerOnboardingService(
    AppDbContext db,
    IActivityLog activityLog,
    TimeProvider timeProvider,
    ILogger<OwnerOnboardingService> logger) : IOwnerOnboardingService
{
    private DateOnly Today() => timeProvider.TodayInRomeAsDateOnly();

    private DateTime Now() => UtcDateTime.TruncateToMicroseconds(timeProvider.GetUtcNow().UtcDateTime);

    /// <summary>The first instant of a day, in Rome: what <see cref="PropertyOwnership.OwnershipStart"/> holds for a day (its Rome date is that day).</summary>
    internal static DateTime StartInstant(DateOnly day) => UtcDateTime.TruncateToMicroseconds(RomeCalendar.StartOfDayUtc(day));

    /// <summary>The last instant of a day, in Rome: what <see cref="PropertyOwnership.OwnershipEnd"/> holds for a last day, so the period is in force through all of it.</summary>
    internal static DateTime EndInstant(DateOnly day) => UtcDateTime.TruncateToMicroseconds(RomeCalendar.StartOfDayUtc(day.AddDays(1))).AddTicks(-10);

    /// <summary>
    /// The ownership periods inside the reach, read once: only those on a property the scope reaches (and that still exists). With an org-wide
    /// scope the query adds no join to the property, so a period on a deleted property is dropped here.
    /// </summary>
    private async Task<List<PeriodRow>> LoadPeriodsAsync(HostScope scope, ISet<Guid> reachablePropertyIds, CancellationToken ct)
    {
        var rows = await db.PropertyOwnerships
            .AsNoTracking()
            .Where(po => po.OrgId == scope.OrgId)
            .InScope(scope, po => po.Property)
            .Select(po => new PeriodRow(po.Id, po.PropertyId, po.PropertyOwnerId, po.SharePercent, po.OwnershipStart, po.OwnershipEnd))
            .ToListAsync(ct);

        return [.. rows.Where(r => reachablePropertyIds.Contains(r.PropertyId))];
    }

    /// <summary>
    /// The owners of the org inside the reach (not anonymized), as they are stored: the fiscal code, the VAT number, the IBAN and the BIC come back
    /// decrypted. An org-wide scope gets every owner; a partial one only the owners that hold a period on a property it reaches.
    /// </summary>
    private async Task<List<PropertyOwner>> LoadVisibleOwnersAsync(HostScope scope, CancellationToken ct)
    {
        var query = db.PropertyOwners.AsNoTracking().Where(o => o.OrgId == scope.OrgId && o.AnonymizedAt == null);
        if (!scope.IsOrgWide)
        {
            var reachablePeriods = db.PropertyOwnerships.Where(po => po.OrgId == scope.OrgId).InScope(scope, po => po.Property);
            query = query.Where(o => reachablePeriods.Any(po => po.PropertyOwnerId == o.Id));
        }

        return await query.ToListAsync(ct);
    }

    private static OwnershipPeriod ToPeriod(PeriodRow row) =>
        new(row.OwnerId, row.SharePercent, RomeCalendar.DateInRome(row.Start), row.End is { } end ? RomeCalendar.DateInRome(end) : null);

    /// <summary>One stored ownership period, as the readiness and the import read it.</summary>
    private sealed record PeriodRow(Guid Id, Guid PropertyId, Guid OwnerId, decimal SharePercent, DateTime Start, DateTime? End);
}
