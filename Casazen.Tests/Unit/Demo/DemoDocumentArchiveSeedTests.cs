using System.Text;
using Casazen.Core.Authorization;
using Casazen.Core.Demo;
using Casazen.Core.Entities;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
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

        await seeding.CreateShowcasePropertyAsync(org.Id, owner.Id, template with { Bookings = [] }, CancellationToken.None);
        await seeding.CreateTouristTaxRemittanceAsync(org.Id, owner.Id, payment, CancellationToken.None);
        var house = await db.Properties.AsNoTracking().SingleAsync(p => p.OrgId == org.Id);

        var archive = new DocumentArchiveService(
            [new PropertyDocumentArchiveSource(db, storage), new LeaseDocumentArchiveSource(db, storage), new TouristTaxRemittanceArchiveSource(db, storage)],
            db,
            Microsoft.Extensions.Options.Options.Create(new DocumentArchiveOptions()),
            NullLogger<DocumentArchiveService>.Instance);
        var scope = new HostScope(org.Id);
        var everything = new DocumentArchiveQuery(null, null, null, null, null, null, 1, DocumentArchiveQuery.MaxPageSize);

        var page = await archive.ListAsync(scope, DocumentArchiveWorld.Everything, everything);

        var certificates = page.Items.Where(i => i.Type == ArchiveDocumentType.PropertyDocument).ToList();
        Assert.NotEmpty(certificates);
        Assert.All(certificates, certificate =>
        {
            Assert.Equal(house.Id, certificate.PropertyId);
            Assert.Equal(template.Name, certificate.PropertyName);
            Assert.StartsWith("TEST-", certificate.Title, StringComparison.Ordinal);
            Assert.Equal("application/pdf", certificate.ContentType);
        });

        var receipt = Assert.Single(page.Items, i => i.Type == ArchiveDocumentType.TouristTax);
        Assert.Equal(house.City.Trim(), receipt.Title);
        Assert.Null(receipt.PropertyId);
        Assert.Equal("application/pdf", receipt.ContentType);
        Assert.Equal(certificates.Count + 1, page.TotalCount);

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
