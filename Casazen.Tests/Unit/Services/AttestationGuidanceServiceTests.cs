using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Seeds;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class AttestationGuidanceServiceTests
{
    private const string OwnerId = "auth0|host";

    [Fact]
    public async Task GetSignatoryOrganizations_ReturnsContacts_WithoutHttpClient()
    {
        await using var db = CreateDb();
        var orgId = Guid.NewGuid();
        db.Orgs.Add(new OrgEntity
        {
            Id = orgId,
            Name = "Host",
            Slug = $"org-{orgId:N}"[..20],
            DisplayName = "Host",
            ContactEmail = "h@example.com",
        });
        var property = new Property
        {
            OrgId = orgId,
            OwnerId = OwnerId,
            Name = "Alloggio",
            Address = "Via Test 1",
            City = "Seveso",
            PostalCode = "20822",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 0m,
            CinCode = $"IT-{Guid.NewGuid():N}"[..16],
            IsActive = true,
        };
        db.Properties.Add(property);
        db.TerritorialRentAgreements.AddRange(CanoneConcordatoMbSeed.BuildAgreements());
        await db.SaveChangesAsync();

        var sut = new AttestationGuidanceService(
            new TerritorialRentAgreementRepository(db),
            new PropertyRepository(db));

        var result = await sut.GetSignatoryOrganizationsAsync(property.Id, OwnerId);

        Assert.NotNull(result);
        Assert.True(result.Organizations.Count >= 1);
        Assert.All(result.Organizations, o =>
        {
            Assert.False(string.IsNullOrWhiteSpace(o.Name));
            Assert.False(string.IsNullOrWhiteSpace(o.Contact));
        });
        Assert.DoesNotContain(typeof(AttestationGuidanceService).GetConstructors()[0].GetParameters(),
            p => p.ParameterType.Name.Contains("Http", StringComparison.OrdinalIgnoreCase));
    }

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options, NullTenantContext.Instance);
    }
}
