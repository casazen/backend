using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Services;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E17/F01/US02/T03 (NF-86, ADR-026, ADR-022, ⚖ the reuse of the number of a rejected document): an invoice the SDI rejected counts as not issued; its correction is a new draft with the same lines and
/// recipient that keeps its series and number (the number of the rejected document, which gave it up), a delivered invoice is corrected with a credit note and never replaced, and the sequence of the
/// numbers has neither a duplicate nor a hole.
/// </summary>
public class FiscalDocumentCorrectionTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// PROOF TEST: data rule. The number of a rejected invoice is reused only by the replacement of that invoice, and only because it never was delivered: the replacement holds the series and the
    /// number from the start, is issued with them (and the day of the invoice it replaces) without reserving another, and the next invoice of the series gets the next number (no duplicate, no hole).
    /// An invoice that was delivered is never rejected afterwards, so it is never replaced: its correction is refused, nothing changes, and a replacement that does not replace a rejected invoice of
    /// its issuer is not issued with the number it holds.
    /// </summary>
    [Fact]
    public async Task Correction_RejectedDocumentNumberReusedOnlyIfNeverDelivered_Test()
    {
        await using var db = NewDb();
        var recorder = new Mock<IDomainEventRecorder>();
        var rig = Rig(db, recorder.Object);
        var first = await SeedBookingAsync(db, seedOrg: true);
        var second = await SeedBookingAsync(db);
        var third = await SeedBookingAsync(db);
        var fourth = await SeedBookingAsync(db);

        // The first invoice of the year goes to the SDI and is rejected: it was never delivered.
        var rejected = await rig.Documents.IssueAsync((await rig.Documents.CreateDraftAsync(first, null)).Id, confirm: true);
        Assert.Equal(("2026", 1, "2026/1", FiscalDocumentSdiStatuses.ToSend), (rejected.Series, rejected.SequenceNumber, rejected.Number, rejected.SdiStatus));
        await SetIssueDayAsync(db, rejected.Id, new DateOnly(2026, 11, 10));
        await RejectedAsync(rig, rejected.Id);

        // The correction: a draft with the same lines and recipient that holds the number the rejected invoice gave up; the rejected one keeps its number for the history and is discarded.
        var replacement = await rig.Corrections.StartCorrectionAsync(rejected.Id, "auth0|host");
        Assert.Equal(
            (FiscalDocumentStatus.Draft, rejected.Id, "2026", 1, "2026/1"),
            (replacement.Status, replacement.ReplacesDocumentId, replacement.Series, replacement.SequenceNumber, replacement.Number));
        Assert.Equal((rejected.Total, rejected.RecipientName, rejected.Lines.Count), (replacement.Total, replacement.RecipientName, replacement.Lines.Count));
        var gaveUp = await db.FiscalDocuments.AsNoTracking().SingleAsync(d => d.Id == rejected.Id);
        Assert.Equal((FiscalDocumentStatus.Discarded, (int?)null, "2026/1", FiscalDocumentSdiStatuses.Rejected), (gaveUp.Status, gaveUp.SequenceNumber, gaveUp.Number, gaveUp.SdiStatus));

        // A second click answers the same draft and records nothing more.
        Assert.Equal(replacement.Id, (await rig.Corrections.StartCorrectionAsync(rejected.Id, "auth0|host")).Id);
        recorder.Verify(
            r => r.Record(DomainEventType.FiscalDocumentCorrected, rejected.Id, It.IsAny<object>(), $"fiscal-document-corrected:{rejected.Id:N}", OrgId),
            Times.Once);

        // The replacement is never discarded: the number would be lost (a hole) and the rejected invoice could not be corrected again.
        var holding = await Assert.ThrowsAsync<DomainRuleException>(() => rig.Documents.DiscardDraftAsync(replacement.Id));
        Assert.Equal(FiscalDocumentSdiErrors.ReplacementHoldsNumber, holding.Code);
        var stillHeld = await db.FiscalDocuments.AsNoTracking().SingleAsync(d => d.Id == replacement.Id);
        Assert.Equal((FiscalDocumentStatus.Draft, (int?)1), (stillHeld.Status, stillHeld.SequenceNumber));

        // Issued, it keeps the number (and the day of the invoice it replaces); no other number is reserved, so the next invoice of the series is the second.
        var reissued = await rig.Documents.IssueAsync(replacement.Id, confirm: true);
        Assert.Equal(
            (FiscalDocumentStatus.Issued, "2026", 1, "2026/1", new DateOnly(2026, 11, 10), FiscalDocumentSdiStatuses.ToSend, rejected.Id),
            (reissued.Status, reissued.Series, reissued.SequenceNumber, reissued.Number, reissued.IssueDate, reissued.SdiStatus, reissued.ReplacesDocumentId));
        var next = await rig.Documents.IssueAsync((await rig.Documents.CreateDraftAsync(second, null)).Id, confirm: true);
        Assert.Equal((2, "2026/2"), (next.SequenceNumber, next.Number));
        Assert.Equal([1, 2], await db.FiscalDocuments.AsNoTracking().Where(d => d.SequenceNumber != null).OrderBy(d => d.SequenceNumber).Select(d => d.SequenceNumber!.Value).ToListAsync());

        // An invoice that was delivered is never rejected afterwards: it cannot be corrected here, and nothing changes.
        var delivered = await rig.Documents.IssueAsync((await rig.Documents.CreateDraftAsync(third, null)).Id, confirm: true);
        await SubmittedAsync(rig, delivered.Id, "88");
        Assert.Equal(SdiRecordResult.Recorded, await rig.Sdi.RecordOutcomeAsync(delivered.Id, new SdiOutcome(SdiOutcomeKind.Delivered)));
        Assert.Equal(SdiRecordResult.Ignored, await rig.Sdi.RecordOutcomeAsync(delivered.Id, new SdiOutcome(SdiOutcomeKind.Rejected, "00417")));
        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => rig.Corrections.StartCorrectionAsync(delivered.Id, "auth0|host"));
        Assert.Equal(FiscalDocumentSdiErrors.NotRejected, refused.Code);
        var kept = await db.FiscalDocuments.AsNoTracking().SingleAsync(d => d.Id == delivered.Id);
        Assert.Equal((FiscalDocumentStatus.Issued, 3, "2026/3", FiscalDocumentSdiStatuses.Delivered), (kept.Status, kept.SequenceNumber, kept.Number, kept.SdiStatus));
        Assert.Empty(await db.FiscalDocuments.AsNoTracking().Where(d => d.ReplacesDocumentId == delivered.Id).ToListAsync());

        // A draft that claims the number of a delivered invoice is not issued with it, and no number is consumed by the refusal.
        var claim = await rig.Documents.CreateDraftAsync(fourth, null);
        var tracked = await db.FiscalDocuments.SingleAsync(d => d.Id == claim.Id);
        (tracked.ReplacesDocumentId, tracked.Series, tracked.SequenceNumber, tracked.Number) = (delivered.Id, "2026", 3, "2026/3");
        await db.SaveChangesAsync();
        var invalid = await Assert.ThrowsAsync<DomainRuleException>(() => rig.Documents.IssueAsync(claim.Id, confirm: true));
        Assert.Equal(FiscalDocumentSdiErrors.ReplacementInvalid, invalid.Code);
        Assert.Equal(FiscalDocumentStatus.Draft, (await db.FiscalDocuments.AsNoTracking().SingleAsync(d => d.Id == claim.Id)).Status);
        Assert.Equal(3, (await db.FiscalNumberSequences.AsNoTracking().SingleAsync()).LastNumber);
    }

    [Fact]
    public async Task Correction_NeedsARejectedInvoiceOfCasazen_AndNeverAnImportedOrOwnerDocument()
    {
        await using var db = NewDb();
        var rig = Rig(db, Mock.Of<IDomainEventRecorder>());
        var booking = await SeedBookingAsync(db, seedOrg: true);
        var toSend = await rig.Documents.IssueAsync((await rig.Documents.CreateDraftAsync(booking, null)).Id, confirm: true);

        Assert.Equal(FiscalDocumentSdiErrors.NotRejected, (await Assert.ThrowsAsync<DomainRuleException>(() => rig.Corrections.StartCorrectionAsync(toSend.Id, null))).Code);
        await Assert.ThrowsAsync<NotFoundException>(() => rig.Corrections.StartCorrectionAsync(Guid.NewGuid(), null));

        var imported = new FiscalDocument { OrgId = OrgId, Source = FiscalDocumentSource.Octorate, Status = FiscalDocumentStatus.Issued, SdiStatus = FiscalDocumentSdiStatuses.Rejected, Series = string.Empty };
        var ownerInvoice = new FiscalDocument
        {
            OrgId = OrgId,
            Source = FiscalDocumentSource.CasaZen,
            Purpose = FiscalDocumentPurpose.OwnerInvoice,
            Status = FiscalDocumentStatus.Issued,
            SdiStatus = FiscalDocumentSdiStatuses.Rejected,
            Series = "PM2026",
            SequenceNumber = 1,
            Number = "PM2026/1",
        };
        db.FiscalDocuments.AddRange(imported, ownerInvoice);
        await db.SaveChangesAsync();

        Assert.Equal(FiscalDocumentErrors.ReadOnly, (await Assert.ThrowsAsync<DomainRuleException>(() => rig.Corrections.StartCorrectionAsync(imported.Id, null))).Code);
        Assert.Equal(OwnerInvoiceErrors.UseOwnerInvoices, (await Assert.ThrowsAsync<DomainRuleException>(() => rig.Corrections.StartCorrectionAsync(ownerInvoice.Id, null))).Code);
    }

    /// <summary>A rejected invoice never was issued: a credit note does not correct it, and neither does the correction of its booking (the host is told, nothing is prepared).</summary>
    [Fact]
    public async Task Correction_ARejectedInvoice_IsNeverCredited()
    {
        await using var db = NewDb();
        var rig = Rig(db, Mock.Of<IDomainEventRecorder>());
        var booking = await SeedBookingAsync(db, seedOrg: true);
        var invoice = await rig.Documents.IssueAsync((await rig.Documents.CreateDraftAsync(booking, null)).Id, confirm: true);
        await RejectedAsync(rig, invoice.Id);

        var credit = await Assert.ThrowsAsync<DomainRuleException>(() => rig.Documents.CreateCreditNoteAsync(invoice.Id, null, "test", confirm: true, "auth0|host"));

        Assert.Equal(FiscalDocumentSdiErrors.UseCorrection, credit.Code);
        Assert.Empty(await db.FiscalDocuments.AsNoTracking().Where(d => d.Kind == FiscalDocumentKind.CreditNote).ToListAsync());

        // Once it has credit notes (made before it was rejected), it is not replaced either.
        var other = await SeedBookingAsync(db);
        var withCredit = await rig.Documents.IssueAsync((await rig.Documents.CreateDraftAsync(other, null)).Id, confirm: true);
        await rig.Documents.CreateCreditNoteAsync(withCredit.Id, 10m, "test", confirm: true, "auth0|host");
        await RejectedAsync(rig, withCredit.Id);
        Assert.Equal(FiscalDocumentSdiErrors.NotCorrectable, (await Assert.ThrowsAsync<DomainRuleException>(() => rig.Corrections.StartCorrectionAsync(withCredit.Id, null))).Code);
    }

    /// <summary>
    /// The automatic runs never discard the replacement either: a booking that ends with nothing collected keeps it, with its number, and the host gets the «Da fare» of the change (the same
    /// as when money was collected), so the number is neither lost nor left without anybody knowing.
    /// </summary>
    [Fact]
    public async Task Correction_ABookingThatEnds_KeepsTheReplacementWithItsNumber_AndTellsTheHost()
    {
        await using var db = NewDb();
        var activities = new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, new FakeTimeProvider(Now));
        var rig = Rig(db, Mock.Of<IDomainEventRecorder>(), activities);
        var booking = await SeedBookingAsync(db, seedOrg: true);
        var invoice = await rig.Documents.IssueAsync((await rig.Documents.CreateDraftAsync(booking, null)).Id, confirm: true);
        await RejectedAsync(rig, invoice.Id);
        var replacement = await rig.Corrections.StartCorrectionAsync(invoice.Id, "auth0|host");

        (await db.Bookings.SingleAsync(b => b.Id == booking)).Status = BookingStatus.Cancelled;
        await db.SaveChangesAsync();
        await rig.Documents.ApplyBookingChangeAsync(booking, BookingChange.Cancelled);

        var kept = await db.FiscalDocuments.AsNoTracking().SingleAsync(d => d.Id == replacement.Id);
        Assert.Equal((FiscalDocumentStatus.Draft, (int?)1, "2026/1"), (kept.Status, kept.SequenceNumber, kept.Number));
        var todo = Assert.Single(await db.InternalActivities.AsNoTracking().Where(a => a.EntityId == replacement.Id && a.CompletedAt == null).ToListAsync());
        Assert.Equal((ActivityAction.ReviewFiscalDocument, FiscalDocumentTodos.BookingChangedReason), (todo.Action!.Value, todo.Reason));
    }

    // ---------------------------------------------------------------- the «To do»

    [Fact]
    public async Task Handler_ARejection_RaisesTheTodoOnce_AndTheCorrectionOrTheCreditClosesIt()
    {
        await using var db = NewDb();
        var booking = await SeedBookingAsync(db, seedOrg: true);
        var propertyId = (await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == booking)).PropertyId;
        var time = new FakeTimeProvider(Now);
        var activities = new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, time);
        var pushes = new List<(string Key, PushAudience Audience, PushNotificationPayload Payload)>();
        var queue = new Mock<IPushNotificationService>();
        queue.Setup(p => p.Enqueue(It.IsAny<string>(), It.IsAny<PushAudience>(), It.IsAny<PushNotificationPayload>()))
            .Callback<string, PushAudience, PushNotificationPayload>((key, audience, payload) => pushes.Add((key, audience, payload)))
            .Returns(true);
        var handler = new FiscalDocumentRejectionHandler(db, activities, new ResxDocumentTexts(), NullLogger<FiscalDocumentRejectionHandler>.Instance, queue.Object);
        var document = new FiscalDocument
        {
            OrgId = OrgId,
            BookingId = booking,
            PropertyId = propertyId,
            Source = FiscalDocumentSource.CasaZen,
            Status = FiscalDocumentStatus.Issued,
            SdiStatus = FiscalDocumentSdiStatuses.Rejected,
            SdiErrorCode = "00417",
            Series = "2026",
            SequenceNumber = 1,
            Number = "2026/1",
        };
        db.FiscalDocuments.Add(document);
        await db.SaveChangesAsync();

        // The event delivered twice raises one item.
        await handler.HandleAsync(Event(DomainEventType.FiscalDocumentSdiRejected, document.Id, "{}"), CancellationToken.None);
        await handler.HandleAsync(Event(DomainEventType.FiscalDocumentSdiRejected, document.Id, "{}"), CancellationToken.None);
        var item = Assert.Single(await db.InternalActivities.AsNoTracking().Where(a => a.Action != null).ToListAsync());
        Assert.Equal(
            (ActivityType.FiscalDocument.ToString(), ActivityAction.CorrectInvoice, "FiscalDocument", document.Id, ActivityUrgency.High, "sdi_rejected", propertyId, (DateTime?)null),
            (item.ActivityType, item.Action!.Value, item.EntityType, item.EntityId!.Value, item.Urgency!.Value, item.Reason, item.PropertyId!.Value, item.CompletedAt));

        // The hosts of the booking are told live, once per document (the same delivery key at the second delivery), in the words of the resx and with the booking as the place to go: no reason, no code.
        Assert.Equal(2, pushes.Count);
        Assert.All(
            pushes,
            p => Assert.Equal(
                (PushDeliveryKeys.FiscalDocumentSdiRejected(document.Id), PushAudience.BookingHosts(booking), PushTypes.FiscalDocumentSdiRejected, booking, PushRoutes.Booking(booking)),
                (p.Key, p.Audience, p.Payload.Type, p.Payload.BookingId, p.Payload.Route)));
        Assert.NotEqual(FiscalDocumentLiveRefresh.SdiRejectedTitleKey, pushes[0].Payload.Title);
        Assert.NotEqual(FiscalDocumentLiveRefresh.SdiRejectedBodyKey, pushes[0].Payload.Body);
        Assert.DoesNotContain("00417", pushes[0].Payload.Title + pushes[0].Payload.Body, StringComparison.Ordinal);

        // The correction closes it.
        await handler.HandleAsync(Event(DomainEventType.FiscalDocumentCorrected, document.Id, "{}"), CancellationToken.None);
        var closed = await db.InternalActivities.AsNoTracking().SingleAsync(a => a.Id == item.Id);
        Assert.Equal((InternalActivity.ClosedBySystem, true), (closed.ClosedBy, closed.CompletedAt is not null));

        // A rejection whose document was corrected before the event was delivered raises nothing; the credit of a document (an owner invoice) closes its item.
        var tracked = await db.FiscalDocuments.SingleAsync(d => d.Id == document.Id);
        tracked.Status = FiscalDocumentStatus.Discarded;
        await db.SaveChangesAsync();
        await handler.HandleAsync(Event(DomainEventType.FiscalDocumentSdiRejected, document.Id, "{}"), CancellationToken.None);
        Assert.Single(await db.InternalActivities.AsNoTracking().Where(a => a.Action != null).ToListAsync());
        Assert.Equal(2, pushes.Count);

        tracked.Status = FiscalDocumentStatus.Issued;
        await db.SaveChangesAsync();
        await handler.HandleAsync(Event(DomainEventType.FiscalDocumentSdiRejected, document.Id, "{}"), CancellationToken.None);
        var payload = JsonSerializer.Serialize(new FiscalDocumentIssuedPayload(Guid.NewGuid(), null, "CreditNote", document.Id), DomainEventRecorder.PayloadJsonOptions);
        await handler.HandleAsync(Event(DomainEventType.FiscalDocumentCreditNoteIssued, Guid.NewGuid(), payload), CancellationToken.None);
        Assert.All(await db.InternalActivities.AsNoTracking().Where(a => a.Action != null).ToListAsync(), a => Assert.NotNull(a.CompletedAt));

        // Any other event is none of its business.
        await handler.HandleAsync(Event(DomainEventType.BookingCancelled, document.Id, "{}"), CancellationToken.None);

        // An invoice without a booking (an owner invoice) has its «To do» and no push: there is no booking to open.
        var withoutBooking = new FiscalDocument
        {
            OrgId = OrgId,
            PropertyId = propertyId,
            Source = FiscalDocumentSource.CasaZen,
            Status = FiscalDocumentStatus.Issued,
            SdiStatus = FiscalDocumentSdiStatuses.Rejected,
            SdiErrorCode = "00417",
            Series = "2026",
            SequenceNumber = 2,
            Number = "2026/2",
        };
        db.FiscalDocuments.Add(withoutBooking);
        await db.SaveChangesAsync();
        var told = pushes.Count;
        await handler.HandleAsync(Event(DomainEventType.FiscalDocumentSdiRejected, withoutBooking.Id, "{}"), CancellationToken.None);
        Assert.Equal(told, pushes.Count);
        Assert.Contains(await db.InternalActivities.AsNoTracking().Where(a => a.EntityId == withoutBooking.Id).ToListAsync(), a => a.CompletedAt is null);

        // The events are not delivered in order: an owner invoice credited since had its closing event delivered first, so the rejection raises no item that nothing would ever close.
        var credited = new FiscalDocument
        {
            OrgId = OrgId,
            PropertyId = propertyId,
            Purpose = FiscalDocumentPurpose.OwnerInvoice,
            Source = FiscalDocumentSource.CasaZen,
            Status = FiscalDocumentStatus.Issued,
            SdiStatus = FiscalDocumentSdiStatuses.Rejected,
            SdiErrorCode = "00417",
            Series = "2026",
            SequenceNumber = 3,
            Number = "2026/3",
        };
        db.FiscalDocuments.Add(credited);
        await db.SaveChangesAsync();
        db.FiscalDocuments.Add(new FiscalDocument
        {
            OrgId = OrgId,
            Kind = FiscalDocumentKind.CreditNote,
            CreditedDocumentId = credited.Id,
            Status = FiscalDocumentStatus.Issued,
            Series = "2026",
            SequenceNumber = 4,
            Number = "2026/4",
        });
        await db.SaveChangesAsync();
        await handler.HandleAsync(Event(DomainEventType.FiscalDocumentSdiRejected, credited.Id, "{}"), CancellationToken.None);
        Assert.Empty(await db.InternalActivities.AsNoTracking().Where(a => a.EntityId == credited.Id).ToListAsync());

        // A guest invoice is never credited once rejected: the draft of a credit note (a change of its booking while it was with the SDI), or a credit note issued before, never hides its item.
        foreach (var noteStatus in new[] { FiscalDocumentStatus.Draft, FiscalDocumentStatus.Issued })
        {
            var guest = new FiscalDocument
            {
                OrgId = OrgId,
                PropertyId = propertyId,
                Source = FiscalDocumentSource.CasaZen,
                Status = FiscalDocumentStatus.Issued,
                SdiStatus = FiscalDocumentSdiStatuses.Rejected,
                SdiErrorCode = "00417",
                Series = "2027",
                SequenceNumber = noteStatus == FiscalDocumentStatus.Draft ? 1 : 2,
                Number = noteStatus == FiscalDocumentStatus.Draft ? "2027/1" : "2027/2",
            };
            db.FiscalDocuments.Add(guest);
            await db.SaveChangesAsync();
            db.FiscalDocuments.Add(new FiscalDocument { OrgId = OrgId, Kind = FiscalDocumentKind.CreditNote, CreditedDocumentId = guest.Id, Status = noteStatus, Series = "2027" });
            await db.SaveChangesAsync();

            await handler.HandleAsync(Event(DomainEventType.FiscalDocumentSdiRejected, guest.Id, "{}"), CancellationToken.None);

            Assert.Single(await db.InternalActivities.AsNoTracking().Where(a => a.EntityId == guest.Id && a.Action == ActivityAction.CorrectInvoice && a.CompletedAt == null).ToListAsync());
        }
    }

    /// <summary>
    /// A rejected guest invoice whose booking changed while it was with the SDI has the draft of a credit note: the correction is not refused because of it (the draft would credit an invoice that
    /// never was issued), it discards it and closes its «Da fare», and the state says the invoice can be corrected. A credit note already issued keeps refusing the correction, and the state says so.
    /// </summary>
    [Fact]
    public async Task Correction_ARejectedInvoiceWithACreditNoteDraft_IsCorrected_AndTheDraftIsDiscarded()
    {
        await using var db = NewDb();
        var time = new FakeTimeProvider(Now);
        var activities = new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, time);
        var rig = Rig(db, Mock.Of<IDomainEventRecorder>(), activities);
        var booking = await SeedBookingAsync(db, seedOrg: true);
        var propertyId = (await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == booking)).PropertyId;
        var invoice = await rig.Documents.IssueAsync((await rig.Documents.CreateDraftAsync(booking, null)).Id, confirm: true);
        var note = new FiscalDocument { OrgId = OrgId, BookingId = booking, Kind = FiscalDocumentKind.CreditNote, CreditedDocumentId = invoice.Id, Status = FiscalDocumentStatus.Draft, Series = string.Empty };
        db.FiscalDocuments.Add(note);
        await db.SaveChangesAsync();
        await activities.CreateAsync(
            new CreateTodoRequest
            {
                Type = ActivityType.FiscalDocument,
                Action = ActivityAction.ReviewFiscalDocument,
                EntityType = FiscalDocumentTodos.EntityType,
                EntityId = note.Id,
                Urgency = ActivityUrgency.Medium,
                PropertyId = propertyId,
                OrgId = OrgId,
                Reason = FiscalDocumentTodos.CreditNoteToIssueReason,
            },
            CancellationToken.None);
        await RejectedAsync(rig, invoice.Id);
        Assert.True((await rig.Sdi.GetStateAsync(invoice.Id))!.CanCorrect);

        var replacement = await rig.Corrections.StartCorrectionAsync(invoice.Id, "auth0|host");

        Assert.Equal(FiscalDocumentStatus.Discarded, (await db.FiscalDocuments.AsNoTracking().SingleAsync(d => d.Id == note.Id)).Status);
        Assert.Empty(await db.InternalActivities.AsNoTracking().Where(a => a.EntityId == note.Id && a.CompletedAt == null).ToListAsync());
        Assert.Equal((FiscalDocumentStatus.Draft, invoice.Id), (replacement.Status, replacement.ReplacesDocumentId));

        // An issued credit note: not corrected here, and the state says it.
        var other = await SeedBookingAsync(db);
        var credited = await rig.Documents.IssueAsync((await rig.Documents.CreateDraftAsync(other, null)).Id, confirm: true);
        await rig.Documents.CreateCreditNoteAsync(credited.Id, 10m, "test", confirm: true, "auth0|host");
        await RejectedAsync(rig, credited.Id);
        Assert.False((await rig.Sdi.GetStateAsync(credited.Id))!.CanCorrect);
        Assert.Equal(FiscalDocumentSdiErrors.NotCorrectable, (await Assert.ThrowsAsync<DomainRuleException>(() => rig.Corrections.StartCorrectionAsync(credited.Id, null))).Code);
    }

    // ---------------------------------------------------------------- helpers

    private static DomainEvent Event(DomainEventType type, Guid entityId, string payload) => new(Guid.NewGuid(), OrgId, type, entityId, Now.UtcDateTime, payload);

    private static async Task SetIssueDayAsync(AppDbContext db, Guid documentId, DateOnly day)
    {
        (await db.FiscalDocuments.SingleAsync(d => d.Id == documentId)).IssueDate = day;
        await db.SaveChangesAsync();
    }

    /// <summary>The document submitted through the provider (its id of the submission): the way a document reaches «submitted».</summary>
    private static async Task SubmittedAsync(TestRig rig, Guid documentId, string transmissionId)
    {
        rig.Provider.Setup(p => p.SubmitDocumentAsync(It.IsAny<SdiDocumentSubmission>(), It.IsAny<CancellationToken>())).ReturnsAsync(SdiSubmissionResult.Success(transmissionId));
        Assert.Equal(SdiSubmitOutcome.Submitted, (await rig.Sdi.SubmitAsync(documentId)).Outcome);
    }

    /// <summary>The document submitted and rejected by the SDI.</summary>
    private static async Task RejectedAsync(TestRig rig, Guid documentId)
    {
        await SubmittedAsync(rig, documentId, $"id-{documentId:N}");
        Assert.Equal(SdiRecordResult.Recorded, await rig.Sdi.RecordOutcomeAsync(documentId, new SdiOutcome(SdiOutcomeKind.Rejected, "00417", "Identificativo fiscale mancante")));
    }

    private sealed record TestRig(
        FiscalDocumentService Documents,
        FiscalDocumentSdiService Sdi,
        FiscalDocumentCorrectionService Corrections,
        Mock<ISdiEInvoiceProvider> Provider);

    private static TestRig Rig(AppDbContext db, IDomainEventRecorder recorder, IInternalActivityService? activities = null)
    {
        var flags = new TestFeatureFlags(FeatureFlags.FiscalDocumentIssuance, FeatureFlags.SdiSubmission);
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
        var documents = new FiscalDocumentService(
            db,
            issuers,
            invoiceData.Object,
            new FiscalNumberingService(db, time, NullLogger<FiscalNumberingService>.Instance),
            xml.Object,
            recorder,
            new MigraDocPdfDocumentRenderer(),
            new ResxDocumentTexts(),
            rules,
            time,
            NullLogger<FiscalDocumentService>.Instance,
            activities: activities);
        var provider = new Mock<ISdiEInvoiceProvider>();
        provider.SetupGet(p => p.IsConfigured).Returns(true);
        provider.SetupGet(p => p.Name).Returns("test");
        return new TestRig(
            documents,
            new FiscalDocumentSdiService(db, provider.Object, recorder, time, NullLogger<FiscalDocumentSdiService>.Instance, flags),
            new FiscalDocumentCorrectionService(db, recorder, time, NullLogger<FiscalDocumentCorrectionService>.Instance, activities),
            provider);
    }

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"FiscalCorrection_{Guid.NewGuid()}").Options, new SdiTenant(), new EphemeralDataProtectionProvider());

    private static async Task<Guid> SeedBookingAsync(AppDbContext db, bool seedOrg = false)
    {
        if (seedOrg)
        {
            db.Orgs.Add(new OrgEntity
            {
                Id = OrgId,
                Name = "TEST Casa Vacanze",
                Slug = $"test-{OrgId:N}"[..20],
                DisplayName = "TEST Casa Vacanze",
                ContactEmail = "test@example.com",
                HasPartitaIva = true,
                PartitaIvaNumber = "01234567897",
                FiscalCode = "01234567897",
            });
            db.OrgFiscalIssuerProfiles.Add(new OrgFiscalIssuerProfile
            {
                OrgId = OrgId,
                IssuerMode = DocumentIssuerMode.CasaZen,
                DocumentRegime = DocumentRegime.VatOrdinary,
                LegalName = "TEST Casa Vacanze Srl",
                AddressStreet = "Via di Test",
                AddressCivic = "1",
                PostalCode = "00187",
                City = "Roma",
                Province = "RM",
                CountryCode = "IT",
            });
        }

        var property = new Property { OrgId = OrgId, OwnerId = "auth0|host", Name = "TEST Casa", Address = "Via 1", City = "Roma", Currency = "EUR" };
        var guest = new Guest { OrgId = OrgId, FirstName = "Test", LastName = "Guest", Email = $"{Guid.NewGuid():N}@example.com" };
        var booking = new Booking
        {
            OrgId = OrgId,
            PropertyId = property.Id,
            GuestId = guest.Id,
            CheckInDate = new DateTime(2026, 11, 1),
            CheckOutDate = new DateTime(2026, 11, 4),
            NumberOfGuests = 2,
            Status = BookingStatus.CheckedOut,
            Source = BookingSource.Direct,
            BasePrice = 350m,
            CleaningFee = 50m,
            TouristTax = 12m,
            TotalPrice = 362m,
        };
        db.Properties.Add(property);
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        return booking.Id;
    }
}
