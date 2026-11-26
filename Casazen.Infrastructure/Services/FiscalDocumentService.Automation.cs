using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Models;
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
        // An owner without an issuer profile that can issue (E17/F01/US01/T02) gets its «Da fare» here, before the lock: inside it the refusal would roll the item back
        // with the transaction. The refusal itself (422 owner_issuer_missing) is the answer: no document is made.
        var booking = await BookingOfAsync(bookingId, ct);
        if (IsActive(booking) && !await HasLiveDocumentAsync(bookingId, ct))
            await RequirePlanAsync(booking, ct);

        // The check and the draft under the lock of the booking: the check-out event and the daily run, or two runs, never both see «no document» and make two drafts.
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db,
            ct,
            (PostgresAdvisoryLocks.Scope.FiscalDocumentBooking, bookingId.ToString("N")));

        if (await HasLiveDocumentAsync(bookingId, ct) && !await PendingSupplements().AnyAsync(o => o.BookingId == bookingId, ct))
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
            .OfOrganizations()
            .Select(p => p.OrgId)
            .Distinct()
            .ToListAsync(ct);

    public async Task<FiscalDocumentsDueResult> ProcessDueDocumentsAsync(Guid orgId, int limit, int lookbackDays, CancellationToken ct = default)
    {
        if (featureFlags is null || !featureFlags.IsEnabled(FeatureFlags.FiscalDocumentIssuance))
            return FiscalDocumentsDueResult.NotRun(FiscalDocumentsDueSkipReasons.IssuanceOff);

        // The setup of the issuer is the one of the other writes: a profile that misses an item the documents need makes nothing (the host is told by the setup, not by a run).
        // The flow «Documents» reads the sections «issuer» and «VAT» of the billing setup (E11/F01/US01/T04: an item that another epic adds to them counts too).
        var settings = await issuers.GetSettingsAsync(orgId, ct);
        if (settings.MissingFields.Count > 0 || !await IssuerAndVatSetupIsCompleteAsync(orgId, ct))
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

        // The active stays that ended (the one definition of «active»), without a live document; with the automatic issue also the ones that only have a draft, and the ones
        // whose document of the agency's services is still a draft beside the owner's document already issued (E17/F01/US01/T02). The most recent first, so a stay that can
        // never get its document (the guest never gave its data) does not hold back the ones behind it.
        var supplements = PendingSupplements();
        var candidates = await db.Bookings
            .AsNoTracking()
            .Where(b => b.OrgId == orgId
                        && AlloggiatiStatusRules.ActiveBookingStatuses.Contains(b.Status)
                        && b.NoShowAt == null
                        && b.CheckOutDate < todayStart
                        && b.CheckOutDate >= since
                        && (supplements.Any(o => o.BookingId == b.Id && o.OrgId == b.OrgId) || !db.FiscalDocuments.Any(d => d.BookingId == b.Id
                                                         && d.Purpose == FiscalDocumentPurpose.GuestStay
                                                         && d.Status != FiscalDocumentStatus.Discarded
                                                         && d.Kind != DocumentKind.CreditNote
                                                         && d.Kind != DocumentKind.Proforma
                                                         && !d.CreditedInFull
                                                         && (!autoIssue || d.Status == FiscalDocumentStatus.Issued))
                            || (autoIssue && db.FiscalDocuments.Any(d => d.BookingId == b.Id
                                                                         && d.Source == FiscalDocumentSource.CasaZen
                                                                         && (d.Purpose == FiscalDocumentPurpose.GuestStay || d.Purpose == FiscalDocumentPurpose.GuestExtraSupplement)
                                                                         && (d.ForAgencyServices || d.SupplementForExtraOrderId != null)
                                                                         && d.Status == FiscalDocumentStatus.Draft
                                                                         && d.Kind != DocumentKind.CreditNote))))
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
                var outcome = await EnsureDraftAsync(candidate.Id, null, ct);
                if (outcome == EnsureDraftOutcome.Created)
                    created++;

                if (autoIssue)
                {
                    // Every draft of the booking in the order of the plan (the owner's document of the stay, then the agency's services, E17/F01/US01/T02). Nobody checks a
                    // document issued here, so it is issued from what the booking bills now: a draft made earlier (the check-out event) may be old, the tourist tax or the
                    // collector confirmed and the data of the invoice changed since are no change of the booking that the corrections follow.
                    foreach (var draftId in await LiveDraftIdsOfAsync(candidate.Id, ct))
                    {
                        if (outcome != EnsureDraftOutcome.Created)
                            await MakeDraftAgainAsync(candidate.Id, draftId, ct);

                        await IssueCoreAsync(draftId, confirm: true, notify: false, ct);
                        issued++;
                    }
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

        // The stays of a portal that ended and then changed on the portal: the import records no event for a stay that is over.
        var changesTold = await TellTheChangesOfEndedPortalStaysAsync(orgId, todayStart, since, limit, ct);

        logger.LogInformation(
            "Fiscal documents due of org {OrgId}: {Examined} stays looked at, {Created} drafts made, {Issued} documents issued, {Skipped} skipped, {Changes} changes of portal stays told",
            orgId,
            examined,
            created,
            issued,
            skipped,
            changesTold);
        return new FiscalDocumentsDueResult(null, examined, created, issued, skipped, changesTold);
    }

    /// <summary>
    /// The issued documents of the stays of a portal that ended and then changed on the portal. The import records no event for a stay that is over, so the corrections of a change never
    /// hear of it, and the document of a portal stay is made after its check-out: each issued document of such a stay is compared with what the booking bills now (the comparison of the
    /// correction of a change) and, when the booking bills something else or is not on any more, the host gets the «To do» of a change (a credit note may be needed), once for each
    /// document and never while a correction is waiting for it. Nothing is credited or rewritten here.
    /// </summary>
    private async Task<int> TellTheChangesOfEndedPortalStaysAsync(Guid orgId, DateTime todayStart, DateTime since, int limit, CancellationToken ct)
    {
        var portals = Enum.GetValues<BookingSource>().Where(FiscalCopy.IsOtaBookingSource).ToArray();
        var documents = await db.FiscalDocuments
            .AsNoTracking()
            .Include(d => d.Lines)
            .Where(d => d.OrgId == orgId
                        && d.Source == FiscalDocumentSource.CasaZen
                        && d.Purpose == FiscalDocumentPurpose.GuestStay
                        && d.Status == FiscalDocumentStatus.Issued
                        && (d.Kind == DocumentKind.Invoice || d.Kind == DocumentKind.Receipt)
                        && !d.CreditedInFull
                        && d.BookingId != null
                        && db.Bookings.Any(b => b.Id == d.BookingId && portals.Contains(b.Source) && b.CheckOutDate < todayStart && b.CheckOutDate >= since))
            .OrderByDescending(d => d.IssueDate)
            .ThenBy(d => d.Id)
            .Take(Math.Max(0, limit))
            .ToListAsync(ct);
        if (documents.Count == 0)
            return 0;

        OrgFiscalIssuerProfile organization;
        try
        {
            (organization, _) = await RequireIssuerAsync(orgId, ct);
        }
        catch (DomainException)
        {
            return 0;
        }

        var platformCollected = _lines.PlatformCollectedChannelsOf(organization);
        var told = 0;
        foreach (var document in documents)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await TodoOfChangeWasRaisedAsync(document.Id, ct) || await PlannedCreditAsync(document.Id, ct) > 0m)
                    continue;

                var booking = await BookingOfAsync(document.BookingId, ct);
                var changed = !IsActive(booking);
                if (!changed)
                {
                    // Compared with the lines the document's own issuer billed (E17/F01/US01/T02): the owner's stay, or the agency's services, or everything.
                    var profile = await IssuerProfileAsync(booking, organization, document.IssuerProfileId, requireComplete: false, ct);
                    var rule = RuleOf(profile, booking);
                    var built = await _lines.BuildAsync(booking.Id, rule, platformCollected, ct);
                    changed = !BillsWhatTheDocumentSays(document, ComparableLinesOf(built, document, organization), rule);
                }

                if (changed)
                    told += (await TellTheHostAsync(booking, document.Id, FiscalDocumentTodos.BookingChangedReason, ct)).TodosRaised;
            }
            catch (Exception ex) when (ex is DomainException or NotFoundException)
            {
                // The rules refuse the comparison now (no tax rule), or the booking is gone: the document is looked at again at the next run.
                db.ChangeTracker.Clear();
                logger.LogInformation(
                    "Document {DocumentId} of org {OrgId} not compared with its booking now ({Code})",
                    document.Id,
                    orgId,
                    ex is DomainException refusal ? refusal.Code : "booking_not_found");
            }
        }

        return told;
    }

    /// <summary>The host was told that the booking of the document changed (<see cref="FiscalDocumentTodos.BookingChangedReason"/>), whatever became of the item since: it is told once.</summary>
    private Task<bool> TodoOfChangeWasRaisedAsync(Guid documentId, CancellationToken ct) =>
        db.InternalActivities
            .AsNoTracking()
            .AnyAsync(a => a.EntityType == FiscalDocumentTodos.EntityType && a.EntityId == documentId && a.Reason == FiscalDocumentTodos.BookingChangedReason, ct);

    /// <summary>The draft made again from the booking, under the lock of the booking as the corrections of a change are: a change handled at the same moment never rewrites it too.</summary>
    private async Task MakeDraftAgainAsync(Guid bookingId, Guid draftId, CancellationToken ct)
    {
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db,
            ct,
            (PostgresAdvisoryLocks.Scope.FiscalDocumentBooking, bookingId.ToString("N")));

        await RefreshDraftAsync(draftId, ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);
    }

    /// <summary>Nothing is missing in the sections «issuer» and «VAT» of the billing setup of the organization (true when no billing setup is wired, as in a service built by hand).</summary>
    private async Task<bool> IssuerAndVatSetupIsCompleteAsync(Guid orgId, CancellationToken ct)
    {
        if (billingSetup is null)
            return true;

        var progress = await billingSetup.GetProgressAsync(orgId, [BillingSetupSections.Issuer, BillingSetupSections.Vat], ct);
        return progress.Missing.Count == 0;
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

    /// <summary>
    /// The drafts CasaZen made for the booking (not a credit note), one for each part of the plan in its order: the document of the stay, then the services of the
    /// agency (E17/F01/US01/T02); the newest of a part when there are several.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> LiveDraftIdsOfAsync(Guid bookingId, CancellationToken ct)
    {
        var drafts = await db.FiscalDocuments
            .AsNoTracking()
            .Where(d => d.BookingId == bookingId
                        && d.Source == FiscalDocumentSource.CasaZen
                        && (d.Purpose == FiscalDocumentPurpose.GuestStay || d.Purpose == FiscalDocumentPurpose.GuestExtraSupplement)
                        && d.Status == FiscalDocumentStatus.Draft
                        && d.Kind != DocumentKind.CreditNote)
            .OrderBy(d => d.ForAgencyServices)
            .ThenByDescending(d => d.CreatedAt)
            .Select(d => new { d.Id, d.ForAgencyServices, d.SupplementForExtraOrderId })
            .ToListAsync(ct);
        return drafts.GroupBy(d => new { d.ForAgencyServices, d.SupplementForExtraOrderId }).Select(g => g.First().Id).ToList();
    }

    /// <summary>
    /// Tells the hosts of the booking that a document was issued (<see cref="FiscalDocumentLiveRefresh"/>): a push that the decorator turns into the entry of the bell and the live
    /// message that makes the open pages read the list of the documents and the booking again. Best effort after the issue is committed.
    /// </summary>
    private void TellTheHostsOfTheIssue(FiscalDocument document, Org org) =>
        FiscalDocumentLiveRefresh.Tell(pushNotifications, texts, document, org, logger);
}
