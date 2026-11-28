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

public sealed partial class FiscalDocumentService
{
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

        if (!await HasLiveDocumentAsync(bookingId, ct))
            return BookingChangeOutcome.None;

        if (!IsActive(booking) && change != BookingChange.Refunded && await RefundInProgressAsync(bookingId, ct) && await HasIssuedLiveDocumentAsync(bookingId, ct))
            throw new FiscalDocumentChangePendingException(bookingId);

        var plan = new ChangePlan();
        await using (var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
                         db,
                         ct,
                         (PostgresAdvisoryLocks.Scope.FiscalDocumentBooking, bookingId.ToString("N"))))
        {
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

    private async Task FollowDraftAsync(FiscalDocument draft, BookingFacts booking, bool active, BookingChange change, ChangePlan plan, CancellationToken ct)
    {
        if (!active)
        {
            // The replacement of an invoice the SDI rejected holds its number (E17/F01/US02/T03): never discarded, the host decides, as when money was collected.
            if (HoldsAReplacedNumber(draft) || await CollectedOfAsync(booking.Id, ct) > 0m)
            {
                plan.Todos.Add((draft.Id, FiscalDocumentTodos.BookingChangedReason));
                return;
            }

            draft.Status = FiscalDocumentStatus.Discarded;
            draft.UpdatedAt = UtcNow;
            await db.SaveChangesAsync(ct);
            await CloseReviewTodoAsync(draft.OrgId, draft.Id, ct);
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
        if (active && change == BookingChange.Refunded)
            return;

        // An invoice the SDI rejected never was issued: a credit note does not correct it (its correction replaces it, E17/F01/US02/T03), so the host is told that the booking changed and
        // decides what to do with the rejected invoice.
        if (issued.SdiStatus == FiscalDocumentSdiStatuses.Rejected)
        {
            plan.Todos.Add((issued.Id, FiscalDocumentTodos.BookingChangedReason));
            return;
        }

        OrgFiscalIssuerProfile organization;
        FiscalTaxRule rule;
        try
        {
            (organization, _) = await RequireIssuerAsync(booking.OrgId, ct);
            var profile = await IssuerProfileAsync(booking, organization, issued.IssuerProfileId, requireComplete: false, ct);
            rule = RuleOf(profile, booking);
        }
        catch (DomainException ex)
        {
            logger.LogWarning("Document {DocumentId} of booking {BookingId}: no correction prepared after {Change} ({Code})", issued.Id, booking.Id, change, ex.Code);
            plan.Todos.Add((issued.Id, FiscalDocumentTodos.BookingChangedReason));
            return;
        }

        plan.AutoIssue = organization.AutoIssueGuestDocuments;
        var credited = await CreditedSoFarAsync(issued.Id, ct);
        var planned = await PlannedCreditAsync(issued.Id, ct);
        var uncovered = issued.Total - credited - planned;
        var reason = texts.Get(ReasonKeyOf(booking, change), DocumentLanguages.Italian);

        if (active)
        {
            var built = await _lines.BuildAsync(booking.Id, rule, _lines.PlatformCollectedChannelsOf(organization), ct);
            FiscalDocumentAmounts? now = null;
            try
            {
                now = FiscalDocumentTotals.FromGross(ComparableLinesOf(built, issued, organization), rule.StampDuty);
            }
            catch (DomainException)
            {
            }

            if (now is not null && Matches(issued, now))
            {
                // The booking bills what the document says again (it went back as it was): a «To do» that asked the host to look at the document, because a credit note may be needed, is done.
                await CloseReviewTodoAsync(issued.OrgId, issued.Id, ct);
                return;
            }

            if (uncovered > 0m)
                await PrepareCreditNoteAsync(issued, credited, uncovered, reason, rule, plan, ct);

            if (now is null)
                return;

            try
            {
                var drafts = await MakeDraftsOfThePlanAsync(booking.Id, null, onlyMissing: false, ct);
                if (drafts.FirstOrDefault(d => d.ForAgencyServices == issued.ForAgencyServices && d.Status == FiscalDocumentStatus.Draft) is { } draft
                    && !plan.IssueInOrder.Contains(draft.Id))
                {
                    plan.IssueInOrder.Add(draft.Id);
                }
            }
            catch (DomainException ex)
            {
                logger.LogWarning("Booking {BookingId}: the new draft after the credit note of {DocumentId} was not made ({Code})", booking.Id, issued.Id, ex.Code);
            }

            return;
        }

        if (issued.PaymentChannel == FiscalPaymentChannel.Portal
            || (FiscalCopy.IsOtaBookingSource(booking.Source) && await OtaPaidOfAsync(booking.Id, ct) is null))
        {
            plan.Todos.Add((issued.Id, FiscalDocumentTodos.BookingChangedReason));
            return;
        }

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
        var note = BuildCreditNote(issued, credited, amount, reason, null, rule);
        db.FiscalDocuments.Add(note);
        await db.SaveChangesAsync(ct);
        plan.CreditNoteIds.Add(note.Id);
        plan.IssueInOrder.Insert(plan.CreditNoteIds.Count - 1, note.Id);
        logger.LogInformation("Credit note draft {CreditNoteId} prepared for document {DocumentId} of booking {BookingId}", note.Id, issued.Id, issued.BookingId);
    }

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

    /// <summary>
    /// The «To do» that asks the host to look at a document (<c>ReviewFiscalDocument</c>: «a credit note may be needed», «check and issue the credit note») done by the system, because what it asked for
    /// has happened or is not needed any more: the document it names was issued or discarded, was made again from its booking, or its booking bills what it says again (FIX/RES-DOCS). The item is found
    /// and closed in the organization of the document, once (a second close keeps the first time and the first closer), and in the transaction of the change that makes it moot: the context, and so the
    /// connection, is the one of that change. A document with no open item is the usual case, and costs one read; one closed or gone since it was read leaves nothing to do.
    /// </summary>
    private async Task CloseReviewTodoAsync(Guid orgId, Guid documentId, CancellationToken ct)
    {
        if (activities is null)
            return;

        if (await activities.FindOpenAsync(orgId, nameof(ActivityAction.ReviewFiscalDocument), FiscalDocumentTodos.EntityType, documentId, ct) is not { } open)
            return;

        try
        {
            await activities.CloseAsync(open.Id, InternalActivity.ClosedBySystem, ct);
        }
        catch (NotFoundException)
        {
            // Closed or gone since it was read: nothing is left to close.
        }
    }

    private static bool Matches(FiscalDocument document, FiscalDocumentAmounts amounts)
    {
        if (document.Total != amounts.Total)
            return false;

        var stored = document.Lines.GroupBy(l => l.LineType).ToDictionary(g => g.Key, g => g.Sum(l => l.TotalPrice));
        var current = amounts.Lines.GroupBy(l => l.LineType).ToDictionary(g => g.Key, g => g.Sum(l => l.TotalPrice));
        return stored.Count == current.Count && stored.All(pair => current.TryGetValue(pair.Key, out var net) && net == pair.Value);
    }

    /// <summary>
    /// The lines the booking bills now that an issued document is compared with (the ones its own issuer bills), <b>without a line of tourist tax that only the default brings</b>: an Italian portal booking
    /// nobody said anything about is billed the tax «da pagare in struttura» since FIX/BE-IT-TOURISTTAX-DEFAULTS, and a document issued before that has no such line. That is no change of the booking: no «To do»
    /// of a change, no credit note and no new document for the sole addition of that line (the tolerance is for a document with no line of tourist tax at all; any other difference is a change, and
    /// the new draft carries the line). Anything else that says who collects (a rule of the comune, a record of the stay, an answer of the organization) is not the default and is compared as it is.
    /// </summary>
    private static IReadOnlyList<FiscalDocumentGrossLine> ComparableLinesOf(GuestDocumentLines built, FiscalDocument issued, OrgFiscalIssuerProfile organization)
    {
        var lines = GuestDocumentLineBuilder.LinesOf(built.Lines, issued, organization);
        return built.TouristTaxFromDefault && !issued.Lines.Any(line => line.LineType == FiscalLineType.TouristTax)
            ? [.. lines.Where(line => line.LineType != FiscalLineType.TouristTax)]
            : lines;
    }

    private static bool BillsWhatTheDocumentSays(FiscalDocument document, IReadOnlyList<FiscalDocumentGrossLine> lines, FiscalTaxRule rule)
    {
        try
        {
            return Matches(document, FiscalDocumentTotals.FromGross(lines, rule.StampDuty));
        }
        catch (DomainException)
        {
            return false;
        }
    }

    private static string ReasonKeyOf(BookingFacts booking, BookingChange change)
    {
        if (IsActive(booking))
            return CorrectionModifiedKey;

        if (change == BookingChange.Refunded)
            return CorrectionRefundedKey;

        return booking.NoShowAt is not null && booking.Status != BookingStatus.Cancelled ? CorrectionNoShowKey : CorrectionCancelledKey;
    }

    private static bool IsActive(BookingFacts booking) =>
        AlloggiatiStatusRules.ActiveBookingStatuses.Contains(booking.Status) && booking.NoShowAt is null;

    private async Task<decimal> CollectedOfAsync(Guid bookingId, CancellationToken ct) =>
        (await CollectedByBookingAsync(db, [bookingId], ct)).GetValueOrDefault(bookingId);

    private Task<decimal?> OtaPaidOfAsync(Guid bookingId, CancellationToken ct) =>
        db.Bookings.AsNoTracking().Where(b => b.Id == bookingId).Select(b => b.OtaPaidAmount).FirstOrDefaultAsync(ct);

    private Task<bool> RefundInProgressAsync(Guid bookingId, CancellationToken ct) =>
        db.PaymentRefunds
            .AsNoTracking()
            .AnyAsync(r => r.Payment.BookingId == bookingId && (r.Status == PaymentRefundStatus.Pending || r.Status == PaymentRefundStatus.RequiresAction), ct);

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

    private sealed class ChangePlan
    {
        public bool AutoIssue { get; set; }

        public int Refreshed { get; set; }

        public int Discarded { get; set; }

        public List<Guid> CreditNoteIds { get; } = [];

        public List<Guid> IssueInOrder { get; } = [];

        public List<(Guid DocumentId, string Reason)> Todos { get; } = [];
    }
}
