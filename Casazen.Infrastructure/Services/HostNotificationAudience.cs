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
/// <para><b>The person in charge must still reach the property (AM-03b).</b> Being named is not enough: a notification carries the
/// name and email of the guest, the dates and the prices of a stay, so it goes only to someone who can open that stay. The
/// rule is the one that lets a person be put in charge (<c>OrgPropertyAccessService</c>): an active member of the org who is not a
/// collaborator limited to some properties, or who was given this one. An account that is in no org team (an owner of before the
/// team) reaches the property it created, as it always did, and only that. The access service also takes the responsibility
/// away when the access is taken away; this check is the second level, so a stale name (a write on another instance, a row
/// changed by hand) fails closed: nobody but the administrators is told.</para>
/// <para>One place for the push (<c>PushDeliveryJob</c>) and the emails (<c>BookingNotifier</c>), so the two cannot disagree.
/// The queries run in jobs with no tenant: every read says <c>IgnoreQueryFilters</c> and scopes to the org explicitly.</para>
/// </remarks>
internal static class HostNotificationAudience
{
    /// <summary>
    /// The users of <paramref name="orgId"/> to tell about the property <paramref name="propertyId"/>, whose person in charge is
    /// <paramref name="responsibleUserId"/> (<c>null</c> = nobody named) and whose creator is <paramref name="creatorUserId"/>.
    /// </summary>
    public static IQueryable<User> UsersToTell(
        AppDbContext db,
        Guid orgId,
        Guid propertyId,
        string? responsibleUserId,
        string creatorUserId)
    {
        // The creator stands in only while nobody is named: once someone is in charge, they are the ones told.
        var inCharge = responsibleUserId ?? creatorUserId;

        return db.Users
            .AsNoTracking()
            .Where(u => u.OrgId == orgId && u.IsActive)
            .Where(u => !db.OrgMembers.IgnoreQueryFilters().Any(m => m.UserId == u.Id && m.Status == OrgMemberStatus.Deactivated))
            .Where(u => (u.Id == inCharge
                         && (
                             // A member of the team reaches the property by its role, or by the grant of «Solo alcuni».
                             db.OrgMembers.IgnoreQueryFilters().Any(m =>
                                 m.UserId == u.Id
                                 && m.OrgId == orgId
                                 && m.Status == OrgMemberStatus.Active
                                 && (m.Role != OrgRole.Collaborator
                                     || m.PropertyScope != PropertyScope.Selected
                                     || db.PropertyMemberAccesses.IgnoreQueryFilters().Any(a =>
                                         a.UserId == u.Id && a.OrgId == orgId && a.PropertyId == propertyId)))
                             // An account in no team keeps the rule of before it: it reaches the property it created.
                             || (u.Id == creatorUserId && !db.OrgMembers.IgnoreQueryFilters().Any(m => m.UserId == u.Id))))
                        || db.OrgMembers.IgnoreQueryFilters().Any(m =>
                            m.UserId == u.Id
                            && m.OrgId == orgId
                            && m.Status == OrgMemberStatus.Active
                            && (m.Role == OrgRole.Owner || m.Role == OrgRole.Admin)));
    }
}
