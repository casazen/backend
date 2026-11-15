using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E17/F01/US02/T01 (NF-116, NF-86, ADR-019, ADR-022): the electronic invoices of CasaZen on their way to the SDI. With no provider nothing is ever «sent»; the provider's answer, the poll and a
/// declaration are the only ways a state changes; the outcomes are idempotent and a delivered invoice is never rejected afterwards (the rule the reuse of a number rests on).
/// </summary>
public class FiscalDocumentSdiTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);

    private const string Xml =
        "<p:FatturaElettronica xmlns:p=\"http://ivaservizi.agenziaentrate.gov.it/docs/xsd/fatture/v1.2\" versione=\"FPR12\"><FatturaElettronicaHeader><DatiTrasmissione>"
        + "<IdTrasmittente><IdPaese>IT</IdPaese><IdCodice>09876543217</IdCodice></IdTrasmittente><ProgressivoInvio>ABCDE12345</ProgressivoInvio></DatiTrasmissione></FatturaElettronicaHeader></p:FatturaElettronica>";

    // ---------------------------------------------------------------- the proof

    /// <summary>
    /// PROOF TEST: data and trust rule. With no provider configured (or a provider but the module off) no document ever reaches <c>submitted</c>, <c>delivered</c> or <c>manual_declared</c> by itself:
    /// the submission answers «not configured» and changes nothing, an outcome that is no answer of the provider (a «submitted», a delivery of a document never submitted) moves nothing, and the
    /// provider is never called. The manual declaration works all the same, and only with the day and the reference of a person.
    /// </summary>
    [Fact]
    public async Task Sdi_NoProvider_NeverMarksSent_Test()
    {
        await using var db = NewDb();
        var document = await SeedAsync(db);
        var recorder = new DomainEventRecorder(db, new SdiTenant(), new FakeTimeProvider(Now));

        // No provider configured: nothing is submitted, nothing changes.
        var service = Service(db, new UnconfiguredSdiEInvoiceProvider(), recorder: recorder);
        var answer = await service.SubmitAsync(document.Id);
        Assert.Equal((SdiSubmitOutcome.NotConfigured, FiscalDocumentSdiStatuses.ToSend), (answer.Outcome, answer.Status));

        // A provider that is configured, with the module off, is the same: it is never called.
        var provider = Configured();
        var off = Service(db, provider.Object, new TestFeatureFlags(), recorder);
        Assert.Equal(SdiSubmitOutcome.NotConfigured, (await off.SubmitAsync(document.Id)).Outcome);
        provider.Verify(p => p.SubmitDocumentAsync(It.IsAny<SdiDocumentSubmission>(), It.IsAny<CancellationToken>()), Times.Never);

        // «Submitted» is no outcome, and a delivery of a document that was never submitted contradicts its state: neither moves it.
        Assert.Equal(SdiRecordResult.AlreadyRecorded, await service.RecordOutcomeAsync(document.Id, new SdiOutcome(SdiOutcomeKind.Submitted)));
        Assert.Equal(SdiRecordResult.Ignored, await service.RecordOutcomeAsync(document.Id, new SdiOutcome(SdiOutcomeKind.Delivered)));
        var stored = await db.FiscalDocuments.AsNoTracking().SingleAsync();
        Assert.Equal((FiscalDocumentSdiStatuses.ToSend, (string?)null, (DateTime?)null), (stored.SdiStatus, stored.SdiTransmissionId, stored.SdiStatusUpdatedAt));
        Assert.Empty(await db.OutboxEvents.ToListAsync());

        // The declaration needs a person: the day and the reference. Without them nothing changes; with them the document is declared, and that is all.
        var invalid = await Assert.ThrowsAsync<DomainRuleException>(() => service.DeclareManuallyAsync(document.Id, new SdiDeclaration(SdiDeclarationKind.Sent)));
        Assert.Equal(FiscalDocumentSdiErrors.DeclarationInvalid, invalid.Code);
        Assert.Equal(["date", "reference"], Assert.IsAssignableFrom<List<string>>(invalid.Extensions["fields"]));
        Assert.Equal(FiscalDocumentSdiStatuses.ToSend, (await db.FiscalDocuments.AsNoTracking().SingleAsync()).SdiStatus);

        var declared = await service.DeclareManuallyAsync(document.Id, new SdiDeclaration(SdiDeclarationKind.Sent, new DateOnly(2026, 11, 19), "IT09876543217_AB123.xml"));
        Assert.Equal((FiscalDocumentSdiStatuses.ManualDeclared, true, false), (declared.Status, declared.CanDeclare, declared.CanSubmit));
        stored = await db.FiscalDocuments.AsNoTracking().SingleAsync();
        Assert.Equal(("IT09876543217_AB123.xml", RomeCalendar.StartOfDayUtc(new DateOnly(2026, 11, 19))), (stored.SdiTransmissionId, stored.SdiStatusUpdatedAt));
    }

    // ---------------------------------------------------------------- the submission

    [Fact]
    public async Task Submit_TheProviderAccepts_IsSubmittedOnce_WithTheIssuerAndTheFileOfTheStoredXml()
    {
        await using var db = NewDb();
        var document = await SeedAsync(db);
        SdiDocumentSubmission? seen = null;
        var provider = Configured();
        provider.Setup(p => p.SubmitDocumentAsync(It.IsAny<SdiDocumentSubmission>(), It.IsAny<CancellationToken>()))
            .Callback<SdiDocumentSubmission, CancellationToken>((submission, _) => seen = submission)
            .ReturnsAsync(SdiSubmissionResult.Success("42"));
        var service = Service(db, provider.Object);

        var answer = await service.SubmitAsync(document.Id);

        Assert.Equal((SdiSubmitOutcome.Submitted, FiscalDocumentSdiStatuses.Submitted), (answer.Outcome, answer.Status));
        var stored = await db.FiscalDocuments.AsNoTracking().SingleAsync();
        Assert.Equal((FiscalDocumentSdiStatuses.Submitted, "42", (string?)null), (stored.SdiStatus, stored.SdiTransmissionId, stored.SdiErrorCode));
        Assert.NotNull(seen);
        Assert.Equal((OrgId, document.Id, document.IssuerProfileId, "01234567897", "TEST Casa Vacanze Srl"), (seen.OrgId, seen.DocumentId, seen.IssuerProfileId, seen.IssuerFiscalId, seen.IssuerName));

        // The file is the one of the XML as it was issued, named as the specifications of the SDI say, after the transmitter the XML itself names.
        Assert.Equal(Xml, seen.Xml);
        Assert.True(SdiFileNames.IsValid(seen.FileName));
        Assert.StartsWith("IT09876543217_", seen.FileName, StringComparison.Ordinal);
        Assert.Equal(SdiFileNames.Of("IT", "09876543217", document.Id), seen.FileName);

        // A second submission (a double click) never sends it twice.
        Assert.Equal(SdiSubmitOutcome.AlreadySubmitted, (await service.SubmitAsync(document.Id)).Outcome);
        provider.Verify(p => p.SubmitDocumentAsync(It.IsAny<SdiDocumentSubmission>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(SdiSubmissionErrors.IssuerPending, SdiSubmissionErrors.IssuerPending)]
    [InlineData(SdiSubmissionErrors.ProviderError, SdiSubmissionErrors.ProviderError)]
    [InlineData("a_code_nobody_knows", SdiSubmissionErrors.ProviderRefused)]
    public async Task Submit_TheProviderRefuses_TheDocumentStaysToSend_WithTheCodeOfTheRefusalAndACleanedNote(string error, string stored)
    {
        await using var db = NewDb();
        var document = await SeedAsync(db);
        var provider = Configured();
        provider.Setup(p => p.SubmitDocumentAsync(It.IsAny<SdiDocumentSubmission>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SdiSubmissionResult.Failure(error, "400 company_already_registered, write to someone@example.com"));
        var service = Service(db, provider.Object);

        var answer = await service.SubmitAsync(document.Id);

        Assert.Equal((SdiSubmitOutcome.Refused, FiscalDocumentSdiStatuses.ToSend, stored), (answer.Outcome, answer.Status, answer.ErrorCode));
        var row = await db.FiscalDocuments.AsNoTracking().SingleAsync();
        Assert.Equal((FiscalDocumentSdiStatuses.ToSend, (string?)null, stored), (row.SdiStatus, row.SdiTransmissionId, row.SdiErrorCode));
        Assert.Equal("400 company_already_registered, write to ***", row.SdiErrorDetail);
    }

    [Fact]
    public async Task Submit_AnAcceptanceWithoutTheIdOfTheSubmission_IsNoAcceptance()
    {
        await using var db = NewDb();
        var document = await SeedAsync(db);
        var provider = Configured();
        provider.Setup(p => p.SubmitDocumentAsync(It.IsAny<SdiDocumentSubmission>(), It.IsAny<CancellationToken>())).ReturnsAsync(new SdiSubmissionResult(true, " ", null));

        var answer = await Service(db, provider.Object).SubmitAsync(document.Id);

        Assert.Equal((SdiSubmitOutcome.Refused, FiscalDocumentSdiStatuses.ToSend), (answer.Outcome, answer.Status));
        Assert.Equal(FiscalDocumentSdiStatuses.ToSend, (await db.FiscalDocuments.AsNoTracking().SingleAsync()).SdiStatus);
    }

    [Fact]
    public async Task Submit_OnlyAnIssuedElectronicInvoiceOfCasazenGoesToTheSdi()
    {
        await using var db = NewDb();
        var provider = Configured();
        var service = Service(db, provider.Object);

        var missing = await Assert.ThrowsAsync<NotFoundException>(() => service.SubmitAsync(Guid.NewGuid()));
        Assert.Equal(FiscalDocumentErrors.NotFound, missing.Code);

        var imported = await SeedAsync(db, source: FiscalDocumentSource.Octorate);
        Assert.Equal(FiscalDocumentErrors.ReadOnly, (await Assert.ThrowsAsync<DomainRuleException>(() => service.SubmitAsync(imported.Id))).Code);

        var receipt = await SeedAsync(db, sdiStatus: FiscalDocumentSdiStatuses.NotApplicable);
        var draft = await SeedAsync(db, status: FiscalDocumentStatus.Draft);
        Assert.Equal(FiscalDocumentSdiErrors.NotElectronic, (await Assert.ThrowsAsync<DomainRuleException>(() => service.SubmitAsync(receipt.Id))).Code);
        Assert.Equal(FiscalDocumentSdiErrors.NotElectronic, (await Assert.ThrowsAsync<DomainRuleException>(() => service.SubmitAsync(draft.Id))).Code);

        var delivered = await SeedAsync(db, sdiStatus: FiscalDocumentSdiStatuses.Delivered);
        Assert.Equal(FiscalDocumentSdiErrors.NotToSend, (await Assert.ThrowsAsync<DomainConflictException>(() => service.SubmitAsync(delivered.Id))).Code);
        provider.Verify(p => p.SubmitDocumentAsync(It.IsAny<SdiDocumentSubmission>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---------------------------------------------------------------- the outcomes

    [Fact]
    public async Task RecordOutcome_Delivered_IsRecordedOnce_AndALaterSubmittedNeverOverridesIt()
    {
        await using var db = NewDb();
        var document = await SeedAsync(db, sdiStatus: FiscalDocumentSdiStatuses.Submitted, transmissionId: "42");
        var service = Service(db, Configured().Object);

        Assert.Equal(SdiRecordResult.Recorded, await service.RecordOutcomeAsync(document.Id, new SdiOutcome(SdiOutcomeKind.Delivered, OccurredAt: Now.UtcDateTime)));
        Assert.Equal(SdiRecordResult.AlreadyRecorded, await service.RecordOutcomeAsync(document.Id, new SdiOutcome(SdiOutcomeKind.Delivered)));
        Assert.Equal(SdiRecordResult.AlreadyRecorded, await service.RecordOutcomeAsync(document.Id, new SdiOutcome(SdiOutcomeKind.Submitted)));

        var stored = await db.FiscalDocuments.AsNoTracking().SingleAsync();
        Assert.Equal((FiscalDocumentSdiStatuses.Delivered, Now.UtcDateTime), (stored.SdiStatus, stored.SdiStatusUpdatedAt));
        var recorded = Assert.Single(await db.OutboxEvents.AsNoTracking().ToListAsync());
        Assert.Equal((DomainEventType.FiscalDocumentSdiDelivered, document.Id, OrgId, $"fiscal-document-sdi-delivered:{document.Id:N}"), (recorded.EventType, recorded.EntityId, recorded.OrgId, recorded.DedupKey));
    }

    [Fact]
    public async Task RecordOutcome_Rejected_KeepsTheCodeAndACleanedReason_AndRecordsItsEventOnce()
    {
        await using var db = NewDb();
        var document = await SeedAsync(db, sdiStatus: FiscalDocumentSdiStatuses.Submitted, transmissionId: "42");
        var service = Service(db, Configured().Object);

        var outcome = new SdiOutcome(SdiOutcomeKind.Rejected, "417", "Identificativo fiscale mancante, cliente mario.rossi@example.com RSSMRA80A01H501U 12345678901");
        Assert.Equal(SdiRecordResult.Recorded, await service.RecordOutcomeAsync(document.Id, outcome));
        Assert.Equal(SdiRecordResult.AlreadyRecorded, await service.RecordOutcomeAsync(document.Id, outcome with { ErrorCode = "00300" }));

        var stored = await db.FiscalDocuments.AsNoTracking().SingleAsync();
        Assert.Equal((FiscalDocumentSdiStatuses.Rejected, "00417", "Identificativo fiscale mancante, cliente *** *** ***"), (stored.SdiStatus, stored.SdiErrorCode, stored.SdiErrorDetail));
        var recorded = Assert.Single(await db.OutboxEvents.AsNoTracking().ToListAsync());
        Assert.Equal((DomainEventType.FiscalDocumentSdiRejected, document.Id, $"fiscal-document-sdi-rejected:{document.Id:N}"), (recorded.EventType, recorded.EntityId, recorded.DedupKey));
        Assert.DoesNotContain("example.com", recorded.PayloadJson, StringComparison.Ordinal);

        // A delivery after a rejection contradicts it: not applied.
        Assert.Equal(SdiRecordResult.Ignored, await service.RecordOutcomeAsync(document.Id, new SdiOutcome(SdiOutcomeKind.Delivered)));
        Assert.Equal(FiscalDocumentSdiStatuses.Rejected, (await db.FiscalDocuments.AsNoTracking().SingleAsync()).SdiStatus);
    }

    /// <summary>
    /// The rule the reuse of a number rests on: a delivered invoice is never rejected afterwards, so a rejected one never was delivered. The rejection is ignored (and no event is recorded).
    /// </summary>
    [Fact]
    public async Task RecordOutcome_ADeliveredInvoice_IsNeverRejectedAfterwards()
    {
        await using var db = NewDb();
        var document = await SeedAsync(db, sdiStatus: FiscalDocumentSdiStatuses.Delivered, transmissionId: "42");
        var service = Service(db, Configured().Object);

        Assert.Equal(SdiRecordResult.Ignored, await service.RecordOutcomeAsync(document.Id, new SdiOutcome(SdiOutcomeKind.Rejected, "00417")));
        var declared = await Assert.ThrowsAsync<DomainConflictException>(() => service.DeclareManuallyAsync(document.Id, new SdiDeclaration(SdiDeclarationKind.Rejected, ErrorCode: "00417")));

        Assert.Equal(FiscalDocumentSdiErrors.StateFinal, declared.Code);
        Assert.Equal(FiscalDocumentSdiStatuses.Delivered, (await db.FiscalDocuments.AsNoTracking().SingleAsync()).SdiStatus);
        Assert.Empty(await db.OutboxEvents.ToListAsync());
    }

    [Fact]
    public async Task RecordOutcome_NeedsAnIssuedElectronicInvoiceOfCasazen()
    {
        await using var db = NewDb();
        var service = Service(db, Configured().Object);
        var imported = await SeedAsync(db, source: FiscalDocumentSource.Octorate, sdiStatus: FiscalDocumentSdiStatuses.Submitted);
        var receipt = await SeedAsync(db, sdiStatus: FiscalDocumentSdiStatuses.NotApplicable);

        Assert.Equal(FiscalDocumentErrors.ReadOnly, (await Assert.ThrowsAsync<DomainRuleException>(() => service.RecordOutcomeAsync(imported.Id, new SdiOutcome(SdiOutcomeKind.Delivered)))).Code);
        Assert.Equal(FiscalDocumentSdiErrors.NotElectronic, (await Assert.ThrowsAsync<DomainRuleException>(() => service.RecordOutcomeAsync(receipt.Id, new SdiOutcome(SdiOutcomeKind.Delivered)))).Code);
        await Assert.ThrowsAsync<NotFoundException>(() => service.RecordOutcomeAsync(Guid.NewGuid(), new SdiOutcome(SdiOutcomeKind.Delivered)));
    }

    // ---------------------------------------------------------------- the declaration by hand

    [Fact]
    public async Task Declare_TheDayAndTheReference_AreChecked_AndARejectionGoesThroughTheOneEntryOfTheOutcomes()
    {
        await using var db = NewDb();
        var document = await SeedAsync(db);
        var service = Service(db, new UnconfiguredSdiEInvoiceProvider());

        // The day: not in the future, not before the issue; the reference: not empty.
        foreach (var declaration in new[]
                 {
                     new SdiDeclaration(SdiDeclarationKind.Sent, new DateOnly(2026, 11, 21), "ref"),
                     new SdiDeclaration(SdiDeclarationKind.Sent, new DateOnly(2026, 11, 1), "ref"),
                     new SdiDeclaration(SdiDeclarationKind.Sent, new DateOnly(2026, 11, 19), "  "),
                 })
        {
            var invalid = await Assert.ThrowsAsync<DomainRuleException>(() => service.DeclareManuallyAsync(document.Id, declaration));
            Assert.Equal(FiscalDocumentSdiErrors.DeclarationInvalid, invalid.Code);
        }

        // A rejection needs its code; with it, the state is rejected and the event is recorded as for the provider's answers.
        var noCode = await Assert.ThrowsAsync<DomainRuleException>(() => service.DeclareManuallyAsync(document.Id, new SdiDeclaration(SdiDeclarationKind.Rejected)));
        Assert.Equal(["errorCode"], Assert.IsAssignableFrom<List<string>>(noCode.Extensions["fields"]));

        var rejected = await service.DeclareManuallyAsync(document.Id, new SdiDeclaration(SdiDeclarationKind.Rejected, ErrorCode: "311", ErrorText: "Codice destinatario non valido"));
        Assert.Equal((FiscalDocumentSdiStatuses.Rejected, "00311", true), (rejected.Status, rejected.ErrorCode, rejected.CanCorrect));
        Assert.Single(await db.OutboxEvents.Where(e => e.EventType == DomainEventType.FiscalDocumentSdiRejected).ToListAsync());

        // A document rejected is rejected: a second declaration is no change, and a declaration of sending is refused.
        await service.DeclareManuallyAsync(document.Id, new SdiDeclaration(SdiDeclarationKind.Rejected, ErrorCode: "311"));
        Assert.Single(await db.OutboxEvents.ToListAsync());
        Assert.Equal(
            FiscalDocumentSdiErrors.StateFinal,
            (await Assert.ThrowsAsync<DomainConflictException>(() => service.DeclareManuallyAsync(document.Id, new SdiDeclaration(SdiDeclarationKind.Sent, new DateOnly(2026, 11, 19), "ref")))).Code);
    }

    [Fact]
    public async Task Declare_ADocumentTheProviderHas_IsDecidedByTheProvider()
    {
        await using var db = NewDb();
        var submitted = await SeedAsync(db, sdiStatus: FiscalDocumentSdiStatuses.Submitted, transmissionId: "42");
        var service = Service(db, Configured().Object);

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => service.DeclareManuallyAsync(submitted.Id, new SdiDeclaration(SdiDeclarationKind.Sent, new DateOnly(2026, 11, 19), "ref")));

        Assert.Equal(FiscalDocumentSdiErrors.NotToSend, conflict.Code);
        Assert.Equal(FiscalDocumentSdiStatuses.Submitted, (await db.FiscalDocuments.AsNoTracking().SingleAsync()).SdiStatus);
    }

    // ---------------------------------------------------------------- the reads of the job and of the webhook

    [Fact]
    public async Task Reads_OfTheJobAndTheWebhook_AreAcrossTheOrgs_AndNeverIncludeADocumentDeclaredByHand()
    {
        await using var db = NewDb();
        var other = Guid.NewGuid();
        var mine = await SeedAsync(db, sdiStatus: FiscalDocumentSdiStatuses.Submitted, transmissionId: "42");
        var theirs = await SeedAsync(db, sdiStatus: FiscalDocumentSdiStatuses.Submitted, transmissionId: "43", orgId: other);
        var declared = await SeedAsync(db, sdiStatus: FiscalDocumentSdiStatuses.ManualDeclared, transmissionId: "IT09876543217_AB123.xml");
        await SeedAsync(db, sdiStatus: FiscalDocumentSdiStatuses.Delivered, transmissionId: "44");
        var service = Service(db, Configured().Object);

        var submitted = await service.SubmittedDocumentsAsync(10);
        Assert.Equal(
            new[] { (OrgId, mine.Id, "42"), (other, theirs.Id, "43") }.OrderBy(t => t.Item2),
            submitted.Select(s => (s.OrgId, s.DocumentId, s.TransmissionId)).OrderBy(t => t.DocumentId));

        Assert.Equal(theirs.Id, (await service.FindByTransmissionAsync("43"))?.DocumentId);
        Assert.Null(await service.FindByTransmissionAsync("IT09876543217_AB123.xml"));
        Assert.Null(await service.FindByTransmissionAsync("unknown"));
        Assert.NotNull(declared);
    }

    [Fact]
    public async Task IssuerRegistrations_AreDerivedFromTheDocuments_AndAskTheProviderNothing()
    {
        await using var db = NewDb();
        var organization = await SeedAsync(db);
        var ownerWithVat = await SeedOwnerProfileAsync(db, "01111111113");
        await SeedOwnerProfileAsync(db, vatNumber: null);
        var provider = Configured();
        var service = Service(db, provider.Object);

        // Nothing was submitted for anyone: unknown (the VAT number is enabled at the first submission); an owner without a VAT number sends no electronic invoice.
        var unknown = await service.IssuerRegistrationsAsync(new HostScope(OrgId));
        Assert.Equal(
            new[] { (organization.IssuerProfileId!.Value, (Guid?)null, "unknown"), (ownerWithVat, (Guid?)OwnerOf(db, ownerWithVat), "unknown") }.OrderBy(t => t.Item1),
            unknown.Select(i => (i.IssuerProfileId, i.PropertyOwnerId, i.State)).OrderBy(t => t.IssuerProfileId));

        // The provider refuses the VAT number of the organization: pending, until a submission of it is accepted after the refusal.
        var refused = await SeedAsync(db, issuerProfileId: organization.IssuerProfileId, errorCode: SdiSubmissionErrors.IssuerPending);
        refused.UpdatedAt = new DateTime(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync();
        Assert.Equal("pending", (await service.IssuerRegistrationsAsync(new HostScope(OrgId))).Single(i => i.PropertyOwnerId is null).State);

        // A submission accepted before the refusal does not clear it; one accepted after it does, while the refused document is still «to send» (the provider enabled the number since).
        var before = await SeedAsync(db, issuerProfileId: organization.IssuerProfileId, sdiStatus: FiscalDocumentSdiStatuses.Submitted, transmissionId: "41");
        before.SdiStatusUpdatedAt = new DateTime(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync();
        Assert.Equal("pending", (await service.IssuerRegistrationsAsync(new HostScope(OrgId))).Single(i => i.PropertyOwnerId is null).State);
        var after = await SeedAsync(db, issuerProfileId: organization.IssuerProfileId, sdiStatus: FiscalDocumentSdiStatuses.Submitted, transmissionId: "42");
        after.SdiStatusUpdatedAt = new DateTime(2026, 11, 10, 10, 0, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync();
        Assert.Equal("registered", (await service.IssuerRegistrationsAsync(new HostScope(OrgId))).Single(i => i.PropertyOwnerId is null).State);

        provider.Verify(p => p.SubmitDocumentAsync(It.IsAny<SdiDocumentSubmission>(), It.IsAny<CancellationToken>()), Times.Never);
        provider.Verify(p => p.GetStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// PROOF TEST: permission rule (the scope of the caller). A caller who reaches only some properties sees the issuers that have a document on a booking of those properties, never every issuer of
    /// the org («senza dichiarazione = in scope» is never the rule); a scope that reaches no property sees none; the whole org sees all of them.
    /// </summary>
    [Fact]
    public async Task IssuerRegistrations_ACallerWithAPartialScope_SeesOnlyTheIssuersOfTheDocumentsOfHisProperties()
    {
        await using var db = NewDb();
        var organization = await SeedAsync(db);
        var ownerWithVat = await SeedOwnerProfileAsync(db, "01111111113");
        var ofHostA = await SeedAsync(db, issuerProfileId: organization.IssuerProfileId, sdiStatus: FiscalDocumentSdiStatuses.Submitted, transmissionId: "1");
        await OnABookingOfAsync(db, ofHostA, "auth0|host-a");
        var ofHostB = await SeedAsync(db, issuerProfileId: ownerWithVat, sdiStatus: FiscalDocumentSdiStatuses.Submitted, transmissionId: "2");
        await OnABookingOfAsync(db, ofHostB, "auth0|host-b");
        var service = Service(db, Configured().Object);

        Assert.Equal(2, (await service.IssuerRegistrationsAsync(new HostScope(OrgId))).Count);
        Assert.Equal([organization.IssuerProfileId!.Value], (await service.IssuerRegistrationsAsync(new HostScope(OrgId, OwnerId: "auth0|host-a"))).Select(i => i.IssuerProfileId).ToArray());
        Assert.Equal([ownerWithVat], (await service.IssuerRegistrationsAsync(new HostScope(OrgId, OwnerId: "auth0|host-b"))).Select(i => i.IssuerProfileId).ToArray());
        Assert.Empty(await service.IssuerRegistrationsAsync(new HostScope(OrgId, OwnerId: "auth0|somebody-else")));
        Assert.Empty(await service.IssuerRegistrationsAsync(new HostScope(OrgId, GrantedToUserId: "auth0|collaborator-without-grants")));
    }

    // ---------------------------------------------------------------- the contributor of the billing setup

    [Fact]
    public async Task BillingSetup_TheIssuersWaitingForTheirVatNumber_AreCountedOnlyWhileTheChannelIsOpen()
    {
        await using var db = NewDb();
        var organization = await SeedAsync(db);
        await SeedAsync(db, issuerProfileId: organization.IssuerProfileId, errorCode: SdiSubmissionErrors.IssuerPending);
        var provider = Configured();
        var service = Service(db, provider.Object);
        var on = new TestFeatureFlags(FeatureFlags.FiscalDocumentIssuance, FeatureFlags.SdiSubmission);

        var contributor = new SdiIssuerRegistrationContributor(service, provider.Object, on);
        var items = await contributor.EvaluateAsync(OrgId, CancellationToken.None);

        Assert.True(await contributor.AppliesAsync(OrgId, CancellationToken.None));
        Assert.Equal(
            [("sdi_issuer_registration_pending", "italy", "pending", (int?)1)],
            items.Select(i => (i.Key, i.Section, i.Reason, i.Count)).ToList());

        // No channel, or the module off: nothing to look at, the section does not apply because of it.
        var closed = new SdiIssuerRegistrationContributor(Service(db, new UnconfiguredSdiEInvoiceProvider()), new UnconfiguredSdiEInvoiceProvider(), on);
        Assert.False(await closed.AppliesAsync(OrgId, CancellationToken.None));
        Assert.Empty(await closed.EvaluateAsync(OrgId, CancellationToken.None));
        var off = new SdiIssuerRegistrationContributor(service, provider.Object, new TestFeatureFlags());
        Assert.False(await off.AppliesAsync(OrgId, CancellationToken.None));
        Assert.Empty(await off.EvaluateAsync(OrgId, CancellationToken.None));
    }

    // ---------------------------------------------------------------- the state a host reads

    [Fact]
    public async Task State_SaysWhatCanBeDone_AndWhyAnInvoiceIsStillToSend()
    {
        await using var db = NewDb();
        var toSend = await SeedAsync(db);
        var refused = await SeedAsync(db, errorCode: SdiSubmissionErrors.ProviderError);
        var rejected = await SeedAsync(db, sdiStatus: FiscalDocumentSdiStatuses.Rejected, errorCode: "00417");
        var receipt = await SeedAsync(db, sdiStatus: FiscalDocumentSdiStatuses.NotApplicable);
        var service = Service(db, Configured().Object);

        Assert.Equal((true, true, false, true), Flags(await service.GetStateAsync(toSend.Id)));
        Assert.Equal(SdiSubmissionErrors.ProviderError, (await service.GetStateAsync(refused.Id))!.ErrorCode);
        Assert.Equal((false, false, true, true), Flags(await service.GetStateAsync(rejected.Id)));
        Assert.Equal((false, false, false, true), Flags(await service.GetStateAsync(receipt.Id)));
        Assert.Null(await service.GetStateAsync(Guid.NewGuid()));

        var noProvider = Service(db, new UnconfiguredSdiEInvoiceProvider());
        Assert.False((await noProvider.GetStateAsync(toSend.Id))!.ProviderConfigured);

        static (bool CanSubmit, bool CanDeclare, bool CanCorrect, bool Found) Flags(FiscalDocumentSdiState? state) =>
            (state?.CanSubmit ?? false, state?.CanDeclare ?? false, state?.CanCorrect ?? false, state is not null);
    }

    // ---------------------------------------------------------------- helpers

    private static FiscalDocumentSdiService Service(AppDbContext db, ISdiEInvoiceProvider provider, IFeatureFlags? flags = null, IDomainEventRecorder? recorder = null) =>
        new(
            db,
            provider,
            recorder ?? new DomainEventRecorder(db, new SdiTenant(), new FakeTimeProvider(Now)),
            new FakeTimeProvider(Now),
            NullLogger<FiscalDocumentSdiService>.Instance,
            flags ?? new TestFeatureFlags(FeatureFlags.FiscalDocumentIssuance, FeatureFlags.SdiSubmission));

    private static Mock<ISdiEInvoiceProvider> Configured()
    {
        var provider = new Mock<ISdiEInvoiceProvider>();
        provider.SetupGet(p => p.IsConfigured).Returns(true);
        provider.SetupGet(p => p.Name).Returns("test");
        return provider;
    }

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"FiscalSdi_{Guid.NewGuid()}").Options, new SdiTenant(), new EphemeralDataProtectionProvider());

    /// <summary>An issued electronic invoice of CasaZen «to send», as the issue leaves it (the XML, the snapshot of the issuer); the arguments change what the test needs.</summary>
    private static async Task<FiscalDocument> SeedAsync(
        AppDbContext db,
        FiscalDocumentSource source = FiscalDocumentSource.CasaZen,
        FiscalDocumentStatus status = FiscalDocumentStatus.Issued,
        string sdiStatus = FiscalDocumentSdiStatuses.ToSend,
        string? transmissionId = null,
        string? errorCode = null,
        Guid? orgId = null,
        Guid? issuerProfileId = null)
    {
        var org = orgId ?? OrgId;
        if (!await db.OrgFiscalIssuerProfiles.AnyAsync(p => p.OrgId == org && p.PropertyOwnerId == null))
        {
            db.Orgs.Add(new OrgEntity
            {
                Id = org,
                Name = "TEST Casa Vacanze",
                Slug = $"test-{org:N}"[..20],
                DisplayName = "TEST Casa Vacanze",
                ContactEmail = "test@example.com",
                HasPartitaIva = true,
                PartitaIvaNumber = "01234567897",
                FiscalCode = "01234567897",
            });
            db.OrgFiscalIssuerProfiles.Add(new OrgFiscalIssuerProfile
            {
                OrgId = org,
                IssuerMode = DocumentIssuerMode.CasaZen,
                DocumentRegime = DocumentRegime.VatOrdinary,
                LegalName = "TEST Casa Vacanze Srl",
                CountryCode = "IT",
            });
            await db.SaveChangesAsync();
        }

        var profile = issuerProfileId ?? (await db.OrgFiscalIssuerProfiles.AsNoTracking().FirstAsync(p => p.OrgId == org && p.PropertyOwnerId == null)).Id;
        var number = await db.FiscalDocuments.IgnoreQueryFilters().CountAsync(d => d.OrgId == org) + 1;
        var document = new FiscalDocument
        {
            OrgId = org,
            Source = source,
            Kind = FiscalDocumentKind.Invoice,
            Status = status,
            IssuerProfileId = profile,
            Series = "2026",
            SequenceNumber = status == FiscalDocumentStatus.Issued ? number : null,
            Number = status == FiscalDocumentStatus.Issued ? $"2026/{number}" : null,
            IssueDate = status == FiscalDocumentStatus.Issued ? new DateOnly(2026, 11, 10) : null,
            Currency = "EUR",
            Total = 110m,
            SdiStatus = sdiStatus,
            SdiTransmissionId = transmissionId,
            SdiErrorCode = errorCode,
            FatturaPaXml = Xml,
            IssuerSnapshot = new FiscalIssuerSnapshot { LegalName = "TEST Casa Vacanze Srl", VatNumber = "01234567897", FiscalCode = "01234567897", CountryCode = "IT" }.ToJson(),
        };
        db.FiscalDocuments.Add(document);
        await db.SaveChangesAsync();
        return document;
    }

    /// <summary>The profile of an owner who issues, with its own VAT number (or none, a private person), in the org of the tests; its id.</summary>
    private static async Task<Guid> SeedOwnerProfileAsync(AppDbContext db, string? vatNumber)
    {
        var owner = new PropertyOwner { OrgId = OrgId, FirstName = "TEST", LastName = "Proprietario", Email = $"{Guid.NewGuid():N}@example.com" };
        db.PropertyOwners.Add(owner);
        var profile = new OrgFiscalIssuerProfile
        {
            OrgId = OrgId,
            PropertyOwnerId = owner.Id,
            IssuerMode = DocumentIssuerMode.CasaZen,
            LegalName = "TEST Proprietario",
            VatNumber = vatNumber,
            CountryCode = "IT",
        };
        db.OrgFiscalIssuerProfiles.Add(profile);
        await db.SaveChangesAsync();
        return profile.Id;
    }

    private static Guid OwnerOf(AppDbContext db, Guid profileId) => db.OrgFiscalIssuerProfiles.AsNoTracking().Single(p => p.Id == profileId).PropertyOwnerId!.Value;

    /// <summary>The document made the one of a booking at a property of the host <paramref name="hostId"/> (what the scope of a caller reaches it through).</summary>
    private static async Task OnABookingOfAsync(AppDbContext db, FiscalDocument document, string hostId)
    {
        var property = new Property { OrgId = OrgId, OwnerId = hostId, Name = "TEST Casa", Address = "Via 1", City = "Roma", Currency = "EUR" };
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
            BasePrice = 100m,
            TotalPrice = 100m,
        };
        db.Properties.Add(property);
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        (await db.FiscalDocuments.SingleAsync(d => d.Id == document.Id)).BookingId = booking.Id;
        await db.SaveChangesAsync();
    }
}

/// <summary>No request: the org of an event or of a read is the one the test names.</summary>
internal sealed class SdiTenant : ITenantContext
{
    public Guid? OrgId => null;

    public bool FilterEnabled => false;
}
