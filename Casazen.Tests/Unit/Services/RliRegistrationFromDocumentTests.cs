using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using static Casazen.Tests.Unit.Services.OrgTeamTestData;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// CF-END: a contract made with the guided service is registered by the service, which sends the receipt; the landlord uploads it among the
/// documents of the lease and then declares the registration with <b>that</b> document, without sending the file again. The one rule that
/// costs if it fails: the document has to be a registration document of <b>this</b> lease and of its org, never another's.
/// </summary>
public sealed class RliRegistrationFromDocumentTests
{
    private const string OwnerId = "auth0|titolare";

    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n% la ricevuta di registrazione\n%%EOF\n");

    private static readonly DateTime RegisteredOn = new(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);

    private sealed record World(AppDbContext Db, Guid OrgId, Guid LeaseId, Guid OtherLeaseId, Mock<IFileStorage> Storage, RliRegistrationService Service);

    private static async Task<World> SeedAsync()
    {
        var db = NewDb();
        var org = AddOrg(db);
        var property = HostScopeScenario.NewProperty(org.Id, OwnerId, "Trullo");
        db.Properties.Add(property);
        var lease = SignedLease(org.Id, property.Id);
        var other = SignedLease(org.Id, property.Id);
        db.LeaseContracts.AddRange(lease, other);
        await db.SaveChangesAsync();

        var storage = new Mock<IFileStorage>();
        storage
            .Setup(s => s.OpenReadAsync(StorageBucket.Private, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Pdf));

        var provider = new Mock<ILeaseRegistrationProvider>();
        provider.SetupGet(p => p.IsConfigured).Returns(false);
        var service = new RliRegistrationService(
            db,
            provider.Object,
            Mock.Of<IFeatureFlags>(),
            storage.Object,
            Mock.Of<IApeComplianceService>(),
            Options.Create(new RliOptions()),
            new OrgHolderService(db),
            NullLogger<RliRegistrationService>.Instance,
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 25, 10, 0, 0, TimeSpan.Zero)));
        return new World(db, org.Id, lease.Id, other.Id, storage, service);
    }

    private static LeaseContract SignedLease(Guid orgId, Guid propertyId) => new()
    {
        OrgId = orgId,
        PropertyId = propertyId,
        Status = LeaseStatus.Signed,
        FiscalRegime = FiscalRegime.CedolareSecca,
        StartDate = HostScopeScenario.Day(2026, 10, 1),
        EndDate = HostScopeScenario.Day(2029, 9, 30),
        MonthlyRent = 800m,
    };

    private static LeaseDocument Document(World world, Guid leaseId, LeaseDocumentKind kind = LeaseDocumentKind.Registration, Guid? orgId = null)
    {
        var document = new LeaseDocument
        {
            LeaseContractId = leaseId,
            OrgId = orgId ?? world.OrgId,
            Kind = kind,
            FileName = "ricevuta.pdf",
            StorageKey = $"orgs/{orgId ?? world.OrgId}/leases/{leaseId}/documents/{Guid.NewGuid():N}.pdf",
            UploadedBy = OwnerId,
            SizeBytes = Pdf.Length,
        };
        world.Db.LeaseDocuments.Add(document);
        return document;
    }

    [Fact]
    public async Task TheReceiptAlreadyUploaded_IsDeclaredWithoutSendingItAgain_AndTheRegistrationKeepsAFileOfItsOwn()
    {
        var world = await SeedAsync();
        var document = Document(world, world.LeaseId);
        await world.Db.SaveChangesAsync();

        var registration = await world.Service.DeclareManualRegistrationFromDocumentAsync(
            world.LeaseId, OwnerId, new ManualRegistrationDocumentDeclaration(" 24101234567890123-000001 ", RegisteredOn, document.Id));

        Assert.Equal(RegistrationStatus.Registered, registration.Status);
        Assert.Equal(RegistrationChannel.Manual, registration.Channel);
        Assert.Equal("24101234567890123-000001", registration.RegistrationCode);
        Assert.NotNull(registration.ReceiptStoragePath);
        // Two files, one owner each: deleting the document never takes the receipt of the registration away, nor the other way round.
        Assert.StartsWith($"leases/{world.OrgId}/{world.LeaseId}/registration/", registration.ReceiptStoragePath);
        Assert.NotEqual(document.StorageKey, registration.ReceiptStoragePath);
        world.Storage.Verify(
            s => s.PutAsync(StorageBucket.Private, registration.ReceiptStoragePath!, It.IsAny<Stream>(), "application/pdf", It.IsAny<CancellationToken>()),
            Times.Once);
        world.Storage.Verify(
            s => s.PutAsync(It.IsAny<StorageBucket>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Equal(LeaseStatus.Registered, (await world.Db.LeaseContracts.AsNoTracking().SingleAsync(l => l.Id == world.LeaseId)).Status);
        Assert.Equal(document.StorageKey, (await world.Db.LeaseDocuments.AsNoTracking().SingleAsync(d => d.Id == document.Id)).StorageKey); // the document keeps its file
    }

    [Fact]
    public async Task ADocumentThatIsNotARegistrationDocumentOfThisLease_IsNotThere_AndTheLeaseStaysSigned()
    {
        var world = await SeedAsync();
        var ofAnotherOrg = Document(world, world.LeaseId, orgId: Guid.NewGuid());
        var ofAnotherLease = Document(world, world.OtherLeaseId);
        var contract = Document(world, world.LeaseId, LeaseDocumentKind.Contract);
        await world.Db.SaveChangesAsync();

        // DATA RULE PROOF (tenancy): neither the receipt of another lease, nor a document that names another org, nor a document of
        // another kind, nor an id that is nothing can be the receipt of this registration: all the same «not there», and no file is read.
        foreach (var id in new[] { ofAnotherOrg.Id, ofAnotherLease.Id, contract.Id, Guid.NewGuid() })
        {
            var refused = await Assert.ThrowsAsync<NotFoundException>(
                () => world.Service.DeclareManualRegistrationFromDocumentAsync(
                    world.LeaseId, OwnerId, new ManualRegistrationDocumentDeclaration("PROT-1", RegisteredOn, id)));
            Assert.Equal("document_not_found", refused.Code);
        }

        world.Storage.Verify(
            s => s.OpenReadAsync(It.IsAny<StorageBucket>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        world.Storage.Verify(
            s => s.PutAsync(It.IsAny<StorageBucket>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.Equal(LeaseStatus.Signed, (await world.Db.LeaseContracts.AsNoTracking().SingleAsync(l => l.Id == world.LeaseId)).Status);
        Assert.Empty(await world.Db.LeaseRegistrations.AsNoTracking().ToListAsync());
    }
}
