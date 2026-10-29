using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SR-07a: what the host's list says is missing is what the activation wizard says — one set of rules, read once for the whole page
/// and applied to each property — and the list describes only the properties it was given, with the nights of the active ones.
/// </summary>
public class PropertyOverviewServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 29, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task GetBlockingStepsManyAsync_SaysForEachPropertyWhatTheWizardSaysForItAlone()
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db);
        var properties = new List<Property>
        {
            await SeedAsync(db, org, "Completa", cin: "IT058091C27G5FFZDZ", documents: [DocumentType.CinCertificate], safety: true),
            await SeedAsync(db, org, "Senza CIN", cin: null, documents: [DocumentType.CinCertificate], safety: true),
            await SeedAsync(db, org, "CIN non valido", cin: "IT-NO", documents: [DocumentType.CinCertificate], safety: true),
            await SeedAsync(db, org, "Senza documenti", cin: "IT058091C27G5FFZDZ", documents: [], safety: true),
            // Lombardia asks for the APE as well: the certificate alone is not enough there.
            await SeedAsync(db, org, "Senza APE", cin: "IT058091C27G5FFZDZ", documents: [DocumentType.CinCertificate], safety: true, region: "LOM"),
            await SeedAsync(db, org, "Senza sicurezza", cin: "IT058091C27G5FFZDZ", documents: [DocumentType.CinCertificate], safety: false),
            await SeedAsync(db, org, "Senza dati base", cin: null, documents: [], safety: false, guests: 0, rate: 0m),
        };
        var service = StatusService(db);

        var many = await service.GetBlockingStepsManyAsync(properties);

        Assert.Equal(properties.Count, many.Count);
        foreach (var property in properties)
        {
            var alone = await service.GetBlockingStepsAsync(property);
            Assert.Equal(Shape(alone), Shape(many[property.Id]));
        }

        // The set is varied on purpose: the shapes are not all the same one.
        Assert.True(many.Values.Select(Shape).Distinct().Count() >= 6);
    }

    [Fact]
    public async Task DescribeAsync_DescribesOnlyTheGivenProperties_NightsOnlyForTheActiveOnes_AndTheLongTermOneByItsPhotos()
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db);
        var active = await SeedAsync(db, org, "Attiva", cin: "IT058091C27G5FFZDZ", documents: [DocumentType.CinCertificate], safety: true);
        var pending = await SeedAsync(db, org, "Da completare", cin: null, documents: [], safety: false);
        var suspended = await SeedAsync(db, org, "Sospesa", cin: null, documents: [], safety: false);
        var longTerm = await SeedAsync(db, org, "Lungo", cin: null, documents: [], safety: false);
        var elsewhere = await SeedAsync(db, org, "Fuori dall'elenco", cin: null, documents: [], safety: false);
        await UpdateAsync(db, active.Id, p =>
        {
            p.ComplianceStatus = PropertyComplianceStatus.Active;
            p.PhotoUrls = ["1.jpg", "2.jpg", "3.jpg", "4.jpg", "5.jpg"];
        });
        await UpdateAsync(db, suspended.Id, p =>
        {
            p.ComplianceStatus = PropertyComplianceStatus.Suspended;
            p.ComplianceSuspensionReasons = ["activation_cin_missing"];
        });
        await UpdateAsync(db, longTerm.Id, p =>
        {
            p.RentalMode = RentalMode.Long;
            p.PhotoUrls = ["cover.jpg", "2.jpg"];
        });
        db.PropertyICalFeeds.Add(new PropertyICalFeed { PropertyId = active.Id, OrgId = org, ImportUrl = "https://example.test/cal.ics" });
        await db.SaveChangesAsync();

        // 15 nights taken out of 24 available: 62.5 %, which the list says as 63.
        var dashboard = new Mock<IHostDashboardService>();
        dashboard
            .Setup(d => d.GetLast30DaysOccupancyAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) =>
                ids.ToDictionary(id => id, _ => new HostDashboardOccupancy(15, 24, 6)));
        var service = new PropertyOverviewService(db, StatusService(db), dashboard.Object);

        var given = await db.Properties.AsNoTracking().Where(p => p.Id != elsewhere.Id).OrderBy(p => p.Name).ToListAsync();
        var overviews = await service.DescribeAsync(given);

        // The same rows, in the same order, and nothing about the property left out of the list.
        Assert.Equal(given.Select(p => p.Id), overviews.Select(o => o.PropertyId));
        var byId = overviews.ToDictionary(o => o.PropertyId);
        Assert.DoesNotContain(elsewhere.Id, byId.Keys);

        var a = byId[active.Id];
        Assert.Equal(PropertyLifecycleStatus.Active, a.LifecycleStatus);
        Assert.Equal(PropertySiteVisibility.Published, a.SiteVisibility);
        Assert.Equal(63, a.Occupancy30);
        Assert.Equal(("1.jpg", 5), (a.CoverPhotoUrl, a.PhotoCount));
        Assert.Empty(a.Missing!);

        var p = byId[pending.Id];
        Assert.Equal(PropertyLifecycleStatus.NeedsAttention, p.LifecycleStatus);
        Assert.Null(p.Occupancy30);
        Assert.Equal(
            new[] { "cin", "documents", "safety", "photos", "calendar" },
            p.Missing!.Select(m => m.Code).ToArray());
        Assert.Equal(new[] { true, true, true, false, false }, p.Missing!.Select(m => m.Blocking).ToArray());

        var s = byId[suspended.Id];
        Assert.Equal(PropertyLifecycleStatus.Suspended, s.LifecycleStatus);
        Assert.Equal(new[] { "activation_cin_missing" }, s.SuspensionReasons!.ToArray());
        Assert.Null(s.Occupancy30);

        var l = byId[longTerm.Id];
        Assert.Null(l.LifecycleStatus);
        Assert.Null(l.Missing);
        Assert.Null(l.SiteVisibility);
        Assert.Equal(("cover.jpg", 2), (l.CoverPhotoUrl, l.PhotoCount));

        // The nights are asked for the one active property of the page, no other.
        dashboard.Verify(
            d => d.GetLast30DaysOccupancyAsync(
                It.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { active.Id })), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // What an API client sees of a step: everything that makes two answers differ.
    private static string Shape(IEnumerable<ComplianceActivationStep> steps) =>
        string.Join(
            " | ",
            steps.Select(step => string.Join(
                ";",
                step.Id,
                step.Status,
                step.Blocker,
                step.MessageKey,
                string.Join(",", step.MessageArgs ?? []),
                string.Join(",", step.Blockers.Select(b => $"{b.Code}({string.Join("/", b.MessageArgs)})")),
                string.Join(",", step.Warnings.Select(w => w.Code)))));

    private static PropertyComplianceStatusService StatusService(AppDbContext db) =>
        new(
            db,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Compliance:RequiredDocuments:default:0"] = "CinCertificate",
                    ["Compliance:RequiredDocuments:LOM:0"] = "CinCertificate",
                    ["Compliance:RequiredDocuments:LOM:1"] = "Ape",
                })
                .Build(),
            new RecordingEmailQueue(),
            EmailTestHelpers.Links(),
            Options.Create(new ComplianceOptions()),
            NullLogger<PropertyComplianceStatusService>.Instance,
            new FixedTimeProvider(new DateTimeOffset(Now)));

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<Guid> SeedOrgAsync(AppDbContext db)
    {
        var org = new OrgEntity
        {
            Name = "Org",
            Slug = $"org-{Guid.NewGuid():N}",
            ContactEmail = "host@villa.test",
        };
        db.Orgs.Add(org);
        await db.SaveChangesAsync();
        return org.Id;
    }

    private static async Task<Property> SeedAsync(
        AppDbContext db,
        Guid org,
        string name,
        string? cin,
        DocumentType[] documents,
        bool safety,
        string? region = null,
        int guests = 4,
        decimal rate = 100m)
    {
        var property = new Property
        {
            OrgId = org,
            OwnerId = "auth0|owner",
            Name = name,
            Address = $"Via Roma {Guid.NewGuid():N}",
            City = "Como",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = guests,
            NightlyRate = rate,
            CinCode = cin,
            RegionCode = region,
            IsActive = true,
        };
        db.Properties.Add(property);
        foreach (var type in documents)
        {
            db.PropertyDocuments.Add(new PropertyDocument
            {
                PropertyId = property.Id,
                OrgId = org,
                FileName = $"{type}.pdf",
                StorageUrl = $"documents/{type}.pdf",
                DocumentType = type,
                UploadedBy = property.OwnerId,
            });
        }

        if (safety)
            db.PropertySafetyChecklists.Add(SafetyChecklistTestData.CompleteAllElectric(property.Id, org));

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return property;
    }

    private static async Task UpdateAsync(AppDbContext db, Guid propertyId, Action<Property> change)
    {
        var property = await db.Properties.SingleAsync(p => p.Id == propertyId);
        change(property);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }
}
