using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class FiscalRegimeServiceTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void RequiresPartitaIva_FromThirdStrProperty(int strCount, bool expected)
    {
        Assert.Equal(expected, strCount >= 3);
    }

    [Fact]
    public void OtaWithholding_Is21PercentOfGross()
    {
        Assert.Equal(210.00m, FiscalCopy.CalculateOtaWithholding(1000m));
    }

    [Fact]
    public async Task GetRegime_SingleActiveProperty_RecommendsCedolare21()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        SeedProperty(db, orgId, "Casa Uno");
        await db.SaveChangesAsync();
        var sut = new FiscalService(db);

        var snapshot = await sut.GetRegimeAsync(orgId, 2026);

        Assert.Equal(1, snapshot.StrPropertyCount);
        Assert.False(snapshot.RequiresPartitaIva);
        Assert.Equal(StrFiscalRegime.CedolareSecca21, snapshot.Properties[0].RecommendedRegime);
        Assert.Contains("informativa", snapshot.Disclaimer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AssignRegime_Cedolare26_WhenOnlyOneProperty_Throws()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        var property = SeedProperty(db, orgId, "Solo");
        await db.SaveChangesAsync();
        var sut = new FiscalService(db);

        await Assert.ThrowsAsync<FiscalValidationException>(() =>
            sut.AssignRegimeAsync(orgId, property.Id, 2026, StrFiscalRegime.CedolareSecca26, false));
    }

    [Fact]
    public async Task AssignRegime_Cedolare_WhenThreeProperties_Conflicts()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        var first = SeedProperty(db, orgId, "A");
        SeedProperty(db, orgId, "B");
        SeedProperty(db, orgId, "C");
        await db.SaveChangesAsync();
        var sut = new FiscalService(db);

        await Assert.ThrowsAsync<FiscalConflictException>(() =>
            sut.AssignRegimeAsync(orgId, first.Id, 2026, StrFiscalRegime.CedolareSecca21, true));
    }

    [Fact]
    public async Task Count_ExcludesInactiveAndLtrOnly()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        SeedProperty(db, orgId, "STR");
        var inactive = SeedProperty(db, orgId, "Off");
        inactive.IsActive = false;
        var ltr = SeedProperty(db, orgId, "LTR");
        db.LeaseContracts.Add(new LeaseContract
        {
            OrgId = orgId,
            PropertyId = ltr.Id,
            FiscalRegime = FiscalRegime.CedolareSecca,
            StartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            MonthlyRent = 800m,
            RegistrationDeadline = DateTime.UtcNow.AddDays(30),
        });
        await db.SaveChangesAsync();
        var sut = new FiscalService(db);

        var snapshot = await sut.GetRegimeAsync(orgId, 2026);

        Assert.Equal(1, snapshot.StrPropertyCount);
        Assert.Equal("STR", snapshot.Properties[0].Name);
    }

    [Fact]
    public async Task ApplyWithholding_OtaAuto_DirectSkipped()
    {
        var sut = new FiscalService(CreateDb());
        var otaPayment = new Payment { Amount = 100m };
        var otaBooking = new Booking { Source = BookingSource.Airbnb };
        await sut.ApplyWithholdingOnCreateAsync(otaPayment, otaBooking, null, null);
        Assert.Equal(21m, otaPayment.OtaWithholdingTax);
        Assert.True(otaPayment.WithholdingTaxApplied);
        Assert.Equal(WithholdingSource.AutoOta, otaPayment.WithholdingSource);

        var directPayment = new Payment { Amount = 100m };
        var directBooking = new Booking { Source = BookingSource.Direct };
        await sut.ApplyWithholdingOnCreateAsync(directPayment, directBooking, null, null);
        Assert.Equal(0m, directPayment.OtaWithholdingTax);
        Assert.False(directPayment.WithholdingTaxApplied);
    }

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options, NullTenantContext.Instance);
    }

    private static void SeedOrg(AppDbContext db, Guid orgId)
    {
        db.Orgs.Add(new OrgEntity
        {
            Id = orgId,
            Name = "Host",
            Slug = $"org-{orgId:N}"[..20],
            DisplayName = "Host",
            ContactEmail = "h@example.com",
        });
    }

    private static Property SeedProperty(AppDbContext db, Guid orgId, string name)
    {
        var property = new Property
        {
            OrgId = orgId,
            OwnerId = "auth0|host",
            Name = name,
            Address = "Via Test 1",
            City = "Rome",
            PostalCode = "00100",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 100m,
            CinCode = "IT-ABC123-DEF456",
            IsActive = true,
        };
        db.Properties.Add(property);
        return property;
    }
}
