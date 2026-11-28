using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Who the platform administrators are, the CasaZen staff: an account whose user role is <see cref="UserRole.Admin"/>, or that holds a membership of
/// the context of the platform administration (<see cref="ContextKey"/>, where the seed puts the role <c>platform_admin</c>). One query for every
/// place that asks, so they cannot disagree: the acceptance of an invitation (staff never join the org of a customer) and the audience of the
/// notifications to the staff (<c>PushAudienceKind.PlatformStaff</c>, read by <c>InAppNotificationJob</c>).
/// </summary>
/// <remarks>
/// <c>Users</c> and <c>UserContextMemberships</c> belong to no tenant, so the query reads the same rows in a request and in a job. It says nothing about
/// whether the account is active: the caller decides that (an inactive administrator is still staff for the invitation, and is never told).
/// </remarks>
internal static class PlatformAdministrators
{
    public const string ContextKey = "admin";

    public static IQueryable<User> Of(AppDbContext db) =>
        db.Users.Where(u =>
            u.Role == UserRole.Admin
            || db.UserContextMemberships.Any(m => m.UserId == u.Id && m.ContextKey == ContextKey));
}
