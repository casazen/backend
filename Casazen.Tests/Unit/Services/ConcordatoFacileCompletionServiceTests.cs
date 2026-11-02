using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Leases;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// CF-DONE, on the EF InMemory database: the lease of the guided service follows its practice. The documents it delivers are read into the
/// draft, the contract is imported as signed only once the practice says it is signed (with the day the contract or the receipt states),
/// the registration is declared from the receipt, and whatever cannot be done by itself is a state for the person, never an error. The
/// collaborators that record a signature and a registration are mocks: what these tests prove is WHEN they are asked and WITH WHAT.
/// </summary>
public sealed class ConcordatoFacileCompletionServiceTests : IDisposable
{
    private const string Actor = "auth0|owner";

    /// <summary>What the reading of a contract writes into a document when it fills the draft (version 3), with the day of the signature it found.</summary>
    private const string ReadIntoDraft = """{"version":3,"kind":"contract","stipulaDate":"2026-09-19","containsAttestation":true,"fields":[]}""";

    private readonly LeaseDraftHarness _harness = new();
    private readonly Mock<IConcordatoFacileStatusCacheService> _statuses = new();
    private readonly Mock<ILeaseSigningService> _signing = new();
    private readonly Mock<IRliRegistrationService> _registrations = new();
    private readonly Mock<ILeaseDocumentReadingService> _receipts = new();
    private readonly Mock<ILeaseDocumentReadQueue> _queue = new();

    public void Dispose() => _harness.Dispose();

    // ---- which leases are the service's ---------------------------------------------------------------------------------

    [Fact]
    public async Task ALeaseThatIsNotTheServices_IsLeftAlone()
    {
        await _harness.SeedAsync();
        var created = await _harness.CreateAsync(LeaseOrigin.Created);

        var result = await NewService().SyncAsync(created.Id, _harness.OrgId, Actor);

        Assert.Equal(CfCompletionStates.NotApplicable, result.State);
        var lease = await _harness.LoadAsync(created.Id);
        Assert.False(lease.IsConcordatoFacileSource);
        Assert.Equal(LeaseOrigin.Created, lease.Origin);
        _statuses.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ALegacyConcordatoDraftWhoseFormWasOpened_BecomesTheServicesDraft_AndItsDocumentsAreReadIntoIt()
    {
        await _harness.SeedAsync();
        var leaseId = await SeedLegacyConcordatoDraftAsync(formOpened: true);

        var result = await NewService().SyncAsync(leaseId, _harness.OrgId, Actor);

        // Imported and marked: the documents are read into it from now on, and the list stops asking for its data.
        var lease = await _harness.LoadAsync(leaseId);
        Assert.True(lease.IsConcordatoFacileSource);
        Assert.Equal(LeaseOrigin.Imported, lease.Origin);
        Assert.Equal(LeaseStatus.Draft, lease.Status);
        Assert.Equal(CfCompletionStates.WaitingForContract, result.State);
    }

    [Fact]
    public async Task AConcordatoDraftWhoseFormWasNeverOpened_IsNotTheServices()
    {
        await _harness.SeedAsync();
        var leaseId = await SeedLegacyConcordatoDraftAsync(formOpened: false);

        var result = await NewService().SyncAsync(leaseId, _harness.OrgId, Actor);

        Assert.Equal(CfCompletionStates.NotApplicable, result.State);
        Assert.False((await _harness.LoadAsync(leaseId)).IsConcordatoFacileSource);
    }

    [Fact]
    public async Task ADraftAlreadyReviewedInCasaZen_IsNeverTakenOverByTheService()
    {
        await _harness.SeedAsync();
        var leaseId = await SeedLegacyConcordatoDraftAsync(formOpened: true);
        await using (var db = _harness.SecondContext())
        {
            var lease = await db.LeaseContracts.SingleAsync(l => l.Id == leaseId);
            lease.ReviewConfirmedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        var result = await NewService().SyncAsync(leaseId, _harness.OrgId, Actor);

        Assert.Equal(CfCompletionStates.NotApplicable, result.State);
        var stored = await _harness.LoadAsync(leaseId);
        Assert.False(stored.IsConcordatoFacileSource);
        Assert.Equal(LeaseOrigin.Created, stored.Origin);
    }

    [Fact]
    public async Task ALeaseOfAnotherOrg_IsNotFound()
    {
        await _harness.SeedAsync();
        var leaseId = await _harness.Service.CreateCfDraftAsync(_harness.PropertyId, _harness.OrgId);

        await Assert.ThrowsAsync<NotFoundException>(() => NewService().SyncAsync(leaseId, Guid.NewGuid(), Actor));
    }

    [Fact]
    public async Task TheListSaysTheServicesDraftAwaitsItsContract_NeverThatItsDataAreToBeCompleted()
    {
        await _harness.SeedAsync();
        var legacy = await SeedLegacyConcordatoDraftAsync(formOpened: true);
        var service = await _harness.Service.CreateCfDraftAsync(_harness.PropertyId, _harness.OrgId);

        // The old draft is a draft like any other until the service takes it over; the service's draft never is.
        var before = await StepOfAsync(legacy);
        Assert.Equal(LeaseNextStepCode.CompleteStep, before);
        await NewService().SyncAsync(legacy, _harness.OrgId, Actor);

        Assert.Equal(LeaseNextStepCode.AwaitPartnerContract, await StepOfAsync(legacy));
        Assert.Equal(LeaseNextStepCode.AwaitPartnerContract, await StepOfAsync(service));
    }

    // ---- the contract ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WithNoContractDocument_TheLeaseWaitsForIt()
    {
        var leaseId = await SeedServiceDraftAsync();

        var result = await NewService().SyncAsync(leaseId, _harness.OrgId, Actor);

        Assert.Equal(new CfCompletionResult(CfCompletionStates.WaitingForContract, LeaseStatus.Draft), result);
        _signing.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AContractReadTheOldWay_IsPutBackToBeReadIntoTheDraft_OnceAndThenWaitedFor()
    {
        var leaseId = await SeedServiceDraftAsync();
        var document = await AddDocumentAsync(leaseId, LeaseDocumentKind.Contract, LeaseDocumentExtractionStatus.Completed, """{"version":1,"fields":[]}""");

        var first = await NewService().SyncAsync(leaseId, _harness.OrgId, Actor);
        var second = await NewService().SyncAsync(leaseId, _harness.OrgId, Actor);

        Assert.Equal(CfCompletionStates.ReadingContract, first.State);
        Assert.Equal(CfCompletionStates.ReadingContract, second.State);
        _queue.Verify(q => q.Enqueue(document.Id), Times.Once);
        Assert.Equal(LeaseDocumentExtractionStatus.Processing, (await LoadDocumentAsync(document.Id)).ExtractionStatus);
    }

    [Fact]
    public async Task ADocumentThatLostItsJob_IsReadAgainAfterAWhile_NotWaitedForForever()
    {
        var leaseId = await SeedServiceDraftAsync();
        var document = await AddDocumentAsync(leaseId, LeaseDocumentKind.Contract, LeaseDocumentExtractionStatus.Processing, null);
        await SetStartedAsync(document.Id, _harness.Clock.GetUtcNow().UtcDateTime.AddMinutes(-20));

        var result = await NewService().SyncAsync(leaseId, _harness.OrgId, Actor);

        Assert.Equal(CfCompletionStates.ReadingContract, result.State);
        _queue.Verify(q => q.Enqueue(document.Id), Times.Once);
    }

    [Fact]
    public async Task ADocumentBeingRead_IsWaitedFor_NotQueuedAgain()
    {
        var leaseId = await SeedServiceDraftAsync();
        var document = await AddDocumentAsync(leaseId, LeaseDocumentKind.Contract, LeaseDocumentExtractionStatus.Processing, null);
        await SetStartedAsync(document.Id, _harness.Clock.GetUtcNow().UtcDateTime.AddMinutes(-1));

        var result = await NewService().SyncAsync(leaseId, _harness.OrgId, Actor);

        Assert.Equal(CfCompletionStates.ReadingContract, result.State);
        _queue.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AReadingThatFailed_IsNotTriedAgain_TheImportIsLeftToThePerson()
    {
        var leaseId = await SeedServiceDraftAsync();
        await AddDocumentAsync(leaseId, LeaseDocumentKind.Contract, LeaseDocumentExtractionStatus.Failed, null);

        var result = await NewService().SyncAsync(leaseId, _harness.OrgId, Actor);

        Assert.Equal(CfCompletionStates.ContractNeedsData, result.State);
        _queue.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(CfPracticeStatus.Received)]
    [InlineData(CfPracticeStatus.InProgress)]
    [InlineData(CfPracticeStatus.AwaitingUser)]
    [InlineData(CfPracticeStatus.DraftSent)]
    [InlineData(CfPracticeStatus.Suspended)]
    [InlineData(CfPracticeStatus.Unknown)]
    public async Task TheContractIsNotImportedAsSigned_UntilThePracticeSaysItIs(CfPracticeStatus status)
    {
        var leaseId = await SeedServiceDraftAsync();
        await AddDocumentAsync(leaseId, LeaseDocumentKind.Contract, LeaseDocumentExtractionStatus.Completed, ReadIntoDraft);
        SetupPractice(status);

        var result = await NewService().SyncAsync(leaseId, _harness.OrgId, Actor);

        Assert.Equal(new CfCompletionResult(CfCompletionStates.WaitingForSignature, LeaseStatus.Draft), result);
        _signing.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task APracticeThatCannotBeRead_LeavesTheImportToThePerson()
    {
        var leaseId = await SeedServiceDraftAsync();
        await AddDocumentAsync(leaseId, LeaseDocumentKind.Contract, LeaseDocumentExtractionStatus.Completed, ReadIntoDraft);
        _statuses
            .Setup(s => s.GetPracticesAsync(leaseId, _harness.OrgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CfStatusResult { Practices = [], LastCheckedAt = null, NextUpdateAt = DateTime.UtcNow, IsStale = true, IsAvailable = false });

        var result = await NewService().SyncAsync(leaseId, _harness.OrgId, Actor);

        Assert.Equal(CfCompletionStates.ContractNeedsData, result.State);
        _signing.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(CfPracticeStatus.SignedReceived)]
    [InlineData(CfPracticeStatus.SentToAssociation)]
    [InlineData(CfPracticeStatus.Attested)]
    [InlineData(CfPracticeStatus.Registered)]
    public async Task OncePracticeSaysTheContractIsSigned_TheDocumentIsImportedWithTheDayItStates(CfPracticeStatus status)
    {
        var leaseId = await SeedServiceDraftAsync();
        var document = await AddDocumentAsync(leaseId, LeaseDocumentKind.Contract, LeaseDocumentExtractionStatus.Completed, ReadIntoDraft);
        SetupPractice(status);
        ImportMakesTheLeaseSigned(leaseId);

        var result = await NewService().SyncAsync(leaseId, _harness.OrgId, Actor);

        // The signed copy is the document the person uploaded, and the day is the one the contract says (not today's).
        _signing.Verify(
            s => s.ImportSignedContractFromDocumentAsync(
                leaseId,
                Actor,
                It.Is<ImportedDocumentDeclaration>(d => d.DocumentId == document.Id && d.StipulaDate == new DateTime(2026, 9, 19)),
                It.IsAny<CancellationToken>()),
            Times.Once);
        // Signed now, no receipt uploaded yet: what is left is the receipt.
        Assert.Equal(new CfCompletionResult(CfCompletionStates.WaitingForReceipt, LeaseStatus.Signed), result);
    }

    [Fact]
    public async Task AClosedPractice_IsNotTheOneThatSaysItIsSigned()
    {
        var leaseId = await SeedServiceDraftAsync();
        await AddDocumentAsync(leaseId, LeaseDocumentKind.Contract, LeaseDocumentExtractionStatus.Completed, ReadIntoDraft);
        // A second practice, closed as a duplicate, is newer than the one under way: it does not count either way.
        _statuses
            .Setup(s => s.GetPracticesAsync(leaseId, _harness.OrgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Practices(
                Practice("P-1111111111111111", CfPracticeStatus.InProgress, TimeSpan.FromDays(-2)),
                Practice("P-2222222222222222", CfPracticeStatus.ClosedWithoutAttestation, TimeSpan.FromDays(-1))));

        var result = await NewService().SyncAsync(leaseId, _harness.OrgId, Actor);

        Assert.Equal(CfCompletionStates.WaitingForSignature, result.State);
    }

    [Fact]
    public async Task WithNoDayInTheContract_TheDayOfTheReceiptIsUsed()
    {
        var leaseId = await SeedServiceDraftAsync();
        var document = await AddDocumentAsync(leaseId, LeaseDocumentKind.Contract, LeaseDocumentExtractionStatus.Completed, """{"version":3,"kind":"contract","fields":[]}""");
        var receipt = await AddDocumentAsync(leaseId, LeaseDocumentKind.Registration, LeaseDocumentExtractionStatus.Completed, null);
        SetupPractice(CfPracticeStatus.Registered);
        SetupReceipt(leaseId, receipt.Id, stipulation: "2026-09-18");
        ImportMakesTheLeaseSigned(leaseId);
        RegistrationMakesTheLeaseRegistered(leaseId);

        var result = await NewService().SyncAsync(leaseId, _harness.OrgId, Actor);

        _signing.Verify(
            s => s.ImportSignedContractFromDocumentAsync(
                leaseId, Actor, It.Is<ImportedDocumentDeclaration>(d => d.DocumentId == document.Id && d.StipulaDate == new DateTime(2026, 9, 18)), It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Equal(CfCompletionStates.Completed, result.State);
    }

    [Fact]
    public async Task WithNoDayAnywhere_TheImportIsLeftToThePerson()
    {
        var leaseId = await SeedServiceDraftAsync();
        await AddDocumentAsync(leaseId, LeaseDocumentKind.Contract, LeaseDocumentExtractionStatus.Completed, """{"version":3,"kind":"contract","fields":[]}""");
        SetupPractice(CfPracticeStatus.Registered);

        var result = await NewService().SyncAsync(leaseId, _harness.OrgId, Actor);

        Assert.Equal(CfCompletionStates.ContractNeedsData, result.State);
        _signing.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AReviewThatBlocksTheImport_IsAStateForThePerson_NotAnError()
    {
        var leaseId = await SeedServiceDraftAsync();
        await AddDocumentAsync(leaseId, LeaseDocumentKind.Contract, LeaseDocumentExtractionStatus.Completed, ReadIntoDraft);
        SetupPractice(CfPracticeStatus.Registered);
        _signing
            .Setup(s => s.ImportSignedContractFromDocumentAsync(leaseId, Actor, It.IsAny<ImportedDocumentDeclaration>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DomainRuleException(LeaseDraftErrorCodes.ReviewBlocked, "LeaseReviewBlocked"));

        var result = await NewService().SyncAsync(leaseId, _harness.OrgId, Actor);

        Assert.Equal(new CfCompletionResult(CfCompletionStates.ContractNeedsData, LeaseStatus.Draft), result);
    }

    [Fact]
    public async Task AnotherCallThatImportedItFirst_IsNotAnError()
    {
        var leaseId = await SeedServiceDraftAsync();
        await AddDocumentAsync(leaseId, LeaseDocumentKind.Contract, LeaseDocumentExtractionStatus.Completed, ReadIntoDraft);
        SetupPractice(CfPracticeStatus.Registered);
        _signing
            .Setup(s => s.ImportSignedContractFromDocumentAsync(leaseId, Actor, It.IsAny<ImportedDocumentDeclaration>(), It.IsAny<CancellationToken>()))
            .Callback(() => SetStatus(leaseId, LeaseStatus.Signed))
            .ThrowsAsync(new DomainConflictException(LeaseDraftErrorCodes.NotDraft, "LeaseNotDraft"));

        var result = await NewService().SyncAsync(leaseId, _harness.OrgId, Actor);

        Assert.Equal(CfCompletionStates.WaitingForReceipt, result.State);
    }

    // ---- the registration -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASignedLease_WithAReceiptReadAsARegistration_DeclaresItWithItsCodeAndDay()
    {
        var leaseId = await SeedServiceLeaseAsync(LeaseStatus.Signed);
        var receipt = await AddDocumentAsync(leaseId, LeaseDocumentKind.Registration, LeaseDocumentExtractionStatus.Completed, null);
        SetupReceipt(leaseId, receipt.Id, code: "TKM26T012345000AB", registrationDate: "2026-09-21");
        RegistrationMakesTheLeaseRegistered(leaseId);

        var result = await NewService().SyncAsync(leaseId, _harness.OrgId, Actor);

        _registrations.Verify(
            r => r.DeclareManualRegistrationFromDocumentAsync(
                leaseId,
                Actor,
                It.Is<ManualRegistrationDocumentDeclaration>(d =>
                    d.RegistrationCode == "TKM26T012345000AB" && d.RegistrationDate == new DateTime(2026, 9, 21) && d.ReceiptDocumentId == receipt.Id),
                It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Equal(new CfCompletionResult(CfCompletionStates.Completed, LeaseStatus.Registered), result);
    }

    [Fact]
    public async Task TheCodeIsTheNumberAndSeries_WhenTheReceiptGivesNoIdentificationCode()
    {
        var leaseId = await SeedServiceLeaseAsync(LeaseStatus.Signed);
        var receipt = await AddDocumentAsync(leaseId, LeaseDocumentKind.Registration, LeaseDocumentExtractionStatus.Completed, null);
        SetupReceipt(leaseId, receipt.Id, code: null, registrationDate: "2026-09-21", number: "012345", series: "3T");
        RegistrationMakesTheLeaseRegistered(leaseId);

        await NewService().SyncAsync(leaseId, _harness.OrgId, Actor);

        _registrations.Verify(
            r => r.DeclareManualRegistrationFromDocumentAsync(
                leaseId, Actor, It.Is<ManualRegistrationDocumentDeclaration>(d => d.RegistrationCode == "012345 3T"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ASignedLease_WithNoReceipt_WaitsForIt()
    {
        var leaseId = await SeedServiceLeaseAsync(LeaseStatus.Signed);

        var result = await NewService().SyncAsync(leaseId, _harness.OrgId, Actor);

        Assert.Equal(new CfCompletionResult(CfCompletionStates.WaitingForReceipt, LeaseStatus.Signed), result);
        _registrations.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AReceiptStillBeingRead_IsWaitedFor()
    {
        var leaseId = await SeedServiceLeaseAsync(LeaseStatus.Signed);
        var receipt = await AddDocumentAsync(leaseId, LeaseDocumentKind.Registration, LeaseDocumentExtractionStatus.Processing, null);
        await SetStartedAsync(receipt.Id, _harness.Clock.GetUtcNow().UtcDateTime.AddMinutes(-1));

        var result = await NewService().SyncAsync(leaseId, _harness.OrgId, Actor);

        Assert.Equal(CfCompletionStates.ReadingReceipt, result.State);
        _registrations.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("Unreadable")]
    [InlineData("NotAReceipt")]
    public async Task AReceiptThatIsNotRead_IsLeftToThePerson(string state)
    {
        var leaseId = await SeedServiceLeaseAsync(LeaseStatus.Signed);
        var receipt = await AddDocumentAsync(leaseId, LeaseDocumentKind.Registration, LeaseDocumentExtractionStatus.Failed, null);
        _receipts
            .Setup(r => r.ReadAsync(leaseId, receipt.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LeaseDocumentReadingDto(receipt.Id, "Registration", state, null));

        var result = await NewService().SyncAsync(leaseId, _harness.OrgId, Actor);

        Assert.Equal(CfCompletionStates.ReceiptNotUsable, result.State);
        _registrations.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ARequestTheAgenziaDiscarded_IsNeverDeclaredAsARegistration()
    {
        var leaseId = await SeedServiceLeaseAsync(LeaseStatus.Signed);
        var receipt = await AddDocumentAsync(leaseId, LeaseDocumentKind.Registration, LeaseDocumentExtractionStatus.NeedsReview, null);
        SetupReceipt(leaseId, receipt.Id, outcome: "Rejected", code: "TKM26T012345000AB", registrationDate: "2026-09-21");

        var result = await NewService().SyncAsync(leaseId, _harness.OrgId, Actor);

        Assert.Equal(CfCompletionStates.ReceiptNotUsable, result.State);
        _registrations.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AReceiptWithNoDay_IsLeftToThePerson()
    {
        var leaseId = await SeedServiceLeaseAsync(LeaseStatus.Signed);
        var receipt = await AddDocumentAsync(leaseId, LeaseDocumentKind.Registration, LeaseDocumentExtractionStatus.Completed, null);
        SetupReceipt(leaseId, receipt.Id, code: "TKM26T012345000AB", registrationDate: null);

        var result = await NewService().SyncAsync(leaseId, _harness.OrgId, Actor);

        Assert.Equal(CfCompletionStates.ReceiptNotUsable, result.State);
        _registrations.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ARegistrationTheServiceRefuses_IsAStateForThePerson_NotAnError()
    {
        var leaseId = await SeedServiceLeaseAsync(LeaseStatus.Signed);
        var receipt = await AddDocumentAsync(leaseId, LeaseDocumentKind.Registration, LeaseDocumentExtractionStatus.Completed, null);
        SetupReceipt(leaseId, receipt.Id, code: "TKM26T012345000AB", registrationDate: "2026-09-21");
        _registrations
            .Setup(r => r.DeclareManualRegistrationFromDocumentAsync(leaseId, Actor, It.IsAny<ManualRegistrationDocumentDeclaration>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DomainRuleException("rli_registration_date_in_future", "RliRegistrationDateInFuture"));

        var result = await NewService().SyncAsync(leaseId, _harness.OrgId, Actor);

        Assert.Equal(new CfCompletionResult(CfCompletionStates.ReceiptNotUsable, LeaseStatus.Signed), result);
    }

    [Fact]
    public async Task ARegisteredLease_IsCompleted_AndNothingIsAskedOfAnyone()
    {
        var leaseId = await SeedServiceLeaseAsync(LeaseStatus.Registered);

        var result = await NewService().SyncAsync(leaseId, _harness.OrgId, Actor);

        Assert.Equal(new CfCompletionResult(CfCompletionStates.Completed, LeaseStatus.Registered), result);
        _signing.VerifyNoOtherCalls();
        _registrations.VerifyNoOtherCalls();
        _queue.VerifyNoOtherCalls();
    }

    // ---- the whole way: the lease of the screenshot ---------------------------------------------------------------------

    [Fact]
    public async Task TheWholeWay_AnOldDraftWithItsDocuments_FollowsThePracticeUpToRegistered()
    {
        await _harness.SeedAsync();
        var leaseId = await SeedLegacyConcordatoDraftAsync(formOpened: true);
        // What the person uploaded: the signed contract with its attestation (read the old way, as the old draft read it) and the receipt.
        var contract = await AddDocumentAsync(leaseId, LeaseDocumentKind.Contract, LeaseDocumentExtractionStatus.Completed, """{"version":1,"fields":[]}""");
        var receipt = await AddDocumentAsync(leaseId, LeaseDocumentKind.Registration, LeaseDocumentExtractionStatus.Completed, null);
        SetupPractice(CfPracticeStatus.Registered);
        SetupReceipt(leaseId, receipt.Id, code: "TKM26T012345000AB", registrationDate: "2026-09-21", stipulation: "2026-09-19");
        ImportMakesTheLeaseSigned(leaseId);
        RegistrationMakesTheLeaseRegistered(leaseId);

        // 1. the draft becomes the service's and the contract is put back to be read into it.
        var first = await NewService().SyncAsync(leaseId, _harness.OrgId, Actor);
        Assert.Equal(CfCompletionStates.ReadingContract, first.State);
        Assert.True((await _harness.LoadAsync(leaseId)).IsConcordatoFacileSource);
        _queue.Verify(q => q.Enqueue(contract.Id), Times.Once);

        // 2. the job has read it into the draft (what ExtractLeaseDocumentDataJob writes), and the next call goes all the way.
        await using (var db = _harness.SecondContext())
        {
            var document = await db.LeaseDocuments.SingleAsync(d => d.Id == contract.Id);
            document.ExtractionStatus = LeaseDocumentExtractionStatus.Completed;
            document.ExtractionResult = ReadIntoDraft;
            await db.SaveChangesAsync();
        }

        var second = await NewService().SyncAsync(leaseId, _harness.OrgId, Actor);

        Assert.Equal(new CfCompletionResult(CfCompletionStates.Completed, LeaseStatus.Registered), second);
        Assert.Equal(LeaseStatus.Registered, (await _harness.LoadAsync(leaseId)).Status);
    }

    // ---- helpers --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The service of one request: its context starts with nothing tracked, as a scoped context of a new request does (the seed and the
    /// mocks write through other contexts, and the harness context would still hold the lease as it was created).
    /// </summary>
    private ConcordatoFacileCompletionService NewService()
    {
        _harness.Db.ChangeTracker.Clear();
        return new(
            _harness.Db,
            _statuses.Object,
            _signing.Object,
            _registrations.Object,
            _receipts.Object,
            _queue.Object,
            NullLogger<ConcordatoFacileCompletionService>.Instance,
            _harness.Clock);
    }

    /// <summary>The draft «Parto da zero» makes: imported, marked, one per property.</summary>
    private async Task<Guid> SeedServiceDraftAsync()
    {
        await _harness.SeedAsync();
        return await _harness.Service.CreateCfDraftAsync(_harness.PropertyId, _harness.OrgId);
    }

    /// <summary>A lease of the service in the given status, as it is after the contract was imported or the registration declared.</summary>
    private async Task<Guid> SeedServiceLeaseAsync(LeaseStatus status)
    {
        var leaseId = await SeedServiceDraftAsync();
        SetStatus(leaseId, status);
        return leaseId;
    }

    /// <summary>
    /// The draft of the screenshots: made on 23 October through the generic draft (origin Created, not marked), typed as a concordato; the
    /// guided form was opened afterwards (the event the signed link leaves).
    /// </summary>
    private async Task<Guid> SeedLegacyConcordatoDraftAsync(bool formOpened)
    {
        var created = await _harness.CreateAsync(LeaseOrigin.Created);
        await using var db = _harness.SecondContext();
        var lease = await db.LeaseContracts.SingleAsync(l => l.Id == created.Id);
        lease.SetContractTerms(LeaseContractType.Concordato, null);
        if (formOpened)
            db.LeaseEvents.Add(new LeaseEvent { LeaseContractId = lease.Id, EventType = LeaseEventType.ConcordatoFacileLinkGenerated, OccurredAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return created.Id;
    }

    private async Task<LeaseDocument> AddDocumentAsync(Guid leaseId, LeaseDocumentKind kind, LeaseDocumentExtractionStatus status, string? result)
    {
        await using var db = _harness.SecondContext();
        var document = new LeaseDocument
        {
            LeaseContractId = leaseId,
            OrgId = _harness.OrgId,
            Kind = kind,
            FileName = "documento.pdf",
            StorageKey = $"orgs/{_harness.OrgId}/leases/{leaseId}/documents/{Guid.NewGuid():N}.pdf",
            UploadedBy = Actor,
            SizeBytes = 28,
            ExtractionStatus = status,
            ExtractionResult = result,
            UploadedAt = _harness.Clock.GetUtcNow().UtcDateTime,
        };
        db.LeaseDocuments.Add(document);
        await db.SaveChangesAsync();
        return document;
    }

    private async Task<LeaseDocument> LoadDocumentAsync(Guid documentId)
    {
        await using var db = _harness.SecondContext();
        return await db.LeaseDocuments.AsNoTracking().SingleAsync(d => d.Id == documentId);
    }

    private async Task SetStartedAsync(Guid documentId, DateTime startedAt)
    {
        await using var db = _harness.SecondContext();
        var document = await db.LeaseDocuments.SingleAsync(d => d.Id == documentId);
        document.ExtractionStartedAt = startedAt;
        await db.SaveChangesAsync();
    }

    private void SetStatus(Guid leaseId, LeaseStatus status)
    {
        using var db = _harness.SecondContext();
        var lease = db.LeaseContracts.Single(l => l.Id == leaseId);
        lease.Status = status;
        db.SaveChanges();
    }

    private void ImportMakesTheLeaseSigned(Guid leaseId) =>
        _signing
            .Setup(s => s.ImportSignedContractFromDocumentAsync(leaseId, Actor, It.IsAny<ImportedDocumentDeclaration>(), It.IsAny<CancellationToken>()))
            .Callback(() => SetStatus(leaseId, LeaseStatus.Signed))
            .ReturnsAsync(new LeaseContract());

    private void RegistrationMakesTheLeaseRegistered(Guid leaseId) =>
        _registrations
            .Setup(r => r.DeclareManualRegistrationFromDocumentAsync(leaseId, Actor, It.IsAny<ManualRegistrationDocumentDeclaration>(), It.IsAny<CancellationToken>()))
            .Callback(() => SetStatus(leaseId, LeaseStatus.Registered))
            .ReturnsAsync(new LeaseRegistration());

    /// <summary>The status API says the one practice of the lease is in <paramref name="status"/>.</summary>
    private void SetupPractice(CfPracticeStatus status) =>
        _statuses
            .Setup(s => s.GetPracticesAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Practices(Practice("P-0123456789abcdef", status, TimeSpan.Zero)));

    private static PracticeDto Practice(string id, CfPracticeStatus status, TimeSpan age) =>
        new() { PracticeId = id, Status = status, StatusChangedAt = DateTime.UtcNow + age };

    private static CfStatusResult Practices(params PracticeDto[] practices) =>
        new() { Practices = practices, LastCheckedAt = DateTime.UtcNow, NextUpdateAt = DateTime.UtcNow.AddMinutes(15), IsStale = false, IsAvailable = true };

    /// <summary>What the reading of the receipt gives for the document (a digital PDF of the Agenzia delle Entrate, no value of a person).</summary>
    private void SetupReceipt(
        Guid leaseId,
        Guid documentId,
        string outcome = "Registered",
        string? code = "TKM26T012345000AB",
        string? registrationDate = "2026-09-21",
        string? stipulation = "2026-09-19",
        string? number = null,
        string? series = null) =>
        _receipts
            .Setup(r => r.ReadAsync(leaseId, documentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LeaseDocumentReadingDto(
                documentId,
                "Registration",
                LeaseDocumentReadingStates.Read,
                new RegistrationReceiptDto(
                    outcome, code, number, series, registrationDate, "Ufficio di Milano", 7140m, "2026-09-19", "2029-09-18", stipulation,
                    "Locazione agevolata", true, "Bollate", "MI", [])));

    /// <summary>The next step the lease list gives the lease, read from the same statement the list of the long-term area uses.</summary>
    private async Task<LeaseNextStepCode> StepOfAsync(Guid leaseId)
    {
        await using var db = _harness.SecondContext();
        var rows = await new LeaseContractRepository(db).GetSummariesAsync(new HostScope(_harness.OrgId), new LeaseListQuery(), _harness.Clock.TodayInRome());
        return rows.Single(r => r.Id == leaseId).NextStep!.Code;
    }
}
