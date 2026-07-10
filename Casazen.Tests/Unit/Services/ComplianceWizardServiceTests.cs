using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class ComplianceWizardServiceTests
{
    private static AppDbContext CreateDb(string dbName)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new AppDbContext(options);
    }

    private static IConfiguration CreateConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Compliance:CinGuidanceUrl"] = "https://www.bdsr.it/cin",
                ["Compliance:RequiredDocuments:default:0"] = "CinCertificate",
                ["Compliance:RequiredDocuments:default:1"] = "SafetyCompliance",
                ["Compliance:GdprRetentionYears"] = "7",
            })
            .Build();

    private static ComplianceWizardService CreateService(AppDbContext db)
    {
        var alloggiati = new Mock<IAlloggiatiWebService>();
        alloggiati.Setup(a => a.ValidateGuestDataAsync(It.IsAny<Guid>())).ReturnsAsync(false);

        return new ComplianceWizardService(
            db,
            CreateConfig(),
            alloggiati.Object,
            Mock.Of<IServiceRequestService>(),
            Mock.Of<ILogger<ComplianceWizardService>>());
    }

    [Fact]
    public async Task Activation_CinMissing_ReturnsPendingCinStep()
    {
        await using var db = CreateDb(nameof(Activation_CinMissing_ReturnsPendingCinStep));
        var property = await SeedPropertyAsync(db, cinCode: null);

        var (_, steps) = await CreateService(db).GetActivationWizardAsync(property.Id);

        var cin = steps.Single(s => s.Id == "cin");
        Assert.Equal("pending", cin.Status);
        Assert.True(cin.Blocker);
    }

    [Fact]
    public async Task Activation_CinValid_MarksCinComplete()
    {
        await using var db = CreateDb(nameof(Activation_CinValid_MarksCinComplete));
        var property = await SeedPropertyAsync(db, cinCode: "IT-12345-0123456789");

        var (_, steps) = await CreateService(db).GetActivationWizardAsync(property.Id);

        var cin = steps.Single(s => s.Id == "cin");
        Assert.Equal("complete", cin.Status);
    }

    [Fact]
    public async Task CompleteActivation_AllBlockersMet_SetsActive()
    {
        await using var db = CreateDb(nameof(CompleteActivation_AllBlockersMet_SetsActive));
        var property = await SeedFullyCompliantPropertyAsync(db);

        var (updated, blockers) = await CreateService(db).CompleteActivationAsync(
            property.Id,
            property.OwnerId,
            new PropertySafetyChecklistInput(true, true, true, property.OwnerId),
            tosAccepted: true);

        Assert.Empty(blockers);
        Assert.Equal(PropertyComplianceStatus.Active, updated.ComplianceStatus);
        Assert.NotNull(updated.ComplianceCompletedAt);
    }

    [Fact]
    public async Task CompleteActivation_BlockersRemaining_StaysPending()
    {
        await using var db = CreateDb(nameof(CompleteActivation_BlockersRemaining_StaysPending));
        var property = await SeedPropertyAsync(db, cinCode: null);

        var (updated, blockers) = await CreateService(db).CompleteActivationAsync(
            property.Id,
            property.OwnerId,
            new PropertySafetyChecklistInput(true, true, true, property.OwnerId),
            tosAccepted: true);

        Assert.Contains("cin", blockers);
        Assert.Equal(PropertyComplianceStatus.Pending, updated.ComplianceStatus);
    }

    [Fact]
    public async Task Summary_ReturnsExpectedCounts()
    {
        await using var db = CreateDb(nameof(Summary_ReturnsExpectedCounts));
        var org = new OrgEntity { Name = "Test Org", Slug = $"org-{Guid.NewGuid():N}" };
        db.Orgs.Add(org);

        var pending = await SeedPropertyAsync(db, org.Id, complianceStatus: PropertyComplianceStatus.Pending);
        var active = await SeedFullyCompliantPropertyAsync(db, org.Id);
        active.ComplianceStatus = PropertyComplianceStatus.Active;
        await db.SaveChangesAsync();

        var guest = new Guest
        {
            FirstName = "Mario",
            LastName = "Rossi",
            Email = $"mario-{Guid.NewGuid():N}@test.com",
        };
        db.Guests.Add(guest);

        db.Bookings.Add(new Booking
        {
            PropertyId = pending.Id,
            OrgId = org.Id,
            GuestId = guest.Id,
            CheckInDate = DateTime.UtcNow.Date.AddDays(-2),
            CheckOutDate = DateTime.UtcNow.Date,
            Status = BookingStatus.CheckedIn,
            NumberOfGuests = 2,
            BasePrice = 100,
            TouristTax = 0,
            TotalPrice = 100,
        });

        await db.SaveChangesAsync();

        var summary = await CreateService(db).GetSummaryAsync(org.Id);

        Assert.True(summary.PropertiesPending.Count >= 1);
        Assert.True(summary.CheckoutsDue.Count >= 1);
    }

    private static async Task<Property> SeedPropertyAsync(
        AppDbContext db,
        Guid? orgId = null,
        string? cinCode = "IT-12345-0123456789",
        PropertyComplianceStatus complianceStatus = PropertyComplianceStatus.Pending)
    {
        var org = orgId.HasValue
            ? await db.Orgs.FindAsync(orgId.Value)
            : new OrgEntity { Name = "Org", Slug = $"slug-{Guid.NewGuid():N}" };

        if (org is null)
            throw new InvalidOperationException("Org not found");

        if (!orgId.HasValue)
            db.Orgs.Add(org);

        var property = new Property
        {
            OrgId = org.Id,
            OwnerId = "auth0|owner",
            Name = "Villa Test",
            Address = "Via Roma 1",
            City = "Rome",
            PostalCode = "00100",
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 100,
            CinCode = cinCode,
            ComplianceStatus = complianceStatus,
            IsActive = true,
        };

        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property;
    }

    private static async Task<Property> SeedFullyCompliantPropertyAsync(AppDbContext db, Guid? orgId = null)
    {
        var property = await SeedPropertyAsync(db, orgId, cinCode: "IT-12345-0123456789");

        db.TouristTaxRates.Add(new TouristTaxRate
        {
            City = property.City,
            RegionCode = "LAZ",
            RatePerPersonPerNight = 2m,
            IsActive = true,
        });

        db.PropertyDocuments.AddRange(
            new PropertyDocument
            {
                PropertyId = property.Id,
                FileName = "cin.pdf",
                StorageUrl = "/docs/cin.pdf",
                DocumentType = DocumentType.CinCertificate,
                UploadedBy = property.OwnerId,
            },
            new PropertyDocument
            {
                PropertyId = property.Id,
                FileName = "safety.pdf",
                StorageUrl = "/docs/safety.pdf",
                DocumentType = DocumentType.SafetyCompliance,
                UploadedBy = property.OwnerId,
            });

        property.SafetyChecklistJson =
            """{"smokeDetector":true,"fireExtinguisher":true,"gasCompliance":true,"acknowledgedAt":"2026-01-01T00:00:00Z"}""";

        await db.SaveChangesAsync();
        return property;
    }
}
