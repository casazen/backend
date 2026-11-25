using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.SiteProfile;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

public sealed class PropertyImportActivationTests
{
    [Fact]
    public async Task Import_ReusesTodoAndPreservesHostEditsAndObservedPrices()
    {
        await using var db = Database();
        var property = AddProperty(db, Guid.NewGuid());
        property.Description = string.Empty;
        property.NightlyRate = 0;
        await db.SaveChangesAsync();
        var service = new PropertyImportReviewService(db, TimeProvider.System);
        var source = "https://quokka.kross.travel/lario-suite-brienno";
        var description = new string('d', 10_000);
        var snapshot = new PropertyImportSnapshot("Kross", "lario-suite-brienno", source, null, description, description,
            70.83m, "EUR",
            [
                new("nr", "Non rimborsabile", "EUR", "night", 2, 70.83m, new(2026, 12, 10), new(2026, 12, 12), "Non rimborsabile", null, null, null, null),
                new("std", "Standard", "EUR", "night", 2, 75.35m, new(2026, 12, 10), new(2026, 12, 12), null, null, null, null, null),
                new("flex", "Super Flex", "EUR", "night", 2, 82.89m, new(2026, 12, 10), new(2026, 12, 12), null, null, null, null, null),
            ],
            [new("beds", "confirm", "La descrizione e i dati strutturati differiscono.", source)],
            new Dictionary<string, string> { ["unmapped"] = "source value" });
        var scope = new HostScope(property.OrgId);

        var review = await service.RecordAsync(property.OrgId, property.Id, snapshot, CancellationToken.None);
        Assert.Equal(3, review.Snapshot.RatePlans.Count);
        Assert.Equal(description, property.Description);
        Assert.Equal(description, SitePropertyProfileJson.Parse(property.SiteProfileJson).Translations["en"].Description);
        Assert.Equal(70.83m, property.NightlyRate);
        Assert.Single(review.OpenFindings);

        property.NightlyRate = 110;
        property.Description = "Valore modificato dall'host";
        await db.SaveChangesAsync();
        await service.RecordAsync(property.OrgId, property.Id, snapshot with { NightlyRate = 80, DescriptionIt = "Nuovo valore sorgente" }, CancellationToken.None);

        Assert.Single(await db.InternalActivities.ToListAsync());
        Assert.Equal(110m, property.NightlyRate);
        Assert.Equal("Valore modificato dall'host", property.Description);
        await Assert.ThrowsAsync<NotFoundException>(() => service.GetAsync(new HostScope(Guid.NewGuid()), property.Id, CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => service.GetAsync(new HostScope(property.OrgId, "a-different-owner"), property.Id, CancellationToken.None));

        var confirmed = await service.RecheckAsync(scope, property.Id, "beds", CancellationToken.None);
        Assert.Empty(confirmed.OpenFindings);
        Assert.Equal(InternalActivity.StatusCompleted, (await db.InternalActivities.SingleAsync()).Status);
    }

    [Fact]
    public async Task SharedBoundary_OnlyAuthorizesTheAssignedPropertyOfEachOrganization()
    {
        await using var db = Database();
        var master = Guid.NewGuid();
        var first = AddProperty(db, Guid.NewGuid());
        var second = AddProperty(db, Guid.NewGuid());
        foreach (var (property, id) in new[] { (first, "101"), (second, "202") })
        {
            var connection = new OctorateConnection
            {
                OrgId = property.OrgId,
                UsesMasterAccount = true,
                Environment = OctorateEnvironment.Test,
                Status = OctorateConnectionStatus.Connected,
            };
            db.OctorateConnections.Add(connection);
            db.OctorateListingMappings.Add(new OctorateListingMapping
            {
                OrgId = property.OrgId,
                ConnectionId = connection.Id,
                PropertyId = property.Id,
                OctorateAccommodationId = id,
                Status = OctorateMappingStatus.Confirmed,
            });
        }
        await db.SaveChangesAsync();
        var host = new Mock<IHostEnvironment>();
        host.SetupGet(h => h.EnvironmentName).Returns("Staging");
        var boundary = new OctorateMasterBoundary(db, Options.Create(new OctorateOptions { MasterOrganizationId = master }), host.Object);

        Assert.Equal(["101"], await boundary.AssignedAsync(first.OrgId, CancellationToken.None) ?? []);
        Assert.Equal(["202"], await boundary.AssignedAsync(second.OrgId, CancellationToken.None) ?? []);
        Assert.Equal(master, await boundary.AuthorizeAsync(first.OrgId, HttpMethod.Get,
            new Uri("https://api.example.test/connect/rest/v1/connection?accommodation=101"), CancellationToken.None));
        await Assert.ThrowsAsync<OctorateAuthException>(() => boundary.AuthorizeAsync(first.OrgId, HttpMethod.Post,
            new Uri("https://api.example.test/connect/rest/v1/connection/IMPORT_ROOMS/202/9"), CancellationToken.None));
        await Assert.ThrowsAsync<OctorateAuthException>(() => boundary.AuthorizeAsync(first.OrgId, HttpMethod.Get,
            new Uri("https://api.example.test/connect/rest/v1/accommodation?page=0"), CancellationToken.None));
    }

    [Fact]
    public async Task PreparationCandidates_ShowExistingListingsWithoutImportAndExcludeMasterMappedAndQueuedListings()
    {
        await using var db = Database();
        var org = new Org { Name = "Quokka360", Slug = "quokka360", ContactEmail = "booking@example.com" };
        var master = new Org { Name = "Master", Slug = "master" };
        db.Orgs.AddRange(org, master);
        AddProperty(db, master.Id).Name = "Master property";
        var candidate = AddProperty(db, org.Id);
        AddProperty(db, org.Id).PublishedOnSite = false;
        var linked = AddProperty(db, org.Id);
        var queued = AddProperty(db, org.Id);
        var connection = new OctorateConnection { OrgId = org.Id, Environment = OctorateEnvironment.Test };
        db.OctorateConnections.Add(connection);
        db.OctorateListingMappings.Add(new OctorateListingMapping
        {
            OrgId = org.Id, ConnectionId = connection.Id, PropertyId = linked.Id,
            Status = OctorateMappingStatus.Confirmed, OctorateAccommodationId = "101",
        });
        db.OctoratePreparations.Add(new OctoratePreparation
        {
            BatchId = Guid.NewGuid(), OrgId = org.Id, PropertyId = queued.Id, Environment = OctorateEnvironment.Test,
        });
        await db.SaveChangesAsync();
        var host = new Mock<IHostEnvironment>();
        host.SetupGet(h => h.EnvironmentName).Returns("Staging");
        var service = new OctoratePreparationService(db, Mock.Of<IOctorateApiClient>(), Mock.Of<IOctorateOrgScope>(), null!,
            Options.Create(new OctorateOptions { MasterOrganizationId = master.Id }), host.Object, TimeProvider.System);

        var result = Assert.Single((await service.CandidatesAsync(null, CancellationToken.None)).Items);
        Assert.Equal((candidate.Id, "Lario Suite", "Quokka360"), (result.PropertyId, result.PropertyName, result.OrganizationName));
        Assert.Equal(["location", "contact.phone"], result.MissingFields);
        Assert.Single((await service.CandidatesAsync("QUOKKA", CancellationToken.None)).Items);
        Assert.Empty((await service.CandidatesAsync("unmatched", CancellationToken.None)).Items);
    }

    private static AppDbContext Database() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static Property AddProperty(AppDbContext db, Guid orgId)
    {
        var property = new Property
        {
            OrgId = orgId,
            OwnerId = "owner",
            Name = "Lario Suite",
            Address = "Via del Lago 1",
            City = "Brienno",
            PostalCode = "22010",
            Country = "IT",
            Currency = "EUR",
            PhotoUrls = ["https://example.test/photo.jpg"],
            MaxGuests = 2,
            Beds = 1,
            CinCode = "IT015146A12HOLV2MZ",
        };
        db.Properties.Add(property);
        return property;
    }
}
