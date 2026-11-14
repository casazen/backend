using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The month-end for many owners at once (E13/F01/US03/T01): the posting, the generation, the approval, the publication and the close of the month for every owner of an
/// agency (about 250 flats) or for the ones listed, each step through the very operation of one owner.
/// </summary>
/// <remarks>
/// <para><b>One owner, one transaction.</b> Generation, approval, publication and close run owner by owner through the single operations (their rules, their refusals,
/// their locks): an owner that fails leaves nothing behind (the tracked changes are dropped) and never stops the others. The result of each owner is a row
/// (<see cref="OwnerBulkOwnerResult"/>); a failure that is not a rule of the domain is logged with the ids and answered with <c>owner_bulk_failed</c>, never swallowed
/// into a success. The posting is the one exception: the ledger of a month is posted from all its sources under one lock, so it is one transaction for the owners asked
/// for, and its rows say what was kept out of each owner's ledger.</para>
/// <para><b>Idempotent.</b> A run that finds the owner already where the step leads (a version generated and not changed since, approved, published, a closed month) changes
/// nothing and says so (<see cref="OwnerBulkOwnerResult.Unchanged"/>), so a run can be repeated after a failure or a deploy.</para>
/// <para><b>Never a waiver, never a flagged approval.</b> A flagged version is skipped with its blocking issues; the close never waives a difference (that needs a reason and
/// <c>agency.owner.financial.manage</c>: it is done owner by owner).</para>
/// </remarks>
public sealed partial class OwnerAccountingService
{
    public async Task<IReadOnlyList<Guid>> ResolveBulkOwnersAsync(
        Guid orgId,
        int year,
        int month,
        OwnerBulkAction action,
        IReadOnlyList<Guid>? ownerIds,
        CancellationToken ct = default)
    {
        var period = Period.Of(year, month);
        EnsureBulkRequest(action, period);
        if (ownerIds is not null)
        {
            // An empty list is never «every owner»: the owners are asked for by omitting the list.
            if (ownerIds.Count == 0)
                throw new DomainRuleException(OwnerAccountingErrors.BulkOwnersEmpty, "OwnerBulkOwnersEmpty");
            if (ownerIds.Count > OwnerBulk.MaxListedOwners)
                throw new DomainRuleException(OwnerAccountingErrors.BulkTooManyOwners, "OwnerBulkTooManyOwners", OwnerBulk.MaxListedOwners);
            return ownerIds.Distinct().ToList();
        }

        var readiness = await GetReadinessAsync(orgId, year, month, ct);
        return readiness.Owners.Select(o => o.OwnerId).ToList();
    }

    public async Task<IReadOnlyList<OwnerBulkOwnerResult>> RunBulkAsync(
        Guid orgId,
        int year,
        int month,
        OwnerBulkAction action,
        IReadOnlyList<Guid> ownerIds,
        string? userId,
        OwnerBulkProgress? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ownerIds);
        var period = Period.Of(year, month);
        EnsureBulkRequest(action, period);
        if (ownerIds.Count == 0)
            throw new DomainRuleException(OwnerAccountingErrors.BulkOwnersEmpty, "OwnerBulkOwnersEmpty");

        // The owners of this org only: an id of another org is the same row as an id that does not exist, and no step is run for it.
        var owners = ownerIds.Distinct().ToList();
        var known = (await db.PropertyOwners.AsNoTracking()
                .Where(o => o.OrgId == orgId && owners.Contains(o.Id))
                .Select(o => o.Id)
                .ToListAsync(ct))
            .ToHashSet();

        IReadOnlyList<OwnerBulkOwnerResult> rows;
        if (action == OwnerBulkAction.Post)
        {
            rows = await PostBulkAsync(orgId, period, owners, known, userId, ct);
            if (progress is not null)
                await progress(rows, ct);
        }
        else
        {
            var closed = (await db.OwnerClosePeriods.AsNoTracking()
                    .Where(p => p.OrgId == orgId && p.PeriodYear == year && p.PeriodMonth == month && p.Status == OwnerClosePeriodStatus.Closed && owners.Contains(p.PropertyOwnerId))
                    .Select(p => p.PropertyOwnerId)
                    .ToListAsync(ct))
                .ToHashSet();
            var done = new List<OwnerBulkOwnerResult>(owners.Count);
            foreach (var owner in owners)
            {
                ct.ThrowIfCancellationRequested();
                done.Add(await BulkOwnerAsync(orgId, period, action, owner, known.Contains(owner), closed.Contains(owner), userId, ct));
                if (progress is not null)
                    await progress(done, ct);
            }

            rows = done;
        }

        logger.LogInformation(
            "Bulk {Action} of {Year}-{Month} of org {OrgId} by {UserId}: {Owners} owners, {Done} done, {Unchanged} unchanged, {Failed} not done",
            action, year, month, orgId, userId, rows.Count, rows.Count(r => r.Ok && !r.Unchanged), rows.Count(r => r.Ok && r.Unchanged), rows.Count(r => !r.Ok));
        return rows;
    }

    /// <summary>The checks of a bulk request that hold for every owner: a known step, and a month that is over for the close (never one refusal per owner).</summary>
    private void EnsureBulkRequest(OwnerBulkAction action, Period period)
    {
        if (!Enum.IsDefined(action))
            throw new DomainRuleException(OwnerAccountingErrors.BulkActionInvalid, "OwnerBulkActionInvalid");

        // A month is closed once it is over, as the close of one owner says.
        if (action == OwnerBulkAction.Close && period.Last >= time.TodayInRomeAsDateOnly())
            throw new DomainConflictException(OwnerAccountingErrors.PeriodNotEnded, "OwnerPeriodNotEnded");
    }

    /// <summary>
    /// The posting of the month for the owners asked for, in one transaction. Each owner's row: its sources kept out of its ledger (a share gap, a rate that is missing, a stay
    /// without a mandate...) are blocking issues and the row fails with the first of them; lines posted or reversed are a done row; nothing to post an unchanged row; a closed
    /// month is never posted again.
    /// </summary>
    private async Task<IReadOnlyList<OwnerBulkOwnerResult>> PostBulkAsync(Guid orgId, Period period, List<Guid> owners, HashSet<Guid> known, string? userId, CancellationToken ct)
    {
        var wanted = owners.Where(known.Contains).ToHashSet();
        var outcome = wanted.Count == 0 ? null : await PostCoreAsync(orgId, period, wanted, userId, ct);

        var rows = new List<OwnerBulkOwnerResult>(owners.Count);
        foreach (var owner in owners)
        {
            if (outcome is null || !known.Contains(owner))
            {
                rows.Add(Failed(owner, OwnerFiscalErrors.OwnerNotFound));
                continue;
            }

            if (outcome.Plan.ClosedOwners.Contains(owner))
            {
                rows.Add(Unchanged(owner, OwnerAccountingErrors.PeriodClosed));
                continue;
            }

            var blocking = PlanIssuesOf(outcome.Plan, owner)
                .Where(i => i.Blocking)
                .Select(i => i.Code)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();
            if (blocking.Count > 0)
                rows.Add(Failed(owner, blocking[0], blocking));
            else
                rows.Add(outcome.Touched.Contains(owner) ? Done(owner) : Unchanged(owner));
        }

        return rows;
    }

    /// <summary>One owner's step (generate, approve, publish or close), its own transaction and its own result: a refusal is its code, anything else is logged and answered as a failure.</summary>
    private async Task<OwnerBulkOwnerResult> BulkOwnerAsync(
        Guid orgId,
        Period period,
        OwnerBulkAction action,
        Guid owner,
        bool known,
        bool closed,
        string? userId,
        CancellationToken ct)
    {
        if (!known)
            return Failed(owner, OwnerFiscalErrors.OwnerNotFound);

        // A closed month went through the whole way: every step is already done for it.
        if (closed)
            return Unchanged(owner, OwnerAccountingErrors.PeriodClosed);

        try
        {
            return action switch
            {
                OwnerBulkAction.Generate => await GenerateForBulkAsync(orgId, period, owner, userId, ct),
                OwnerBulkAction.Approve => await ApproveForBulkAsync(orgId, period, owner, userId, ct),
                OwnerBulkAction.Publish => await PublishForBulkAsync(orgId, period, owner, userId, ct),
                OwnerBulkAction.Close => await CloseForBulkAsync(orgId, period, owner, userId, ct),
                _ => throw new DomainRuleException(OwnerAccountingErrors.BulkActionInvalid, "OwnerBulkActionInvalid"),
            };
        }
        catch (DomainException ex)
        {
            return FailedFrom(owner, ex);
        }
        catch (NotFoundException ex)
        {
            return Failed(owner, ex.Code ?? "not_found");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not a rule of the domain (the database, a file): the owner is not done and the run goes on; the next run finds it where it is.
            logger.LogError(
                "Bulk {Action} of {Year}-{Month} of org {OrgId}: owner {OwnerId} not done ({Error})",
                action, period.Year, period.Month, orgId, owner, ex.GetType().Name);
            return Failed(owner, OwnerAccountingErrors.BulkFailed);
        }
        finally
        {
            // What a failed step left half-done in the context (a version marked published whose file was not stored) must never be saved by the next owner's step.
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// The version of the month, unless the one in force is still the picture of the ledger (<see cref="GenerateCoreAsync"/>). A version that comes out flagged is a version
    /// made: the row is <c>ok</c> with the note <c>owner_statement_flagged</c> and the blocking issues, and the approval will skip it.
    /// </summary>
    private async Task<OwnerBulkOwnerResult> GenerateForBulkAsync(Guid orgId, Period period, Guid owner, string? userId, CancellationToken ct)
    {
        var outcome = await GenerateCoreAsync(orgId, owner, period, userId, skipWhenCurrent: true, ct);
        var version = outcome.Version;
        if (version.Status == OwnerStatementVersionStatus.Flagged)
            return new OwnerBulkOwnerResult(owner, true, OwnerAccountingErrors.VersionFlagged, !outcome.Created, BlockingCodesOf(version));

        return outcome.Created ? Done(owner) : Unchanged(owner, OwnerAccountingErrors.VersionUnchanged);
    }

    /// <summary>The approval of the version in force: never a flagged one (its blocking issues are the reason), nothing to do for one already approved or published.</summary>
    private async Task<OwnerBulkOwnerResult> ApproveForBulkAsync(Guid orgId, Period period, Guid owner, string? userId, CancellationToken ct)
    {
        var version = await CurrentVersionAsync(orgId, owner, period, ct);
        if (version is null)
            return Failed(owner, OwnerAccountingErrors.VersionNotFound);

        switch (version.Status)
        {
            case OwnerStatementVersionStatus.Flagged:
                return Failed(owner, OwnerAccountingErrors.VersionFlagged, BlockingCodesOf(version));
            case OwnerStatementVersionStatus.Approved or OwnerStatementVersionStatus.Published:
                return Unchanged(owner);
            default:
                await ApproveVersionAsync(orgId, version.Id, userId, ct);
                return Done(owner);
        }
    }

    /// <summary>The publication of the approved version in force (its file, its PDF and the notice to the owner, as the single publication does them); nothing to do for one already published.</summary>
    private async Task<OwnerBulkOwnerResult> PublishForBulkAsync(Guid orgId, Period period, Guid owner, string? userId, CancellationToken ct)
    {
        var version = await CurrentVersionAsync(orgId, owner, period, ct);
        if (version is null)
            return Failed(owner, OwnerAccountingErrors.VersionNotFound);

        switch (version.Status)
        {
            case OwnerStatementVersionStatus.Flagged:
                return Failed(owner, OwnerAccountingErrors.VersionFlagged, BlockingCodesOf(version));
            case OwnerStatementVersionStatus.Published:
                return Unchanged(owner);
            case OwnerStatementVersionStatus.Approved:
                await PublishVersionAsync(orgId, version.Id, userId, ct);
                return Done(owner);
            default:
                return Failed(owner, OwnerAccountingErrors.VersionNotApproved);
        }
    }

    /// <summary>The close of the month on the approved version: reconciled to the confirmed payouts or the paid wires, and never with a waiver (a difference or a negative balance is a row that fails).</summary>
    private async Task<OwnerBulkOwnerResult> CloseForBulkAsync(Guid orgId, Period period, Guid owner, string? userId, CancellationToken ct)
    {
        try
        {
            await CloseAsync(orgId, owner, period.Year, period.Month, new OwnerCloseCommand(), mayWaive: false, userId, ct);
            return Done(owner);
        }
        catch (DomainConflictException ex) when (ex.Code == OwnerAccountingErrors.PeriodClosed)
        {
            // Closed by someone else since the run began: the month is where the step leads.
            return Unchanged(owner, OwnerAccountingErrors.PeriodClosed);
        }
    }

    /// <summary>The version in force of the owner's month: the newest that is not superseded.</summary>
    private Task<OwnerStatementVersion?> CurrentVersionAsync(Guid orgId, Guid ownerId, Period period, CancellationToken ct) =>
        db.OwnerStatementVersions.AsNoTracking()
            .Where(v => v.OrgId == orgId && v.PropertyOwnerId == ownerId && v.PeriodYear == period.Year && v.PeriodMonth == period.Month
                && v.Status != OwnerStatementVersionStatus.Superseded)
            .OrderByDescending(v => v.VersionNumber)
            .FirstOrDefaultAsync(ct);

    private static OwnerBulkOwnerResult Done(Guid owner) => new(owner, true);

    private static OwnerBulkOwnerResult Unchanged(Guid owner, string? code = null) => new(owner, true, code, true);

    private static OwnerBulkOwnerResult Failed(Guid owner, string code, IReadOnlyList<string>? issues = null) => new(owner, false, code, false, issues);

    /// <summary>A refusal of the domain as a row: its code, the blocking issues it names and the facts that go with it (never a message, never a name).</summary>
    private static OwnerBulkOwnerResult FailedFrom(Guid owner, DomainException ex)
    {
        IReadOnlyList<string>? issues = null;
        Dictionary<string, object?>? details = null;
        foreach (var (key, value) in ex.Extensions)
        {
            if (key == "issues" && value is IEnumerable<string> codes)
                issues = codes.ToList();
            else
                (details ??= [])[key] = value;
        }

        return new OwnerBulkOwnerResult(owner, false, ex.Code, false, issues, details);
    }
}
