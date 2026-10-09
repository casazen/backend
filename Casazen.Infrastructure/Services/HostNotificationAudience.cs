using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Who is told when something happens on a property of an org (AM-03): the member in charge of it
/// (<see cref="Property.ResponsibleUserId"/>) and the org's administrators (the owner and the <c>Admin</c>s). Not, as before,
/// whoever has a user role of <c>Admin</c> or <c>PropertyManager</c> (the old rule read <c>User.Role</c>, which says nothing about
/// an org team: a platform admin was in every org, a property manager of the team was in none).
/// </summary>
/// <remarks>
/// <para>While nobody is named responsible, the creator of the property (<see cref="Property.OwnerId"/>) stands in, so a manager
/// who added a property keeps hearing about it until someone is put in charge. A deactivated member is never told, and neither
/// is an account that is not active or not in the org. A person in charge who has since left leaves the administrators
/// (the owner at least) as the only ones to hear.</para>
/// <para>One place for the push (<c>PushDeliveryJob</c>) and the emails (<c>BookingNotifier</c>), so the two cannot disagree.
/// The queries run in jobs with no tenant: every read says <c>IgnoreQueryFilters</c> and scopes to the org explicitly.</para>
/// </remarks>
internal static class HostNotificationAudience
{
    /// <summary>
    /// The users of <paramref name="orgId"/> to tell about a property whose person in charge is
    /// <paramref name="responsibleUserId"/> (<c>null</c> = nobody named) and whose creator is <paramref name="creatorUserId"/>.
    /// </summary>
    public static IQueryable<User> UsersToTell(AppDbContext db, Guid orgId, string? responsibleUserId, string creatorUserId)
    {
        // The creator stands in only while nobody is named: once someone is in charge, they are the ones told.
        var inCharge = responsibleUserId ?? creatorUserId;

        return db.Users
            .AsNoTracking()
            .Where(u => u.OrgId == orgId && u.IsActive)
            .Where(u => !db.OrgMembers.IgnoreQueryFilters().Any(m => m.UserId == u.Id && m.Status == OrgMemberStatus.Deactivated))
            .Where(u => u.Id == inCharge
                        || db.OrgMembers.IgnoreQueryFilters().Any(m =>
                            m.UserId == u.Id
                            && m.OrgId == orgId
                            && m.Status == OrgMemberStatus.Active
                            && (m.Role == OrgRole.Owner || m.Role == OrgRole.Admin)));
    }
}
