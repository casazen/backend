using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Http;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class SupplierServiceRegistrationTests
{
    [Fact]
    public async Task RegisterAsync_UserAlreadyHasSupplierOrg_ReturnsExistingRegistrationWithoutOverwrite()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        var userId = $"auth0|existing-supplier-{Guid.NewGuid():N}";
        var org = new OrgEntity
        {
            Name = "Existing Supplier Srl",
            Slug = $"existing-supplier-{Guid.NewGuid():N}"[..30],
            DisplayName = "Existing Supplier Srl",
            ContactEmail = "existing-supplier@test.com",
            OrgType = OrgType.Supplier,
            PlanTier = PlanTier.Starter,
        };
        db.Orgs.Add(org);
        db.SupplierProfiles.Add(new SupplierProfile
        {
            OrgId = org.Id,
            Email = org.ContactEmail,
            LegalName = org.DisplayName,
            Phone = "+39 06 111111",
            ComuniJson = "[\"H501\"]",
        });
        db.Users.Add(new User
        {
            Id = userId,
            Email = org.ContactEmail,
            FirstName = "Existing",
            LastName = "Supplier",
            SupplierOrgId = org.Id,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var (returnedOrg, returnedProfile, claim) = await service.RegisterAsync(new SupplierRegistration(
            "new-registration@test.com",
            "New Supplier Srl",
            "+39 06 222222",
            "F205",
            InviteToken: null,
            userId,
            AccountEmail: org.ContactEmail));

        Assert.Equal(org.Id, returnedOrg.Id);
        Assert.Equal(org.Id, returnedProfile.OrgId);
        Assert.Null(claim);
        Assert.Single(db.Orgs);
        Assert.Single(db.SupplierProfiles);
        Assert.Equal(org.Id, db.Users.Single(u => u.Id == userId).SupplierOrgId);
        Assert.DoesNotContain(db.SupplierProfiles, sp => sp.Email == "new-registration@test.com");
    }

    [Fact]
    public async Task GetActiveByComune_ActiveSupplierWithEmptyCategories_DoesNotMatchRequestedCategory()
    {
        // A4-05: "no categories" used to pass every category filter. A supplier is found only for what it declared.
        await using var db = CreateDbContext();
        var service = CreateService(db);
        var emptyOrgId = await SeedActiveSupplierAsync(db, "[]");

        var found = await service.GetActiveByComune("Roma", "cleaning");

        Assert.DoesNotContain(found, sp => sp.OrgId == emptyOrgId);
        Assert.Contains(await service.GetActiveByComune("Roma", null), sp => sp.OrgId == emptyOrgId);
    }

    [Theory]
    [InlineData("cleaning")]
    [InlineData(" Cleaning ")]
    public async Task GetActiveByComune_CategoryCode_ReturnsOnlySuppliersThatDeclaredIt(string category)
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);
        var cleaningOrgId = await SeedActiveSupplierAsync(db, """["maintenance","cleaning"]""");
        var maintenanceOrgId = await SeedActiveSupplierAsync(db, """["maintenance"]""");

        var found = await service.GetActiveByComune("Roma", category);

        Assert.Contains(found, sp => sp.OrgId == cleaningOrgId);
        Assert.DoesNotContain(found, sp => sp.OrgId == maintenanceOrgId);
    }

    [Fact]
    public async Task GetActiveByComune_ItalianLabel_ThrowsInvalidServiceCategory()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);
        await SeedActiveSupplierAsync(db, """["Pulizie"]""");

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => service.GetActiveByComune("Roma", "Pulizie"));

        Assert.Equal(ServiceCategories.InvalidCategoryCode, ex.Code);
    }

    [Fact]
    public async Task UpdateProfileAsync_UnknownCategory_ThrowsAndLeavesProfileUnchanged()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);
        var orgId = await SeedActiveSupplierAsync(db, """["cleaning"]""");

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => service.UpdateProfileAsync(
            orgId, "Nuovo nome", null, null, ["cleaning", "Giardinaggio"], null, "Bio", null));

        Assert.Equal(ServiceCategories.InvalidCategoryCode, ex.Code);
        db.ChangeTracker.Clear();
        var profile = await db.SupplierProfiles.SingleAsync(sp => sp.OrgId == orgId);
        Assert.Equal("""["cleaning"]""", profile.CategoriesJson);
        Assert.NotEqual("Nuovo nome", profile.LegalName);
        Assert.Null(profile.Bio);
    }

    [Fact]
    public async Task UpdateProfileAsync_Codes_StoresNormalizedDistinctCodes()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);
        var orgId = await SeedActiveSupplierAsync(db, "[]");

        var profile = await service.UpdateProfileAsync(
            orgId, null, null, null, ["Gardening", "events", "gardening"], null, null, null);

        Assert.Equal("""["gardening","events"]""", profile!.CategoriesJson);
    }

    [Fact]
    public async Task CreateInviteAsync_UnknownCategory_ThrowsWithoutCreatingInvite()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => service.CreateInviteAsync(
            "invitee@test.com", "H501", ["Pulizie"], null));

        Assert.Equal(ServiceCategories.InvalidCategoryCode, ex.Code);
        Assert.Empty(db.SupplierInviteRecords);
    }

    [Fact]
    public async Task GetUnmappedCategoriesAsync_LegacyValuesKept_ListsOnlyValuesThatAreNotCodes()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);
        var orgId = await SeedActiveSupplierAsync(db, """["cleaning","Idraulica speciale"]""");
        var invite = new SupplierInviteRecord
        {
            Email = "legacy-invite@test.com",
            ComuneCode = "H501",
            CategoriesJson = """["Pulizie extra"]""",
            ExpiresAt = DateTime.UtcNow.AddDays(1),
        };
        db.SupplierInviteRecords.Add(invite);
        var legacyRequest = new ServiceRequest
        {
            OrgId = Guid.NewGuid(),
            PropertyId = Guid.NewGuid(),
            SupplierOrgId = orgId,
            Category = "foo",
        };
        db.ServiceRequests.AddRange(
            legacyRequest,
            new ServiceRequest { OrgId = Guid.NewGuid(), PropertyId = Guid.NewGuid(), SupplierOrgId = orgId, Category = "cleaning" });
        await db.SaveChangesAsync();

        var unmapped = await service.GetUnmappedCategoriesAsync();

        Assert.Equal(
            new[]
            {
                new UnmappedServiceCategory("supplier_profile", orgId, "Idraulica speciale"),
                new UnmappedServiceCategory("supplier_invite", invite.Id, "Pulizie extra"),
                new UnmappedServiceCategory("service_request", legacyRequest.Id, "foo"),
            },
            unmapped);
    }

    private static async Task<Guid> SeedActiveSupplierAsync(AppDbContext db, string categoriesJson)
    {
        var org = new OrgEntity
        {
            Name = "Categories Srl",
            Slug = $"categories-{Guid.NewGuid():N}"[..30],
            DisplayName = "Categories Srl",
            ContactEmail = $"{Guid.NewGuid():N}@test.com",
            OrgType = OrgType.Supplier,
        };
        db.Orgs.Add(org);
        db.SupplierProfiles.Add(new SupplierProfile
        {
            OrgId = org.Id,
            Email = org.ContactEmail,
            LegalName = org.DisplayName,
            Phone = "+39 06 333333",
            Status = SupplierStatus.Active,
            ComuniJson = """["058091"]""",
            CategoriesJson = categoriesJson,
        });
        await db.SaveChangesAsync();
        return org.Id;
    }

    private static SupplierService CreateService(AppDbContext db) =>
        new(
            db,
            Mock.Of<IEmailQueue>(),
            EmailTestHelpers.Links(),
            Mock.Of<ISafeExternalHttpClient>(),
            Options.Create(new SupplierRegistrationOptions()),
            NullLogger<SupplierService>.Instance);

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }
}
