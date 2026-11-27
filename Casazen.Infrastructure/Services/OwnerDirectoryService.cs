using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IOwnerDirectoryService"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>One statement.</b> The owners and, for each, the number of its properties, the kind and the currency of its fiscal profile and the mandate in force are one
/// projected query: correlated scalar sub-queries, never a read for each owner and never an entity with its encrypted identifiers (only the columns of the list are read).
/// The profile in force is the one that covers today, else the newest (the rule of the page of the owner); the mandate in force is the owner's own (no ownership) that
/// covers today, the latest start when more do: <see cref="OwnerMandateRules.InForce"/> says it in memory and a test keeps the two in step.</item>
/// <item><b>Reach.</b> See <see cref="IOwnerDirectoryService"/>: a partial scope keeps only the owners with at least one period, every period on a property it reaches
/// (<c>InScope</c> on the properties, the one way a list applies the reach), exactly what <see cref="IOwnerMandateService"/> lets the caller open.</item>
/// <item><b>Logs</b> carry the org, the reach and the count: never a name, an address or an IBAN.</item>
/// </list>
/// </remarks>
public sealed class OwnerDirectoryService(AppDbContext db, TimeProvider time, ILogger<OwnerDirectoryService> logger) : IOwnerDirectoryService
{
    public async Task<IReadOnlyList<OwnerDirectoryRow>> ListAsync(HostScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var orgId = scope.OrgId;
        var today = time.TodayInRomeAsDateOnly();

        // An ownership is stored with the last instant of its last day (Rome): it is not ended today when it has no end or its end is not before the first instant of today.
        var startOfToday = RomeCalendar.StartOfDayUtc(today);
        var existing = db.Properties.Where(p => p.OrgId == orgId).Select(p => p.Id);

        var owners = db.PropertyOwners.AsNoTracking().Where(o => o.OrgId == orgId);
        if (!scope.IsOrgWide)
        {
            // Every period of the owner on a property the scope reaches, and at least one: an owner with no period is never in a partial reach.
            var reachable = db.Properties.Where(p => p.OrgId == orgId).InScope(scope).Select(p => p.Id);
            owners = owners.Where(o =>
                db.PropertyOwnerships.Any(po => po.OrgId == orgId && po.PropertyOwnerId == o.Id)
                && !db.PropertyOwnerships.Any(po => po.OrgId == orgId && po.PropertyOwnerId == o.Id && !reachable.Contains(po.PropertyId)));
        }

        var rows = await owners
            .OrderBy(o => o.LastName)
            .ThenBy(o => o.FirstName)
            .ThenBy(o => o.Id)
            .Select(o => new
            {
                o.Id,
                o.FirstName,
                o.LastName,
                o.Email,
                o.Phone,
                o.TaxRegime,
                o.Iban,
                o.CreatedAt,
                PropertyCount = db.PropertyOwnerships
                    .Where(po => po.OrgId == orgId && po.PropertyOwnerId == o.Id && (po.OwnershipEnd == null || po.OwnershipEnd >= startOfToday) && existing.Contains(po.PropertyId))
                    .Select(po => po.PropertyId)
                    .Distinct()
                    .Count(),

                // The profile in force today, else the newest: one ordered sub-query for each of the two facts, so a missing profile is a plain null and never a half-built object.
                EntityKind = db.OwnerFiscalProfiles
                    .Where(p => p.OrgId == orgId && p.PropertyOwnerId == o.Id)
                    .OrderByDescending(p => p.ValidFrom <= today && (p.ValidTo == null || p.ValidTo >= today))
                    .ThenByDescending(p => p.ValidFrom)
                    .Select(p => (OwnerEntityKind?)p.EntityKind)
                    .FirstOrDefault(),
                SettlementCurrency = db.OwnerFiscalProfiles
                    .Where(p => p.OrgId == orgId && p.PropertyOwnerId == o.Id)
                    .OrderByDescending(p => p.ValidFrom <= today && (p.ValidTo == null || p.ValidTo >= today))
                    .ThenByDescending(p => p.ValidFrom)
                    .Select(p => p.SettlementCurrency)
                    .FirstOrDefault(),

                // The mandate in force today: the owner's own versions (no ownership), the latest start. Its id says whether there is one; the other facts are of that same version.
                MandateId = db.OwnerMandates
                    .Where(m => m.OrgId == orgId && m.PropertyOwnerId == o.Id && m.PropertyOwnershipId == null && m.ValidFrom <= today && (m.ValidTo == null || m.ValidTo >= today))
                    .OrderByDescending(m => m.ValidFrom)
                    .Select(m => (Guid?)m.Id)
                    .FirstOrDefault(),
                MandateFrom = db.OwnerMandates
                    .Where(m => m.OrgId == orgId && m.PropertyOwnerId == o.Id && m.PropertyOwnershipId == null && m.ValidFrom <= today && (m.ValidTo == null || m.ValidTo >= today))
                    .OrderByDescending(m => m.ValidFrom)
                    .Select(m => (DateOnly?)m.ValidFrom)
                    .FirstOrDefault(),
                MandateTo = db.OwnerMandates
                    .Where(m => m.OrgId == orgId && m.PropertyOwnerId == o.Id && m.PropertyOwnershipId == null && m.ValidFrom <= today && (m.ValidTo == null || m.ValidTo >= today))
                    .OrderByDescending(m => m.ValidFrom)
                    .Select(m => m.ValidTo)
                    .FirstOrDefault(),
                MandatePercent = db.OwnerMandates
                    .Where(m => m.OrgId == orgId && m.PropertyOwnerId == o.Id && m.PropertyOwnershipId == null && m.ValidFrom <= today && (m.ValidTo == null || m.ValidTo >= today))
                    .OrderByDescending(m => m.ValidFrom)
                    .Select(m => (decimal?)m.CommissionPercent)
                    .FirstOrDefault(),
                MandateBase = db.OwnerMandates
                    .Where(m => m.OrgId == orgId && m.PropertyOwnerId == o.Id && m.PropertyOwnershipId == null && m.ValidFrom <= today && (m.ValidTo == null || m.ValidTo >= today))
                    .OrderByDescending(m => m.ValidFrom)
                    .Select(m => (CommissionBase?)m.CommissionBase)
                    .FirstOrDefault(),
                MandateIncludesVat = db.OwnerMandates
                    .Where(m => m.OrgId == orgId && m.PropertyOwnerId == o.Id && m.PropertyOwnershipId == null && m.ValidFrom <= today && (m.ValidTo == null || m.ValidTo >= today))
                    .OrderByDescending(m => m.ValidFrom)
                    .Select(m => (bool?)m.CommissionIncludesVat)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        logger.LogInformation("Owner list of org {OrgId} read: {Count} owners, {Reach}", orgId, rows.Count, scope.IsOrgWide ? "whole org" : "partial reach");
        return
        [
            .. rows.Select(r => new OwnerDirectoryRow(
                r.Id,
                r.FirstName,
                r.LastName,
                r.Email,
                r.Phone,
                r.TaxRegime,
                r.Iban,
                r.CreatedAt,
                r.PropertyCount,
                r.EntityKind,
                r.SettlementCurrency,
                r.MandateId is { } mandateId && r.MandateFrom is { } validFrom && r.MandatePercent is { } percent && r.MandateBase is { } commissionBase && r.MandateIncludesVat is { } includesVat
                    ? new OwnerDirectoryMandate(mandateId, validFrom, r.MandateTo, percent, commissionBase, includesVat)
                    : null)),
        ];
    }
}
