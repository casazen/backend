using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Whose name the withholding of a stay is reported on (E18/F01/US02, NF-117, ⚖ I6): the owner who was paid, except for a property of several co-owners. There the co-owner who
/// signed the mandate (<see cref="DocumentPlanRules.SignatoryOf"/>, the one rule of who signs: the mandate in force that day, then the largest share, then the oldest ownership, then
/// the smallest id) takes everything on its own name, <b>unless its mandate says <see cref="CoOwnerTaxReporting.PerShare"/></b>: then each co-owner keeps its share. The day is the
/// check-out day of the stay, as for the documents of the stay. The ownerships and the mandates of the properties are read once; the lookup then costs nothing.
/// </summary>
internal sealed class ReportingOwners
{
    private readonly ILookup<Guid, PropertyOwnership> _ownerships;
    private readonly IReadOnlyList<OwnerMandate> _mandates;

    private ReportingOwners(IReadOnlyList<PropertyOwnership> ownerships, IReadOnlyList<OwnerMandate> mandates)
    {
        _ownerships = ownerships.ToLookup(o => o.PropertyId);
        _mandates = mandates;
    }

    /// <summary>The ownerships of the properties and the mandates of their owners, of the org (every query names it).</summary>
    public static async Task<ReportingOwners> ReadAsync(AppDbContext db, Guid orgId, IReadOnlyCollection<Guid> propertyIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(propertyIds);

        if (propertyIds.Count == 0)
            return new ReportingOwners([], []);

        var ownerships = await db.PropertyOwnerships.AsNoTracking()
            .Where(o => o.OrgId == orgId && propertyIds.Contains(o.PropertyId))
            .ToListAsync(cancellationToken);
        var ownerIds = ownerships.Select(o => o.PropertyOwnerId).Distinct().ToList();
        var mandates = await db.OwnerMandates.AsNoTracking()
            .Where(m => m.OrgId == orgId && ownerIds.Contains(m.PropertyOwnerId))
            .ToListAsync(cancellationToken);
        return new ReportingOwners(ownerships, mandates);
    }

    /// <summary>
    /// The owner the evidence of <paramref name="ownerId"/> on a stay of <paramref name="propertyId"/> checking out on <paramref name="day"/> is reported on, and whether it is the
    /// share of a co-owner kept on its own name. The owner itself for a stay with no property, for a property of one owner on that day and for an owner that was not one of its
    /// owners then (the evidence is never moved on a guess).
    /// </summary>
    public (Guid OwnerId, bool OwnShare) ReportingOwnerOf(Guid ownerId, Guid? propertyId, DateOnly day)
    {
        if (propertyId is not { } property)
            return (ownerId, false);

        var active = _ownerships[property].Where(o => DocumentPlanRules.IsActiveOn(o.OwnershipStart, o.OwnershipEnd, day)).ToList();
        if (active.Select(o => o.PropertyOwnerId).Distinct().Count() < 2 || active.All(o => o.PropertyOwnerId != ownerId))
            return (ownerId, false);

        var signatory = DocumentPlanRules.SignatoryOf(
            [.. active.Select(o => new ActiveOwnership(o.Id, o.PropertyOwnerId, o.SharePercent, o.OwnershipStart))],
            _mandates,
            day)!.Value;
        var mandate = OwnerMandateRules.InForce(_mandates, signatory.OwnerId, signatory.OwnershipId, day);
        return mandate?.CoOwnerTaxReporting == CoOwnerTaxReporting.PerShare ? (ownerId, true) : (signatory.OwnerId, false);
    }
}
