using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// LT-02 (A7-02, A7-16, A7-20): offline signature and provider events on an in-memory database. The PostgreSQL flow
/// through the API (row lock, storage, authorization) is in <c>LeaseSigningIntegrationTests</c>.
/// </summary>
public class LeaseSigningServiceTests : IDisposable
{
    private const string SessionId = "session-1";
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n% firmato\n%%EOF\n");

    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options);

    private readonly Mock<ILeaseESignService> _provider = new();
    private readonly Mock<IFeatureFlags> _flags = new();
    private readonly Mock<ILeaseTemplateService> _templates = new();
    private readonly Mock<IFileStorage> _storage = new();
    private readonly Mock<IApeComplianceService> _ape = new();
    private readonly List<string> _storedKeys = [];

    public LeaseSigningServiceTests()
    {
        _provider.SetupGet(p => p.IsConfigured).Returns(true);
        _storage
            .Setup(s => s.PutAsync(StorageBucket.Private, It.IsAny<string>(), It.IsAny<Stream>(), "application/pdf", It.IsAny<CancellationToken>()))
            .Callback<StorageBucket, string, Stream, string, CancellationToken>((_, key, _, _, _) => _storedKeys.Add(key))
            .Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task HandleProviderEventAsync_ProviderFlagOff_IgnoresTheEventWithoutCallingTheProvider()
    {
        var lease = await SeedLeaseAsync(LeaseStatus.AwaitingSignature);

        await CreateSut(providerFlag: false).HandleProviderEventAsync("payload");

        _provider.Verify(p => p.ParseWebhookEventAsync(It.IsAny<string>()), Times.Never);
        _provider.Verify(p => p.DownloadSignedDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(LeaseStatus.AwaitingSignature, (await ReloadAsync(lease.Id)).Status);
    }

    [Fact]
    public async Task HandleProviderEventAsync_AllSignedOnRegisteredLease_NeverMovesItBackToSigned()
    {
        var lease = await SeedLeaseAsync(LeaseStatus.Registered, stipula: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
        ProviderEvent(ESignEventKind.AllSigned);

        await CreateSut().HandleProviderEventAsync("payload");

        var stored = await ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Registered, stored.Status);
        Assert.Equal(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), stored.StipulaDate);
        _provider.Verify(p => p.DownloadSignedDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(_db.LeaseEvents.Where(e => e.EventType == LeaseEventType.AllPartiesSigned));
    }

    [Fact]
    public async Task HandleProviderEventAsync_SignerSigned_LeasePartiallySignedAndSignerRecorded()
    {
        var lease = await SeedLeaseAsync(LeaseStatus.AwaitingSignature, withProviderSigners: true);
        var tenant = lease.Parties.Single(p => p.Role == PartyRole.Tenant);
        ProviderEvent(ESignEventKind.SignerSigned, $"signer-{tenant.Id}");

        await CreateSut().HandleProviderEventAsync("payload");

        Assert.Equal(LeaseStatus.PartiallySigned, (await ReloadAsync(lease.Id)).Status);
        var signer = await _db.LeaseSigners.AsNoTracking().SingleAsync(s => s.PartyId == tenant.Id);
        Assert.Equal(LeaseSignerStatus.Signed, signer.Status);
        Assert.Null(signer.SigningUrl);
        var landlord = await _db.LeaseSigners.AsNoTracking().SingleAsync(s => s.PartyId != tenant.Id);
        Assert.Equal(LeaseSignerStatus.Pending, landlord.Status);
        // The event names the party by id, never by email (A7-17).
        var partyEvent = Assert.Single(_db.LeaseEvents.Where(e => e.EventType == LeaseEventType.PartySignedDocument));
        Assert.Equal(tenant.Id.ToString(), partyEvent.Payload);
    }

    [Fact]
    public async Task HandleProviderEventAsync_SignerSignedReplayed_RecordsTheSignatureOnce()
    {
        var lease = await SeedLeaseAsync(LeaseStatus.AwaitingSignature, withProviderSigners: true);
        var tenant = lease.Parties.Single(p => p.Role == PartyRole.Tenant);
        ProviderEvent(ESignEventKind.SignerSigned, $"signer-{tenant.Id}");
        var sut = CreateSut();

        await sut.HandleProviderEventAsync("payload");
        await sut.HandleProviderEventAsync("payload");

        Assert.Single(_db.LeaseEvents.Where(e => e.EventType == LeaseEventType.PartySignedDocument));
    }

    [Fact]
    public async Task HandleProviderEventAsync_AllSignedOnAugustFirstWithStartOctoberFirst_SignedWithDeadlineAugust31()
    {
        // LT-04 (A7-04): signed 1/8 (00:30 in Rome, still 31/7 in UTC), start 1/10 → stipula 1/8, deadline 31/8.
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 7, 31, 22, 30, 0, TimeSpan.Zero));
        var lease = await SeedLeaseAsync(LeaseStatus.PartiallySigned, withProviderSigners: true, start: new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        ProviderEvent(ESignEventKind.AllSigned);
        _provider.Setup(p => p.DownloadSignedDocumentAsync(SessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Pdf));

        await CreateSut(clock: clock).HandleProviderEventAsync("payload");

        var stored = await ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Signed, stored.Status);
        Assert.Equal(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), stored.StipulaDate);
        Assert.Equal(new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc), stored.RegistrationDeadline);
        // The signed PDF is copied into the private bucket: the lease points to our file, never to a provider path.
        var key = Assert.Single(_storedKeys);
        Assert.Equal(key, stored.SignedPdfStoragePath);
        Assert.True(StorageKeys.IsValid(key));
        Assert.All(_db.LeaseSigners.AsNoTracking(), s => Assert.Equal(LeaseSignerStatus.Signed, s.Status));
        var signed = Assert.Single(_db.LeaseEvents.Where(e => e.EventType == LeaseEventType.AllPartiesSigned));
        Assert.Equal(LeaseSigningService.ProviderEventPayload, signed.Payload);
        Assert.Equal(new DateTime(2026, 7, 31, 22, 30, 0, DateTimeKind.Utc), signed.OccurredAt);
    }

    [Fact]
    public async Task HandleProviderEventAsync_AllSignedWithoutAPdf_ThrowsAndLeaseStaysInProgress()
    {
        var lease = await SeedLeaseAsync(LeaseStatus.AwaitingSignature, withProviderSigners: true);
        ProviderEvent(ESignEventKind.AllSigned);
        _provider.Setup(p => p.DownloadSignedDocumentAsync(SessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream("not a pdf"u8.ToArray()));

        await Assert.ThrowsAsync<ESignProviderException>(() => CreateSut().HandleProviderEventAsync("payload"));

        var stored = await ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.AwaitingSignature, stored.Status);
        Assert.Null(stored.SignedPdfStoragePath);
        Assert.Null(stored.StipulaDate);
        Assert.Empty(_storedKeys);
    }

    [Fact]
    public async Task InitiateProviderSigningAsync_ProviderFlagOff_ThrowsConflictWithoutCallingTheProvider()
    {
        var lease = await SeedLeaseAsync(LeaseStatus.Draft);

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => CreateSut(providerFlag: false).InitiateProviderSigningAsync(lease.Id));

        Assert.Equal(LeaseSigningErrorCodes.ProviderUnavailable, ex.Code);
        _provider.Verify(p => p.InitiateSigningAsync(It.IsAny<LeaseContract>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(LeaseStatus.Draft, (await ReloadAsync(lease.Id)).Status);
    }

    [Fact]
    public async Task InitiateProviderSigningAsync_ProviderAvailable_PersistsEveryPartyLink()
    {
        var lease = await SeedLeaseAsync(LeaseStatus.Draft);
        _templates.Setup(t => t.GeneratePdfAsync(It.IsAny<LeaseContract>())).ReturnsAsync(Pdf);
        _provider
            .Setup(p => p.InitiateSigningAsync(It.IsAny<LeaseContract>(), Pdf, It.IsAny<CancellationToken>()))
            .ReturnsAsync((LeaseContract l, byte[] _, CancellationToken _) => new SigningSessionResult(
                SessionId,
                l.Parties.Select(p => new ProviderSigner(p.Id, $"signer-{p.Id}", $"https://esign.invalid/{p.Id}", new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc))).ToList()));

        var signers = await CreateSut().InitiateProviderSigningAsync(lease.Id);

        Assert.Equal(2, signers.Count);
        Assert.All(signers, s =>
        {
            Assert.Equal(LeaseSignatureMethod.Provider, s.Method);
            Assert.Equal(LeaseSignerStatus.Pending, s.Status);
            Assert.StartsWith("https://esign.invalid/", s.SigningUrl, StringComparison.Ordinal);
        });
        var stored = await ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.AwaitingSignature, stored.Status);
        Assert.Equal(SessionId, stored.ExternalSigningSessionId);
        // Persisted (A7-16): a new read returns the same links.
        var reread = await CreateSut().GetSignersAsync(lease.Id);
        Assert.Equal(signers.Select(s => s.SigningUrl), reread.Select(s => s.SigningUrl));
    }

    [Fact]
    public async Task DeclareOfflineSignatureAsync_StipulaInTheFuture_Throws422AndStoresNothing()
    {
        var lease = await SeedLeaseAsync(LeaseStatus.Draft);
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.Zero));

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => CreateSut(clock: clock).DeclareOfflineSignatureAsync(
            lease.Id, "auth0|owner", new OfflineSignatureDeclaration(new DateTime(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc), new MemoryStream(Pdf), Pdf.Length)));

        Assert.Equal(LeaseSigningErrorCodes.StipulaDateInFuture, ex.Code);
        Assert.Empty(_storedKeys);
    }

    [Fact]
    public async Task DeclareOfflineSignatureAsync_NotAPdf_Throws422AndStoresNothing()
    {
        var lease = await SeedLeaseAsync(LeaseStatus.Draft);
        var content = "PK\u0003\u0004 a zip file"u8.ToArray();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => CreateSut().DeclareOfflineSignatureAsync(
            lease.Id, "auth0|owner", new OfflineSignatureDeclaration(new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc), new MemoryStream(content), content.Length)));

        Assert.Equal(LeaseSigningErrorCodes.SignedContractInvalid, ex.Code);
        Assert.Empty(_storedKeys);
        Assert.Equal(LeaseStatus.Draft, (await ReloadAsync(lease.Id)).Status);
    }

    [Fact]
    public async Task DeclareOfflineSignatureAsync_TemplateNotApproved_Throws422AndStoresNothing()
    {
        var lease = await SeedLeaseAsync(LeaseStatus.Draft);
        _templates.Setup(t => t.EnsureFinalContractAvailable(It.IsAny<LeaseContract>()))
            .Throws(new DomainRuleException("contract_template_not_approved", "LeaseContractTemplateNotApproved"));

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => CreateSut().DeclareOfflineSignatureAsync(
            lease.Id, "auth0|owner", new OfflineSignatureDeclaration(new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc), new MemoryStream(Pdf), Pdf.Length)));

        Assert.Equal("contract_template_not_approved", ex.Code);
        Assert.Empty(_storedKeys);
        Assert.Equal(LeaseStatus.Draft, (await ReloadAsync(lease.Id)).Status);
    }

    [Fact]
    public async Task DeclareOfflineSignatureAsync_SignedPdfAndStipula_LeaseSignedEveryPartySignedOffline()
    {
        var lease = await SeedLeaseAsync(LeaseStatus.AwaitingSignature, withProviderSigners: true);
        var stipula = new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc);

        await CreateSut().DeclareOfflineSignatureAsync(
            lease.Id, "auth0|owner", new OfflineSignatureDeclaration(stipula, new MemoryStream(Pdf), Pdf.Length));

        var stored = await ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Signed, stored.Status);
        Assert.Equal(stipula, stored.StipulaDate);
        // min(stipula 20/8, start 1/9) + 30 days.
        Assert.Equal(new DateTime(2026, 9, 19, 0, 0, 0, DateTimeKind.Utc), stored.RegistrationDeadline);
        Assert.Equal("auth0|owner", stored.StipulaDeclaredByUserId);
        Assert.Equal(Assert.Single(_storedKeys), stored.SignedPdfStoragePath);
        Assert.All(_db.LeaseSigners.AsNoTracking(), s =>
        {
            Assert.Equal(LeaseSignatureMethod.Offline, s.Method);
            Assert.Equal(LeaseSignerStatus.Signed, s.Status);
            Assert.Equal(stipula, s.SignedAt);
            Assert.Null(s.SigningUrl);
        });
        var signed = Assert.Single(_db.LeaseEvents.Where(e => e.EventType == LeaseEventType.AllPartiesSigned));
        Assert.Equal(LeaseSigningService.OfflineEventPayload, signed.Payload);
    }

    [Fact]
    public async Task GetSignersAsync_OfflineBeforeUpload_EveryPartyPendingOnPaperOrPdf()
    {
        var lease = await SeedLeaseAsync(LeaseStatus.Draft);

        var signers = await CreateSut().GetSignersAsync(lease.Id);

        Assert.Equal([PartyRole.Landlord, PartyRole.Tenant], signers.Select(s => s.Role));
        Assert.All(signers, s =>
        {
            Assert.Equal(LeaseSignatureMethod.Offline, s.Method);
            Assert.Equal(LeaseSignerStatus.Pending, s.Status);
            Assert.Null(s.SigningUrl);
            Assert.False(s.SigningUrlExpired);
        });
    }

    [Fact]
    public async Task GetSignersAsync_ProviderLinkPastItsExpiry_FlaggedExpired()
    {
        var lease = await SeedLeaseAsync(LeaseStatus.AwaitingSignature, withProviderSigners: true, linkExpiresAt: new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc));

        var signers = await CreateSut().GetSignersAsync(lease.Id);

        Assert.All(signers, s =>
        {
            Assert.Equal(LeaseSignatureMethod.Provider, s.Method);
            Assert.True(s.SigningUrlExpired);
            Assert.NotNull(s.SigningUrl);
        });
    }

    [Fact]
    public async Task GetSigningStateAsync_TemplateNotApprovedAndProviderNotConfigured_OfflineWithTheBlocker()
    {
        var lease = await SeedLeaseAsync(LeaseStatus.Draft);
        _provider.SetupGet(p => p.IsConfigured).Returns(false);
        _templates.Setup(t => t.GetFinalContractBlocker(It.IsAny<LeaseContract>())).Returns("contract_template_not_approved");

        var state = await CreateSut(providerFlag: true).GetSigningStateAsync(lease.Id);

        // Flag on but no configured provider: the provider path does not exist (LT-02).
        Assert.False(state.ProviderSigningAvailable);
        Assert.False(state.ContractAvailable);
        Assert.Equal("contract_template_not_approved", state.ContractUnavailableCode);
        Assert.Equal(2, state.Signers.Count);
    }

    [Fact]
    public async Task GetSigningStateAsync_SignedLease_ContractNoLongerAvailable()
    {
        var lease = await SeedLeaseAsync(LeaseStatus.Signed, stipula: new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc));

        var state = await CreateSut(providerFlag: false).GetSigningStateAsync(lease.Id);

        Assert.False(state.ProviderSigningAvailable);
        Assert.False(state.ContractAvailable);
        Assert.Equal(LeaseSigningErrorCodes.AlreadySigned, state.ContractUnavailableCode);
        Assert.All(state.Signers, s => Assert.Equal(LeaseSignerStatus.Signed, s.Status));
        _templates.Verify(t => t.GetFinalContractBlocker(It.IsAny<LeaseContract>()), Times.Never);
    }

    [Fact]
    public async Task DeclareStipulaAsync_SignedLeaseWithoutStipula_RecordsItAndTheDeadline()
    {
        var lease = await SeedLeaseAsync(LeaseStatus.Signed);

        await CreateSut().DeclareStipulaAsync(lease.Id, "auth0|owner", new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc));

        var stored = await ReloadAsync(lease.Id);
        Assert.Equal(new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc), stored.StipulaDate);
        Assert.Equal(new DateTime(2026, 9, 19, 0, 0, 0, DateTimeKind.Utc), stored.RegistrationDeadline);
        Assert.Equal(LeaseStatus.Signed, stored.Status);
        Assert.Single(_db.LeaseEvents.Where(e => e.EventType == LeaseEventType.StipulaDeclared));
    }

    [Fact]
    public async Task DeclareStipulaAsync_DraftLease_Throws422()
    {
        var lease = await SeedLeaseAsync(LeaseStatus.Draft);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => CreateSut().DeclareStipulaAsync(
            lease.Id, "auth0|owner", new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc)));

        Assert.Equal(LeaseSigningErrorCodes.StipulaLeaseNotSigned, ex.Code);
    }

    private LeaseSigningService CreateSut(bool providerFlag = true, TimeProvider? clock = null)
    {
        _flags.Setup(f => f.IsEnabled(FeatureFlags.ESignProvider)).Returns(providerFlag);
        return new LeaseSigningService(
            _db,
            _provider.Object,
            _flags.Object,
            _templates.Object,
            _storage.Object,
            _ape.Object,
            Mock.Of<ILogger<LeaseSigningService>>(),
            clock ?? new FixedTimeProvider(new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.Zero)));
    }

    private void ProviderEvent(ESignEventKind kind, string? signerId = null) =>
        _provider.Setup(p => p.ParseWebhookEventAsync("payload")).ReturnsAsync(new ESignEvent(SessionId, kind, signerId));

    private async Task<LeaseContract> SeedLeaseAsync(
        LeaseStatus status,
        bool withProviderSigners = false,
        DateTime? stipula = null,
        DateTime? start = null,
        DateTime? linkExpiresAt = null)
    {
        var orgId = Guid.NewGuid();
        var property = new Property { Id = Guid.NewGuid(), OwnerId = "auth0|owner", OrgId = orgId, Name = "Casa" };
        var lease = new LeaseContract
        {
            PropertyId = property.Id,
            Property = property,
            OrgId = orgId,
            Status = status,
            FiscalRegime = FiscalRegime.CedolareSecca,
            StartDate = start ?? new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2030, 8, 31, 0, 0, 0, DateTimeKind.Utc),
            MonthlyRent = 1200m,
            ExternalSigningSessionId = withProviderSigners ? SessionId : null,
            Parties =
            [
                new Party { Role = PartyRole.Landlord, FirstName = "Mario", LastName = "Rossi", FiscalCode = "RSSMRA80A01H501Z", Citizenship = "IT", ContactEmail = "mario@example.com" },
                new Party { Role = PartyRole.Tenant, FirstName = "Giulia", LastName = "Verdi", FiscalCode = "VRDGLI85B02F205X", Citizenship = "IT", ContactEmail = "giulia@example.com" },
            ],
        };
        if (stipula is { } date)
            lease.RecordStipula(date);

        _db.Properties.Add(property);
        _db.LeaseContracts.Add(lease);
        if (withProviderSigners)
        {
            foreach (var party in lease.Parties)
            {
                _db.LeaseSigners.Add(new LeaseSigner
                {
                    OrgId = orgId,
                    LeaseContractId = lease.Id,
                    PartyId = party.Id,
                    Method = LeaseSignatureMethod.Provider,
                    Status = LeaseSignerStatus.Pending,
                    ExternalSignerId = $"signer-{party.Id}",
                    SigningUrl = $"https://esign.invalid/{party.Id}",
                    SigningUrlExpiresAt = linkExpiresAt ?? new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
                });
            }
        }

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return lease;
    }

    private Task<LeaseContract> ReloadAsync(Guid leaseId) =>
        _db.LeaseContracts.AsNoTracking().SingleAsync(l => l.Id == leaseId);
}
