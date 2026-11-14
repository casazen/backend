using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using DocumentKind = Casazen.Core.Entities.Enums.FiscalDocumentKind;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// What the documents of a booking do when the booking changes (E12/F01/US01/T01, T03, I12): a draft follows the booking; an issued document is never rewritten, it is corrected by a credit
/// note (the reversal receipt of a receipt: the same kind, the same series) and, when the booking is still on, a new draft.
/// </summary>
public sealed partial class FiscalDocumentService
{
    /// <summary>The reason printed on a correction, by what happened (<c>SharedResources</c>, Italian: the language of the lines of a document).</summary>
    private const string CorrectionModifiedKey = "FiscalDocCorrectionModified";
    private const string CorrectionCancelledKey = "FiscalDocCorrectionCancelled";
    private const string CorrectionNoShowKey = "FiscalDocCorrectionNoShow";
    private const string CorrectionRefundedKey = "FiscalDocCorrectionRefunded";

    public async Task<BookingChangeOutcome> ApplyBookingChangeAsync(Guid bookingId, BookingChange change, CancellationToken ct = default)
    {
        BookingFacts booking;
        try
        {
            booking = await BookingOfAsync(bookingId, ct);
        }
        catch (NotFoundException)
        {
            logger.LogInformation("Booking {BookingId} not found: no document to follow its change ({Change})", bookingId, change);
            return BookingChangeOutcome.None;
        }

        // Nothing to follow for a booking with no live document (none yet, or all credited in full: the next one is made by the daily run).
        if (!await HasLiveDocumentAsync(bookingId, ct))
            return BookingChangeOutcome.None;

        // A cancelled booking (or a no-show) is credited by what the guest ends up paying: a refund still under way is not known yet. The event is delivered again; the refund that
        // succeeds records PaymentRefunded, which prepares the credit note, so nothing is ever consumed with no effect.
        if (!IsActive(booking) && change != BookingChange.Refunded && await RefundInProgressAsync(bookingId, ct) && await HasIssuedLiveDocumentAsync(bookingId, ct))
            throw new FiscalDocumentChangePendingException(bookingId);

        var plan = new ChangePlan();
        await using (var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
                         db,
                         ct,
                         (PostgresAdvisoryLocks.Scope.FiscalDocumentBooking, bookingId.ToString("N"))))
        {
            // The booking and its documents as they are now, under the lock: an event of the same booking may have corrected them while this one waited.
            booking = await BookingOfAsync(bookingId, ct);
            var active = IsActive(booking);
            var documents = await db.FiscalDocuments
                .Include(d => d.Lines)
                .Where(d => d.BookingId == bookingId
                            && d.Source == FiscalDocumentSource.CasaZen
                            && d.Purpose == FiscalDocumentPurpose.GuestStay
                            && d.Status != FiscalDocumentStatus.Discarded
                            && d.Kind != DocumentKind.CreditNote
                            && !d.CreditedInFull)
                .OrderBy(d => d.CreatedAt)
                .ToListAsync(ct);

            foreach (var document in documents.Where(d => d.Status == FiscalDocumentStatus.Draft))
                await FollowDraftAsync(document, booking, active, change, plan, ct);

            foreach (var document in documents.Where(d => d.Status == FiscalDocumentStatus.Issued && d.Kind is DocumentKind.Invoice or DocumentKind.Receipt))
                await CorrectIssuedAsync(document, booking, active, change, plan, ct);

            if (transaction is not null)
                await transaction.CommitAsync(ct);
        }

        return await FinishAsync(booking, plan, ct);
    }

    /// <summary>
    /// A draft follows its booking: made again from it while the booking is active; discarded when the booking bills nothing any more and nothing was collected for it; kept, with a «To do»
    /// for the host, when money was collected (the host decides) or when the draft cannot be made again (its data is missing now).
    /// </summary>
    private async Task FollowDraftAsync(FiscalDocument draft, BookingFacts booking, bool active, BookingChange change, ChangePlan plan, CancellationToken ct)
    {
        if (!active)
        {
            if (await CollectedOfAsync(booking.Id, ct) > 0m)
            {
                plan.Todos.Add((draft.Id, FiscalDocumentTodos.BookingChangedReason));
                return;
            }

            draft.Status = FiscalDocumentStatus.Discarded;
            draft.UpdatedAt = UtcNow;
            await db.SaveChangesAsync(ct);
            plan.Discarded++;
            logger.LogInformation("Draft {DocumentId} of booking {BookingId} discarded: the booking is no longer active ({Change})", draft.Id, booking.Id, change);
            return;
        }

        try
        {
            await RebuildAsync(draft, ct);
            plan.Refreshed++;
        }
        catch (DomainException ex)
        {
            // The draft cannot be made again now (the data of the guest or of the issuer is missing): it keeps what it had, and the host is told to look at it.
            logger.LogWarning("Draft {DocumentId} of booking {BookingId} not made again after {Change}: {Code}", draft.Id, booking.Id, change, ex.Code);
            plan.Todos.Add((draft.Id, FiscalDocumentTodos.BookingChangedReason));
        }
    }

    /// <summary>
    /// The issued document of a booking that changed is never rewritten: it is corrected by credit notes, decided from the state of the booking and of the documents, never from the event
    /// (so an event delivered again, or two events of one change, give one correction).
    /// <list type="bullet">
    /// <item>The booking is still on and bills what the document says (a change of notes, an edit that changed nothing billed): nothing.</item>
    /// <item>The booking is still on and bills something else (the dates, the guests, the amounts): a full credit note of what no credit note covers yet, and a new draft with the new lines.</item>
    /// <item>The booking was cancelled, or is a no-show (⚖ I12: what the guest is charged is the consideration): a credit note for the difference between the document and what the guest
    /// paid, net of the refunds. Nothing paid is a full credit note; a penalty that equals the document keeps it; a refund is the credit note of the amount refunded, its VAT split in
    /// proportion to the original lines. A portal collected the money itself: CasaZen does not know what it kept, so the host is told and decides.</item>
    /// </list>
    /// What cannot be decided here (the issuer's profile is incomplete, no tax rule) is the «To do» of T01: a credit note may be needed.
    /// </summary>
    private async Task CorrectIssuedAsync(FiscalDocument issued, BookingFacts booking, bool active, BookingChange change, ChangePlan plan, CancellationToken ct)
    {
        // A refund of an active booking is its own price (an edit of the price is a BookingModified): nothing to correct.
        if (active && change == BookingChange.Refunded)
            return;

        OrgFiscalIssuerProfile profile;
        FiscalTaxRule rule;
        try
        {
            (profile, _) = await RequireIssuerAsync(booking.OrgId, ct);
            rule = RuleOf(profile, booking);
        }
        catch (DomainException ex)
        {
            logger.LogWarning("Document {DocumentId} of booking {BookingId}: no correction prepared after {Change} ({Code})", issued.Id, booking.Id, change, ex.Code);
            plan.Todos.Add((issued.Id, FiscalDocumentTodos.BookingChangedReason));
            return;
        }

        plan.AutoIssue = profile.AutoIssueGuestDocuments;
        var credited = await CreditedSoFarAsync(issued.Id, ct);
        var planned = await PlannedCreditAsync(issued.Id, ct);
        var uncovered = issued.Total - credited - planned;
        var reason = texts.Get(ReasonKeyOf(booking, change), DocumentLanguages.Italian);

        if (active)
        {
            // The lines the booking bills now, against what the document says.
            var built = await _lines.BuildAsync(booking.Id, rule, _lines.PlatformCollectedChannelsOf(profile), ct);
            FiscalDocumentAmounts? now = null;
            try
            {
                now = FiscalDocumentTotals.FromGross(built.Lines, rule.StampDuty);
            }
            catch (DomainException)
            {
                // The booking bills nothing now (a price of zero): the whole document is credited and there is no new draft.
            }

            if (now is not null && Matches(issued, now))
                return;

            if (uncovered > 0m)
                await PrepareCreditNoteAsync(issued, credited, uncovered, reason, rule, plan, ct);

            if (now is null)
                return;

            try
            {
                var draft = await CreateDraftAsync(booking.Id, null, ct);
                plan.IssueInOrder.Add(draft.Id);
            }
            catch (DomainException ex)
            {
                // The new draft cannot be made now (the data of the guest is missing): the host sees the credit note, the daily run makes the draft when the data is there.
                logger.LogWarning("Booking {BookingId}: the new draft after the credit note of {DocumentId} was not made ({Code})", booking.Id, issued.Id, ex.Code);
            }

            return;
        }

        // Not on any more. A portal collected the money: CasaZen does not know what it kept, only the host does.
        if (issued.PaymentChannel == FiscalPaymentChannel.Portal)
        {
            plan.Todos.Add((issued.Id, FiscalDocumentTodos.BookingChangedReason));
            return;
        }

        // What the guest ends up paying is the consideration: collected net of the refunds, never more than the document.
        var charged = Math.Min(issued.Total, Math.Max(await CollectedOfAsync(booking.Id, ct), 0m));
        var needed = uncovered - charged;
        if (needed > 0m)
            await PrepareCreditNoteAsync(issued, credited, needed, reason, rule, plan, ct);
    }

    /// <summary>The draft credit note of a correction: saved, so that what it credits is counted at once, and kept for the host or for the automatic issue.</summary>
    private async Task PrepareCreditNoteAsync(
        FiscalDocument issued,
        decimal credited,
        decimal amount,
        string reason,
        FiscalTaxRule rule,
        ChangePlan plan,
        CancellationToken ct)
    {
        // The whole of what is left is the document as stored; a part is spread over its lines in proportion.
        var note = BuildCreditNote(issued, credited, amount, reason, null, rule);
        db.FiscalDocuments.Add(note);
        await db.SaveChangesAsync(ct);
        plan.CreditNoteIds.Add(note.Id);
        plan.IssueInOrder.Insert(plan.CreditNoteIds.Count - 1, note.Id);
        logger.LogInformation("Credit note draft {CreditNoteId} prepared for document {DocumentId} of booking {BookingId}", note.Id, issued.Id, issued.BookingId);
    }

    /// <summary>
    /// After the corrections are committed: with the automatic issue of the profile, the credit notes and then the new draft are issued through the same path as the API (a new draft cannot be
    /// issued before its credit note, so the chain stops at the first refusal); what is still a draft is the host's «To do» («Controlla ed emetti la nota di credito»); the documents that
    /// need a look are the «To do» of T01. A «To do» is raised once for a document while it is open.
    /// </summary>
    private async Task<BookingChangeOutcome> FinishAsync(BookingFacts booking, ChangePlan plan, CancellationToken ct)
    {
        var issuedIds = new HashSet<Guid>();
        if (plan.AutoIssue)
        {
            foreach (var id in plan.IssueInOrder)
            {
                try
                {
                    await IssueCoreAsync(id, confirm: true, notify: true, ct);
                    issuedIds.Add(id);
                }
                catch (DomainException ex)
                {
                    db.ChangeTracker.Clear();
                    logger.LogWarning("Document {DocumentId} of booking {BookingId} not issued after its correction ({Code}): it waits for the host", id, booking.Id, ex.Code);
                    break;
                }
            }
        }

        var todos = 0;
        foreach (var creditNoteId in plan.CreditNoteIds.Where(id => !issuedIds.Contains(id)))
            todos += (await TellTheHostAsync(booking, creditNoteId, FiscalDocumentTodos.CreditNoteToIssueReason, ct)).TodosRaised;
        foreach (var (documentId, reason) in plan.Todos)
            todos += (await TellTheHostAsync(booking, documentId, reason, ct)).TodosRaised;

        return new BookingChangeOutcome(plan.Refreshed, plan.Discarded, plan.CreditNoteIds.Count, issuedIds.Count, todos);
    }

    /// <summary>The «To do» of a document for the host (no personal data: a code, the document and its property); an item already open is the one raised.</summary>
    private async Task<BookingChangeOutcome> TellTheHostAsync(BookingFacts booking, Guid documentId, string reason, CancellationToken ct)
    {
        if (activities is null)
            return BookingChangeOutcome.None;

        try
        {
            await activities.CreateAsync(
                new CreateTodoRequest
                {
                    Type = ActivityType.FiscalDocument,
                    Action = ActivityAction.ReviewFiscalDocument,
                    EntityType = FiscalDocumentTodos.EntityType,
                    EntityId = documentId,
                    Urgency = ActivityUrgency.Medium,
                    PropertyId = booking.PropertyId,
                    OrgId = booking.OrgId,
                    Reason = reason,
                },
                ct);
            return new BookingChangeOutcome(0, 0, 0, 0, 1);
        }
        catch (NotFoundException)
        {
            // The property is gone (deleted since): nobody to tell, and a retry would find the same.
            logger.LogInformation("Document {DocumentId} of booking {BookingId}: property not found, no «To do» raised ({Reason})", documentId, booking.Id, reason);
            return BookingChangeOutcome.None;
        }
    }

    /// <summary>The document bills what the booking bills now: the same total and the same net for every type of line.</summary>
    private static bool Matches(FiscalDocument document, FiscalDocumentAmounts amounts)
    {
        if (document.Total != amounts.Total)
            return false;

        var stored = document.Lines.GroupBy(l => l.LineType).ToDictionary(g => g.Key, g => g.Sum(l => l.TotalPrice));
        var current = amounts.Lines.GroupBy(l => l.LineType).ToDictionary(g => g.Key, g => g.Sum(l => l.TotalPrice));
        return stored.Count == current.Count && stored.All(pair => current.TryGetValue(pair.Key, out var net) && net == pair.Value);
    }

    /// <summary>The key of the reason printed on a correction: what happened to the booking as it is now.</summary>
    private static string ReasonKeyOf(BookingFacts booking, BookingChange change)
    {
        if (IsActive(booking))
            return CorrectionModifiedKey;

        if (change == BookingChange.Refunded)
            return CorrectionRefundedKey;

        return booking.NoShowAt is not null && booking.Status != BookingStatus.Cancelled ? CorrectionNoShowKey : CorrectionCancelledKey;
    }

    /// <summary>The booking bills a document: a status of the one definition of the code and no no-show mark (<see cref="AlloggiatiStatusRules"/>).</summary>
    private static bool IsActive(BookingFacts booking) =>
        AlloggiatiStatusRules.ActiveBookingStatuses.Contains(booking.Status) && booking.NoShowAt is null;

    /// <summary>The money collected for a booking, net of the refunds.</summary>
    private async Task<decimal> CollectedOfAsync(Guid bookingId, CancellationToken ct) =>
        (await CollectedByBookingAsync(db, [bookingId], ct)).GetValueOrDefault(bookingId);

    /// <summary>A refund of a payment of the booking was asked and Stripe has not confirmed it (pending, or waiting for an action of the guest's bank).</summary>
    private Task<bool> RefundInProgressAsync(Guid bookingId, CancellationToken ct) =>
        db.PaymentRefunds
            .AsNoTracking()
            .AnyAsync(r => r.Payment.BookingId == bookingId && (r.Status == PaymentRefundStatus.Pending || r.Status == PaymentRefundStatus.RequiresAction), ct);

    /// <summary>The booking has an issued document that is not a credit note, not a proforma and not credited in full.</summary>
    private Task<bool> HasIssuedLiveDocumentAsync(Guid bookingId, CancellationToken ct) =>
        db.FiscalDocuments
            .AsNoTracking()
            .AnyAsync(
                d => d.BookingId == bookingId
                     && d.Purpose == FiscalDocumentPurpose.GuestStay
                     && d.Status == FiscalDocumentStatus.Issued
                     && d.Kind != DocumentKind.CreditNote
                     && d.Kind != DocumentKind.Proforma
                     && !d.CreditedInFull,
                ct);

    /// <summary>What a change decided, to be done once the corrections are committed.</summary>
    private sealed class ChangePlan
    {
        /// <summary>The profile issues without a person.</summary>
        public bool AutoIssue { get; set; }

        public int Refreshed { get; set; }

        public int Discarded { get; set; }

        /// <summary>The credit notes prepared, as drafts.</summary>
        public List<Guid> CreditNoteIds { get; } = [];

        /// <summary>What the automatic issue issues, in order: the credit notes, then the new draft.</summary>
        public List<Guid> IssueInOrder { get; } = [];

        /// <summary>The documents that need a look, with the reason of the «To do».</summary>
        public List<(Guid DocumentId, string Reason)> Todos { get; } = [];
    }
}
