using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using DocumentKind = Casazen.Core.Entities.Enums.FiscalDocumentKind;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The drafts at the end of every stay, the daily run and the bulk issue (E12/F01/US01/T02).
/// </summary>
public sealed partial class FiscalDocumentService
{
    public async Task<EnsureDraftOutcome> EnsureDraftAsync(Guid bookingId, string? userId, CancellationToken ct = default)
    {
        // The check and the draft under the lock of the booking: the check-out event and the daily run, or two runs, never both see «no document» and make two drafts.
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db,
            ct,
            (PostgresAdvisoryLocks.Scope.FiscalDocumentBooking, bookingId.ToString("N")));

        if (await HasLiveDocumentAsync(bookingId, ct))
            return EnsureDraftOutcome.AlreadyHasDocument;

        await CreateDraftAsync(bookingId, userId, ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);

        return EnsureDraftOutcome.Created;
    }

    public async Task<IReadOnlyList<Guid>> OrgsWithDocumentsDueAsync(CancellationToken ct = default) =>
        await db.OrgFiscalIssuerProfiles
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Select(p => p.OrgId)
            .Distinct()
            .ToListAsync(ct);

    public async Task<FiscalDocumentsDueResult> ProcessDueDocumentsAsync(Guid orgId, int limit, int lookbackDays, CancellationToken ct = default)
    {
        if (featureFlags is null || !featureFlags.IsEnabled(FeatureFlags.FiscalDocumentIssuance))
            return FiscalDocumentsDueResult.NotRun(FiscalDocumentsDueSkipReasons.IssuanceOff);

        // The setup of the issuer is the one of the other writes: a profile that misses an item the documents need makes nothing (the host is told by the setup, not by a run).
        var settings = await issuers.GetSettingsAsync(orgId, ct);
        if (settings.MissingFields.Count > 0)
            return FiscalDocumentsDueResult.NotRun(FiscalDocumentsDueSkipReasons.ProfileIncomplete);

        // One run per organization at a time, for the whole run: a retry or a manual trigger finds the lock taken and leaves.
        await using var run = await PostgresAdvisoryLocks.TryAcquireSessionLockAsync(
            db,
            PostgresAdvisoryLocks.Scope.FiscalDocumentsDueRun,
            orgId.ToString("N"),
            ct);
        if (run is null)
            return FiscalDocumentsDueResult.NotRun(FiscalDocumentsDueSkipReasons.RunInProgress);

        var autoIssue = settings.Issuer.AutoIssueGuestDocuments;
        var today = timeProvider.TodayInRomeAsDateOnly();
        var todayStart = timeProvider.TodayInRome();
        var since = todayStart.AddDays(-Math.Max(1, lookbackDays));

        // The active stays that ended (the one definition of «active»), without a live document; with the automatic issue also the ones that only have a draft. The most recent
        // first, so a stay that can never get its document (the guest never gave its data) does not hold back the ones behind it.
        var candidates = await db.Bookings
            .AsNoTracking()
            .Where(b => b.OrgId == orgId
                        && AlloggiatiStatusRules.ActiveBookingStatuses.Contains(b.Status)
                        && b.NoShowAt == null
                        && b.CheckOutDate < todayStart
                        && b.CheckOutDate >= since
                        && !db.FiscalDocuments.Any(d => d.BookingId == b.Id
                                                        && d.Purpose == FiscalDocumentPurpose.GuestStay
                                                        && d.Status != FiscalDocumentStatus.Discarded
                                                        && d.Kind != DocumentKind.CreditNote
                                                        && d.Kind != DocumentKind.Proforma
                                                        && !d.CreditedInFull
                                                        && (!autoIssue || d.Status == FiscalDocumentStatus.Issued)))
            .OrderByDescending(b => b.CheckOutDate)
            .ThenBy(b => b.Id)
            .Select(b => new { b.Id, b.CheckOutDate })
            .Take(Math.Max(0, limit))
            .ToListAsync(ct);

        var examined = 0;
        var created = 0;
        var issued = 0;
        var skipped = 0;
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            // The day is a Europe/Rome day: the stay that ends today is not over yet, whatever the stored instant says.
            if (RomeCalendar.DateInRome(candidate.CheckOutDate) >= today)
                continue;

            examined++;
            try
            {
                if (await EnsureDraftAsync(candidate.Id, null, ct) == EnsureDraftOutcome.Created)
                    created++;

                if (autoIssue && await LiveDraftIdOfAsync(candidate.Id, ct) is { } draftId)
                {
                    await IssueCoreAsync(draftId, confirm: true, notify: false, ct);
                    issued++;
                }
            }
            catch (Exception ex) when (ex is DomainException or NotFoundException)
            {
                // The rules refuse this stay now (data missing, no tax rule), or it is gone: it keeps what it had and is read again at the next run.
                db.ChangeTracker.Clear();
                skipped++;
                logger.LogInformation(
                    "Booking {BookingId} of org {OrgId}: no document made now ({Code})",
                    candidate.Id,
                    orgId,
                    ex is DomainException refusal ? refusal.Code : "booking_not_found");
            }
        }

        logger.LogInformation(
            "Fiscal documents due of org {OrgId}: {Examined} stays looked at, {Created} drafts made, {Issued} documents issued, {Skipped} skipped",
            orgId,
            examined,
            created,
            issued,
            skipped);
        return new FiscalDocumentsDueResult(null, examined, created, issued, skipped);
    }

    public async Task<FiscalDocumentBatchResult> IssueBatchAsync(HostScope scope, FiscalDocumentBatchRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(request);

        var requested = (request.DocumentIds ?? []).Distinct().ToList();
        var properties = (request.PropertyIds ?? []).Distinct().ToList();
        var byPeriodOrProperty = request.From is not null || request.To is not null || properties.Count > 0;

        // An empty selection is never «all»: nothing named, no period, no property is a refusal.
        if (requested.Count == 0 && !byPeriodOrProperty)
            throw new DomainRuleException(FiscalDocumentErrors.BatchSelectionRequired, "FiscalDocumentBatchSelectionRequired");
        if (requested.Count > FiscalDocumentBatch.MaxDocuments)
            throw new DomainRuleException(FiscalDocumentErrors.BatchTooLarge, "FiscalDocumentBatchTooLarge", FiscalDocumentBatch.MaxDocuments);
        if (request.From is { } from && request.To is { } to && to < from)
            throw new DomainRuleException(FiscalDocumentErrors.BatchPeriodInvalid, "FiscalDocumentBatchPeriodInvalid");

        // What the caller reaches: the scope of the list, narrowed in SQL (a caller restricted to some properties never reaches the rest).
        var reach = Matching(scope, new FiscalDocumentCriteria());
        var plan = new List<(Guid Id, string? ErrorCode)>();

        if (requested.Count > 0)
        {
            var found = await reach
                .Where(d => requested.Contains(d.Id))
                .Select(d => new { d.Id, d.Source, d.Status })
                .ToListAsync(ct);
            foreach (var id in requested)
            {
                var document = found.FirstOrDefault(d => d.Id == id);
                if (document is null)
                    plan.Add((id, FiscalDocumentErrors.NotFound));
                else if (document.Source != FiscalDocumentSource.CasaZen)
                    plan.Add((id, FiscalDocumentErrors.ReadOnly));
                else if (document.Status != FiscalDocumentStatus.Draft)
                    plan.Add((id, FiscalDocumentErrors.NotDraft));
                else
                    plan.Add((id, null));
            }
        }

        var remaining = 0;
        if (byPeriodOrProperty)
        {
            // The drafts of the stays of the period (their last day) and of the properties; a correction (a credit note) is only issued when it is named.
            var selected = reach.Where(d => d.Source == FiscalDocumentSource.CasaZen
                                            && d.Purpose == FiscalDocumentPurpose.GuestStay
                                            && d.Status == FiscalDocumentStatus.Draft
                                            && d.Kind != DocumentKind.CreditNote
                                            && !requested.Contains(d.Id));
            if (request.From is { } start)
                selected = selected.Where(d => d.ServicePeriodTo >= start);
            if (request.To is { } end)
                selected = selected.Where(d => d.ServicePeriodTo <= end);
            if (properties.Count > 0)
                selected = selected.Where(d => d.PropertyId != null && properties.Contains(d.PropertyId.Value));

            var room = Math.Max(0, FiscalDocumentBatch.MaxDocuments - requested.Count);
            var total = await selected.CountAsync(ct);
            var ids = await selected
                .OrderBy(d => d.ServicePeriodTo)
                .ThenBy(d => d.CreatedAt)
                .ThenBy(d => d.Id)
                .Select(d => d.Id)
                .Take(room)
                .ToListAsync(ct);
            remaining = total - ids.Count;
            plan.AddRange(ids.Select(id => (id, (string?)null)));
        }

        var rows = new List<FiscalDocumentBatchRow>(plan.Count);
        foreach (var (id, errorCode) in plan)
        {
            ct.ThrowIfCancellationRequested();
            if (errorCode is not null)
            {
                rows.Add(new FiscalDocumentBatchRow(id, false, null, errorCode));
                continue;
            }

            // Each draft through the issue path of the API, in its own transaction: one that fails leaves nothing behind for the next.
            try
            {
                var issued = await IssueCoreAsync(id, confirm: true, notify: false, ct);
                rows.Add(new FiscalDocumentBatchRow(id, true, issued.Number, null));
            }
            catch (DomainException ex)
            {
                db.ChangeTracker.Clear();
                rows.Add(new FiscalDocumentBatchRow(id, false, null, ex.Code));
                logger.LogInformation("Bulk issue: document {DocumentId} not issued ({Code})", id, ex.Code);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                db.ChangeTracker.Clear();
                rows.Add(new FiscalDocumentBatchRow(id, false, null, FiscalDocumentErrors.IssueFailed));
                logger.LogError("Bulk issue: document {DocumentId} not issued ({Error})", id, ex.GetType().Name);
            }
        }

        logger.LogInformation(
            "Bulk issue of org {OrgId}: {Issued} issued, {Failed} not issued, {Remaining} more in the selection",
            scope.OrgId,
            rows.Count(r => r.Issued),
            rows.Count(r => !r.Issued),
            remaining);
        return new FiscalDocumentBatchResult(rows, remaining);
    }

    /// <summary>
    /// A booking has a live document when it has one that is not discarded, not a credit note, not a proforma and not credited in full: whoever issued it (the one «never two
    /// documents for one stay», the documents imported from Octorate included). A document credited in full is over, and the booking may have its next one.
    /// </summary>
    private Task<bool> HasLiveDocumentAsync(Guid bookingId, CancellationToken ct) =>
        db.FiscalDocuments
            .AsNoTracking()
            .AnyAsync(
                d => d.BookingId == bookingId
                     && d.Purpose == FiscalDocumentPurpose.GuestStay
                     && d.Status != FiscalDocumentStatus.Discarded
                     && d.Kind != DocumentKind.CreditNote
                     && d.Kind != DocumentKind.Proforma
                     && !d.CreditedInFull,
                ct);

    /// <summary>The newest draft CasaZen made for the booking (not a credit note); <c>null</c> when it has none.</summary>
    private Task<Guid?> LiveDraftIdOfAsync(Guid bookingId, CancellationToken ct) =>
        db.FiscalDocuments
            .AsNoTracking()
            .Where(d => d.BookingId == bookingId
                        && d.Source == FiscalDocumentSource.CasaZen
                        && d.Purpose == FiscalDocumentPurpose.GuestStay
                        && d.Status == FiscalDocumentStatus.Draft
                        && d.Kind != DocumentKind.CreditNote)
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => (Guid?)d.Id)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Tells the hosts of the booking that a document was issued (<see cref="FiscalDocumentLiveRefresh"/>): a push that the decorator turns into the entry of the bell and the live
    /// message that makes the open pages read the list of the documents and the booking again. Best effort after the issue is committed.
    /// </summary>
    private void TellTheHostsOfTheIssue(FiscalDocument document, Org org) =>
        FiscalDocumentLiveRefresh.Tell(pushNotifications, texts, document, org, logger);
}
