using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Whether a member of an org can be given the work of a property (E24/F01/US01, NF-120): an active member of the org, with an active account, who reaches the
/// property. The rule is the one that lets a person be put in charge of a property (<c>OrgPropertyAccessService.SetResponsibleAsync</c>, AM-03b, AM-02c): every
/// property for a member that is not a collaborator «Solo alcuni», the given ones of a mode it works in for one that is. The setup of the cleaner of a property and
/// the automation that creates the cleaning both ask it, so a member deactivated, or who lost the access to the property, never gets a cleaning.
/// </summary>
internal static class PropertyStaffReach
{
    /// <summary>
    /// True when <paramref name="userId"/> is an active member of <paramref name="orgId"/> with an active account and reaches the property
    /// <paramref name="propertyId"/> (of rental mode <paramref name="propertyMode"/>). Every read names the org: the caller is a request of the org or a handler of
    /// the bus working for it, and the tables are read with the filters lifted to name it explicitly.
    /// </summary>
    public static async Task<bool> CanBeGivenWorkAsync(
        AppDbContext db,
        Guid orgId,
        Guid propertyId,
        RentalMode propertyMode,
        string userId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return false;

        var member = await db.OrgMembers.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.UserId == userId && m.OrgId == orgId && m.Status == OrgMemberStatus.Active)
            .Select(m => new { m.Role, m.PropertyScope })
            .FirstOrDefaultAsync(cancellationToken);
        if (member is null)
            return false;

        var activeAccount = await db.Users.AsNoTracking()
            .AnyAsync(u => u.Id == userId && u.OrgId == orgId && u.IsActive, cancellationToken);
        if (!activeAccount)
            return false;

        if (member.Role != OrgRole.Collaborator || member.PropertyScope != PropertyScope.Selected)
            return true;

        var areas = await db.UserContextMemberships.AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => m.ContextKey)
            .ToListAsync(cancellationToken);
        var inItsAreas = OrgRoleCatalog.SingleModeOf(areas) is not { } worksIn || worksIn == propertyMode;

        return inItsAreas
               && await db.PropertyMemberAccesses.IgnoreQueryFilters().AsNoTracking()
                   .AnyAsync(a => a.UserId == userId && a.OrgId == orgId && a.PropertyId == propertyId, cancellationToken);
    }
}
