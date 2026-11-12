using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>The decisions of the agency on the owner stays and the rules it gives them (E07/F07/US01/T01).</summary>
public sealed partial class OwnerStayService
{
    /// <summary>The most stays a list of the console answers.</summary>
    private const int MaxAgencyList = 500;

    public async Task<IReadOnlyList<OwnerStayAdminView>> ListForAgencyAsync(
        Guid orgId,
        OwnerStayStatus? status,
        Guid? propertyId,
        CancellationToken cancellationToken = default)
    {
        var query = Stays().AsNoTracking().Where(s => s.OrgId == orgId);
        if (status is { } wanted)
            query = query.Where(s => s.Status == wanted);
        if (propertyId is { } property)
            query = query.Where(s => s.PropertyId == property);

        var stays = await query
            .OrderByDescending(s => s.StartDate)
            .ThenBy(s => s.Id)
            .Take(MaxAgencyList)
            .ToListAsync(cancellationToken);
        return await AdminViewsAsync(stays, cancellationToken);
    }

    public async Task<OwnerStayAdminView> ApproveAsync(Guid orgId, Guid stayId, string actorUserId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        var propertyId = await PropertyOfAsync(orgId, stayId, cancellationToken);

        OwnerStayRequest stay;
        await using (var transaction = await LockAsync(propertyId, cancellationToken))
        {
            stay = await Stays().FirstAsync(s => s.Id == stayId, cancellationToken);
            if (await ApproveCoreAsync(stay, actorUserId, automatic: false, cancellationToken))
                await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }

        logger.LogInformation("Owner stay {StayId} approved by user {UserId}", stay.Id, actorUserId);
        EnqueueProcess(stay.Id);
        return (await AdminViewsAsync([stay], cancellationToken))[0];
    }

    public async Task<OwnerStayAdminView> RejectAsync(Guid orgId, Guid stayId, string actorUserId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        var propertyId = await PropertyOfAsync(orgId, stayId, cancellationToken);

        OwnerStayRequest stay;
        await using (var transaction = await LockAsync(propertyId, cancellationToken))
        {
            stay = await Stays().FirstAsync(s => s.Id == stayId, cancellationToken);
            if (stay.Status != OwnerStayStatus.Rejected)
            {
                if (stay.Status != OwnerStayStatus.Requested)
                    throw new DomainConflictException(OwnerStayErrors.NotPending, OwnerStayErrors.NotPendingMessageKey);

                var now = Now();
                stay.Status = OwnerStayStatus.Rejected;
                stay.DecidedAt = now;
                stay.DecidedByUserId = actorUserId;
                stay.UpdatedAt = now;
                Audit(stay, actorUserId, OwnerPortalAuditStatuses.Rejected);
                await db.SaveChangesAsync(cancellationToken);
            }

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }

        EnqueueProcess(stay.Id);
        return (await AdminViewsAsync([stay], cancellationToken))[0];
    }

    public async Task<OwnerStayAdminView> CancelByAgencyAsync(Guid orgId, Guid stayId, string actorUserId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        var propertyId = await PropertyOfAsync(orgId, stayId, cancellationToken);

        OwnerStayRequest stay;
        await using (var transaction = await LockAsync(propertyId, cancellationToken))
        {
            stay = await Stays().FirstAsync(s => s.Id == stayId, cancellationToken);
            if (stay.Status != OwnerStayStatus.Cancelled)
            {
                if (stay.Status == OwnerStayStatus.Rejected || stay.EndDate <= Today())
                    throw new DomainConflictException(OwnerStayErrors.NotCancellable, OwnerStayErrors.NotCancellableMessageKey);

                var now = Now();
                stay.Status = OwnerStayStatus.Cancelled;
                stay.CancelledAt = now;
                stay.CancelledBy = actorUserId;
                stay.UpdatedAt = now;
                Audit(stay, actorUserId, OwnerPortalAuditStatuses.Cancelled);
            }

            // Its own block, and only that one, is given back now (also the one a withdrawal of the owner left to the background work).
            if (stay.CalendarBlockId is not null && stay.BlockReleasedAt is null)
                await ReleaseCoreAsync(stay, actorUserId, cancellationToken);

            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }

        logger.LogInformation("Owner stay {StayId} cancelled by user {UserId}", stay.Id, actorUserId);
        EnqueueProcess(stay.Id);
        return (await AdminViewsAsync([stay], cancellationToken))[0];
    }

    public async Task<OwnerStayAdminView> RetrySyncAsync(Guid orgId, Guid stayId, string actorUserId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        var propertyId = await PropertyOfAsync(orgId, stayId, cancellationToken);

        OwnerStayRequest stay;
        await using (var transaction = await LockAsync(propertyId, cancellationToken))
        {
            stay = await Stays().FirstAsync(s => s.Id == stayId, cancellationToken);
            var processes = await ProcessesAsync([stay.BlockChangeProcessId, stay.ReleaseChangeProcessId], cancellationToken);
            var now = Now();
            if (stay.Status == OwnerStayStatus.Approved && BlockSync(stay, processes) == OwnerStaySyncState.Failed && stay.BlockChangeProcessId is { } blockProcess)
            {
                await changeRunner.RetryAsync(blockProcess, cancellationToken);
                stay.SyncState = OwnerStaySyncState.Pending;
                stay.SyncCheckedAt = now;
                stay.UpdatedAt = now;
                Audit(stay, actorUserId, OwnerPortalAuditStatuses.SyncRetried);
            }
            else if (stay.Status == OwnerStayStatus.Cancelled && stay.CalendarBlockId is not null && stay.BlockReleasedAt is null)
            {
                await ReleaseCoreAsync(stay, actorUserId, cancellationToken);
            }
            else if (stay.Status == OwnerStayStatus.Cancelled
                     && ReleaseSync(stay, processes) == OwnerStaySyncState.Failed
                     && stay.ReleaseChangeProcessId is { } releaseProcess)
            {
                await changeRunner.RetryAsync(releaseProcess, cancellationToken);
                stay.ReleaseSyncState = OwnerStaySyncState.Pending;
                stay.SyncCheckedAt = now;
                stay.UpdatedAt = now;
                Audit(stay, actorUserId, OwnerPortalAuditStatuses.SyncRetried);
            }

            // Nothing failed: nothing is sent, the stay answers as it is (a repeat is harmless).
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }

        return (await AdminViewsAsync([stay], cancellationToken))[0];
    }

    public async Task<IReadOnlyList<OwnerStayRuleView>> ListRulesAsync(Guid orgId, Guid? propertyId, CancellationToken cancellationToken = default)
    {
        var query = db.OwnerStayRules.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]).AsNoTracking().Where(r => r.OrgId == orgId);
        if (propertyId is { } property)
            query = query.Where(r => r.PropertyId == property);

        var rules = await query.OrderBy(r => r.PropertyId).ThenBy(r => r.PropertyOwnerId).ToListAsync(cancellationToken);
        return rules.Select(OwnerStayRules.ViewOf).ToList();
    }

    public async Task<OwnerStayRuleView> SaveRuleAsync(Guid orgId, OwnerStayRuleInput input, string actorUserId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        OwnerStayRules.EnsureValid(input);

        var propertyOfOrg = await db.Properties
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AnyAsync(p => p.Id == input.PropertyId && p.OrgId == orgId, cancellationToken);
        if (!propertyOfOrg)
            throw RuleTargetNotFound(OwnerStayErrors.PropertyNotFound, OwnerStayErrors.PropertyNotFoundMessageKey);

        if (input.PropertyOwnerId is { } ownerId
            && !await db.PropertyOwnerships
                .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .AnyAsync(o => o.OrgId == orgId && o.PropertyId == input.PropertyId && o.PropertyOwnerId == ownerId, cancellationToken))
        {
            throw RuleTargetNotFound(OwnerStayErrors.OwnerNotFound, OwnerStayErrors.OwnerNotFoundMessageKey);
        }

        await using var transaction = await LockAsync(input.PropertyId, cancellationToken);
        var rule = await db.OwnerStayRules
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .FirstOrDefaultAsync(r => r.OrgId == orgId && r.PropertyId == input.PropertyId && r.PropertyOwnerId == input.PropertyOwnerId, cancellationToken);
        var now = Now();
        if (rule is null)
        {
            rule = new OwnerStayRule { OrgId = orgId, PropertyId = input.PropertyId, PropertyOwnerId = input.PropertyOwnerId, CreatedAt = now };
            db.OwnerStayRules.Add(rule);
        }

        rule.YearlyNightAllowance = input.YearlyNightAllowance;
        rule.MinAdvanceDays = input.MinAdvanceDays;
        rule.MaxAdvanceDays = input.MaxAdvanceDays;
        rule.MinNights = input.MinNights;
        rule.MaxNights = input.MaxNights;
        rule.ApprovalRequired = input.ApprovalRequired;
        rule.ActiveFrom = input.ActiveFrom;
        rule.ActiveUntil = input.ActiveUntil;
        rule.BlackoutsJson = OwnerStayRules.WriteBlackouts(input.Blackouts ?? []);
        rule.UpdatedAt = now;
        rule.UpdatedByUserId = actorUserId;
        AuditRule(rule, actorUserId, OwnerPortalAuditStatuses.Saved);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        return OwnerStayRules.ViewOf(rule);
    }

    public async Task DeleteRuleAsync(Guid orgId, Guid ruleId, string actorUserId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        var rule = await db.OwnerStayRules
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .FirstOrDefaultAsync(r => r.Id == ruleId && r.OrgId == orgId, cancellationToken)
            ?? throw RuleTargetNotFound(OwnerStayErrors.RuleNotFound, OwnerStayErrors.RuleNotFoundMessageKey);

        db.OwnerStayRules.Remove(rule);
        AuditRule(rule, actorUserId, OwnerPortalAuditStatuses.Removed);
        await db.SaveChangesAsync(cancellationToken);
    }

    // ---------------------------------------------------------------- shared by the agency and the background work

    /// <summary>
    /// Approves a request that is still one: the owner profile still holds the property, the arrival is not past, the dates are still free, and the block is
    /// created with the id of the stay (in the transaction of the caller, when it has one). False when the stay is already approved with its block.
    /// </summary>
    private async Task<bool> ApproveCoreAsync(OwnerStayRequest stay, string? actorUserId, bool automatic, CancellationToken cancellationToken)
    {
        if (stay.Status == OwnerStayStatus.Approved && stay.CalendarBlockId is not null)
            return false;
        if (stay.Status != OwnerStayStatus.Requested)
            throw new DomainConflictException(OwnerStayErrors.NotPending, OwnerStayErrors.NotPendingMessageKey);
        if (stay.StartDate < Today())
            throw new DomainRuleException(OwnerStayErrors.InPast, OwnerStayErrors.InPastMessageKey);

        var owns = await OwnerPortalAccessResolver.InForce(db, timeProvider.GetUtcNow().UtcDateTime)
            .AnyAsync(o => o.OrgId == stay.OrgId && o.PropertyId == stay.PropertyId && o.PropertyOwnerId == stay.PropertyOwnerId, cancellationToken);
        if (!owns)
            throw RuleTargetNotFound(OwnerStayErrors.OwnerNotFound, OwnerStayErrors.OwnerNotFoundMessageKey);

        ManualBlockChange block;
        var existing = await db.CalendarBlocks
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == stay.Id, cancellationToken);
        if (existing is not null)
        {
            // The block of this stay is there already (written by an approval that did not finish): it is linked, never created twice.
            block = new ManualBlockChange(existing, null, null);
        }
        else
        {
            await EnsureDatesFreeAsync(stay.OrgId, stay.PropertyId, stay.StartDate, stay.EndDate, stay.Id, cancellationToken);
            block = await calendarBlocks.CreateTrackedAsync(
                new ManualBlockRequest(
                    stay.PropertyId,
                    stay.StartDate.ToDateTime(TimeOnly.MinValue),
                    stay.EndDate.ToDateTime(TimeOnly.MinValue),
                    CalendarBlockReason.Owner,
                    null,
                    stay.Id),
                cancellationToken);
        }

        var now = Now();
        stay.Status = OwnerStayStatus.Approved;
        stay.CalendarBlockId = block.Block.Id;
        stay.BlockChangeProcessId = block.ChangeProcessId;
        stay.SyncState = OwnerStayRules.SyncOf(block.ChangeProcessId is not null, block.ChangeState);
        stay.SyncErrorCode = null;
        stay.SyncCheckedAt = now;
        stay.DecidedAt = now;
        stay.DecidedByUserId = actorUserId;
        stay.UpdatedAt = now;
        Audit(stay, actorUserId, automatic ? OwnerPortalAuditStatuses.AutoApproved : OwnerPortalAuditStatuses.Approved);
        if (stay.SyncState == OwnerStaySyncState.Acknowledged)
            Audit(stay, null, OwnerPortalAuditStatuses.SyncAcknowledged);

        return true;
    }

    /// <summary>Gives back the block of a withdrawn or cancelled stay, and only that one, with the change that opens its nights again.</summary>
    private async Task ReleaseCoreAsync(OwnerStayRequest stay, string? actor, CancellationToken cancellationToken)
    {
        var change = await calendarBlocks.ReleaseOwnerStayBlockAsync(stay.CalendarBlockId!.Value, cancellationToken);
        var now = Now();
        stay.BlockReleasedAt = now;
        stay.ReleaseChangeProcessId = change?.ChangeProcessId;
        stay.ReleaseSyncState = change is null
            ? OwnerStaySyncState.Acknowledged
            : OwnerStayRules.SyncOf(change.ChangeProcessId is not null, change.ChangeState);
        stay.SyncCheckedAt = now;
        stay.UpdatedAt = now;
        Audit(stay, actor, OwnerPortalAuditStatuses.Released);
        if (stay.ReleaseSyncState == OwnerStaySyncState.Acknowledged)
            Audit(stay, null, OwnerPortalAuditStatuses.ReleaseAcknowledged);

        logger.LogInformation("Owner stay {StayId}: its block was given back", stay.Id);
    }

    /// <summary>The property of a stay of the org (the key of its lock); 404 for a stay of another org or a missing one.</summary>
    private async Task<Guid> PropertyOfAsync(Guid orgId, Guid stayId, CancellationToken cancellationToken) =>
        await Stays()
            .AsNoTracking()
            .Where(s => s.Id == stayId && s.OrgId == orgId)
            .Select(s => (Guid?)s.PropertyId)
            .FirstOrDefaultAsync(cancellationToken)
        ?? throw StayNotFound(stayId);

    /// <summary>The stays as the console reads them: the stay and the name of its owner profile (never a fiscal or bank value of it).</summary>
    private async Task<List<OwnerStayAdminView>> AdminViewsAsync(IReadOnlyCollection<OwnerStayRequest> stays, CancellationToken cancellationToken)
    {
        var views = await ViewsAsync(stays, cancellationToken);
        var ownerIds = stays.Select(s => s.PropertyOwnerId).Distinct().ToList();
        var owners = await db.PropertyOwners
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(o => ownerIds.Contains(o.Id))
            .Select(o => new { o.Id, o.FirstName, o.LastName, o.LegalName })
            .ToListAsync(cancellationToken);
        var names = owners.ToDictionary(
            o => o.Id,
            o => OwnerFieldRules.DisplayName(new PropertyOwner { FirstName = o.FirstName, LastName = o.LastName, LegalName = o.LegalName }));
        var automatic = stays.ToDictionary(s => s.Id, s => s.AutoApprove);

        return views
            .Select(view => new OwnerStayAdminView(view, names.GetValueOrDefault(view.PropertyOwnerId, string.Empty), automatic[view.Id]))
            .ToList();
    }

    private void AuditRule(OwnerStayRule rule, string actorUserId, string status) =>
        activityLog.Record(OrgActivity.Of(
            rule.OrgId,
            OrgActivityType.OwnerPortalChanged,
            actorUserId,
            rule.Id.ToString(),
            (OrgActivityDetailKeys.Part, OwnerPortalParts.StayRule),
            (OrgActivityDetailKeys.Status, status)));

    private static NotFoundException RuleTargetNotFound(string code, string messageKey) =>
        new($"Owner stay target not found: {code}")
        {
            Code = code,
            MessageKey = messageKey,
        };
}
