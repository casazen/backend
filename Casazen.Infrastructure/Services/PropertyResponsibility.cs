using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Takes the responsibility for properties away from a person who no longer reaches them (AM-03b). A property names the member
/// in charge of it (<see cref="Casazen.Core.Entities.Property.ResponsibleUserId"/>); that member is told, with the org's
/// administrators, what happens on the property, and what is told is the name and email of a guest, the dates and the prices of
/// a stay. When the access to the property is taken away (the owner narrows the properties of a collaborator, or removes the
/// person from the org) the person must not stay in charge of what they cannot open any more: the responsibility goes back to
/// nobody, which is the state of a property nobody was put in charge of, where the creator stands in while it reaches the
/// property and the owner and the administrators are always told (<c>HostNotificationAudience</c>).
/// </summary>
/// <remarks>
/// Written under the org's people lock, in the same <c>SaveChanges</c> as the change of the access, so there is no instant in
/// which the access is gone and the name is still there; <c>OrgPropertyAccessService.SetResponsibleAsync</c> takes the same lock,
/// so a person cannot be put in charge of a property in the middle of its revocation. The audience reads the reach again anyway.
/// </remarks>
internal static class PropertyResponsibility
{
    /// <summary>
    /// Stages the end of the responsibility of <paramref name="userId"/> for the properties of <paramref name="orgId"/> that are not
    /// in <paramref name="keep"/> (the ones the person still reaches; empty = none): <c>ResponsibleUserId</c> goes back to
    /// <c>null</c>. The caller saves. Soft-deleted properties are included, so one that comes back does not bring the old name
    /// with it. Returns the ids of the properties released, for the log.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> ReleaseAsync(
        AppDbContext db,
        Guid orgId,
        string userId,
        IReadOnlyCollection<Guid> keep,
        DateTime now,
        CancellationToken cancellationToken)
    {
        // IgnoreQueryFilters: scoped to the org the caller passes, which is its own (never a value of the request).
        var query = db.Properties.IgnoreQueryFilters().Where(p => p.OrgId == orgId && p.ResponsibleUserId == userId);
        if (keep.Count > 0)
        {
            var kept = keep.ToList();
            query = query.Where(p => !kept.Contains(p.Id));
        }

        var released = await query.ToListAsync(cancellationToken);
        foreach (var property in released)
        {
            property.ResponsibleUserId = null;
            property.UpdatedAt = now;
        }

        return released.Select(p => p.Id).ToList();
    }

    /// <summary>
    /// A property that changes mode can stop being reachable for the person in charge of it (AM-02c): a collaborator «Solo alcuni»
    /// that works in one area only reaches the given properties of the mode of that area. Stages the end of that responsibility on
    /// <paramref name="property"/> (tracked; the caller saves) and says whether it did. Everybody else keeps the name: a member that
    /// reaches every property by its role or its scope, one that works in both areas, one with no area known (the rule of
    /// <see cref="OrgRoleCatalog.SingleModeOf"/>, the same as the lists and the snapshot apply).
    /// </summary>
    public static async Task<bool> ReleaseIfOutsideItsAreasAsync(
        AppDbContext db,
        Property property,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (property.ResponsibleUserId is not { } responsible)
            return false;

        // IgnoreQueryFilters: scoped to the org of the property, which is the one the caller works in (a job, with no tenant).
        var limited = await db.OrgMembers.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(
                m => m.UserId == responsible
                     && m.OrgId == property.OrgId
                     && m.Role == OrgRole.Collaborator
                     && m.PropertyScope == PropertyScope.Selected,
                cancellationToken);
        if (!limited)
            return false;

        var areas = await db.UserContextMemberships.AsNoTracking()
            .Where(c => c.UserId == responsible)
            .Select(c => c.ContextKey)
            .ToListAsync(cancellationToken);
        if (OrgRoleCatalog.SingleModeOf(areas) is not { } worksIn || worksIn == property.RentalMode)
            return false;

        property.ResponsibleUserId = null;
        property.UpdatedAt = now;
        return true;
    }
}
