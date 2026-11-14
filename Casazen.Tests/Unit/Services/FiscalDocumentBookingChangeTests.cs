using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using DocumentKind = Casazen.Core.Entities.Enums.FiscalDocumentKind;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E12/F01/US01/T03 (NF-83, NF-112, ⚖ I12, acceptance step B12): when a booking that already has an issued document changes, CasaZen prepares the correcting document and never rewrites the
/// issued one. A date change is a full credit note and a new draft; a cancellation, a no-show and a refund are a credit note for what the guest does not pay, its VAT split in proportion to
/// the original lines; a receipt is corrected by a reversal receipt; each correction is a draft with a «To do» (issued at once with the automatic issue); every correction is idempotent per
/// event; a credit note never exceeds what is left to credit.
/// </summary>
public class FiscalDocumentBookingChangeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid OrgId = Guid.NewGuid();

    /// <summary>
    /// PROOF TEST: money rule. A cancelled booking whose guest was refunded 100.00 of the 362.00 it paid gets a credit note of exactly 100.00 (what the guest does not pay any more), with the VAT split
    /// in proportion to the lines of the original (82.88 of the stay and 13.81 of the cleaning at 10%, 3.31 of tourist tax out of VAT: 91.21 taxable, 8.79 VAT); the original is not touched.
    /// A penalty that equals the document keeps it; a part of the document paid is credited for the difference; nothing paid is a full credit note, the document as stored.
    /// </summary>
    [Fact]
    public async Task BookingRefund_CreditNoteEqualsRefund_VatProportional_Test()
    {
        await using var db = NewDb();
        await SeedOrgAsync(db);
        var property = await AddPropertyAsync(db);
        var service = ServiceOf(db);
        var handler = HandlerOf(service);

        // Refunded 100.00 of 362.00: the guest pays 262.00.
        var refunded = await AddBookingAsync(db, property);
        var original = await IssueAsync(service, refunded);
        var before = Snapshot(await ReloadAsync(db, original.Id));
        var payment = await AddPaymentAsync(db, refunded, 362m, refunded: 100m);
        await CancelAsync(db, refunded);

        await handler.HandleAsync(RefundEvent(payment, refunded, 100m), CancellationToken.None);

        var note = Assert.Single(await db.FiscalDocuments.AsNoTracking().Include(d => d.Lines).Where(d => d.Kind == DocumentKind.CreditNote && d.BookingId == refunded).ToListAsync());
        Assert.Equal((FiscalDocumentStatus.Draft, original.Id, 100.00m), (note.Status, note.CreditedDocumentId, note.Total));
        Assert.Equal((91.21m, 8.79m), (note.TaxableBase, note.VatAmount));
        Assert.Equal(
            [(FiscalLineType.Accommodation, 82.88m), (FiscalLineType.Cleaning, 13.81m), (FiscalLineType.TouristTax, 3.31m)],
            note.Lines.OrderBy(l => l.LineNumber).Select(l => (l.LineType, l.TotalPrice + (l.VatAmount ?? 0m))).ToArray());
        Assert.Equal(before, Snapshot(await ReloadAsync(db, original.Id)));
        var todo = Assert.Single(await TodosAsync(db));
        Assert.Equal((FiscalDocumentTodos.CreditNoteToIssueReason, note.Id), (todo.Reason, todo.EntityId));

        // Issued, the draft takes the next number of the series of the document, and the document keeps standing (it is credited in part).
        var issued = await service.IssueAsync(note.Id, confirm: true);
        Assert.Equal(("2026/2", FiscalDocumentStatus.Issued, DocumentKind.CreditNote, original.Series), (issued.Number, issued.Status, issued.Kind, issued.Series));
        Assert.False((await ReloadAsync(db, original.Id)).CreditedInFull);

        // A penalty that equals the document: the guest pays all of it, the document stays and nothing is corrected.
        var penalty = await AddBookingAsync(db, property);
        await IssueAsync(service, penalty);
        await AddPaymentAsync(db, penalty, 362m);
        await CancelAsync(db, penalty);
        await handler.HandleAsync(BookingEvent(DomainEventType.BookingCancelled, penalty), CancellationToken.None);
        Assert.Empty(await db.FiscalDocuments.AsNoTracking().Where(d => d.Kind == DocumentKind.CreditNote && d.BookingId == penalty).ToListAsync());

        // A part of the document paid (a deposit of 100.00 kept): credited for the difference, 262.00.
        var deposit = await AddBookingAsync(db, property);
        await IssueAsync(service, deposit);
        await AddPaymentAsync(db, deposit, 100m);
        await CancelAsync(db, deposit);
        await handler.HandleAsync(BookingEvent(DomainEventType.BookingCancelled, deposit), CancellationToken.None);
        Assert.Equal(262.00m, Assert.Single(await db.FiscalDocuments.AsNoTracking().Where(d => d.Kind == DocumentKind.CreditNote && d.BookingId == deposit).ToListAsync()).Total);

        // Nothing paid: a full credit note, the lines and amounts of the document as stored.
        var unpaid = await AddBookingAsync(db, property);
        var unpaidDocument = await IssueAsync(service, unpaid);
        await CancelAsync(db, unpaid);
        await handler.HandleAsync(BookingEvent(DomainEventType.BookingCancelled, unpaid), CancellationToken.None);
        var full = Assert.Single(await db.FiscalDocuments.AsNoTracking().Include(d => d.Lines).Where(d => d.Kind == DocumentKind.CreditNote && d.BookingId == unpaid).ToListAsync());
        var stored = await ReloadAsync(db, unpaidDocument.Id);
        Assert.Equal((stored.TaxableBase, stored.VatAmount, stored.Total), (full.TaxableBase, full.VatAmount, full.Total));
        Assert.Equal(
            stored.Lines.OrderBy(l => l.LineNumber).Select(l => (l.LineType, l.TotalPrice, l.VatAmount)).ToArray(),
            full.Lines.OrderBy(l => l.LineNumber).Select(l => (l.LineType, l.TotalPrice, l.VatAmount)).ToArray());
    }

    /// <summary>
    /// PROOF TEST: money rule. A date change of an issued booking is a full credit note of the document (its amounts as stored) and a new draft with the new lines; the document is not touched.
    /// The draft is made beside the credit note, but cannot be issued before it (409), and issued after it: consecutive numbers of one series. With the automatic issue of the profile the credit
    /// note and then the new document are issued at once. A change that bills nothing different (notes, a guest more at the same price) corrects nothing.
    /// </summary>
    [Fact]
    public async Task BookingDateChange_FullCreditAndNewDraft_Test()
    {
        await using var db = NewDb();
        await SeedOrgAsync(db);
        var property = await AddPropertyAsync(db);
        var service = ServiceOf(db);
        var handler = HandlerOf(service);
        var booking = await AddBookingAsync(db, property);
        var original = await IssueAsync(service, booking);
        var before = Snapshot(await ReloadAsync(db, original.Id));

        // Nothing billed changes: nothing is corrected.
        await ChangeBookingAsync(db, booking, b => b.SpecialRequests = "TEST nota");
        Assert.Equal(BookingChangeOutcome.None, await service.ApplyBookingChangeAsync(booking, BookingChange.Modified));

        // The host moves the check-out by two nights.
        await ChangeBookingAsync(db, booking, b => { b.CheckOutDate = b.CheckOutDate.AddDays(2); b.BasePrice = 550m; b.TouristTax = 20m; b.TotalPrice = 570m; });
        await handler.HandleAsync(BookingEvent(DomainEventType.BookingModified, booking), CancellationToken.None);

        var documents = await db.FiscalDocuments.AsNoTracking().Include(d => d.Lines).Where(d => d.BookingId == booking).ToListAsync();
        Assert.Equal(3, documents.Count);
        var note = Assert.Single(documents, d => d.Kind == DocumentKind.CreditNote);
        var draft = Assert.Single(documents, d => d.Kind == DocumentKind.Invoice && d.Id != original.Id);
        var stored = Assert.Single(documents, d => d.Id == original.Id);
        Assert.Equal((FiscalDocumentStatus.Draft, original.Id, stored.Total), (note.Status, note.CreditedDocumentId, note.Total));
        Assert.Equal(
            stored.Lines.OrderBy(l => l.LineNumber).Select(l => (l.LineType, l.TotalPrice, l.VatAmount)).ToArray(),
            note.Lines.OrderBy(l => l.LineNumber).Select(l => (l.LineType, l.TotalPrice, l.VatAmount)).ToArray());
        Assert.Equal((FiscalDocumentStatus.Draft, 570.00m), (draft.Status, draft.Total));
        Assert.Equal(
            [(FiscalLineType.Accommodation, 500m), (FiscalLineType.Cleaning, 50m), (FiscalLineType.TouristTax, 20m)],
            draft.Lines.OrderBy(l => l.LineNumber).Select(l => (l.LineType, l.TotalPrice + (l.VatAmount ?? 0m))).ToArray());
        Assert.Equal(before, Snapshot(stored));
        Assert.Equal((FiscalDocumentTodos.CreditNoteToIssueReason, note.Id), ((await TodosAsync(db)).Single().Reason, (await TodosAsync(db)).Single().EntityId));

        // The new document waits for the credit note: it cannot be issued before it.
        var refused = await Assert.ThrowsAsync<DomainConflictException>(() => service.IssueAsync(draft.Id, confirm: true));
        Assert.Equal(FiscalDocumentErrors.AlreadyIssued, refused.Code);
        var creditNote = await service.IssueAsync(note.Id, confirm: true);
        var replacement = await service.IssueAsync(draft.Id, confirm: true);
        Assert.Equal(("2026/2", "2026/3"), (creditNote.Number, replacement.Number));
        Assert.True((await ReloadAsync(db, original.Id)).CreditedInFull);

        // With the automatic issue: the credit note and the new document are issued at once, and the same event again changes nothing.
        await using var auto = NewDb();
        await SeedOrgAsync(auto, autoIssue: true);
        var autoProperty = await AddPropertyAsync(auto);
        var autoService = ServiceOf(auto);
        var autoBooking = await AddBookingAsync(auto, autoProperty);
        await IssueAsync(autoService, autoBooking);
        await ChangeBookingAsync(auto, autoBooking, b => { b.CheckOutDate = b.CheckOutDate.AddDays(2); b.BasePrice = 550m; b.TouristTax = 20m; b.TotalPrice = 570m; });

        var done = await autoService.ApplyBookingChangeAsync(autoBooking, BookingChange.Modified);
        var repeated = await autoService.ApplyBookingChangeAsync(autoBooking, BookingChange.Modified);

        Assert.Equal((1, 2, 0), (done.CreditNotesPrepared, done.DocumentsIssued, done.TodosRaised));
        Assert.Equal(BookingChangeOutcome.None, repeated);
        var all = await auto.FiscalDocuments.AsNoTracking().Where(d => d.BookingId == autoBooking).OrderBy(d => d.SequenceNumber).ToListAsync();
        Assert.Equal(["2026/1", "2026/2", "2026/3"], all.Select(d => d.Number!).ToList());
        Assert.Equal([DocumentKind.Invoice, DocumentKind.CreditNote, DocumentKind.Invoice], all.Select(d => d.Kind).ToList());
        Assert.All(all, d => Assert.Equal(FiscalDocumentStatus.Issued, d.Status));
        Assert.Empty(await TodosAsync(auto));
    }

    /// <summary>
    /// PROOF TEST: data rule. A change delivered twice (the same event, or the two events of one change: the refund and the cancellation) is one correction: one credit note, one new draft, one «To do».
    /// The state is read as it is when the event is handled, so the order of the events does not matter; the amounts credited (issued or still drafts) are never credited again.
    /// </summary>
    [Fact]
    public async Task BookingChangeEvent_ProcessedTwice_OneCorrection_Test()
    {
        await using var db = NewDb();
        await SeedOrgAsync(db);
        var property = await AddPropertyAsync(db);
        var service = ServiceOf(db);
        var handler = HandlerOf(service);

        // A date change, the same event twice.
        var moved = await AddBookingAsync(db, property);
        await IssueAsync(service, moved);
        await ChangeBookingAsync(db, moved, b => { b.CheckOutDate = b.CheckOutDate.AddDays(1); b.BasePrice = 450m; b.TotalPrice = 462m; });
        var modified = BookingEvent(DomainEventType.BookingModified, moved);
        await handler.HandleAsync(modified, CancellationToken.None);
        await handler.HandleAsync(modified, CancellationToken.None);
        var documents = await db.FiscalDocuments.AsNoTracking().Where(d => d.BookingId == moved).ToListAsync();
        Assert.Equal(1, documents.Count(d => d.Kind == DocumentKind.CreditNote));
        Assert.Equal(2, documents.Count(d => d.Kind == DocumentKind.Invoice));
        Assert.Single(await TodosAsync(db));

        // A cancellation with a refund: the refund and the cancellation arrive in either order and twice; one credit note of what is refunded, never more.
        var cancelled = await AddBookingAsync(db, property);
        await IssueAsync(service, cancelled);
        var payment = await AddPaymentAsync(db, cancelled, 362m, refunded: 362m);
        await CancelAsync(db, cancelled);
        foreach (var delivery in new[]
                 {
                     RefundEvent(payment, cancelled, 362m),
                     BookingEvent(DomainEventType.BookingCancelled, cancelled),
                     RefundEvent(payment, cancelled, 362m),
                     BookingEvent(DomainEventType.BookingCancelled, cancelled),
                 })
        {
            await handler.HandleAsync(delivery, CancellationToken.None);
        }

        var notes = await db.FiscalDocuments.AsNoTracking().Where(d => d.Kind == DocumentKind.CreditNote && d.BookingId == cancelled).ToListAsync();
        Assert.Equal(362.00m, Assert.Single(notes).Total);
        Assert.Equal(2, (await TodosAsync(db)).Count);

        // Even with the credit note issued and the document credited in full: nothing more.
        await service.IssueAsync(notes[0].Id, confirm: true);
        await handler.HandleAsync(BookingEvent(DomainEventType.BookingCancelled, cancelled), CancellationToken.None);
        Assert.Equal(1, await db.FiscalDocuments.CountAsync(d => d.Kind == DocumentKind.CreditNote && d.BookingId == cancelled));
    }

    [Fact]
    public async Task NoShow_IsCreditedLikeACancellation_ForWhatTheGuestIsNotCharged()
    {
        await using var db = NewDb();
        await SeedOrgAsync(db);
        var property = await AddPropertyAsync(db);
        var service = ServiceOf(db);
        var handler = HandlerOf(service);
        var chargedNothing = await AddBookingAsync(db, property);
        var chargedADeposit = await AddBookingAsync(db, property);
        await IssueAsync(service, chargedNothing);
        await IssueAsync(service, chargedADeposit);
        await AddPaymentAsync(db, chargedADeposit, 100m);
        foreach (var booking in new[] { chargedNothing, chargedADeposit })
            await ChangeBookingAsync(db, booking, b => { b.Status = BookingStatus.Confirmed; b.NoShowAt = Now.UtcDateTime.AddDays(-5); });

        await handler.HandleAsync(BookingEvent(DomainEventType.BookingNoShowMarked, chargedNothing), CancellationToken.None);
        await handler.HandleAsync(BookingEvent(DomainEventType.BookingNoShowMarked, chargedADeposit), CancellationToken.None);

        Assert.Equal(362.00m, Assert.Single(await db.FiscalDocuments.AsNoTracking().Where(d => d.Kind == DocumentKind.CreditNote && d.BookingId == chargedNothing).ToListAsync()).Total);
        var deposit = Assert.Single(await db.FiscalDocuments.AsNoTracking().Include(d => d.Lines).Where(d => d.Kind == DocumentKind.CreditNote && d.BookingId == chargedADeposit).ToListAsync());
        Assert.Equal(262.00m, deposit.Total);
        Assert.All(deposit.Lines, l => Assert.Contains("no-show", l.Description, StringComparison.Ordinal));
    }

    /// <summary>
    /// A receipt (the document of a private person, no VAT) is corrected by a reversal receipt: a credit note of the same kind of document, in the same series, with no XML and nothing to send to the
    /// SDI. A portal collected the money itself: CasaZen does not know what it kept, so no credit note is made and the host is told (the «To do» of a change).
    /// </summary>
    [Fact]
    public async Task Receipt_IsReversedByAReversalReceipt_AndAPortalIsNotCreditedByGuessing()
    {
        await using var db = NewDb();
        await SeedOrgAsync(db, regime: DocumentRegime.PrivatePerson);
        var property = await AddPropertyAsync(db);
        var service = ServiceOf(db);
        var handler = HandlerOf(service);
        var receiptBooking = await AddBookingAsync(db, property);
        var receipt = await IssueAsync(service, receiptBooking);
        Assert.Equal(DocumentKind.Receipt, receipt.Kind);
        await CancelAsync(db, receiptBooking);

        await handler.HandleAsync(BookingEvent(DomainEventType.BookingCancelled, receiptBooking), CancellationToken.None);

        var reversal = await service.IssueAsync(Assert.Single(await db.FiscalDocuments.AsNoTracking().Where(d => d.Kind == DocumentKind.CreditNote).ToListAsync()).Id, confirm: true);
        Assert.Equal((DocumentKind.CreditNote, receipt.Id, receipt.Series, "2026/2"), (reversal.Kind, reversal.CreditedDocumentId, reversal.Series, reversal.Number));
        Assert.Equal((null, FiscalDocumentSdiStatuses.NotApplicable, 362.00m), (reversal.FatturaPaXml, reversal.SdiStatus, reversal.Total));
        Assert.True((await ReloadAsync(db, receipt.Id)).CreditedInFull);

        var portalBooking = await AddBookingAsync(db, property, BookingSource.Airbnb, companyCollect: true);
        var portalDocument = await IssueAsync(service, portalBooking);
        Assert.Equal(FiscalPaymentChannel.Portal, portalDocument.PaymentChannel);
        await CancelAsync(db, portalBooking);

        var outcome = await service.ApplyBookingChangeAsync(portalBooking, BookingChange.Cancelled);

        Assert.Equal((0, 1), (outcome.CreditNotesPrepared, outcome.TodosRaised));
        Assert.Equal(1, await db.FiscalDocuments.CountAsync(d => d.Kind == DocumentKind.CreditNote));
        var todo = Assert.Single(await TodosAsync(db), t => t.EntityId == portalDocument.Id);
        Assert.Equal(FiscalDocumentTodos.BookingChangedReason, todo.Reason);
    }

    /// <summary>
    /// PROOF TEST: money rule (⚖ I12). A stay of a portal the host collects from has no payment of CasaZen: what the guest paid is what the channel manager reports. A deposit of 100.00 of the
    /// 350.00 credits the difference, a document paid in full is kept, and while the channel manager says nothing nobody knows what was paid, so there is no credit note of the whole
    /// document by guessing: the host is told and decides.
    /// </summary>
    [Fact]
    public async Task PortalStayTheHostCollectsFrom_IsCreditedByWhatTheChannelManagerSaysWasPaid_AndNeverByGuessing()
    {
        await using var db = NewDb();
        await SeedOrgAsync(db);
        var property = await AddPropertyAsync(db);
        var service = ServiceOf(db);
        var handler = HandlerOf(service);

        var deposit = await AddBookingAsync(db, property, BookingSource.BookingCom, companyCollect: false);
        var depositDocument = await IssueAsync(service, deposit);
        Assert.Equal(FiscalPaymentChannel.Outside, depositDocument.PaymentChannel);
        await ChangeBookingAsync(db, deposit, b => b.OtaPaidAmount = 100m);
        await CancelAsync(db, deposit);
        await handler.HandleAsync(BookingEvent(DomainEventType.BookingCancelled, deposit), CancellationToken.None);
        Assert.Equal(250.00m, Assert.Single(await db.FiscalDocuments.AsNoTracking().Where(d => d.Kind == DocumentKind.CreditNote && d.BookingId == deposit).ToListAsync()).Total);

        var paid = await AddBookingAsync(db, property, BookingSource.BookingCom, companyCollect: false);
        await IssueAsync(service, paid);
        await ChangeBookingAsync(db, paid, b => b.OtaPaidAmount = 350m);
        await CancelAsync(db, paid);
        await handler.HandleAsync(BookingEvent(DomainEventType.BookingCancelled, paid), CancellationToken.None);
        Assert.Empty(await db.FiscalDocuments.AsNoTracking().Where(d => d.Kind == DocumentKind.CreditNote && d.BookingId == paid).ToListAsync());

        var silent = await AddBookingAsync(db, property, BookingSource.BookingCom, companyCollect: false);
        var silentDocument = await IssueAsync(service, silent);
        await CancelAsync(db, silent);
        var outcome = await service.ApplyBookingChangeAsync(silent, BookingChange.Cancelled);
        Assert.Equal((0, 1), (outcome.CreditNotesPrepared, outcome.TodosRaised));
        Assert.Empty(await db.FiscalDocuments.AsNoTracking().Where(d => d.Kind == DocumentKind.CreditNote && d.BookingId == silent).ToListAsync());
        Assert.Equal(FiscalDocumentTodos.BookingChangedReason, Assert.Single(await TodosAsync(db), t => t.EntityId == silentDocument.Id).Reason);
    }

    /// <summary>
    /// A cancelled booking whose refund is still under way cannot be credited yet: the event is delivered again (never consumed with no effect), and the refund that succeeds is the event that
    /// prepares the credit note. A refund of a booking that is still on is its own price: no correction. A draft of a booking that bills nothing any more, with nothing collected, is discarded.
    /// </summary>
    [Fact]
    public async Task CancelledBookingWithARefundUnderWay_WaitsForTheRefund_AndADraftIsDiscarded_AndARefundOfAnActiveBookingIsNothing()
    {
        await using var db = NewDb();
        await SeedOrgAsync(db);
        var property = await AddPropertyAsync(db);
        var service = ServiceOf(db);
        var handler = HandlerOf(service);
        var booking = await AddBookingAsync(db, property);
        await IssueAsync(service, booking);
        var payment = await AddPaymentAsync(db, booking, 362m);
        db.PaymentRefunds.Add(new PaymentRefund { PaymentId = payment, OrgId = OrgId, Amount = 362m, Status = PaymentRefundStatus.Pending, Origin = PaymentRefundOrigin.Host, IdempotencyKey = "TEST-1" });
        await db.SaveChangesAsync();

        // A refund of a booking that is still on: nothing.
        await handler.HandleAsync(RefundEvent(payment, booking, 100m), CancellationToken.None);
        Assert.Equal(1, await db.FiscalDocuments.CountAsync());

        await CancelAsync(db, booking);
        await Assert.ThrowsAsync<FiscalDocumentChangePendingException>(() => handler.HandleAsync(BookingEvent(DomainEventType.BookingCancelled, booking), CancellationToken.None));
        Assert.Equal(1, await db.FiscalDocuments.CountAsync());

        // The refund succeeds: the payment says so and PaymentRefunded prepares the credit note, of what was refunded.
        var refund = await db.PaymentRefunds.SingleAsync();
        refund.Status = PaymentRefundStatus.Succeeded;
        var paid = await db.Payments.SingleAsync();
        paid.RefundedAmount = 362m;
        paid.Status = PaymentStatus.Refunded;
        await db.SaveChangesAsync();
        await handler.HandleAsync(RefundEvent(payment, booking, 362m), CancellationToken.None);
        Assert.Equal(362.00m, Assert.Single(await db.FiscalDocuments.AsNoTracking().Where(d => d.Kind == DocumentKind.CreditNote).ToListAsync()).Total);

        // A draft that was never issued: discarded when the booking is cancelled with nothing collected.
        var draftOnly = await AddBookingAsync(db, property);
        var draft = await service.CreateDraftAsync(draftOnly, null);
        await CancelAsync(db, draftOnly);
        await handler.HandleAsync(BookingEvent(DomainEventType.BookingCancelled, draftOnly), CancellationToken.None);
        Assert.Equal(FiscalDocumentStatus.Discarded, (await ReloadAsync(db, draft.Id)).Status);
    }

    /// <summary>
    /// Credit notes respect the cap: the draft of a credit note is checked again when it is issued, under the lock of the series, so a credit note issued meanwhile (by hand) leaves the draft
    /// refused (422 <c>credit_amount_exceeds</c>), never more credited than the document. The draft of a credit note is not made again from the booking, and can be discarded.
    /// </summary>
    [Fact]
    public async Task CreditNoteDraft_IsCappedAgainWhenIssued_AndIsNotMadeAgainFromTheBooking()
    {
        await using var db = NewDb();
        await SeedOrgAsync(db);
        var property = await AddPropertyAsync(db);
        var service = ServiceOf(db);
        var booking = await AddBookingAsync(db, property);
        var original = await IssueAsync(service, booking);
        await CancelAsync(db, booking);
        await service.ApplyBookingChangeAsync(booking, BookingChange.Cancelled);
        var note = await db.FiscalDocuments.AsNoTracking().SingleAsync(d => d.Kind == DocumentKind.CreditNote);
        Assert.Equal(362.00m, note.Total);

        // The draft stays as it is whatever the booking says; the host credits 100.00 by hand meanwhile.
        Assert.Equal(note.Total, (await service.RefreshDraftAsync(note.Id)).Total);
        await service.CreateCreditNoteAsync(original.Id, 100m, "TEST", confirm: true, null);

        var exceeded = await Assert.ThrowsAsync<DomainRuleException>(() => service.IssueAsync(note.Id, confirm: true));
        Assert.Equal(FiscalDocumentErrors.CreditAmountExceeds, exceeded.Code);
        Assert.Equal(FiscalDocumentStatus.Draft, (await ReloadAsync(db, note.Id)).Status);

        await service.DiscardDraftAsync(note.Id);
        Assert.Equal(FiscalDocumentStatus.Discarded, (await ReloadAsync(db, note.Id)).Status);
    }

    [Fact]
    public async Task Handler_ListensToTheChangesOfABooking_AndNothingElse_AndAnErrorIsAnAnswerOrIsThrownAgain()
    {
        var bookingId = Guid.NewGuid();
        var documents = new Mock<IFiscalDocumentService>();
        var flags = new TestFeatureFlags(FeatureFlags.FiscalDocumentIssuance);
        var handler = new FiscalDocumentBookingChangeHandler(documents.Object, flags, NullLogger<FiscalDocumentBookingChangeHandler>.Instance);
        documents.Setup(d => d.ApplyBookingChangeAsync(It.IsAny<Guid>(), It.IsAny<BookingChange>(), It.IsAny<CancellationToken>())).ReturnsAsync(BookingChangeOutcome.None);

        foreach (var (type, change) in new[]
                 {
                     (DomainEventType.BookingModified, BookingChange.Modified),
                     (DomainEventType.BookingCancelled, BookingChange.Cancelled),
                     (DomainEventType.BookingNoShowMarked, BookingChange.NoShow),
                 })
        {
            await handler.HandleAsync(BookingEvent(type, bookingId), CancellationToken.None);
            documents.Verify(d => d.ApplyBookingChangeAsync(bookingId, change, It.IsAny<CancellationToken>()), Times.Once);
        }

        // The refund names its booking in the payload; a payload that names none is ignored.
        var payment = Guid.NewGuid();
        await handler.HandleAsync(RefundEvent(payment, bookingId, 10m), CancellationToken.None);
        documents.Verify(d => d.ApplyBookingChangeAsync(bookingId, BookingChange.Refunded, It.IsAny<CancellationToken>()), Times.Once);
        await handler.HandleAsync(new DomainEvent(Guid.NewGuid(), OrgId, DomainEventType.PaymentRefunded, payment, Now.UtcDateTime, "not json"), CancellationToken.None);
        await handler.HandleAsync(new DomainEvent(Guid.NewGuid(), OrgId, DomainEventType.PaymentRefunded, payment, Now.UtcDateTime, "{}"), CancellationToken.None);

        // Every other event, an empty entity and the issuance off: nothing more.
        foreach (var type in Enum.GetValues<DomainEventType>().Where(t => FiscalDocumentBookingChangeHandler.ChangeOf(t) is null))
            await handler.HandleAsync(BookingEvent(type, bookingId), CancellationToken.None);
        await handler.HandleAsync(BookingEvent(DomainEventType.BookingModified, Guid.Empty), CancellationToken.None);
        await new FiscalDocumentBookingChangeHandler(documents.Object, new TestFeatureFlags(), NullLogger<FiscalDocumentBookingChangeHandler>.Instance)
            .HandleAsync(BookingEvent(DomainEventType.BookingCancelled, bookingId), CancellationToken.None);
        documents.Verify(d => d.ApplyBookingChangeAsync(It.IsAny<Guid>(), It.IsAny<BookingChange>(), It.IsAny<CancellationToken>()), Times.Exactly(4));

        // A refusal of the rules is an answer; a refund under way and any other failure are thrown again, so the outbox delivers the event again.
        documents.Setup(d => d.ApplyBookingChangeAsync(It.IsAny<Guid>(), It.IsAny<BookingChange>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DomainRuleException(FiscalDocumentErrors.ProfileIncomplete, "IssuerProfileIncomplete"));
        await handler.HandleAsync(BookingEvent(DomainEventType.BookingCancelled, bookingId), CancellationToken.None);
        documents.Setup(d => d.ApplyBookingChangeAsync(It.IsAny<Guid>(), It.IsAny<BookingChange>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FiscalDocumentChangePendingException(bookingId));
        await Assert.ThrowsAsync<FiscalDocumentChangePendingException>(() => handler.HandleAsync(BookingEvent(DomainEventType.BookingCancelled, bookingId), CancellationToken.None));
        documents.Setup(d => d.ApplyBookingChangeAsync(It.IsAny<Guid>(), It.IsAny<BookingChange>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the database is down"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(BookingEvent(DomainEventType.BookingCancelled, bookingId), CancellationToken.None));
    }

    /// <summary>
    /// The refund of a payment records <c>PaymentRefunded</c> once for each amount refunded in all (a refund seen again records nothing, a bigger total records one more), and the mark of a
    /// no-show has its own key: the two events the fiscal documents follow besides the booking changes.
    /// </summary>
    [Fact]
    public async Task RefundRecordsPaymentRefundedOncePerAmount_AndTheNoShowHasItsKey()
    {
        await using var db = NewDb();
        await SeedOrgAsync(db);
        var property = await AddPropertyAsync(db);
        var bookingId = await AddBookingAsync(db, property);
        var paymentId = await AddPaymentAsync(db, bookingId, 362m);
        var recorder = new DomainEventRecorder(db, new ChangeTenant(), new FakeTimeProvider(Now));
        var refunds = new PaymentRefundService(
            db,
            Mock.Of<IStripeService>(),
            Mock.Of<IPaymentRefundRetryScheduler>(),
            Mock.Of<IEmailQueue>(),
            NullLogger<PaymentRefundService>.Instance,
            new FakeTimeProvider(Now),
            recorder);
        db.PaymentRefunds.Add(new PaymentRefund { PaymentId = paymentId, OrgId = OrgId, Amount = 100m, Status = PaymentRefundStatus.Succeeded, Origin = PaymentRefundOrigin.Host, IdempotencyKey = "TEST-A" });
        await db.SaveChangesAsync();
        var payment = await db.Payments.SingleAsync();

        await refunds.RecomputePaymentAsync(payment, CancellationToken.None);
        await db.SaveChangesAsync();
        await refunds.RecomputePaymentAsync(payment, CancellationToken.None);
        await db.SaveChangesAsync();
        db.PaymentRefunds.Add(new PaymentRefund { PaymentId = paymentId, OrgId = OrgId, Amount = 50m, Status = PaymentRefundStatus.Succeeded, Origin = PaymentRefundOrigin.Host, IdempotencyKey = "TEST-B" });
        await db.SaveChangesAsync();
        await refunds.RecomputePaymentAsync(payment, CancellationToken.None);
        await db.SaveChangesAsync();

        var events = await db.OutboxEvents.AsNoTracking().Where(e => e.EventType == DomainEventType.PaymentRefunded).OrderBy(e => e.OccurredAt).ThenBy(e => e.DedupKey).ToListAsync();
        Assert.Equal([PaymentEvents.RefundedKeyOf(paymentId, 100m), PaymentEvents.RefundedKeyOf(paymentId, 150m)], events.Select(e => e.DedupKey).Order().ToList());
        Assert.All(events, e => Assert.Equal((paymentId, OrgId), (e.EntityId, e.OrgId)));
        var payload = JsonSerializer.Deserialize<PaymentRefundedPayload>(events[0].PayloadJson, DomainEventRecorder.PayloadJsonOptions);
        Assert.Equal((paymentId, bookingId), (payload!.PaymentId, payload.BookingId));
        Assert.DoesNotContain("TEST", events[0].PayloadJson, StringComparison.Ordinal);

        var booking = new Booking { Id = bookingId };
        Assert.Equal($"booking-no-show:{bookingId}", BookingEvents.DedupKeyOf(DomainEventType.BookingNoShowMarked, booking));
        Assert.Equal(62, (int)DomainEventType.BookingNoShowMarked);
        Assert.Equal(63, (int)DomainEventType.PaymentRefunded);
    }

    // ---------------------------------------------------------------- helpers

    private static FiscalDocumentBookingChangeHandler HandlerOf(FiscalDocumentService service) =>
        new(service, new TestFeatureFlags(FeatureFlags.FiscalDocumentIssuance), NullLogger<FiscalDocumentBookingChangeHandler>.Instance);

    private static DomainEvent BookingEvent(DomainEventType type, Guid bookingId) =>
        new(Guid.NewGuid(), OrgId, type, bookingId, Now.UtcDateTime, "{}");

    private static DomainEvent RefundEvent(Guid paymentId, Guid bookingId, decimal refunded) =>
        new(
            Guid.NewGuid(),
            OrgId,
            DomainEventType.PaymentRefunded,
            paymentId,
            Now.UtcDateTime,
            JsonSerializer.Serialize(new PaymentRefundedPayload(paymentId, bookingId, refunded), DomainEventRecorder.PayloadJsonOptions));

    private static async Task<FiscalDocument> IssueAsync(FiscalDocumentService service, Guid bookingId) =>
        await service.IssueAsync((await service.CreateDraftAsync(bookingId, null)).Id, confirm: true);

    private static async Task<FiscalDocument> ReloadAsync(AppDbContext db, Guid documentId) =>
        await db.FiscalDocuments.AsNoTracking().Include(d => d.Lines).SingleAsync(d => d.Id == documentId);

    private static async Task<List<InternalActivity>> TodosAsync(AppDbContext db) =>
        await db.InternalActivities.AsNoTracking().Where(a => a.Action != null).ToListAsync();

    private static string Snapshot(FiscalDocument d) =>
        string.Join('|', d.Number, d.Status, d.TaxableBase, d.VatAmount, d.StampDuty, d.Total, d.PaymentChannel, d.RuleVersion, d.IssuerSnapshot, d.UpdatedAt.Ticks,
            string.Join(',', d.Lines.OrderBy(l => l.LineNumber).Select(l => $"{l.LineNumber}:{l.LineType}:{l.Description}:{l.TotalPrice}:{l.VatRatePercent}:{l.VatAmount}")));

    private static FiscalDocumentService ServiceOf(AppDbContext db)
    {
        var flags = new TestFeatureFlags(FeatureFlags.FiscalDocumentIssuance);
        var rules = TaxRuleCatalogs.Resolver();
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Test");
        var time = new FakeTimeProvider(Now);
        var issuers = new DocumentIssuerService(db, flags, rules, environment.Object, time, NullLogger<DocumentIssuerService>.Instance);
        var invoiceData = new Mock<IInvoiceDataService>();
        invoiceData.Setup(i => i.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid bookingId, CancellationToken _) => new InvoiceDataView(
                bookingId, true, Now.UtcDateTime, InvoiceRecipientKind.Company, "TEST Cliente Srl", null, "IT09876543217", "Via del Cliente", "5",
                "00100", "Roma", "RM", "IT", "ABC1234", null, InvoiceDataSource.Host, Now.UtcDateTime));
        var xml = new Mock<IFatturaPaXmlBuilder>();
        xml.Setup(x => x.Build(It.IsAny<FatturaPaInput>())).Returns("<p:FatturaElettronica/>");
        var texts = new ResxDocumentTexts();
        return new FiscalDocumentService(
            db,
            issuers,
            invoiceData.Object,
            new FiscalNumberingService(db, time, NullLogger<FiscalNumberingService>.Instance),
            xml.Object,
            Mock.Of<IDomainEventRecorder>(),
            new MigraDocPdfDocumentRenderer(),
            texts,
            rules,
            time,
            NullLogger<FiscalDocumentService>.Instance,
            new GuestDocumentLineBuilder(db, texts, Options.Create(new FiscalDocumentRulesOptions()), Options.Create(new SwissTouristTaxOptions())),
            new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, time),
            null,
            flags);
    }

    private static async Task CancelAsync(AppDbContext db, Guid bookingId) =>
        await ChangeBookingAsync(db, bookingId, b => { b.Status = BookingStatus.Cancelled; b.CancelledAt = Now.UtcDateTime; });

    private static async Task ChangeBookingAsync(AppDbContext db, Guid bookingId, Action<Booking> change)
    {
        var booking = await db.Bookings.SingleAsync(b => b.Id == bookingId);
        change(booking);
        booking.UpdatedAt = Now.UtcDateTime.AddMinutes(5);
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> AddPaymentAsync(AppDbContext db, Guid bookingId, decimal amount, decimal refunded = 0m)
    {
        var payment = new Payment
        {
            OrgId = OrgId,
            BookingId = bookingId,
            Amount = amount,
            RefundedAmount = refunded,
            Status = refunded <= 0m ? PaymentStatus.Completed : refunded >= amount ? PaymentStatus.Refunded : PaymentStatus.PartiallyRefunded,
            Method = PaymentMethod.CreditCard,
            TransactionId = $"pi_test_{Guid.NewGuid():N}",
        };
        payment.StripePaymentIntentId = payment.TransactionId;
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        return payment.Id;
    }

    private static async Task SeedOrgAsync(AppDbContext db, bool autoIssue = false, DocumentRegime regime = DocumentRegime.VatOrdinary)
    {
        db.Orgs.Add(new OrgEntity
        {
            Id = OrgId,
            Name = "TEST Casa Vacanze",
            Slug = $"test-{OrgId:N}"[..20],
            DisplayName = "TEST Casa Vacanze",
            ContactEmail = "test@example.com",
            HasPartitaIva = regime != DocumentRegime.PrivatePerson,
            PartitaIvaNumber = regime != DocumentRegime.PrivatePerson ? "01234567897" : null,
            FiscalCode = "01234567897",
        });
        db.OrgFiscalIssuerProfiles.Add(new OrgFiscalIssuerProfile
        {
            OrgId = OrgId,
            IssuerMode = DocumentIssuerMode.CasaZen,
            DocumentRegime = regime,
            LegalName = "TEST Casa Vacanze Srl",
            AddressStreet = "Via di Test",
            AddressCivic = "1",
            PostalCode = "00187",
            City = "Roma",
            Province = "RM",
            CountryCode = "IT",
            AutoIssueGuestDocuments = autoIssue,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> AddPropertyAsync(AppDbContext db)
    {
        var property = new Property { OrgId = OrgId, OwnerId = "auth0|host", Name = "TEST Casa", Address = "Via 1", City = "Roma", Currency = "EUR" };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property.Id;
    }

    /// <summary>A stay of three nights (2026-11-14 to 2026-11-17), checked out: 300.00 for the stay and 50.00 for the cleaning at 10%, 12.00 of tourist tax out of VAT: 362.00.</summary>
    private static async Task<Guid> AddBookingAsync(
        AppDbContext db,
        Guid propertyId,
        BookingSource source = BookingSource.Direct,
        bool? companyCollect = null)
    {
        var guest = new Guest { OrgId = OrgId, FirstName = "Test", LastName = "Guest", Email = $"{Guid.NewGuid():N}@example.com" };
        var booking = new Booking
        {
            OrgId = OrgId,
            PropertyId = propertyId,
            GuestId = guest.Id,
            CheckInDate = new DateTime(2026, 11, 14),
            CheckOutDate = new DateTime(2026, 11, 17),
            NumberOfGuests = 2,
            Status = BookingStatus.CheckedOut,
            Source = source,
            BasePrice = 350m,
            CleaningFee = 50m,
            TouristTax = source == BookingSource.Direct ? 12m : 0m,
            TotalPrice = source == BookingSource.Direct ? 362m : 350m,
            OtaCompanyCollect = companyCollect,
        };
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        return booking.Id;
    }

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"FiscalChanges_{Guid.NewGuid()}").Options, new ChangeTenant(), new EphemeralDataProtectionProvider());

    private sealed class ChangeTenant : ITenantContext
    {
        public Guid? OrgId => null;

        public bool FilterEnabled => false;
    }
}
