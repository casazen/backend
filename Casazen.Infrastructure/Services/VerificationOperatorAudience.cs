using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Who is told that the identity of a guest waits for an operator (E03/F04/US02/T03, AC-NF50-3, AC-NF24-3): the active users of the org who
/// <b>hold <c>verification.decision</c></b> in the short-rent context (the owner, the administrators, the property managers: the roles the seed
/// gives it to) <b>and reach the property</b> with the rule of the host scope (<c>HostScopeResolver</c>): an active member reaches it by its role,
/// a collaborator «Solo alcuni» only through a grant of the property. An account in no org team keeps the rule of before the team: it is told
/// about the property it created, as the owner it is (its permission comes from the token). Nobody else: a collaborator in charge of the
/// property, an accountant or a member of another org is never told.
/// </summary>
/// <remarks>
/// One place for the push (<c>PushDeliveryJob</c>), the bell (<c>InAppNotificationJob</c>) and the e-mails (<see cref="VerificationOperatorAlerts"/>),
/// so the three cannot disagree. The query runs in jobs and in the anonymous guest request, with no tenant: every read ignores the query filters
/// and scopes to the org explicitly.
/// </remarks>
internal static class VerificationOperatorAudience
{
    /// <summary>The users of <paramref name="orgId"/> who decide the verifications of the property <paramref name="propertyId"/>, created by <paramref name="creatorUserId"/>.</summary>
    public static IQueryable<User> UsersToTell(AppDbContext db, Guid orgId, Guid propertyId, string creatorUserId)
    {
        const string permission = HostPermissions.VerificationDecision;
        const string shortRent = OrgRoleCatalog.ShortRent;

        return db.Users
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(u => u.OrgId == orgId && u.IsActive)
            .Where(u =>
                // A member of the team: it holds the permission through its short-rent role and reaches the property.
                (db.UserContextMemberships.Any(c =>
                     c.UserId == u.Id
                     && c.ContextKey == shortRent
                     && db.RolePermissions.Any(rp => rp.RoleId == c.RoleId && rp.PermissionKey == permission))
                 && db.OrgMembers.IgnoreQueryFilters().Any(m =>
                     m.UserId == u.Id
                     && m.OrgId == orgId
                     && m.Status == OrgMemberStatus.Active
                     && (m.Role != OrgRole.Collaborator
                         || m.PropertyScope != PropertyScope.Selected
                         || db.PropertyMemberAccesses.IgnoreQueryFilters().Any(a =>
                             a.UserId == u.Id && a.OrgId == orgId && a.PropertyId == propertyId))))
                // An account in no team: the creator of the property, as before the team, when its short-rent role holds the permission
                // or it has no such role yet (the owner on its token, ContextAccessBootstrap).
                || (u.Id == creatorUserId
                    && !db.OrgMembers.IgnoreQueryFilters().Any(m => m.UserId == u.Id)
                    && (!db.UserContextMemberships.Any(c => c.UserId == u.Id && c.ContextKey == shortRent)
                        || db.UserContextMemberships.Any(c =>
                            c.UserId == u.Id
                            && c.ContextKey == shortRent
                            && db.RolePermissions.Any(rp => rp.RoleId == c.RoleId && rp.PermissionKey == permission)))));
    }
}
