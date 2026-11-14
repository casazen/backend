using System.Reflection;
using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Models;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Services;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Fiscal;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Hangfire;
using Hangfire.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using DocumentKind = Casazen.Core.Entities.Enums.FiscalDocumentKind;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E12/F01/US01/T02 (NF-83, NF-112, ADR-022, ADR-026): the drafts at the end of every stay (the check-out event and the daily run, one draft for a booking), the automatic issue, the
/// bulk issue (an empty selection never means «all», one row per document, only what the caller reaches), the documents of a booking and the push that makes the open pages refresh.
/// </summary>
public class FiscalDocumentAutomationTests
{
    /// <summary>09:00 in Rome on 2026-11-20: the stays that ended before that day are over.</summary>
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);

    private static readonly Guid OrgId = Guid.NewGuid();

    /// <summary>
    /// PROOF TEST: data rule (T02 point 2). The daily run makes one draft for each active stay that ended before today and has no document, and again nothing the second time: a
    /// stay that is not over (today, ahead), cancelled, a no-show, older than the look-back or that already has a document (the check-out made it) gets none; a booking whose data is
    /// missing is skipped and counted, not a failure; at most the limit a run, the most recent first. With the automatic issue the drafts are issued, the one the event made too;
    /// without it they wait. Nothing runs with the issuance off or a profile that misses an item.
    /// </summary>
    [Fact]
    public async Task DueJob_OneDraftPerBooking_Test()
    {
        await using var db = NewDb();
        await SeedOrgAsync(db);
        var property = await AddPropertyAsync(db, "auth0|host");
        var direct = await AddBookingAsync(db, property, BookingStatus.CheckedOut, BookingSource.Direct, checkOut: new DateTime(2026, 11, 16));
        var portal = await AddBookingAsync(db, property, BookingStatus.Confirmed, BookingSource.Airbnb, checkOut: new DateTime(2026, 11, 18));
        await AddBookingAsync(db, property, BookingStatus.CheckedIn, BookingSource.Direct, checkOut: new DateTime(2026, 11, 22));
        await AddBookingAsync(db, property, BookingStatus.CheckedOut, BookingSource.Direct, checkOut: new DateTime(2026, 11, 20));
        await AddBookingAsync(db, property, BookingStatus.Cancelled, BookingSource.Direct, checkOut: new DateTime(2026, 11, 14));
        await AddBookingAsync(db, property, BookingStatus.Confirmed, BookingSource.Direct, checkOut: new DateTime(2026, 11, 14), noShow: true);
        await AddBookingAsync(db, property, BookingStatus.CheckedOut, BookingSource.Direct, checkOut: new DateTime(2026, 9, 1));
        var madeByTheEvent = await AddBookingAsync(db, property, BookingStatus.CheckedOut, BookingSource.Direct, checkOut: new DateTime(2026, 11, 19));
        var service = ServiceOf(db);
        await service.CreateDraftAsync(madeByTheEvent, null);

        var first = await service.ProcessDueDocumentsAsync(OrgId, 500, 31);
        var second = await service.ProcessDueDocumentsAsync(OrgId, 500, 31);

        Assert.Equal((2, 2, 0, 0), (first.Examined, first.DraftsCreated, first.DocumentsIssued, first.Skipped));
        Assert.Equal((0, 0, 0, 0), (second.Examined, second.DraftsCreated, second.DocumentsIssued, second.Skipped));
        var drafts = await db.FiscalDocuments.AsNoTracking().ToListAsync();
        Assert.Equal(3, drafts.Count);
        Assert.All(drafts, d => Assert.Equal(FiscalDocumentStatus.Draft, d.Status));
        Assert.Equal(new[] { direct, portal, madeByTheEvent }.Order().ToList(), drafts.Select(d => d.BookingId!.Value).Order().ToList());
        Assert.Single(drafts, d => d.BookingId == madeByTheEvent);

        // Nothing runs with the issuance off, or with a profile that misses an item.
        Assert.Equal(FiscalDocumentsDueSkipReasons.IssuanceOff, (await ServiceOf(db, issuanceOn: false).ProcessDueDocumentsAsync(OrgId, 500, 31)).NotRunReason);
        var profile = await db.OrgFiscalIssuerProfiles.SingleAsync();
        profile.LegalName = null;
        await db.SaveChangesAsync();
        Assert.Equal(FiscalDocumentsDueSkipReasons.ProfileIncomplete, (await service.ProcessDueDocumentsAsync(OrgId, 500, 31)).NotRunReason);
    }

    /// <summary>
    /// The flow «Documents» reads the sections «issuer» and «VAT» of the billing setup (E11/F01/US01/T04): an item missing in them (the tax rule of the regime, or one that another epic
    /// adds) stops the daily run of the organization, and nothing of the other sections does (a series or the automatic issue not confirmed yet never holds back a draft).
    /// </summary>
    [Fact]
    public async Task DueJob_ReadsTheIssuerAndVatSectionsOfTheBillingSetup_AndNothingElse()
    {
        await using var db = NewDb();
        await SeedOrgAsync(db);
        var property = await AddPropertyAsync(db, "auth0|host");
        await AddBookingAsync(db, property, BookingStatus.CheckedOut, BookingSource.Direct, checkOut: new DateTime(2026, 11, 17));
        IReadOnlyCollection<string>? asked = null;
        var progress = new BillingSetupProgress(2, 2, []);
        var setup = new Mock<IBillingSetupService>();
        setup
            .Setup(s => s.GetProgressAsync(OrgId, It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, IReadOnlyCollection<string>?, CancellationToken>((_, sections, _) => asked = sections)
            .ReturnsAsync(() => progress);
        var service = ServiceOf(db, billingSetup: setup.Object);

        // An item missing in the setup of the issuer or of the VAT: the run does nothing for the organization.
        progress = new BillingSetupProgress(1, 2, [new BillingSetupItem(BillingSetupItemKeys.TaxRuleMissing, BillingSetupSections.Vat, BillingSetupReasons.Unavailable)]);
        Assert.Equal(FiscalDocumentsDueSkipReasons.ProfileIncomplete, (await service.ProcessDueDocumentsAsync(OrgId, 500, 31)).NotRunReason);
        Assert.Empty(await db.FiscalDocuments.AsNoTracking().ToListAsync());
        Assert.Equal([BillingSetupSections.Issuer, BillingSetupSections.Vat], asked);

        // Nothing missing in those two sections (the others are not even asked): the draft is made.
        progress = new BillingSetupProgress(2, 2, []);
        var run = await service.ProcessDueDocumentsAsync(OrgId, 500, 31);
        Assert.Null(run.NotRunReason);
        Assert.Equal(1, run.DraftsCreated);
    }

    [Fact]
    public async Task DueJob_TheLimitTakesTheMostRecentFirst_AndTheRestWaitsForTheNextRun()
    {
        await using var db = NewDb();
        await SeedOrgAsync(db);
        var property = await AddPropertyAsync(db, "auth0|host");
        var older = await AddBookingAsync(db, property, BookingStatus.CheckedOut, BookingSource.Direct, checkOut: new DateTime(2026, 11, 14));
        var newer = await AddBookingAsync(db, property, BookingStatus.CheckedOut, BookingSource.Direct, checkOut: new DateTime(2026, 11, 18));
        var service = ServiceOf(db);

        var first = await service.ProcessDueDocumentsAsync(OrgId, 1, 31);
        Assert.Equal(newer, Assert.Single(await db.FiscalDocuments.AsNoTracking().ToListAsync()).BookingId);
        Assert.Equal(1, first.DraftsCreated);

        var second = await service.ProcessDueDocumentsAsync(OrgId, 1, 31);
        Assert.Equal(1, second.DraftsCreated);
        Assert.Equal(new[] { older, newer }.Order().ToList(), (await db.FiscalDocuments.AsNoTracking().ToListAsync()).Select(d => d.BookingId!.Value).Order().ToList());
    }

    [Fact]
    public async Task DueJob_WithTheAutomaticIssue_IssuesTheDrafts_AndWithoutItTheyWait_AndABookingWithoutDataIsSkipped()
    {
        await using var db = NewDb();
        await SeedOrgAsync(db, autoIssue: true);
        var property = await AddPropertyAsync(db, "auth0|host");
        var fresh = await AddBookingAsync(db, property, BookingStatus.CheckedOut, BookingSource.Direct, checkOut: new DateTime(2026, 11, 17));
        var draftedAlready = await AddBookingAsync(db, property, BookingStatus.CheckedOut, BookingSource.Direct, checkOut: new DateTime(2026, 11, 18));
        var withoutData = await AddBookingAsync(db, property, BookingStatus.CheckedOut, BookingSource.Direct, checkOut: new DateTime(2026, 11, 19));
        var service = ServiceOf(db, recipient: id => id == withoutData ? null : Company(id));
        await service.CreateDraftAsync(draftedAlready, null);

        var first = await service.ProcessDueDocumentsAsync(OrgId, 500, 31);
        var second = await service.ProcessDueDocumentsAsync(OrgId, 500, 31);

        // The draft the event made is issued too; the booking without data is skipped (and read again at the next run), never a failure.
        Assert.Equal((3, 1, 2, 1), (first.Examined, first.DraftsCreated, first.DocumentsIssued, first.Skipped));
        Assert.Equal((1, 0, 0, 1), (second.Examined, second.DraftsCreated, second.DocumentsIssued, second.Skipped));
        var documents = await db.FiscalDocuments.AsNoTracking().ToListAsync();
        Assert.Equal(2, documents.Count);
        Assert.All(documents, d => Assert.Equal(FiscalDocumentStatus.Issued, d.Status));
        Assert.Equal(["2026/1", "2026/2"], documents.Select(d => d.Number!).Order().ToList());
        Assert.DoesNotContain(documents, d => d.BookingId == withoutData);

        // Without the automatic issue the same stays only get their drafts.
        await using var manual = NewDb();
        await SeedOrgAsync(manual, autoIssue: false);
        var manualProperty = await AddPropertyAsync(manual, "auth0|host");
        await AddBookingAsync(manual, manualProperty, BookingStatus.CheckedOut, BookingSource.Direct, checkOut: new DateTime(2026, 11, 17));
        var result = await ServiceOf(manual).ProcessDueDocumentsAsync(OrgId, 500, 31);
        Assert.Equal((1, 0), (result.DraftsCreated, result.DocumentsIssued));
        Assert.Equal(FiscalDocumentStatus.Draft, (await manual.FiscalDocuments.AsNoTracking().SingleAsync()).Status);
    }

    /// <summary>
    /// PROOF TEST (T02 point 1): the check-out event makes the draft once, and never the problem of the check-out: delivered twice it is one draft; a refusal of the rules (the data of the
    /// guest is missing) is an answer logged with its code, not a failure; any other failure is thrown again so the outbox delivers the event again; nothing happens for another event or
    /// with the issuance off.
    /// </summary>
    [Fact]
    public async Task CheckOutHandler_ErrorNeverFailsCheckOut_Test()
    {
        await using var db = NewDb();
        await SeedOrgAsync(db);
        var property = await AddPropertyAsync(db, "auth0|host");
        var booking = await AddBookingAsync(db, property, BookingStatus.CheckedOut, BookingSource.Direct, checkOut: new DateTime(2026, 11, 19));
        var flags = new TestFeatureFlags(FeatureFlags.FiscalDocumentIssuance);
        var handler = new FiscalDocumentCheckOutHandler(ServiceOf(db), flags, NullLogger<FiscalDocumentCheckOutHandler>.Instance);

        await handler.HandleAsync(Event(DomainEventType.CheckOutCompleted, booking), CancellationToken.None);
        await handler.HandleAsync(Event(DomainEventType.CheckOutCompleted, booking), CancellationToken.None);
        Assert.Equal((FiscalDocumentStatus.Draft, 1), ((await db.FiscalDocuments.AsNoTracking().SingleAsync()).Status, await db.FiscalDocuments.CountAsync()));

        // Another event, a booking that does not exist and the issuance off: nothing.
        await handler.HandleAsync(Event(DomainEventType.CheckInCompleted, booking), CancellationToken.None);
        await handler.HandleAsync(Event(DomainEventType.CheckOutCompleted, Guid.NewGuid()), CancellationToken.None);
        await new FiscalDocumentCheckOutHandler(ServiceOf(db), new TestFeatureFlags(), NullLogger<FiscalDocumentCheckOutHandler>.Instance)
            .HandleAsync(Event(DomainEventType.CheckOutCompleted, booking), CancellationToken.None);
        Assert.Equal(1, await db.FiscalDocuments.CountAsync());

        // The rules refuse (an answer, not a failure) or the database fails (the event is delivered again).
        var documents = new Mock<IFiscalDocumentService>();
        var refusing = new FiscalDocumentCheckOutHandler(documents.Object, flags, NullLogger<FiscalDocumentCheckOutHandler>.Instance);
        documents.Setup(d => d.EnsureDraftAsync(It.IsAny<Guid>(), null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DomainRuleException(FiscalDocumentErrors.InvoiceDataMissing, "FiscalDocumentInvoiceDataMissing"));
        await refusing.HandleAsync(Event(DomainEventType.CheckOutCompleted, booking), CancellationToken.None);
        documents.Setup(d => d.EnsureDraftAsync(It.IsAny<Guid>(), null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DomainConflictException(FiscalDocumentErrors.AlreadyIssued, "FiscalDocumentAlreadyIssued"));
        await refusing.HandleAsync(Event(DomainEventType.CheckOutCompleted, booking), CancellationToken.None);
        documents.Setup(d => d.EnsureDraftAsync(It.IsAny<Guid>(), null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the database is down"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => refusing.HandleAsync(Event(DomainEventType.CheckOutCompleted, booking), CancellationToken.None));
    }

    /// <summary>
    /// PROOF TEST: permission rule (T02 point 3). A caller restricted to some properties issues only the documents of the properties it reaches, by period, by property and by id: any other
    /// id is a row «not found», the same answer as an id that does not exist, and the drafts out of its reach stay drafts; a caller who reaches the whole org issues the rest.
    /// </summary>
    [Fact]
    public async Task IssueBatch_PartialScopeIssuesOnlyReachable_Test()
    {
        await using var db = NewDb();
        await SeedOrgAsync(db);
        var mine = await AddPropertyAsync(db, "auth0|host-a");
        var others = await AddPropertyAsync(db, "auth0|host-b");
        var service = ServiceOf(db);
        var a1 = (await service.CreateDraftAsync(await AddBookingAsync(db, mine, BookingStatus.CheckedOut, BookingSource.Direct, checkOut: new DateTime(2026, 11, 16)), null)).Id;
        var a2 = (await service.CreateDraftAsync(await AddBookingAsync(db, mine, BookingStatus.CheckedOut, BookingSource.Direct, checkOut: new DateTime(2026, 11, 17)), null)).Id;
        var b1 = (await service.CreateDraftAsync(await AddBookingAsync(db, others, BookingStatus.CheckedOut, BookingSource.Direct, checkOut: new DateTime(2026, 11, 16)), null)).Id;
        var b2 = (await service.CreateDraftAsync(await AddBookingAsync(db, others, BookingStatus.CheckedOut, BookingSource.Direct, checkOut: new DateTime(2026, 11, 17)), null)).Id;
        var restricted = new HostScope(OrgId, OwnerId: "auth0|host-a");

        // By period and by the other property's id: only what the caller reaches is issued; the property out of reach selects nothing.
        var byPeriod = await service.IssueBatchAsync(restricted, new FiscalDocumentBatchRequest(null, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), null));
        Assert.Equal([a1, a2], byPeriod.Rows.Select(r => r.Id).ToList());
        Assert.All(byPeriod.Rows, r => Assert.True(r.Issued));
        var otherProperty = await service.IssueBatchAsync(restricted, new FiscalDocumentBatchRequest(null, null, null, [others]));
        Assert.Empty(otherProperty.Rows);

        // By id: the draft of another host's property is «not found», whatever the caller guesses.
        var byId = await service.IssueBatchAsync(restricted, new FiscalDocumentBatchRequest([b1, Guid.NewGuid()], null, null, null));
        Assert.Equal(2, byId.Rows.Count);
        Assert.All(byId.Rows, r => Assert.Equal((false, FiscalDocumentErrors.NotFound), (r.Issued, r.ErrorCode)));
        Assert.Equal([FiscalDocumentStatus.Draft, FiscalDocumentStatus.Draft], await db.FiscalDocuments.AsNoTracking().Where(d => d.Id == b1 || d.Id == b2).Select(d => d.Status).ToListAsync());

        // The whole org reaches the rest.
        var all = await service.IssueBatchAsync(new HostScope(OrgId), new FiscalDocumentBatchRequest([b1, b2], null, null, null));
        Assert.All(all.Rows, r => Assert.True(r.Issued));
        Assert.All(await db.FiscalDocuments.AsNoTracking().ToListAsync(), d => Assert.Equal(FiscalDocumentStatus.Issued, d.Status));
    }

    /// <summary>
    /// PROOF TEST: data rule (T02 point 3). The answer has one row per document and never says «all issued» when one failed: a draft whose booking was cancelled meanwhile, a document that
    /// is no draft any more and an id that does not exist each get their stable code, the others are issued with consecutive numbers, and what failed is left as it was (a draft with no
    /// number). An empty selection (nothing, empty lists), too many documents and a period that ends before it starts are 422, never «all».
    /// </summary>
    [Fact]
    public async Task IssueBatch_ReportsEachFailure_Test()
    {
        await using var db = NewDb();
        await SeedOrgAsync(db);
        var property = await AddPropertyAsync(db, "auth0|host");
        var service = ServiceOf(db);
        var okBooking1 = await AddBookingAsync(db, property, BookingStatus.CheckedOut, BookingSource.Direct, checkOut: new DateTime(2026, 11, 14));
        var okBooking2 = await AddBookingAsync(db, property, BookingStatus.CheckedOut, BookingSource.Direct, checkOut: new DateTime(2026, 11, 15));
        var badBooking = await AddBookingAsync(db, property, BookingStatus.CheckedOut, BookingSource.Direct, checkOut: new DateTime(2026, 11, 16));
        var doneBooking = await AddBookingAsync(db, property, BookingStatus.CheckedOut, BookingSource.Direct, checkOut: new DateTime(2026, 11, 17));
        var ok1 = (await service.CreateDraftAsync(okBooking1, null)).Id;
        var ok2 = (await service.CreateDraftAsync(okBooking2, null)).Id;
        var bad = (await service.CreateDraftAsync(badBooking, null)).Id;
        var done = (await service.IssueAsync((await service.CreateDraftAsync(doneBooking, null)).Id, confirm: true)).Id;
        await ChangeBookingAsync(db, badBooking, b => { b.Status = BookingStatus.Cancelled; b.CancelledAt = Now.UtcDateTime; });
        var unknown = Guid.NewGuid();

        var result = await service.IssueBatchAsync(new HostScope(OrgId), new FiscalDocumentBatchRequest([ok1, bad, ok2, done, unknown], null, null, null));

        Assert.Equal([ok1, bad, ok2, done, unknown], result.Rows.Select(r => r.Id).ToList());
        Assert.Equal([true, false, true, false, false], result.Rows.Select(r => r.Issued).ToList());
        Assert.Equal(
            [null, FiscalDocumentErrors.BookingNotActive, null, FiscalDocumentErrors.NotDraft, FiscalDocumentErrors.NotFound],
            result.Rows.Select(r => r.ErrorCode).ToList());
        Assert.Equal(["2026/2", "2026/3"], result.Rows.Where(r => r.Issued).Select(r => r.Number!).ToList());
        Assert.Equal((2, 3, 0), (result.Issued, result.Failed, result.Remaining));
        var left = await db.FiscalDocuments.AsNoTracking().SingleAsync(d => d.Id == bad);
        Assert.Equal((FiscalDocumentStatus.Draft, null), (left.Status, left.Number));

        // An empty selection is never «all»; too many documents; a period that ends before it starts.
        var everyone = new HostScope(OrgId);
        foreach (var empty in new[]
                 {
                     new FiscalDocumentBatchRequest(null, null, null, null),
                     new FiscalDocumentBatchRequest([], null, null, []),
                     new FiscalDocumentBatchRequest(null, null, null, []),
                 })
        {
            var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.IssueBatchAsync(everyone, empty));
            Assert.Equal(FiscalDocumentErrors.BatchSelectionRequired, refused.Code);
        }

        var tooMany = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.IssueBatchAsync(everyone, new FiscalDocumentBatchRequest(Enumerable.Range(0, FiscalDocumentBatch.MaxDocuments + 1).Select(_ => Guid.NewGuid()).ToList(), null, null, null)));
        Assert.Equal(FiscalDocumentErrors.BatchTooLarge, tooMany.Code);
        var backwards = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.IssueBatchAsync(everyone, new FiscalDocumentBatchRequest(null, new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 1), null)));
        Assert.Equal(FiscalDocumentErrors.BatchPeriodInvalid, backwards.Code);
    }

    [Fact]
    public async Task IssueBatch_SelectionByPeriodIssuesTheDraftsOfTheStays_OldestFirst_AndAnEmptyRemainderIsNotAnError()
    {
        await using var db = NewDb();
        await SeedOrgAsync(db);
        var property = await AddPropertyAsync(db, "auth0|host");
        var service = ServiceOf(db);
        for (var i = 0; i < 3; i++)
            await service.CreateDraftAsync(await AddBookingAsync(db, property, BookingStatus.CheckedOut, BookingSource.Direct, checkOut: new DateTime(2026, 11, 10 + i)), null);

        // The drafts of the stays of the period, the oldest check-out first, numbered in that order.
        var request = new FiscalDocumentBatchRequest(null, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), null);
        var result = await service.IssueBatchAsync(new HostScope(OrgId), request);

        Assert.Equal((3, 0, 0), (result.Issued, result.Failed, result.Remaining));
        Assert.Equal(["2026/1", "2026/2", "2026/3"], result.Rows.Select(r => r.Number!).ToList());
        Assert.Equal(
            [new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 11), new DateOnly(2026, 11, 12)],
            (await db.FiscalDocuments.AsNoTracking().OrderBy(d => d.SequenceNumber).ToListAsync()).Select(d => d.ServicePeriodTo!.Value).ToList());

        // Nothing left to issue: an empty answer, not an error.
        Assert.Empty((await service.IssueBatchAsync(new HostScope(OrgId), request)).Rows);
    }

    /// <summary>
    /// The live refresh (T02 point 5): the issue of one document, asked by a person, tells the hosts of the booking (the push the decorator turns into the bell and the live message), once for
    /// the document and with no guest and no amount in the text; the bulk issue and the daily run do not notify (a message for each of hundreds of documents is no help to anybody).
    /// </summary>
    [Fact]
    public async Task Issue_TellsTheHostsOfTheBookingOnce_ButTheBulkIssueAndTheDailyRunDoNot()
    {
        await using var db = NewDb();
        await SeedOrgAsync(db, autoIssue: true);
        var property = await AddPropertyAsync(db, "auth0|host");
        var pushes = new List<(string Key, PushAudience Audience, PushNotificationPayload Payload)>();
        var queue = new Mock<IPushNotificationService>();
        queue.Setup(p => p.Enqueue(It.IsAny<string>(), It.IsAny<PushAudience>(), It.IsAny<PushNotificationPayload>()))
            .Callback<string, PushAudience, PushNotificationPayload>((key, audience, payload) => pushes.Add((key, audience, payload)))
            .Returns(true);
        var service = ServiceOf(db, push: queue.Object);
        var one = await AddBookingAsync(db, property, BookingStatus.CheckedOut, BookingSource.Direct, checkOut: new DateTime(2026, 11, 16));
        var two = await AddBookingAsync(db, property, BookingStatus.CheckedOut, BookingSource.Direct, checkOut: new DateTime(2026, 11, 17));

        var issued = await service.IssueAsync((await service.CreateDraftAsync(one, null)).Id, confirm: true);

        var push = Assert.Single(pushes);
        Assert.Equal(PushDeliveryKeys.FiscalDocumentIssued(issued.Id), push.Key);
        Assert.Equal(PushAudience.BookingHosts(one), push.Audience);
        Assert.Equal((PushTypes.FiscalDocumentIssued, one, PushRoutes.Booking(one)), (push.Payload.Type, push.Payload.BookingId, push.Payload.Route));
        Assert.Equal("fiscal-document-issued", PushTypes.FiscalDocumentIssued);
        var said = push.Payload.Title + "|" + push.Payload.Body;
        Assert.DoesNotContain("Guest", said, StringComparison.Ordinal);
        Assert.DoesNotContain("TEST Cliente", said, StringComparison.Ordinal);
        Assert.DoesNotContain("350", said, StringComparison.Ordinal);

        // The daily run (the automatic issue) and the bulk issue say nothing.
        await service.ProcessDueDocumentsAsync(OrgId, 500, 31);
        var draft = (await service.CreateDraftAsync(await AddBookingAsync(db, property, BookingStatus.CheckedOut, BookingSource.Manual, checkOut: new DateTime(2026, 11, 15)), null)).Id;
        await service.IssueBatchAsync(new HostScope(OrgId), new FiscalDocumentBatchRequest([draft], null, null, null));
        Assert.Single(pushes);
        Assert.Equal(FiscalDocumentStatus.Issued, (await db.FiscalDocuments.AsNoTracking().SingleAsync(d => d.BookingId == two)).Status);

        // A credit note asked by a person is told too; a push that cannot be queued never fails the issue.
        var credit = await service.CreateCreditNoteAsync(issued.Id, 50m, "TEST", confirm: true, null);
        Assert.Equal(PushDeliveryKeys.FiscalDocumentIssued(credit.Id), pushes[^1].Key);
        queue.Setup(p => p.Enqueue(It.IsAny<string>(), It.IsAny<PushAudience>(), It.IsAny<PushNotificationPayload>())).Throws(new InvalidOperationException("push down"));
        var third = await AddBookingAsync(db, property, BookingStatus.CheckedOut, BookingSource.Direct, checkOut: new DateTime(2026, 11, 14));
        Assert.Equal(FiscalDocumentStatus.Issued, (await service.IssueAsync((await service.CreateDraftAsync(third, null)).Id, confirm: true)).Status);
    }

    [Fact]
    public async Task EnsureDraft_MakesOneDraftWhateverTheCaller_AndLeavesABookingWithADocumentAlone()
    {
        await using var db = NewDb();
        await SeedOrgAsync(db);
        var property = await AddPropertyAsync(db, "auth0|host");
        var booking = await AddBookingAsync(db, property, BookingStatus.CheckedOut, BookingSource.Direct, checkOut: new DateTime(2026, 11, 16));
        var imported = await AddBookingAsync(db, property, BookingStatus.CheckedOut, BookingSource.Direct, checkOut: new DateTime(2026, 11, 16));
        db.FiscalDocuments.Add(new FiscalDocument
        {
            OrgId = OrgId,
            BookingId = imported,
            Source = FiscalDocumentSource.Octorate,
            Kind = DocumentKind.Invoice,
            Status = FiscalDocumentStatus.Issued,
            Number = "OCT/1",
            Currency = "EUR",
            Total = 100m,
            OctorateDocumentId = "D-1",
        });
        await db.SaveChangesAsync();
        var service = ServiceOf(db);

        Assert.Equal(EnsureDraftOutcome.Created, await service.EnsureDraftAsync(booking, null));
        Assert.Equal(EnsureDraftOutcome.AlreadyHasDocument, await service.EnsureDraftAsync(booking, null));
        Assert.Equal(EnsureDraftOutcome.AlreadyHasDocument, await service.EnsureDraftAsync(imported, null));
        Assert.Equal(1, await db.FiscalDocuments.CountAsync(d => d.BookingId == booking));

        // A document discarded is no document: the next call makes a new draft.
        await service.DiscardDraftAsync((await db.FiscalDocuments.SingleAsync(d => d.BookingId == booking)).Id);
        Assert.Equal(EnsureDraftOutcome.Created, await service.EnsureDraftAsync(booking, null));
        Assert.Equal(1, await db.FiscalDocuments.CountAsync(d => d.BookingId == booking && d.Status == FiscalDocumentStatus.Draft));
    }

    [Fact]
    public async Task OrgsWithDocumentsDue_AreTheOnesWithAnIssuerProfile_WhateverTheTenant()
    {
        await using var db = NewDb();
        await SeedOrgAsync(db);
        var other = Guid.NewGuid();
        db.Orgs.Add(NewOrg(other));
        db.OrgFiscalIssuerProfiles.Add(new OrgFiscalIssuerProfile { OrgId = other, DocumentRegime = DocumentRegime.VatOrdinary });
        db.Orgs.Add(NewOrg(Guid.NewGuid()));
        await db.SaveChangesAsync();

        var orgs = await ServiceOf(db).OrgsWithDocumentsDueAsync();

        Assert.Equal(new[] { OrgId, other }.Order().ToList(), orgs.Order().ToList());
    }

    // ---------------------------------------------------------------- the job and its registration

    [Fact]
    public async Task Job_AnOrgThatFailsDoesNotStopTheOthers_AndEachRunsUnderItsOwnTenant_AndNothingRunsWithTheIssuanceOff()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var entered = new List<Guid>();
        var processed = new List<Guid>();
        var documents = new Mock<IFiscalDocumentService>();
        documents.Setup(d => d.OrgsWithDocumentsDueAsync(It.IsAny<CancellationToken>())).ReturnsAsync([first, second]);
        documents.Setup(d => d.ProcessDueDocumentsAsync(first, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("one org is broken"));
        documents.Setup(d => d.ProcessDueDocumentsAsync(second, FiscalDocumentsDueJob.MaxBookingsPerRun, 31, It.IsAny<CancellationToken>()))
            .Callback<Guid, int, int, CancellationToken>((org, _, _, _) => processed.Add(org))
            .ReturnsAsync(new FiscalDocumentsDueResult(null, 1, 1, 0, 0));
        var services = new ServiceCollection();
        services.AddScoped(_ => documents.Object);
        services.AddScoped<IBackgroundTenantScope>(_ => new RecordingTenantScope(entered));
        await using var provider = services.BuildServiceProvider();

        var job = new FiscalDocumentsDueJob(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new TestFeatureFlags(FeatureFlags.FiscalDocumentIssuance),
            Options.Create(new FiscalDocumentRulesOptions { DueLookbackDays = 31 }),
            NullLogger<FiscalDocumentsDueJob>.Instance);

        await job.ExecuteAsync(CancellationToken.None);

        Assert.Equal([first, second], entered);
        Assert.Equal([second], processed);

        var off = new FiscalDocumentsDueJob(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new TestFeatureFlags(),
            Options.Create(new FiscalDocumentRulesOptions()),
            NullLogger<FiscalDocumentsDueJob>.Instance);
        documents.Invocations.Clear();
        await off.ExecuteAsync(CancellationToken.None);
        documents.VerifyNoOtherCalls();
    }

    [Fact]
    public void Job_IsRegisteredOnlyWhileTheIssuanceIsOn_DailyAt0510InRome_AndNeverRunsTwiceAtOnce()
    {
        Assert.Equal("fiscal-documents-due", FiscalDocumentsDueJob.RecurringJobId);

        var on = Schedule(issuance: true);
        var job = on.Registered[FiscalDocumentsDueJob.RecurringJobId];
        Assert.Equal(("10 5 * * *", RomeCalendar.TimeZone), (job.Cron, job.Options.TimeZone));
        Assert.Equal(typeof(FiscalDocumentsDueJob), job.Job.Type);
        on.Manager.Verify(m => m.RemoveIfExists(FiscalDocumentsDueJob.RecurringJobId), Times.Never);
        var attribute = job.Job.Method.GetCustomAttribute<DisableConcurrentExecutionAttribute>();
        Assert.NotNull(attribute);
        Assert.InRange(attribute.TimeoutSec, 1, 300);

        var off = Schedule(issuance: false);
        Assert.DoesNotContain(FiscalDocumentsDueJob.RecurringJobId, off.Registered.Keys);
        off.Manager.Verify(m => m.RemoveIfExists(FiscalDocumentsDueJob.RecurringJobId), Times.Once);
    }

    // ---------------------------------------------------------------- the endpoints

    [Fact]
    public async Task Controller_DocumentsOfABooking_AreServedToWhoMayReadItsPayments_AndNotFoundToTheOthers()
    {
        var bookingId = Guid.NewGuid();
        var documents = new Mock<IFiscalDocumentService>();
        documents.Setup(d => d.ListAsync(It.IsAny<HostScope>(), It.Is<FiscalDocumentCriteria>(c => c.BookingId == bookingId), 1, FiscalDocumentPage.MaxPageSize, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FiscalDocumentPage(
                [
                    Document(bookingId, FiscalDocumentStatus.Issued, "2026/1", 362m),
                    Document(bookingId, FiscalDocumentStatus.Discarded, null, 362m),
                    Document(bookingId, FiscalDocumentStatus.Draft, null, 120m),
                ],
                3,
                1,
                FiscalDocumentPage.MaxPageSize));
        var resources = new Mock<IHostResourceLookup>();
        resources.Setup(r => r.ForBookingAsync(bookingId, It.IsAny<CancellationToken>())).ReturnsAsync(new HostResource(OrgId, "auth0|owner", Guid.NewGuid()));

        var denied = await Controller(documents.Object, resources.Object, allowed: false, new HostScope(OrgId, GrantedToUserId: "auth0|collaborator")).ForBooking(bookingId);
        Assert.Equal(StatusCodes.Status404NotFound, (denied.Result as IStatusCodeActionResult)?.StatusCode);
        documents.Verify(d => d.ListAsync(It.IsAny<HostScope>(), It.IsAny<FiscalDocumentCriteria>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);

        var served = await Controller(documents.Object, resources.Object, allowed: true, new HostScope(OrgId)).ForBooking(bookingId);
        var rows = Assert.IsAssignableFrom<IReadOnlyList<BookingFiscalDocumentDto>>(Assert.IsType<OkObjectResult>(served.Result).Value);
        Assert.Equal([("Invoice", "Issued", "2026/1", 362m, "EUR"), ("Invoice", "Draft", null, 120m, "EUR")], rows.Select(r => (r.Kind, r.Status, r.Number, r.Total, r.Currency)).ToList());
        Assert.All(rows, r => Assert.Equal(nameof(FiscalDocumentPaymentState.ToCollect), r.PaymentState));

        // Unknown booking: the same answer as one the caller may not read.
        var missing = await Controller(documents.Object, Mock.Of<IHostResourceLookup>(), allowed: true, new HostScope(OrgId)).ForBooking(Guid.NewGuid());
        Assert.Equal(StatusCodes.Status404NotFound, (missing.Result as IStatusCodeActionResult)?.StatusCode);
    }

    [Fact]
    public async Task Controller_IssueBatch_PassesTheScopeOfTheCaller_AndAnswersWithTheCounts()
    {
        var okId = Guid.NewGuid();
        var failedId = Guid.NewGuid();
        HostScope? seen = null;
        var documents = new Mock<IFiscalDocumentService>();
        documents.Setup(d => d.IssueBatchAsync(It.IsAny<HostScope>(), It.IsAny<FiscalDocumentBatchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<HostScope, FiscalDocumentBatchRequest, CancellationToken>((scope, _, _) => seen = scope)
            .ReturnsAsync(new FiscalDocumentBatchResult(
                [new FiscalDocumentBatchRow(okId, true, "2026/1", null), new FiscalDocumentBatchRow(failedId, false, null, FiscalDocumentErrors.BookingNotActive)],
                4));
        var scope = new HostScope(OrgId, GrantedToUserId: "auth0|collaborator");

        var answer = await Controller(documents.Object, Mock.Of<IHostResourceLookup>(), allowed: true, scope)
            .IssueBatch(new IssueFiscalDocumentsBatchRequest { DocumentIds = [okId, failedId] });

        var body = Assert.IsType<FiscalDocumentBatchResultDto>(Assert.IsType<OkObjectResult>(answer.Result).Value);
        Assert.Equal(scope, seen);
        Assert.Equal((1, 1, 4), (body.Issued, body.Failed, body.Remaining));
        Assert.Equal([(okId, true, "2026/1", (string?)null), (failedId, false, null, FiscalDocumentErrors.BookingNotActive)], body.Rows.Select(r => (r.Id, r.Issued, r.Number, r.ErrorCode)).ToList());
    }

    [Fact]
    public void BatchLimits_AndCodes_AreTheOnesOfTheBrief()
    {
        Assert.Equal(200, FiscalDocumentBatch.MaxDocuments);
        Assert.Equal(500, FiscalDocumentsDueJob.MaxBookingsPerRun);
        Assert.Equal("document_batch_selection_required", FiscalDocumentErrors.BatchSelectionRequired);
        Assert.Equal("document_batch_too_large", FiscalDocumentErrors.BatchTooLarge);
        Assert.Equal("document_batch_period_invalid", FiscalDocumentErrors.BatchPeriodInvalid);
    }

    // ---------------------------------------------------------------- helpers

    private static DomainEvent Event(DomainEventType type, Guid entityId) =>
        new(Guid.NewGuid(), OrgId, type, entityId, Now.UtcDateTime, "{}");

    private static InvoiceDataView Company(Guid bookingId) =>
        new(bookingId, true, Now.UtcDateTime, InvoiceRecipientKind.Company, "TEST Cliente Srl", null, "IT09876543217", "Via del Cliente", "5",
            "00100", "Roma", "RM", "IT", "ABC1234", null, InvoiceDataSource.Host, Now.UtcDateTime);

    private static FiscalDocument Document(Guid bookingId, FiscalDocumentStatus status, string? number, decimal total) =>
        new()
        {
            OrgId = OrgId,
            BookingId = bookingId,
            Source = FiscalDocumentSource.CasaZen,
            Kind = DocumentKind.Invoice,
            Status = status,
            Number = number,
            Total = total,
            Currency = "EUR",
            PaymentState = FiscalDocumentPaymentState.ToCollect,
        };

    private static FiscalDocumentService ServiceOf(
        AppDbContext db,
        bool issuanceOn = true,
        IPushNotificationService? push = null,
        Func<Guid, InvoiceDataView?>? recipient = null,
        IBillingSetupService? billingSetup = null)
    {
        var flags = issuanceOn ? new TestFeatureFlags(FeatureFlags.FiscalDocumentIssuance) : new TestFeatureFlags();
        var rules = TaxRuleCatalogs.Resolver();
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Test");
        var time = new FakeTimeProvider(Now);
        var issuers = new DocumentIssuerService(db, flags, rules, environment.Object, time, NullLogger<DocumentIssuerService>.Instance);
        var invoiceData = new Mock<IInvoiceDataService>();
        invoiceData.Setup(i => i.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid bookingId, CancellationToken _) => recipient is null ? Company(bookingId) : recipient(bookingId));
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
            push,
            flags,
            billingSetup);
    }

    private static (Mock<IRecurringJobManager> Manager, Dictionary<string, (Job Job, string Cron, RecurringJobOptions Options)> Registered) Schedule(bool issuance)
    {
        var registered = new Dictionary<string, (Job, string, RecurringJobOptions)>();
        var manager = new Mock<IRecurringJobManager>();
        manager
            .Setup(m => m.AddOrUpdate(It.IsAny<string>(), It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<RecurringJobOptions>()))
            .Callback<string, Job, string, RecurringJobOptions>((id, job, cron, options) => registered[id] = (job, cron, options));
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.FiscalDocumentIssuance)).Returns(issuance);

        RecurringJobsRegistration.Configure(manager.Object, flags.Object);

        return (manager, registered);
    }

    private static FiscalDocumentsController Controller(IFiscalDocumentService documents, IHostResourceLookup resources, bool allowed, HostScope scope)
    {
        var authorization = new Mock<IAuthorizationService>();
        authorization
            .Setup(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<IEnumerable<IAuthorizationRequirement>>()))
            .ReturnsAsync(allowed ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        var org = new Mock<IOrgContextResolver>();
        org.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OrgId);
        var scopes = new Mock<IHostScopeResolver>();
        scopes.Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>(), OrgId, It.IsAny<CancellationToken>())).ReturnsAsync(scope);
        var localizer = new Mock<IStringLocalizer<SharedResources>>();
        localizer.Setup(l => l[It.IsAny<string>()]).Returns((string name) => new LocalizedString(name, name, resourceNotFound: true));
        var requestServices = new ServiceCollection().AddSingleton(localizer.Object).BuildServiceProvider();
        var identity = new ClaimsIdentity([new Claim("sub", "auth0|collaborator"), new Claim(ClaimTypes.NameIdentifier, "auth0|collaborator")], "test");
        return new FiscalDocumentsController(documents, resources, authorization.Object, org.Object, scopes.Object, NullLogger<FiscalDocumentsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity), RequestServices = requestServices },
            },
        };
    }

    private static async Task ChangeBookingAsync(AppDbContext db, Guid bookingId, Action<Booking> change)
    {
        var booking = await db.Bookings.SingleAsync(b => b.Id == bookingId);
        change(booking);
        booking.UpdatedAt = Now.UtcDateTime;
        await db.SaveChangesAsync();
    }

    private static OrgEntity NewOrg(Guid id) => new()
    {
        Id = id,
        Name = "TEST Casa Vacanze",
        Slug = $"test-{id:N}"[..20],
        DisplayName = "TEST Casa Vacanze",
        ContactEmail = "test@example.com",
        HasPartitaIva = true,
        PartitaIvaNumber = "01234567897",
        FiscalCode = "01234567897",
    };

    private static async Task SeedOrgAsync(AppDbContext db, bool autoIssue = false)
    {
        db.Orgs.Add(NewOrg(OrgId));
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
            AutoIssueGuestDocuments = autoIssue,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> AddPropertyAsync(AppDbContext db, string ownerId)
    {
        var property = new Property { OrgId = OrgId, OwnerId = ownerId, Name = "TEST Casa", Address = "Via 1", City = "Roma", Currency = "EUR" };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property.Id;
    }

    private static async Task<Guid> AddBookingAsync(
        AppDbContext db,
        Guid propertyId,
        BookingStatus status,
        BookingSource source,
        DateTime checkOut,
        bool noShow = false)
    {
        var guest = new Guest { OrgId = OrgId, FirstName = "Test", LastName = "Guest", Email = $"{Guid.NewGuid():N}@example.com" };
        var booking = new Booking
        {
            OrgId = OrgId,
            PropertyId = propertyId,
            GuestId = guest.Id,
            CheckInDate = checkOut.AddDays(-3),
            CheckOutDate = checkOut,
            NumberOfGuests = 2,
            Status = status,
            Source = source,
            BasePrice = 350m,
            CleaningFee = 50m,
            TotalPrice = 350m,
            NoShowAt = noShow ? Now.UtcDateTime.AddDays(-6) : null,
        };
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        return booking.Id;
    }

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"FiscalAutomation_{Guid.NewGuid()}").Options, new AutomationTenant(), new EphemeralDataProtectionProvider());

    private sealed class AutomationTenant : ITenantContext
    {
        public Guid? OrgId => null;

        public bool FilterEnabled => false;
    }

    private sealed class RecordingTenantScope(List<Guid> entered) : IBackgroundTenantScope
    {
        public void EnterOrg(Guid orgId) => entered.Add(orgId);
    }
}
