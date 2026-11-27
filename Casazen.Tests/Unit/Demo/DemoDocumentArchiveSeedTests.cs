using System.Text;
using Casazen.Core.Authorization;
using Casazen.Core.Demo;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Demo;

/// <summary>
/// NF-99, E07/F03/US03: what the showcase writes for the house of the scenario <c>document-archive</c> (Appartamento Brera) is what the archive
/// lists to the owner of the demo org. The house and the payment of its comune are seeded by the real code of the showcase over an InMemory context
/// and a private bucket that keeps what is put in it, and read back through the real sources and the real service of the archive.
/// </summary>
public class DemoDocumentArchiveSeedTests
{
    [Fact]
    public async Task DocumentArchive_TheSeededDemoHouse_ListsItsTestCertificatesAndTheReceiptOfItsTouristTaxPayment_ToTheOwner()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var org = HostScopeScenario.NewOrg("Host", $"host-{suffix}");
        var owner = HostScopeScenario.NewUser($"auth0|titolare-{suffix}", org.Id, UserRole.PropertyOwner);
        var db = HostScopeScenario.NewInMemoryDb();
        db.Orgs.Add(org);
        db.Users.Add(owner);
        await db.SaveChangesAsync();
        var storage = new InMemoryFileStorage();
        var seeding = Seeding(db, storage);

        var template = DemoShowcaseCatalog.Properties.Single(p => p.Scenarios.Contains(DemoShowcaseCatalog.Scenario.DocumentArchive));
        var payment = Assert.Single(DemoShowcaseCatalog.TouristTaxRemittances, t => t.PropertyKey == template.Key);

        // The house as the reset creates it (its details, its compliance fill with the TEST certificates of its region, its marker), without the
        // stays, which the archive does not read, and then the payment of its comune with the invented receipt.
        await seeding.CreateShowcasePropertyAsync(org.Id, owner.Id, template with { Bookings = [] }, CancellationToken.None);
        await seeding.CreateTouristTaxRemittanceAsync(org.Id, owner.Id, payment, CancellationToken.None);
        var house = await db.Properties.AsNoTracking().SingleAsync(p => p.OrgId == org.Id);

        // The owner of the org (the whole org) asks the archive for everything, through the three real sources over the same context and bucket.
        var archive = new DocumentArchiveService(
            [new PropertyDocumentArchiveSource(db, storage), new LeaseDocumentArchiveSource(db, storage), new TouristTaxRemittanceArchiveSource(db, storage)],
            db,
            Microsoft.Extensions.Options.Options.Create(new DocumentArchiveOptions()),
            NullLogger<DocumentArchiveService>.Instance);
        var scope = new HostScope(org.Id);
        var everything = new DocumentArchiveQuery(null, null, null, null, null, null, 1, DocumentArchiveQuery.MaxPageSize);

        var page = await archive.ListAsync(scope, DocumentArchiveWorld.Everything, everything);

        // At least one TEST certificate of the house (the region of Brera asks for some), listed under the house...
        var certificates = page.Items.Where(i => i.Type == ArchiveDocumentType.PropertyDocument).ToList();
        Assert.NotEmpty(certificates);
        Assert.All(certificates, certificate =>
        {
            Assert.Equal(house.Id, certificate.PropertyId);
            Assert.Equal(template.Name, certificate.PropertyName);
            Assert.StartsWith("TEST-", certificate.Title, StringComparison.Ordinal);
            Assert.Equal("application/pdf", certificate.ContentType);
        });

        // ...and the receipt of the one payment of the house to its comune, which belongs to the org and to no property.
        var receipt = Assert.Single(page.Items, i => i.Type == ArchiveDocumentType.TouristTax);
        Assert.Equal(house.City.Trim(), receipt.Title);
        Assert.Null(receipt.PropertyId);
        Assert.Equal("application/pdf", receipt.ContentType);
        Assert.Equal(certificates.Count + 1, page.TotalCount);

        // Each of them opens: the file is the invented PDF that says it is test data, read from the private bucket under the key of its row.
        foreach (var row in new[] { certificates[0], receipt })
        {
            var file = await archive.OpenAsync(scope, DocumentArchiveWorld.Everything, row.Type, row.Id);

            var text = await ReadAsync(file);
            Assert.StartsWith("%PDF", text, StringComparison.Ordinal);
            Assert.Contains(DemoDocumentPdf.Marker, text, StringComparison.Ordinal);
        }
    }

    private static async Task<string> ReadAsync(ArchiveFile file)
    {
        await using (file.Content)
        {
            using var copy = new MemoryStream();
            await file.Content.CopyToAsync(copy);
            return Encoding.ASCII.GetString(copy.ToArray());
        }
    }

    /// <summary>The seeding code of the showcase over <paramref name="db"/> and <paramref name="storage"/>, as <c>DemoComplianceTrafficLightSeedTests</c> builds it.</summary>
    private static DemoEnvironmentService Seeding(AppDbContext db, IFileStorage storage)
    {
        var gate = new Mock<IDemoEnvironmentGate>();
        gate.SetupGet(g => g.IsAvailable).Returns(true);
        var hostEnvironment = new Mock<IHostEnvironment>();
        hostEnvironment.SetupGet(h => h.EnvironmentName).Returns("Staging");
        return new DemoEnvironmentService(
            db,
            storage,
            Mock.Of<IPropertyComplianceStatusService>(),
            Microsoft.Extensions.Options.Options.Create(new ComplianceOptions()),
            gate.Object,
            NullLogger<DemoEnvironmentService>.Instance,
            new FakeTimeProvider(new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero)),
            Mock.Of<IServiceScopeFactory>(),
            Mock.Of<ICrmContactIndex>(),
            Mock.Of<IFeatureFlags>(),
            hostEnvironment.Object);
    }
}
