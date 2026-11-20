using System.Reflection;
using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data.Encryption;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Infrastructure;
using Casazen.Tests.Unit.Logging;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Fiscal;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using AllowAnonymousAttribute = Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute;
using AuthorizeAttribute = Microsoft.AspNetCore.Authorization.AuthorizeAttribute;
using CasazenPolicies = Casazen.Web.Authorization.CasazenPolicies;
using FeatureGateAttribute = Casazen.Web.Infrastructure.FeatureGateAttribute;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// FIX/BE-TD17-JOB-EXIT, decision 2 of the fiscal decisions of 2026-11-20 (research R3): the exit of the replacement draft of a TD17 the SDI rejected that the month no longer supports. It is closed as
/// «Chiusa, non più dovuta» (an internal outcome, never «scarto confermato») with a mandatory reason, the user and the day; the draft keeps its series, its sequence number and its number, which are never
/// freed and never given to another document; only the replacement of a rejected TD17 that the system reports as kept can be closed, only by who manages the self-billed invoices of the whole organization, in
/// the own organization; the free note of the closing is encrypted at rest and never logged.
/// </summary>
public class SelfBillingCloseNotDueTests
{
    private const string Host = "auth0|agency";
    private const string Caller = "auth0|caller";

    /// <summary>
    /// PROOF TEST: money and numbering rule. A kept replacement (the commissions of its month were reversed) is closed as «non più dovuta»: the draft becomes discarded with its reason, the user (named in the read
    /// model) and the day, and <b>keeps its series, its sequence number and its number</b>; the rejected original stays as history and is never corrected again. The generic discard stays refused. Neither the drafts
    /// of the month nor the monthly run take the number or touch the closed draft; when the commission comes back a new draft is made and, issued, takes the NEXT number of the series, never the closed one (no
    /// duplicate), so the series has a documented gap. The same call again with the same reason answers the closed draft unchanged; another reason is refused.
    /// </summary>
    [Fact]
    public async Task ClosedNotDue_KeepsTheNumberReserved_AndNeverGivesItAgain_Test()
    {
        await using var rig = await SelfBillingRig.CreateAsync();
        await rig.AddAsync(new User { Id = Host, Email = "mario.rossi@example.com", FirstName = "Mario", LastName = "Rossi", Role = UserRole.PropertyManager });
        var (original, replacement) = await KeptReplacementAsync(rig);
        Assert.Equal((FiscalDocumentStatus.Draft, (int?)1, "TD17-2026/1"), (replacement.Status, replacement.SequenceNumber, replacement.Number));

        // Before the closing the generic discard is refused (the draft holds the number) and the list says why it is kept.
        Assert.Equal(FiscalDocumentSdiErrors.ReplacementHoldsNumber, (await Assert.ThrowsAsync<DomainRuleException>(() => rig.Service.DiscardDraftAsync(rig.OrgId, replacement.Id))).Code);
        Assert.Equal(SelfBillingKeptReasons.NotInLedger, Assert.Single(await rig.Service.ListAsync(rig.OrgId, new SelfBillingCriteria(2026, 10)), r => r.Id == replacement.Id).KeptReason);

        // The host closes it as no longer due, with the reason of the portal credit note and a note (the 13th of November, 09:30 UTC).
        rig.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 13, 9, 30, 0, TimeSpan.Zero));
        var closed = await rig.Service.CloseNotDueAsync(rig.OrgId, replacement.Id, SelfBillingCloseReasons.PortalCreditNote, "  Nota di credito 4711 del portale  ", Host);

        var row = closed.Row;
        Assert.Equal((replacement.Id, FiscalDocumentStatus.Discarded, "TD17-2026/1", (Guid?)original.Id, (string?)null), (row.Id, row.Status, row.Number, row.ReplacesDocumentId, row.KeptReason));
        var closedAt = new DateTime(2026, 11, 13, 9, 30, 0, DateTimeKind.Utc);
        Assert.Equal(("portal_credit_note", (DateTime?)closedAt, "Mario Rossi", "Nota di credito 4711 del portale"), (row.ClosedReason, row.ClosedAt, row.ClosedBy, row.ClosedNote));

        // The document keeps its series, its sequence number and its display number, with the reason, the note, the user and the day; nothing was made or sent to the SDI.
        var stored = await rig.DocumentAsync(replacement.Id);
        Assert.Equal((FiscalDocumentStatus.Discarded, "TD17-2026", (int?)1, "TD17-2026/1", (Guid?)original.Id), (stored.Status, stored.Series, stored.SequenceNumber, stored.Number, stored.ReplacesDocumentId));
        Assert.Equal(("portal_credit_note", "Nota di credito 4711 del portale", Host, (DateTime?)closedAt), (stored.ClosedNotDueReason, stored.ClosedNotDueNote, stored.ClosedNotDueByUserId, stored.ClosedNotDueAt));
        Assert.Equal((FiscalDocumentSdiStatuses.NotApplicable, (string?)null, (DateOnly?)null), (stored.SdiStatus, stored.FatturaPaXml, stored.IssueDate));

        // The rejected original stays as history (its display number, its «rejected»), is not closed itself and is never corrected again (it is no longer issued).
        var history = await rig.DocumentAsync(original.Id);
        Assert.Equal((FiscalDocumentStatus.Discarded, (int?)null, "TD17-2026/1", FiscalDocumentSdiStatuses.Rejected, (DateTime?)null), (history.Status, history.SequenceNumber, history.Number, history.SdiStatus, history.ClosedNotDueAt));
        Assert.Equal(SelfBillingErrors.NotCorrectable, (await Assert.ThrowsAsync<DomainRuleException>(() => CorrectionsOf(rig).StartSelfBillingCorrectionAsync(rig.OrgId, original.Id, Host))).Code);

        // The generic discard stays refused, and the list shows the closed draft with the history of its number next to the rejected original, which names it.
        Assert.Equal(FiscalDocumentErrors.NotDraft, (await Assert.ThrowsAsync<DomainRuleException>(() => rig.Service.DiscardDraftAsync(rig.OrgId, replacement.Id))).Code);
        var listed = await rig.Service.ListAsync(rig.OrgId, new SelfBillingCriteria(2026, 10));
        Assert.Equal(new[] { original.Id, replacement.Id }.Order(), listed.Select(r => r.Id).Order());
        var listedClosed = Assert.Single(listed, r => r.Id == replacement.Id);
        Assert.Equal((FiscalDocumentStatus.Discarded, "TD17-2026/1", "portal_credit_note", "Mario Rossi", false), (listedClosed.Status, listedClosed.Number, listedClosed.ClosedReason, listedClosed.ClosedBy, listedClosed.CanCorrect));
        var listedOriginal = Assert.Single(listed, r => r.Id == original.Id);
        Assert.Equal(((Guid?)replacement.Id, false, (string?)null), (listedOriginal.ReplacedByDocumentId, listedOriginal.CanCorrect, listedOriginal.ClosedReason));
        Assert.Equal(replacement.Id, Assert.Single(listed, r => r.ClosedReason != null).Id);

        // Neither the drafts of the month (a click) nor the monthly run take the number again or touch the closed draft.
        var snapshot = await SnapshotAsync(rig);
        var generation = await rig.Service.CreateDraftsForPeriodAsync(rig.OrgId, 2026, 10, Host);
        Assert.Equal((0, 0, 0, 0), (generation.Created, generation.Updated, generation.AlreadyIssued, generation.Discarded));
        Assert.Null(generation.Kept);
        var run = await rig.Service.RunMonthlyDraftsAsync(rig.OrgId, 2026, 10);
        Assert.Equal((0, 0), (run.Created, run.OpenDrafts));
        Assert.Equal(snapshot, await SnapshotAsync(rig));

        // The commission comes back for the same holder, portal and month: the month makes a NEW draft, with no number; issued, it takes the NEXT number, never the closed one.
        await rig.AddAsync(rig.Fee(rig.Company, rig.Lake, await rig.StayAsync(rig.Lake, BookingSource.BookingCom, new DateTime(2026, 10, 20)), 30.00m));
        var again = await rig.Service.CreateDraftsForPeriodAsync(rig.OrgId, 2026, 10, Host);
        var fresh = Assert.Single(again.Drafts);
        Assert.Equal((1, SelfBillingResults.Created), (again.Created, fresh.Result));
        Assert.NotEqual(replacement.Id, fresh.DocumentId);
        Assert.Null((await rig.DocumentAsync(fresh.DocumentId)).SequenceNumber);
        var issued = (await rig.Service.IssueAsync(rig.OrgId, fresh.DocumentId, confirm: true, Host)).Document;
        Assert.Equal(("TD17-2026/2", (int?)2), (issued.Number, issued.SequenceNumber));
        Assert.Equal([1, 2], await SequenceNumbersAsync(rig));
        Assert.Equal(2, (await rig.Db.FiscalNumberSequences.AsNoTracking().SingleAsync()).LastNumber);
        var untouched = await rig.DocumentAsync(replacement.Id);
        Assert.Equal((FiscalDocumentStatus.Discarded, (int?)1, "TD17-2026/1", "portal_credit_note", (DateTime?)closedAt), (untouched.Status, untouched.SequenceNumber, untouched.Number, untouched.ClosedNotDueReason, untouched.ClosedNotDueAt));

        // The same call again (a double click, a retry) answers the closed draft and changes nothing: who closed it, why and when is never rewritten; another reason is refused.
        rig.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 14, 8, 0, 0, TimeSpan.Zero));
        var twice = await rig.Service.CloseNotDueAsync(rig.OrgId, replacement.Id, "PORTAL_CREDIT_NOTE", "another note", "auth0|someone-else");
        Assert.Equal(((DateTime?)closedAt, "Mario Rossi", "Nota di credito 4711 del portale"), (twice.Row.ClosedAt, twice.Row.ClosedBy, twice.Row.ClosedNote));
        var other = await Assert.ThrowsAsync<DomainRuleException>(() => rig.Service.CloseNotDueAsync(rig.OrgId, replacement.Id, SelfBillingCloseReasons.DuplicateOrError, null, Host));
        Assert.Equal(SelfBillingErrors.NotClosable, other.Code);
        var still = await rig.DocumentAsync(replacement.Id);
        Assert.Equal(("portal_credit_note", "Nota di credito 4711 del portale", Host), (still.ClosedNotDueReason, still.ClosedNotDueNote, still.ClosedNotDueByUserId));
    }

    /// <summary>
    /// PROOF TEST: permission and tenant rule. A closing needs a reason (missing, blank or unknown: 422 <c>close_reason_required</c>) and a short note (422 <c>close_note_too_long</c>); only the replacement of a
    /// rejected TD17 that the month no longer supports is closed (422 <c>self_billing_not_closable</c> for one the month still gives, an ordinary draft, an issued TD17, the rejected original), a document that is no TD17
    /// is not found, and a refusal changes nothing. A TD17 of another organization is not found (404); a caller who reaches only some properties is refused (403) before any service is asked; the action is behind
    /// <c>agency.invoice.manage</c> (the policy of the controller, never anonymous), <c>Features:FiscalDocumentIssuance</c> and <c>Features:SdiSubmission</c>; a caller who reaches the whole organization is served and is the
    /// user the closing records.
    /// </summary>
    [Fact]
    public async Task ClosedNotDue_NeedsReasonPermissionAndOrganization_Test()
    {
        await using var rig = await SelfBillingRig.CreateAsync();
        var bookingFee = rig.Fee(rig.Company, rig.Lake, await rig.StayAsync(rig.Lake, BookingSource.BookingCom, new DateTime(2026, 10, 5)), 100.00m);
        var airbnbFee = rig.Fee(rig.Company, rig.Mountain, await rig.StayAsync(rig.Mountain, BookingSource.Airbnb, new DateTime(2026, 10, 12)), 60.00m);
        await rig.AddAsync(bookingFee, airbnbFee);
        var made = await rig.Service.CreateDraftsForPeriodAsync(rig.OrgId, 2026, 10, Host);
        var bookingOriginal = made.Drafts.Single(d => d.PortalName == "Booking.com B.V.").DocumentId;
        var airbnbOriginal = made.Drafts.Single(d => d.PortalName == "Airbnb Ireland UC").DocumentId;
        var corrections = CorrectionsOf(rig);
        var replacements = new List<Guid>();
        foreach (var id in new[] { bookingOriginal, airbnbOriginal })
        {
            await rig.Service.IssueAsync(rig.OrgId, id, confirm: true, Host);
            await RejectedBySdiAsync(rig, id);
            replacements.Add((await corrections.StartSelfBillingCorrectionAsync(rig.OrgId, id, Host)).Id);
        }

        var (bookingReplacement, airbnbReplacement) = (replacements[0], replacements[1]);

        // The month still gives both TD17: a replacement it would adopt is issued, not closed (and a refusal changes nothing).
        await AssertNotClosableAsync(rig, bookingReplacement);
        await AssertNotClosableAsync(rig, airbnbReplacement);

        // The Airbnb commission is reversed: its replacement is kept and can be closed; the Booking.com one is still supported.
        await rig.AddAsync(rig.Reversal(airbnbFee));
        Assert.Equal(SelfBillingKeptReasons.NotInLedger, Assert.Single(await rig.Service.ListAsync(rig.OrgId, new SelfBillingCriteria(2026, 10)), r => r.Id == airbnbReplacement).KeptReason);
        await AssertNotClosableAsync(rig, bookingReplacement);

        // The reason is mandatory and is one of the three; the note is short. Every refusal changes nothing.
        var before = await SnapshotAsync(rig);
        foreach (var reason in new string?[] { null, string.Empty, "   ", "other", SelfBillingKeptReasons.NotInLedger })
        {
            var refused = await Assert.ThrowsAsync<DomainRuleException>(() => rig.Service.CloseNotDueAsync(rig.OrgId, airbnbReplacement, reason, null, Host));
            Assert.Equal((SelfBillingErrors.CloseReasonRequired, "SelfBillingCloseReasonRequired"), (refused.Code, refused.MessageKey));
        }

        var tooLong = await Assert.ThrowsAsync<DomainRuleException>(() => rig.Service.CloseNotDueAsync(rig.OrgId, airbnbReplacement, SelfBillingCloseReasons.DuplicateOrError, new string('x', FiscalDocument.ClosedNotDueNoteMaxLength + 1), Host));
        Assert.Equal(SelfBillingErrors.CloseNoteTooLong, tooLong.Code);
        Assert.Equal(before, await SnapshotAsync(rig));

        // Nothing else is closed: a TD17 nobody replaced (an ordinary draft, then the same one issued), the rejected originals, a document that is no TD17, an unknown one.
        await rig.AddAsync(rig.Fee(rig.Company, rig.Lake, await rig.StayAsync(rig.Lake, BookingSource.BookingCom, new DateTime(2026, 9, 14)), 50.00m, year: 2026, month: 9));
        var september = Assert.Single((await rig.Service.CreateDraftsForPeriodAsync(rig.OrgId, 2026, 9, Host)).Drafts).DocumentId;
        await AssertNotClosableAsync(rig, september);
        await rig.Service.IssueAsync(rig.OrgId, september, confirm: true, Host);
        await AssertNotClosableAsync(rig, september);
        await AssertNotClosableAsync(rig, bookingOriginal);
        await AssertNotClosableAsync(rig, airbnbOriginal);
        var guest = new FiscalDocument { OrgId = rig.OrgId, Purpose = FiscalDocumentPurpose.GuestStay, Status = FiscalDocumentStatus.Draft, Series = "2026" };
        await rig.AddAsync(guest);
        foreach (var notFound in new[] { guest.Id, Guid.NewGuid() })
            Assert.Equal(FiscalDocumentErrors.NotFound, (await Assert.ThrowsAsync<NotFoundException>(() => rig.Service.CloseNotDueAsync(rig.OrgId, notFound, SelfBillingCloseReasons.DuplicateOrError, null, Host))).Code);

        // Another organization finds nothing, at the service and at the controller of that organization, whatever its reach; and it changes nothing.
        var beforeForeign = await SnapshotAsync(rig);
        var otherOrg = Guid.NewGuid();
        Assert.Equal(FiscalDocumentErrors.NotFound, (await Assert.ThrowsAsync<NotFoundException>(() => rig.Service.CloseNotDueAsync(otherOrg, airbnbReplacement, SelfBillingCloseReasons.DuplicateOrError, null, Host))).Code);
        var request = new CloseSelfBillingNotDueRequest { Reason = SelfBillingCloseReasons.HolderNoLongerSubject };
        var foreign = ControllerOf(rig, otherOrg, new HostScope(otherOrg));
        Assert.Equal(FiscalDocumentErrors.NotFound, (await Assert.ThrowsAsync<NotFoundException>(() => foreign.CloseNotDue(airbnbReplacement, request))).Code);
        Assert.Equal(beforeForeign, await SnapshotAsync(rig));

        // A caller who reaches only some properties is refused before the services are asked anything (a TD17 spans the properties of its holder).
        foreach (var restricted in new[] { new HostScope(rig.OrgId, GrantedToUserId: "auth0|solo-alcuni"), new HostScope(rig.OrgId, OwnerId: "auth0|solo-alcuni") })
        {
            var service = new Mock<ISelfBillingService>(MockBehavior.Strict);
            var strict = new SelfBillingController(service.Object, new Mock<IFiscalDocumentCorrectionService>(MockBehavior.Strict).Object, Org(rig.OrgId), Scope(rig.OrgId, restricted)) { ControllerContext = Context() };
            Assert.IsType<ForbidResult>((await strict.CloseNotDue(airbnbReplacement, request)).Result);
            service.VerifyNoOtherCalls();
        }

        Assert.Equal(beforeForeign, await SnapshotAsync(rig));

        // The permission, the flags and the route: the policy of the controller and no weaker one on the action, never anonymous.
        var action = typeof(SelfBillingController).GetMethod(nameof(SelfBillingController.CloseNotDue))!;
        Assert.Equal("{id:guid}/close-not-due", action.GetCustomAttribute<HttpPostAttribute>()!.Template);
        Assert.Empty(action.GetCustomAttributes<AllowAnonymousAttribute>());
        Assert.Empty(action.GetCustomAttributes<AuthorizeAttribute>());
        Assert.Equal(new[] { CasazenPolicies.AgencyInvoiceManage }, typeof(SelfBillingController).GetCustomAttributes<AuthorizeAttribute>(inherit: false).Select(a => a.Policy));
        Assert.Empty(typeof(SelfBillingController).GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
        Assert.Equal(new[] { FeatureFlags.FiscalDocumentIssuance }, typeof(SelfBillingController).GetCustomAttributes<FeatureGateAttribute>(inherit: false).Select(g => g.Flag));
        Assert.Equal(new[] { FeatureFlags.SdiSubmission }, action.GetCustomAttributes<FeatureGateAttribute>().Select(g => g.Flag));

        // A caller who reaches the whole organization is served (the note may be as long as the limit): 200 with the closed draft, the user it records is the caller's.
        var served = ControllerOf(rig, rig.OrgId, new HostScope(rig.OrgId));
        var ok = Assert.IsType<OkObjectResult>((await served.CloseNotDue(airbnbReplacement, request with { Note = new string('x', FiscalDocument.ClosedNotDueNoteMaxLength) })).Result);
        var dto = Assert.IsType<SelfBillingDto>(ok.Value);
        Assert.Equal((airbnbReplacement, "Discarded", "holder_no_longer_subject", (Guid?)airbnbOriginal), (dto.Invoice.Id, dto.Invoice.Status, dto.Invoice.ClosedReason, dto.Invoice.ReplacesDocumentId));
        Assert.Equal(FiscalDocument.ClosedNotDueNoteMaxLength, dto.Invoice.ClosedNote!.Length);
        var stored = await rig.DocumentAsync(airbnbReplacement);
        Assert.Equal((FiscalDocumentStatus.Discarded, Caller, (int?)2), (stored.Status, stored.ClosedNotDueByUserId, stored.SequenceNumber));

        // Closed, it is no longer a draft the month keeps: the Booking.com replacement is still open and the numbers are the two of the series.
        Assert.Equal(FiscalDocumentStatus.Draft, (await rig.DocumentAsync(bookingReplacement)).Status);
        Assert.Equal([1, 2, 3], await SequenceNumbersAsync(rig));
    }

    /// <summary>
    /// PROOF TEST: privacy rule. The free note of a closing can name a person or a document of the portal: its column is one of the encrypted ones (the purpose of the fiscal documents, no length: a payload that
    /// holds none of the text and reads back), the service reads it back in clear for the read models, and the log of the closing carries ids and the code of the reason, never the note.
    /// </summary>
    [Fact]
    public async Task ClosedNotDue_TheNoteIsEncryptedAtRest_AndNeverLogged_Test()
    {
        await using var rig = await SelfBillingRig.CreateAsync();
        var (_, replacement) = await KeptReplacementAsync(rig);
        var log = new CapturingLogger<SelfBillingService>();
        var service = new SelfBillingService(
            rig.Db, rig.Documents, rig.Issuers, rig.Rules, new PortalAccountHolderResolver(rig.Db), new ResxDocumentTexts(), Options.Create(SelfBillingRig.Shipped()), rig.Clock, log);
        const string note = "Nota di credito 4711 di Booking.com per il signor Mario Rossi";

        await service.CloseNotDueAsync(rig.OrgId, replacement.Id, SelfBillingCloseReasons.DuplicateOrError, note, Host);

        // The column is encrypted with the purpose of the fiscal documents and has no length: what is stored is a payload that holds none of the text and reads back.
        Assert.Contains(
            EncryptedColumns.All,
            c => c.EntityType == typeof(FiscalDocument) && c.Property == nameof(FiscalDocument.ClosedNotDueNote) && c.Purpose == EncryptedColumns.FiscalDocumentPurpose);
        var column = rig.Db.Model.FindEntityType(typeof(FiscalDocument))!.FindProperty(nameof(FiscalDocument.ClosedNotDueNote))!;
        var converter = Assert.IsType<EncryptedStringConverter>(column.GetValueConverter());
        Assert.Null(column.GetMaxLength());
        var stored = (string)converter.ConvertToProvider(note)!;
        Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, stored, StringComparison.Ordinal);
        Assert.NotEqual(note, stored);
        Assert.Equal(note, converter.ConvertFromProvider(stored));

        // The read models give it back in clear; the log carries the ids and the code of the reason, never the note.
        Assert.Equal(note, (await service.GetAsync(rig.OrgId, replacement.Id)).Row.ClosedNote);
        var closing = Assert.Single(log.Entries, e => e.Values.ContainsKey("Reason") && e.Level == LogLevel.Information);
        Assert.Equal((SelfBillingCloseReasons.DuplicateOrError, replacement.Id, rig.OrgId), ((string?)closing.Values["Reason"], (Guid)closing.Values["DocumentId"]!, (Guid)closing.Values["OrgId"]!));
        foreach (var word in new[] { "Nota di credito", "Booking.com", "Mario Rossi", note })
            Assert.DoesNotContain(word, log.AllOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// The «Da fare» of the drafts of the month follows the closing: a kept replacement is an open draft that nothing could close, so the item of its month stood for ever; closed as «non più dovuta» it was the last
    /// open draft and the system closes the item.
    /// </summary>
    [Fact]
    public async Task ClosedNotDue_ClosesTheItemOfTheMonthWhenItWasTheLastOpenDraft_Test()
    {
        await using var rig = await SelfBillingRig.CreateAsync();
        var (_, replacement) = await KeptReplacementAsync(rig);
        var activities = new InternalActivityService(rig.Db, NullLogger<InternalActivityService>.Instance, rig.Clock);
        var service = new SelfBillingService(
            rig.Db, rig.Documents, rig.Issuers, rig.Rules, new PortalAccountHolderResolver(rig.Db), new ResxDocumentTexts(), Options.Create(SelfBillingRig.Shipped()), rig.Clock, NullLogger<SelfBillingService>.Instance, activities);

        // The run of the month finds the kept replacement open and raises the item for it: nothing in the month can close it.
        Assert.Equal(1, (await service.RunMonthlyDraftsAsync(rig.OrgId, 2026, 10)).OpenDrafts);
        var item = Assert.Single(await rig.Db.InternalActivities.AsNoTracking().Where(a => a.ActivityType == nameof(ActivityType.SelfBillingDraftsToReview)).ToListAsync());
        Assert.Null(item.CompletedAt);

        await service.CloseNotDueAsync(rig.OrgId, replacement.Id, SelfBillingCloseReasons.HolderNoLongerSubject, null, Host);

        var closed = Assert.Single(await rig.Db.InternalActivities.AsNoTracking().Where(a => a.ActivityType == nameof(ActivityType.SelfBillingDraftsToReview)).ToListAsync());
        Assert.Equal((item.Id, InternalActivity.ClosedBySystem), (closed.Id, closed.ClosedBy));
        Assert.NotNull(closed.CompletedAt);
    }

    /// <summary>The list of the self-billed invoices, which now includes the closed drafts, is translated to SQL by PostgreSQL (InMemory accepts what Npgsql cannot turn into SQL).</summary>
    [Fact]
    public async Task List_IsTranslatedToSqlByNpgsql()
    {
        var statements = new List<string>();
        await using var db = NpgsqlTranslationProbe.NewContext(statements);
        var service = new SelfBillingService(
            db,
            Mock.Of<IFiscalDocumentService>(),
            Mock.Of<IDocumentIssuerService>(),
            Mock.Of<IFiscalTaxRuleResolver>(),
            Mock.Of<IPortalAccountHolderResolver>(),
            new ResxDocumentTexts(),
            Options.Create(SelfBillingRig.Shipped()),
            TimeProvider.System,
            NullLogger<SelfBillingService>.Instance);

        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => service.ListAsync(Guid.NewGuid(), new SelfBillingCriteria(2026, 10)));

        Assert.NotEmpty(statements);
        Assert.Contains(statements, sql => sql.Contains("ClosedNotDueAt", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// October 2026 of the company with ONE Booking.com commission: its TD17 (<c>TD17-2026/1</c>) is drafted, issued, rejected by the SDI and corrected (the replacement holds the number), and then the commission is
    /// reversed: nothing in the ledger supports the replacement any more, so the month keeps it (<c>ledger_gives_no_td17</c>). Both documents are read back from the database as they are.
    /// </summary>
    private static async Task<(FiscalDocument Original, FiscalDocument Replacement)> KeptReplacementAsync(SelfBillingRig rig)
    {
        var fee = rig.Fee(rig.Company, rig.Lake, await rig.StayAsync(rig.Lake, BookingSource.BookingCom, new DateTime(2026, 10, 5)), 100.00m);
        await rig.AddAsync(fee);
        var drafted = Assert.Single((await rig.Service.CreateDraftsForPeriodAsync(rig.OrgId, 2026, 10, Host)).Drafts);
        await rig.Service.IssueAsync(rig.OrgId, drafted.DocumentId, confirm: true, Host);
        await RejectedBySdiAsync(rig, drafted.DocumentId);
        var replacement = await CorrectionsOf(rig).StartSelfBillingCorrectionAsync(rig.OrgId, drafted.DocumentId, Host);
        await rig.AddAsync(rig.Reversal(fee));
        return (await rig.DocumentAsync(drafted.DocumentId), await rig.DocumentAsync(replacement.Id));
    }

    /// <summary>The closing is refused with 422 <c>self_billing_not_closable</c> and nothing is changed.</summary>
    private static async Task AssertNotClosableAsync(SelfBillingRig rig, Guid documentId)
    {
        var before = await SnapshotAsync(rig);
        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => rig.Service.CloseNotDueAsync(rig.OrgId, documentId, SelfBillingCloseReasons.PortalCreditNote, "note", Host));
        Assert.Equal((SelfBillingErrors.NotClosable, "SelfBillingNotClosable"), (refused.Code, refused.MessageKey));
        Assert.Equal(before, await SnapshotAsync(rig));
    }

    private static FiscalDocumentCorrectionService CorrectionsOf(SelfBillingRig rig) =>
        new(rig.Db, Mock.Of<IDomainEventRecorder>(), rig.Clock, NullLogger<FiscalDocumentCorrectionService>.Instance);

    /// <summary>The SDI rejects the TD17 (the answer of the channel, recorded the way the webhook and the poll record it).</summary>
    private static async Task RejectedBySdiAsync(SelfBillingRig rig, Guid documentId)
    {
        var provider = new Mock<ISdiEInvoiceProvider>();
        provider.SetupGet(p => p.IsConfigured).Returns(true);
        var sdi = new FiscalDocumentSdiService(rig.Db, provider.Object, Mock.Of<IDomainEventRecorder>(), rig.Clock, NullLogger<FiscalDocumentSdiService>.Instance, new TestFeatureFlags(FeatureFlags.SdiSubmission));
        Assert.Equal(SdiRecordResult.Recorded, await sdi.RecordOutcomeAsync(documentId, new SdiOutcome(SdiOutcomeKind.Rejected, "00417", "Identificativo fiscale mancante")));
    }

    /// <summary>The sequence numbers the documents hold, in order: what the unique index of the series would see.</summary>
    private static async Task<List<int>> SequenceNumbersAsync(SelfBillingRig rig) =>
        await rig.Db.FiscalDocuments.AsNoTracking().Where(d => d.SequenceNumber != null).OrderBy(d => d.SequenceNumber).Select(d => d.SequenceNumber!.Value).ToListAsync();

    /// <summary>Every document as it is stored: its status, its numbers, its state with the SDI, what it replaces and how it was closed.</summary>
    private static async Task<List<string>> SnapshotAsync(SelfBillingRig rig) =>
        (await rig.Db.FiscalDocuments.AsNoTracking().OrderBy(d => d.Id).ToListAsync())
        .Select(d => $"{d.Id}|{d.Status}|{d.SequenceNumber}|{d.Number}|{d.SdiStatus}|{d.ReplacesDocumentId}|{d.ClosedNotDueReason}|{d.ClosedNotDueAt:O}|{d.ClosedNotDueByUserId}|{d.ClosedNotDueNote}")
        .ToList();

    /// <summary>The controller of the self-billed invoices of <paramref name="orgId"/> for a caller of <paramref name="scope"/>.</summary>
    private static SelfBillingController ControllerOf(SelfBillingRig rig, Guid orgId, HostScope scope) =>
        new(rig.Service, CorrectionsOf(rig), Org(orgId), Scope(orgId, scope)) { ControllerContext = Context() };

    private static ControllerContext Context()
    {
        var localizer = new Mock<IStringLocalizer<SharedResources>>();
        localizer.Setup(l => l[It.IsAny<string>()]).Returns((string name) => new LocalizedString(name, name, resourceNotFound: true));
        localizer.Setup(l => l[It.IsAny<string>(), It.IsAny<object[]>()]).Returns((string name, object[] _) => new LocalizedString(name, name, resourceNotFound: true));
        var services = new ServiceCollection().AddSingleton(localizer.Object).BuildServiceProvider();
        var identity = new ClaimsIdentity([new Claim("sub", Caller), new Claim(ClaimTypes.NameIdentifier, Caller)], "test");
        return new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity), RequestServices = services } };
    }

    private static IOrgContextResolver Org(Guid orgId)
    {
        var resolver = new Mock<IOrgContextResolver>();
        resolver.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(orgId);
        return resolver.Object;
    }

    private static IHostScopeResolver Scope(Guid orgId, HostScope scope)
    {
        var resolver = new Mock<IHostScopeResolver>();
        resolver.Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>(), orgId, It.IsAny<CancellationToken>())).ReturnsAsync(scope);
        return resolver.Object;
    }
}
