using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.OrgTeam;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// What the services of the org team (AM-02) share: who the caller is in the org, the seats lock and the unique-violation
/// check. The tables are read with <c>IgnoreQueryFilters</c>, scoped to the org passed in: the org is the caller's, read from
/// its own member row by the controller, never from the client.
/// </summary>
internal static class OrgTeamAccess
{
    /// <summary>
    /// The caller's own member row, which must be active and of a role that manages people (<see cref="OrgTeamRules.CanManageTeam"/>).
    /// The policy of the endpoints already checked the permission; this is the service's own guard, on the member row, which
    /// is the source of truth. <see cref="UnauthorizedAccessException"/> is a generic 403.
    /// </summary>
    public static async Task<OrgMember> RequireManagerAsync(
        AppDbContext db,
        Guid orgId,
        string actorUserId,
        CancellationToken cancellationToken)
    {
        var actor = await db.OrgMembers.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(
                m => m.OrgId == orgId && m.UserId == actorUserId && m.Status == OrgMemberStatus.Active,
                cancellationToken);

        if (actor is null || !OrgTeamRules.CanManageTeam(actor.Role))
            throw new UnauthorizedAccessException("The caller does not manage the people of this org.");

        return actor;
    }

    /// <summary>
    /// The caller's own member row, which must be active, whatever its role (AM-02b: any member asks the administrators for
    /// access). <see cref="UnauthorizedAccessException"/> is a generic 403.
    /// </summary>
    public static async Task<OrgMember> RequireMemberAsync(
        AppDbContext db,
        Guid orgId,
        string userId,
        CancellationToken cancellationToken)
    {
        var member = await db.OrgMembers.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(
                m => m.OrgId == orgId && m.UserId == userId && m.Status == OrgMemberStatus.Active,
                cancellationToken);

        return member ?? throw new UnauthorizedAccessException("The caller is not an active member of this org.");
    }

    /// <summary>
    /// Opens the transaction that decides about the seats of <paramref name="orgId"/> (or joins the one open) and takes its
    /// seats lock first, then <paramref name="further"/> in the order given. The caller commits the transaction it gets
    /// back (<c>null</c> when it joined one, or off PostgreSQL).
    /// </summary>
    public static Task<IDbContextTransaction?> BeginSeatsTransactionAsync(
        AppDbContext db,
        Guid orgId,
        CancellationToken cancellationToken,
        params (PostgresAdvisoryLocks.Scope Scope, string Key)[] further) =>
        PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db,
            cancellationToken,
            [OrgSeatService.SeatsLock(orgId), .. further]);

    /// <summary>True when <paramref name="exception"/> is a PostgreSQL unique violation (23505), of the index <paramref name="constraint"/> when given.</summary>
    public static bool IsUniqueViolation(DbUpdateException exception, string? constraint = null) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres
        && (constraint is null || string.Equals(postgres.ConstraintName, constraint, StringComparison.Ordinal));
}
