using System.Reflection;
using System.Security.Claims;
using System.Xml.Linq;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Services;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Fiscal;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using AllowAnonymousAttribute = Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute;
using AuthorizeAttribute = Microsoft.AspNetCore.Authorization.AuthorizeAttribute;
using CasazenPolicies = Casazen.Web.Authorization.CasazenPolicies;
using FeatureGateAttribute = Casazen.Web.Infrastructure.FeatureGateAttribute;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// FIX/BE-TD17-CORRECTION (E17/F01/US02/T03 and US03, NF-86, NF-116): a self-billed invoice (TD17) the SDI rejected counts as not issued, so it is corrected like a rejected invoice to a guest and
/// re-sent with the same number. Its correction is a draft that holds the series and the number of the rejected TD17 (which keeps its display number for the history, gives up its sequence number and is
/// discarded), made once, only for a TD17 of the caller's organization that the SDI rejected and nobody credited, behind <c>agency.invoice.manage</c>; the host fixes what the SDI refused and issues it from
/// the self-billed invoices under the number it holds, and the numbering of the series has neither a hole nor a duplicate.
/// </summary>
public class SelfBillingCorrectionTests
{
    private const string Host = "auth0|agency";

    /// <summary>
    /// PROOF TEST: money and data rule. The correction of a TD17 the SDI rejected makes a draft that holds its series and its number and is the same TD17 (the issuer, the month, the foreign supplier,
    /// the amounts and every line as stored), discards the rejected one without its sequence number (it keeps its display number and its «rejected» for the history), and records the event the
    /// «Da fare» of the rejection closes on. The replacement is nothing but a draft: no XML, no day, nothing sent, and never discarded (the number would be a hole). Issued from the self-billed
    /// invoices it uses the held number and the day of the TD17 it replaces and reserves no other, so the next TD17 of the series is the next number: the numbering has no hole and no duplicate.
    /// </summary>
    [Fact]
    public async Task RejectedSelfBilling_Correction_MakesAReplacementHoldingTheNumber_Test()
    {
        await using var rig = await SelfBillingRig.CreateAsync();
        var recorder = new Mock<IDomainEventRecorder>();
        var (rejected, neighbour) = await RejectedFirstAsync(rig);
        Assert.Equal((FiscalDocumentStatus.Issued, FiscalDocumentSdiStatuses.Rejected, "TD17-2026/1", (int?)1), (rejected.Status, rejected.SdiStatus, rejected.Number, rejected.SequenceNumber));
        Assert.Equal(("TD17-2026/2", (int?)2), (neighbour.Number, neighbour.SequenceNumber));

        var replacement = await CorrectionsOf(rig, recorder.Object).StartSelfBillingCorrectionAsync(rig.OrgId, rejected.Id, Host);

        // The replacement holds the series and the number of the rejected TD17 and is that TD17 again: issuer, month, supplier, amounts and lines as stored.
        var draft = await rig.DocumentAsync(replacement.Id);
        Assert.Equal((replacement.Id, FiscalDocumentStatus.Draft, (Guid?)rejected.Id, "TD17-2026", (int?)1, "TD17-2026/1"), (draft.Id, draft.Status, draft.ReplacesDocumentId, draft.Series, draft.SequenceNumber, draft.Number));
        Assert.Equal((FiscalDocumentPurpose.SelfBilling, rejected.IssuerProfileId, (int?)2026, (int?)10), (draft.Purpose, draft.IssuerProfileId, draft.PeriodYear, draft.PeriodMonth));
        Assert.Equal(("Booking.com B.V.", "NL805734958B01", "NL", "SRL1234", "societa@pec.example.com"), (draft.RecipientName, draft.RecipientVatNumber, draft.RecipientCountryCode, draft.RecipientSdiCode, draft.RecipientPec));
        Assert.Equal((250.00m, 55.00m, 305.00m), (draft.TaxableBase, draft.VatAmount, draft.Total));
        Assert.Equal((rejected.TaxableBase, rejected.VatAmount, rejected.StampDuty, rejected.Total, rejected.VatRatePercent, rejected.Currency), (draft.TaxableBase, draft.VatAmount, draft.StampDuty, draft.Total, draft.VatRatePercent, draft.Currency));
        Assert.Equal(2, draft.Lines.Count);
        Assert.Equal(LinesOf(rejected), LinesOf(draft));

        // Nothing but a draft: no snapshot of the issuer, no XML, no day, nothing sent to the SDI.
        Assert.Null(draft.IssuerSnapshot);
        Assert.Null(draft.FatturaPaXml);
        Assert.Null(draft.IssueDate);
        Assert.Equal(FiscalDocumentSdiStatuses.NotApplicable, draft.SdiStatus);

        // The rejected TD17 gave the number up and keeps its display number, its day and its rejection for the history; the event is recorded once, with ids only.
        var gaveUp = await rig.DocumentAsync(rejected.Id);
        Assert.Equal((FiscalDocumentStatus.Discarded, (int?)null, "TD17-2026/1", FiscalDocumentSdiStatuses.Rejected, rejected.IssueDate), (gaveUp.Status, gaveUp.SequenceNumber, gaveUp.Number, gaveUp.SdiStatus, gaveUp.IssueDate));
        Assert.Equal([1, 2], await SequenceNumbersAsync(rig));
        recorder.Verify(
            r => r.Record(DomainEventType.FiscalDocumentCorrected, rejected.Id, new FiscalDocumentCorrectedPayload(rejected.Id, replacement.Id, null), $"fiscal-document-corrected:{rejected.Id:N}", rig.OrgId),
            Times.Once);

        // The replacement is never discarded: the number would be lost (a hole in the series) and the rejected TD17 could not be corrected again.
        var holding = await Assert.ThrowsAsync<DomainRuleException>(() => rig.Service.DiscardDraftAsync(rig.OrgId, replacement.Id));
        Assert.Equal(FiscalDocumentSdiErrors.ReplacementHoldsNumber, holding.Code);
        var stillHeld = await rig.DocumentAsync(replacement.Id);
        Assert.Equal((FiscalDocumentStatus.Draft, (int?)1), (stillHeld.Status, stillHeld.SequenceNumber));

        // Issued from the self-billed invoices (nine days later), it uses the number it holds and the day of the TD17 it replaces, reserves no other and is left to send.
        rig.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero));
        var issued = (await rig.Service.IssueAsync(rig.OrgId, replacement.Id, confirm: true, Host)).Document;
        Assert.Equal((FiscalDocumentStatus.Issued, "TD17-2026", (int?)1, "TD17-2026/1", new DateOnly(2026, 11, 12), FiscalDocumentSdiStatuses.ToSend), (issued.Status, issued.Series, issued.SequenceNumber, issued.Number, issued.IssueDate, issued.SdiStatus));
        Assert.Equal((Guid?)rejected.Id, issued.ReplacesDocumentId);
        var xml = XDocument.Parse(issued.FatturaPaXml!);
        Assert.Equal(("TD17", "TD17-2026/1", "305.00"), (xml.Descendants("TipoDocumento").Single().Value, xml.Descendants("Numero").Single().Value, xml.Descendants("ImportoTotaleDocumento").Single().Value));
        Assert.Empty(XsdValidator.Validate(issued.FatturaPaXml!, new FatturaPaSchemaProvider().GetSchemas()));
        Assert.Equal([1, 2], await SequenceNumbersAsync(rig));

        // The next TD17 of the series is the next number: no duplicate, no hole.
        var september = Assert.Single((await rig.Service.CreateDraftsForPeriodAsync(rig.OrgId, 2026, 9, null)).Drafts);
        var next = (await rig.Service.IssueAsync(rig.OrgId, september.DocumentId, confirm: true, null)).Document;
        Assert.Equal(("TD17-2026/3", (int?)3), (next.Number, next.SequenceNumber));
        Assert.Equal([1, 2, 3], await SequenceNumbersAsync(rig));
        Assert.Equal(3, (await rig.Db.FiscalNumberSequences.AsNoTracking().SingleAsync()).LastNumber);
    }

    /// <summary>
    /// PROOF TEST: data rule. A second start of the correction of the same TD17 (a double click, a retry) answers the replacement the first one made, with its lines: one replacement, one event, the
    /// numbers unchanged. Once the replacement is issued the rejected TD17 is not corrected again, and neither is the replacement, which the SDI did not reject.
    /// </summary>
    [Fact]
    public async Task RejectedSelfBilling_Correction_IsIdempotent_Test()
    {
        await using var rig = await SelfBillingRig.CreateAsync();
        var recorder = new Mock<IDomainEventRecorder>();
        var corrections = CorrectionsOf(rig, recorder.Object);
        var (rejected, _) = await RejectedFirstAsync(rig);

        var first = await corrections.StartSelfBillingCorrectionAsync(rig.OrgId, rejected.Id, Host);
        var second = await corrections.StartSelfBillingCorrectionAsync(rig.OrgId, rejected.Id, Host);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal((FiscalDocumentStatus.Draft, 2), (second.Status, second.Lines.Count));
        Assert.Equal(1, await rig.Db.FiscalDocuments.AsNoTracking().CountAsync(d => d.ReplacesDocumentId == rejected.Id));
        Assert.Equal([1, 2], await SequenceNumbersAsync(rig));
        recorder.Verify(r => r.Record(DomainEventType.FiscalDocumentCorrected, It.IsAny<Guid>(), It.IsAny<object>(), It.IsAny<string?>(), It.IsAny<Guid?>()), Times.Once);

        // Issued, the replacement is no draft any more: the rejected TD17 is not corrected a second time, and the replacement (which the SDI did not reject) is not corrected either.
        await rig.Service.IssueAsync(rig.OrgId, first.Id, confirm: true, Host);
        Assert.Equal(SelfBillingErrors.NotCorrectable, (await Assert.ThrowsAsync<DomainRuleException>(() => corrections.StartSelfBillingCorrectionAsync(rig.OrgId, rejected.Id, Host))).Code);
        Assert.Equal(SelfBillingErrors.NotCorrectable, (await Assert.ThrowsAsync<DomainRuleException>(() => corrections.StartSelfBillingCorrectionAsync(rig.OrgId, first.Id, Host))).Code);
        Assert.Equal(1, await rig.Db.FiscalDocuments.AsNoTracking().CountAsync(d => d.ReplacesDocumentId == rejected.Id));
        recorder.Verify(r => r.Record(DomainEventType.FiscalDocumentCorrected, It.IsAny<Guid>(), It.IsAny<object>(), It.IsAny<string?>(), It.IsAny<Guid?>()), Times.Once);
    }

    /// <summary>
    /// PROOF TEST: permission and tenant rule. The correction of a TD17 of another organization is not found (404) and changes nothing; a caller whose reach is some properties is refused (403) before
    /// any service is asked; the action is behind <c>agency.invoice.manage</c> (the policy of the controller, no weaker one on the action, never anonymous), <c>Features:FiscalDocumentIssuance</c> and
    /// <c>Features:SdiSubmission</c>; a caller who reaches the whole organization is served with the replacement draft (201), which names the TD17 it replaces.
    /// </summary>
    [Fact]
    public async Task SelfBillingCorrection_NeedsThePermissionAndTheOrganization_Test()
    {
        await using var rig = await SelfBillingRig.CreateAsync();
        var recorder = new Mock<IDomainEventRecorder>();
        var (rejected, _) = await RejectedFirstAsync(rig);
        var before = await SnapshotAsync(rig);

        // Another organization finds nothing: at the service and at the controller of that organization, whatever its reach.
        var otherOrg = Guid.NewGuid();
        Assert.Equal(FiscalDocumentErrors.NotFound, (await Assert.ThrowsAsync<NotFoundException>(() => CorrectionsOf(rig, recorder.Object).StartSelfBillingCorrectionAsync(otherOrg, rejected.Id, Host))).Code);
        var foreign = new SelfBillingController(rig.Service, CorrectionsOf(rig, recorder.Object), Org(otherOrg), Scope(otherOrg, new HostScope(otherOrg))) { ControllerContext = Context() };
        Assert.Equal(FiscalDocumentErrors.NotFound, (await Assert.ThrowsAsync<NotFoundException>(() => foreign.Correct(rejected.Id))).Code);
        Assert.Equal(before, await SnapshotAsync(rig));

        // A caller who reaches only some properties is refused before the services are asked anything (a TD17 spans the properties of its holder).
        foreach (var restricted in new[] { new HostScope(rig.OrgId, GrantedToUserId: "auth0|solo-alcuni"), new HostScope(rig.OrgId, OwnerId: "auth0|solo-alcuni") })
        {
            var service = new Mock<ISelfBillingService>(MockBehavior.Strict);
            var corrections = new Mock<IFiscalDocumentCorrectionService>(MockBehavior.Strict);
            var controller = new SelfBillingController(service.Object, corrections.Object, Org(rig.OrgId), Scope(rig.OrgId, restricted)) { ControllerContext = Context() };
            Assert.IsType<ForbidResult>((await controller.Correct(rejected.Id)).Result);
            service.VerifyNoOtherCalls();
            corrections.VerifyNoOtherCalls();
        }

        Assert.Equal(before, await SnapshotAsync(rig));
        recorder.VerifyNoOtherCalls();

        // The permission, the flags and the route are the ones of the other self-billed actions: the policy of the controller and no weaker one on the action, never anonymous.
        var action = typeof(SelfBillingController).GetMethod(nameof(SelfBillingController.Correct))!;
        Assert.Equal("{id:guid}/correct", action.GetCustomAttribute<HttpPostAttribute>()!.Template);
        Assert.Empty(action.GetCustomAttributes<AllowAnonymousAttribute>());
        Assert.Empty(action.GetCustomAttributes<AuthorizeAttribute>());
        Assert.Equal(new[] { CasazenPolicies.AgencyInvoiceManage }, typeof(SelfBillingController).GetCustomAttributes<AuthorizeAttribute>(inherit: false).Select(a => a.Policy));
        Assert.Empty(typeof(SelfBillingController).GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
        Assert.Equal(new[] { FeatureFlags.FiscalDocumentIssuance }, typeof(SelfBillingController).GetCustomAttributes<FeatureGateAttribute>(inherit: false).Select(g => g.Flag));
        Assert.Equal(new[] { FeatureFlags.SdiSubmission }, action.GetCustomAttributes<FeatureGateAttribute>().Select(g => g.Flag));

        // A caller who reaches the whole organization is served: 201 with the replacement draft, which names the rejected TD17 and cannot be corrected itself.
        var served = new SelfBillingController(rig.Service, CorrectionsOf(rig, recorder.Object), Org(rig.OrgId), Scope(rig.OrgId, new HostScope(rig.OrgId))) { ControllerContext = Context() };
        var created = Assert.IsType<CreatedAtActionResult>((await served.Correct(rejected.Id)).Result);
        var dto = Assert.IsType<SelfBillingDto>(created.Value);
        Assert.Equal((nameof(SelfBillingController.Get), "Draft", "TD17-2026/1", (Guid?)rejected.Id, false), (created.ActionName, dto.Invoice.Status, dto.Invoice.Number, dto.Invoice.ReplacesDocumentId, dto.Invoice.CanCorrect));
        Assert.Equal(dto.Invoice.Id, (Guid)created.RouteValues!["id"]!);
        Assert.Equal(2, dto.Lines.Count);
        Assert.Equal("auth0|solo-alcuni", (await rig.DocumentAsync(dto.Invoice.Id)).CreatedByUserId);
    }

    /// <summary>
    /// PROOF TEST: data rule. Only a TD17 of the organization that is issued and rejected by the SDI, with its number and no issued credit note, is corrected: a draft, one to send, one submitted, one
    /// the SDI delivered, a discarded draft and a rejected one that has an issued credit note are refused (422 <c>self_billing_not_correctable</c>), a document that is no TD17 (an invoice to a guest, an
    /// owner invoice) is not found (404), and nothing is made, changed, numbered or recorded by a refusal. The list says which TD17 can be corrected.
    /// </summary>
    [Fact]
    public async Task SelfBillingCorrection_RefusesWhatIsNotRejected_Test()
    {
        await using var rig = await SelfBillingRig.CreateAsync();
        var recorder = new Mock<IDomainEventRecorder>();
        var corrections = CorrectionsOf(rig, recorder.Object);
        await rig.StandardMonthAsync();
        var generation = await rig.Service.CreateDraftsForPeriodAsync(rig.OrgId, 2026, 10, Host);
        var booking = generation.Drafts.Single(d => d.PortalName == "Booking.com B.V.").DocumentId;
        var airbnb = generation.Drafts.Single(d => d.PortalName == "Airbnb Ireland UC").DocumentId;
        var september = Assert.Single((await rig.Service.CreateDraftsForPeriodAsync(rig.OrgId, 2026, 9, Host)).Drafts).DocumentId;

        // A draft is not corrected; neither is one that was discarded (it never was issued).
        await AssertRefusedAsync(corrections, rig, booking);
        await rig.Service.DiscardDraftAsync(rig.OrgId, september);
        await AssertRefusedAsync(corrections, rig, september);

        // Issued and to send, submitted, delivered: the SDI has not rejected it (a delivered one never is, and is not credited from here either).
        await rig.Service.IssueAsync(rig.OrgId, booking, confirm: true, Host);
        await AssertRefusedAsync(corrections, rig, booking);
        foreach (var state in new[] { FiscalDocumentSdiStatuses.Submitted, FiscalDocumentSdiStatuses.Delivered })
        {
            await SetSdiStatusAsync(rig, booking, state);
            await AssertRefusedAsync(corrections, rig, booking);
        }

        // Rejected, but credited: the accountant decides, the correction would bill the same operation twice.
        await rig.Service.IssueAsync(rig.OrgId, airbnb, confirm: true, Host);
        await RejectedBySdiAsync(rig, airbnb);
        rig.Db.ChangeTracker.Clear();
        Assert.True(Assert.Single(await rig.Service.ListAsync(rig.OrgId, new SelfBillingCriteria(2026, 10)), r => r.Id == airbnb).CanCorrect);
        rig.Db.FiscalDocuments.Add(new FiscalDocument
        {
            OrgId = rig.OrgId,
            Purpose = FiscalDocumentPurpose.SelfBilling,
            Kind = FiscalDocumentKind.CreditNote,
            Status = FiscalDocumentStatus.Issued,
            CreditedDocumentId = airbnb,
            Series = "TD17-2026",
        });
        await rig.Db.SaveChangesAsync();
        rig.Db.ChangeTracker.Clear();
        await AssertRefusedAsync(corrections, rig, airbnb);
        Assert.False(Assert.Single(await rig.Service.ListAsync(rig.OrgId, new SelfBillingCriteria(2026, 10)), r => r.Id == airbnb).CanCorrect);

        // Documents that are no TD17 of the organization are not found, even rejected and with a number: the guest invoice and the owner invoice have their own corrections.
        var guest = new FiscalDocument { OrgId = rig.OrgId, Purpose = FiscalDocumentPurpose.GuestStay, Status = FiscalDocumentStatus.Issued, SdiStatus = FiscalDocumentSdiStatuses.Rejected, Series = "2026", SequenceNumber = 9, Number = "2026/9" };
        var ownerInvoice = new FiscalDocument { OrgId = rig.OrgId, Purpose = FiscalDocumentPurpose.OwnerInvoice, Status = FiscalDocumentStatus.Issued, SdiStatus = FiscalDocumentSdiStatuses.Rejected, Series = "PM2026", SequenceNumber = 1, Number = "PM2026/1" };
        await rig.AddAsync(guest, ownerInvoice);
        foreach (var other in new[] { guest.Id, ownerInvoice.Id, Guid.NewGuid() })
            Assert.Equal(FiscalDocumentErrors.NotFound, (await Assert.ThrowsAsync<NotFoundException>(() => corrections.StartSelfBillingCorrectionAsync(rig.OrgId, other, Host))).Code);

        // The guest endpoint of the correction still refuses every TD17, whatever its state: its answer is unchanged.
        Assert.Equal(SelfBillingErrors.NotCorrectable, (await Assert.ThrowsAsync<DomainRuleException>(() => corrections.StartCorrectionAsync(airbnb, Host))).Code);

        // No refusal made, changed, numbered or recorded anything: the two numbers of the series are the two TD17 that were issued.
        Assert.Empty(await rig.Db.FiscalDocuments.AsNoTracking().Where(d => d.ReplacesDocumentId != null).ToListAsync());
        Assert.Equal([1, 2], await rig.Db.FiscalDocuments.AsNoTracking().Where(d => d.Purpose == FiscalDocumentPurpose.SelfBilling && d.SequenceNumber != null).OrderBy(d => d.SequenceNumber).Select(d => d.SequenceNumber!.Value).ToListAsync());
        recorder.VerifyNoOtherCalls();

        static async Task AssertRefusedAsync(FiscalDocumentCorrectionService service, SelfBillingRig world, Guid documentId)
        {
            var before = await SnapshotAsync(world);
            var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.StartSelfBillingCorrectionAsync(world.OrgId, documentId, Host));
            Assert.Equal((SelfBillingErrors.NotCorrectable, "SelfBillingCorrectionNotApplicable"), (refused.Code, refused.MessageKey));
            Assert.Equal(before, await SnapshotAsync(world));
        }
    }

    /// <summary>
    /// PROOF TEST: data rule. The list and the detail show the replacement as a draft and the rejected original as discarded (with its number and its «rejected»), each naming the other; only the
    /// TD17 the SDI rejected is offered for correction, and once corrected neither is. The drafts of the month never make a second TD17 for the holder, portal and month while the replacement
    /// exists: they make it again, keeping its number, and when the host has fixed the issuer profile (what the SDI refused) the replacement is made again and issued with the held number.
    /// </summary>
    [Fact]
    public async Task RejectedSelfBilling_Correction_TheReadModelsShowBothAndTheMonthKeepsOneTd17_Test()
    {
        await using var rig = await SelfBillingRig.CreateAsync();
        var (rejected, neighbour) = await RejectedFirstAsync(rig);

        // Before the correction the list offers it on the rejected TD17 only.
        var listed = await rig.Service.ListAsync(rig.OrgId, new SelfBillingCriteria(2026, 10));
        Assert.Equal([rejected.Id], listed.Where(r => r.CanCorrect).Select(r => r.Id));
        Assert.Equal(2, listed.Count);

        var replacement = await CorrectionsOf(rig, Mock.Of<IDomainEventRecorder>()).StartSelfBillingCorrectionAsync(rig.OrgId, rejected.Id, Host);

        // The list: the replacement as a draft, the original as discarded, the neighbour as it was; none can be corrected.
        var after = await rig.Service.ListAsync(rig.OrgId, new SelfBillingCriteria(2026, 10));
        Assert.Equal(3, after.Count);
        var original = Assert.Single(after, r => r.Id == rejected.Id);
        var draft = Assert.Single(after, r => r.Id == replacement.Id);
        Assert.Equal((FiscalDocumentStatus.Discarded, "TD17-2026/1", FiscalDocumentSdiStatuses.Rejected, false, (Guid?)null, (Guid?)replacement.Id), (original.Status, original.Number, original.SdiStatus, original.CanCorrect, original.ReplacesDocumentId, original.ReplacedByDocumentId));
        Assert.Equal((FiscalDocumentStatus.Draft, "TD17-2026/1", false, (Guid?)rejected.Id, (Guid?)null), (draft.Status, draft.Number, draft.CanCorrect, draft.ReplacesDocumentId, draft.ReplacedByDocumentId));
        Assert.Equal((original.HolderName, original.PortalName, original.PeriodMonth, original.Total), (draft.HolderName, draft.PortalName, draft.PeriodMonth, draft.Total));
        var other = Assert.Single(after, r => r.Id == neighbour.Id);
        Assert.Equal((FiscalDocumentStatus.Issued, "TD17-2026/2", false), (other.Status, other.Number, other.CanCorrect));

        // The detail of each says the same (a discarded original is read as it was issued; the replacement is a draft with its lines).
        var originalDetail = await rig.Service.GetAsync(rig.OrgId, rejected.Id);
        var draftDetail = await rig.Service.GetAsync(rig.OrgId, replacement.Id);
        Assert.Equal((FiscalDocumentStatus.Discarded, (Guid?)replacement.Id, 0m), (originalDetail.Row.Status, originalDetail.Row.ReplacedByDocumentId, originalDetail.EstimatedAmount));
        Assert.Equal((FiscalDocumentStatus.Draft, (Guid?)rejected.Id, 2), (draftDetail.Row.Status, draftDetail.Row.ReplacesDocumentId, draftDetail.Document.Lines.Count));

        // The month is not made again: the replacement is made again from the ledger and keeps its number, the neighbour is left alone, no second TD17 appears for the holder and portal.
        var again = await rig.Service.CreateDraftsForPeriodAsync(rig.OrgId, 2026, 10, Host);
        Assert.Equal((0, 1, 1, 0), (again.Created, again.Updated, again.AlreadyIssued, again.Discarded));
        var outcome = Assert.Single(again.Drafts, d => d.PortalName == "Booking.com B.V.");
        Assert.Equal((replacement.Id, SelfBillingResults.Updated), (outcome.DocumentId, outcome.Result));
        Assert.Equal(3, await rig.Db.FiscalDocuments.AsNoTracking().CountAsync(d => d.Purpose == FiscalDocumentPurpose.SelfBilling && d.PeriodMonth == 10));
        Assert.Equal(2, await rig.Db.FiscalDocuments.AsNoTracking().CountAsync(d => d.Purpose == FiscalDocumentPurpose.SelfBilling && d.PeriodMonth == 10 && d.Status != FiscalDocumentStatus.Discarded));
        var kept = await rig.DocumentAsync(replacement.Id);
        Assert.Equal((FiscalDocumentStatus.Draft, (int?)1, (Guid?)rejected.Id), (kept.Status, kept.SequenceNumber, kept.ReplacesDocumentId));

        // The SDI refused the code of the holder: the host fixes the issuer profile. The replacement is no longer what the profile gives (409, no number taken); made again it is, and it is issued
        // under the number it holds, with the new code in the file.
        var profile = await rig.Db.OrgFiscalIssuerProfiles.SingleAsync(p => p.Id == rejected.IssuerProfileId);
        profile.IssuerSdiRecipientCode = "NEW5678";
        await rig.Db.SaveChangesAsync();
        rig.Db.ChangeTracker.Clear();
        var outdated = await Assert.ThrowsAsync<DomainConflictException>(() => rig.Service.IssueAsync(rig.OrgId, replacement.Id, confirm: true, Host));
        Assert.Equal(FiscalDocumentErrors.DraftOutdated, outdated.Code);
        Assert.Equal([1, 2], await SequenceNumbersAsync(rig));
        Assert.Equal(1, (await rig.Service.CreateDraftsForPeriodAsync(rig.OrgId, 2026, 10, Host)).Updated);
        var issued = (await rig.Service.IssueAsync(rig.OrgId, replacement.Id, confirm: true, Host)).Document;
        Assert.Equal((FiscalDocumentStatus.Issued, (int?)1, "TD17-2026/1", "NEW5678"), (issued.Status, issued.SequenceNumber, issued.Number, XDocument.Parse(issued.FatturaPaXml!).Descendants("CodiceDestinatario").Single().Value));
        Assert.Equal([1, 2], await SequenceNumbersAsync(rig));
    }

    /// <summary>
    /// PROOF TEST: data rule. The month that no longer supports a replacement (its commissions were reversed) never drops it: the draft holds the number of a rejected TD17, so discarding it would leave
    /// a hole in the numbering. It is kept as it is, still holding its number, and the host cannot discard it either.
    /// </summary>
    [Fact]
    public async Task RejectedSelfBilling_Correction_TheMonthNeverDropsTheDraftHoldingTheNumber_Test()
    {
        await using var rig = await SelfBillingRig.CreateAsync();
        var fee = rig.Fee(rig.Company, rig.Lake, await rig.StayAsync(rig.Lake, BookingSource.BookingCom, new DateTime(2026, 10, 5)), 100.00m);
        await rig.AddAsync(fee);
        var drafted = Assert.Single((await rig.Service.CreateDraftsForPeriodAsync(rig.OrgId, 2026, 10, Host)).Drafts);
        await rig.Service.IssueAsync(rig.OrgId, drafted.DocumentId, confirm: true, Host);
        await RejectedBySdiAsync(rig, drafted.DocumentId);
        var replacement = await CorrectionsOf(rig, Mock.Of<IDomainEventRecorder>()).StartSelfBillingCorrectionAsync(rig.OrgId, drafted.DocumentId, Host);

        // The commission is reversed: nothing in the ledger supports a TD17 of the month any more.
        await rig.AddAsync(rig.Reversal(fee));
        var generation = await rig.Service.CreateDraftsForPeriodAsync(rig.OrgId, 2026, 10, Host);

        Assert.Equal((0, 0, 0, 0), (generation.Created, generation.Updated, generation.AlreadyIssued, generation.Discarded));
        Assert.Empty(generation.Drafts);
        var kept = await rig.DocumentAsync(replacement.Id);
        Assert.Equal((FiscalDocumentStatus.Draft, (int?)1, "TD17-2026/1", (Guid?)drafted.DocumentId), (kept.Status, kept.SequenceNumber, kept.Number, kept.ReplacesDocumentId));
        Assert.Equal(FiscalDocumentSdiErrors.ReplacementHoldsNumber, (await Assert.ThrowsAsync<DomainRuleException>(() => rig.Service.DiscardDraftAsync(rig.OrgId, replacement.Id))).Code);
        Assert.Contains(await rig.Service.ListAsync(rig.OrgId, new SelfBillingCriteria(2026, 10)), r => r.Id == replacement.Id && r.Status == FiscalDocumentStatus.Draft);
        Assert.Equal([1], await SequenceNumbersAsync(rig));

        // A draft nobody replaces is dropped as it always was (the rule of the guard is the held number, nothing else).
        var other = rig.Fee(rig.Company, rig.Mountain, await rig.StayAsync(rig.Mountain, BookingSource.Airbnb, new DateTime(2026, 10, 12)), 60.00m);
        await rig.AddAsync(other);
        var airbnb = Assert.Single((await rig.Service.CreateDraftsForPeriodAsync(rig.OrgId, 2026, 10, Host)).Drafts, d => d.PortalName == "Airbnb Ireland UC").DocumentId;
        await rig.AddAsync(rig.Reversal(other));
        Assert.Equal(1, (await rig.Service.CreateDraftsForPeriodAsync(rig.OrgId, 2026, 10, Host)).Discarded);
        Assert.Equal(FiscalDocumentStatus.Discarded, (await rig.DocumentAsync(airbnb)).Status);
        Assert.Equal(FiscalDocumentStatus.Draft, (await rig.DocumentAsync(replacement.Id)).Status);
    }

    // ---------------------------------------------------------------- helpers

    private static FiscalDocumentCorrectionService CorrectionsOf(SelfBillingRig rig, IDomainEventRecorder recorder) =>
        new(rig.Db, recorder, rig.Clock, NullLogger<FiscalDocumentCorrectionService>.Instance);

    /// <summary>
    /// October 2026 of the company, drafted and issued on 12 November: the Booking.com TD17 (<c>TD17-2026/1</c>) and the Airbnb one (<c>TD17-2026/2</c>); the SDI then rejects the first. Both are
    /// read back from the database as they are.
    /// </summary>
    private static async Task<(FiscalDocument Rejected, FiscalDocument Neighbour)> RejectedFirstAsync(SelfBillingRig rig)
    {
        await rig.StandardMonthAsync();
        var generation = await rig.Service.CreateDraftsForPeriodAsync(rig.OrgId, 2026, 10, Host);
        var booking = generation.Drafts.Single(d => d.PortalName == "Booking.com B.V.");
        var airbnb = generation.Drafts.Single(d => d.PortalName == "Airbnb Ireland UC");
        var first = (await rig.Service.IssueAsync(rig.OrgId, booking.DocumentId, confirm: true, Host)).Document;
        var second = (await rig.Service.IssueAsync(rig.OrgId, airbnb.DocumentId, confirm: true, Host)).Document;
        await RejectedBySdiAsync(rig, first.Id);
        rig.Db.ChangeTracker.Clear();
        return (await rig.DocumentAsync(first.Id), await rig.DocumentAsync(second.Id));
    }

    /// <summary>The SDI rejects the TD17 (the answer of the channel, recorded the way the webhook and the poll record it).</summary>
    private static async Task RejectedBySdiAsync(SelfBillingRig rig, Guid documentId)
    {
        var provider = new Mock<ISdiEInvoiceProvider>();
        provider.SetupGet(p => p.IsConfigured).Returns(true);
        var sdi = new FiscalDocumentSdiService(rig.Db, provider.Object, Mock.Of<IDomainEventRecorder>(), rig.Clock, NullLogger<FiscalDocumentSdiService>.Instance, new TestFeatureFlags(FeatureFlags.SdiSubmission));
        Assert.Equal(SdiRecordResult.Recorded, await sdi.RecordOutcomeAsync(documentId, new SdiOutcome(SdiOutcomeKind.Rejected, "00417", "Identificativo fiscale mancante")));
    }

    private static async Task SetSdiStatusAsync(SelfBillingRig rig, Guid documentId, string status)
    {
        (await rig.Db.FiscalDocuments.SingleAsync(d => d.Id == documentId)).SdiStatus = status;
        await rig.Db.SaveChangesAsync();
        rig.Db.ChangeTracker.Clear();
    }

    private static IEnumerable<(int Number, FiscalLineType Type, string Text, decimal Net, decimal Rate, decimal? Vat)> LinesOf(FiscalDocument document) =>
        document.Lines.OrderBy(l => l.LineNumber).Select(l => (l.LineNumber, l.LineType, l.Description, l.TotalPrice, l.VatRatePercent, l.VatAmount));

    /// <summary>The sequence numbers the documents hold, in order: what the unique index of the series would see.</summary>
    private static async Task<List<int>> SequenceNumbersAsync(SelfBillingRig rig) =>
        await rig.Db.FiscalDocuments.AsNoTracking().Where(d => d.SequenceNumber != null).OrderBy(d => d.SequenceNumber).Select(d => d.SequenceNumber!.Value).ToListAsync();

    /// <summary>Every document as it is stored: its status, its numbers, its state with the SDI and what it replaces.</summary>
    private static async Task<List<string>> SnapshotAsync(SelfBillingRig rig) =>
        (await rig.Db.FiscalDocuments.AsNoTracking().OrderBy(d => d.Id).ToListAsync())
        .Select(d => $"{d.Id}|{d.Status}|{d.SequenceNumber}|{d.Number}|{d.SdiStatus}|{d.ReplacesDocumentId}")
        .ToList();

    private static ControllerContext Context()
    {
        var localizer = new Mock<IStringLocalizer<SharedResources>>();
        localizer.Setup(l => l[It.IsAny<string>()]).Returns((string name) => new LocalizedString(name, name, resourceNotFound: true));
        localizer.Setup(l => l[It.IsAny<string>(), It.IsAny<object[]>()]).Returns((string name, object[] _) => new LocalizedString(name, name, resourceNotFound: true));
        var services = new ServiceCollection().AddSingleton(localizer.Object).BuildServiceProvider();
        var identity = new ClaimsIdentity([new Claim("sub", "auth0|solo-alcuni"), new Claim(ClaimTypes.NameIdentifier, "auth0|solo-alcuni")], "test");
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
