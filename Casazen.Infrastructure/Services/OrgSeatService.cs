using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IOrgSeatService" />
/// <remarks>
/// <para>Seats = active members + pending invitations whose expiry has not passed (the accountant counts like everyone
/// else; a deactivated member does not). The limit is the one of the org's <b>effective</b> plan
/// (<see cref="IEntitlementService.ResolveEffectiveTier"/>): with a subscription not in good standing it falls back to
/// Starter, the members stay and every new invitation is refused, as the wave decision D35 wants. The expired-but-not-yet-marked
/// invitations are excluded by the date, so a seat is free the moment an invitation expires, whether or not the
/// maintenance job has run.</para>
/// <para>The tables are read with <c>IgnoreQueryFilters</c>, scoped to the org passed in: the services and the job that
/// use this class act for an org that is not necessarily the tenant of the request.</para>
/// </remarks>
public sealed class OrgSeatService(
    AppDbContext db,
    IEntitlementService entitlement,
    TimeProvider? timeProvider = null) : IOrgSeatService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>The key of the seats lock of an org (<see cref="PostgresAdvisoryLocks.Scope.OrgSeats"/>).</summary>
    internal static (PostgresAdvisoryLocks.Scope Scope, string Key) SeatsLock(Guid orgId) =>
        (PostgresAdvisoryLocks.Scope.OrgSeats, orgId.ToString("N"));

    public async Task<OrgSeatUsage> GetUsageAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;

        var org = await db.Orgs.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orgId, cancellationToken);
        var tier = org is null ? PlanTier.Starter : entitlement.ResolveEffectiveTier(org);
        var max = entitlement.ResolveMaxSeats(tier);

        var activeMembers = await db.OrgMembers.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(m => m.OrgId == orgId && m.Status == OrgMemberStatus.Active, cancellationToken);
        var pendingInvitations = await db.OrgInvitations.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(
                i => i.OrgId == orgId && i.Status == OrgInvitationStatus.Pending && i.ExpiresAt > now,
                cancellationToken);

        return new OrgSeatUsage(max, activeMembers, pendingInvitations);
    }

    public async Task EnsureSeatAvailableAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        var usage = await GetUsageAsync(orgId, cancellationToken);
        if (!usage.CanInvite)
            throw new DomainConflictException(OrgSeatErrors.LimitReached, "OrgSeatLimitReached");
    }
}
