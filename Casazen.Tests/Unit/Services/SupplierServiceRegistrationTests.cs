using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
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

        var (returnedOrg, returnedProfile) = await service.RegisterAsync(
            "new-registration@test.com",
            "New Supplier Srl",
            "+39 06 222222",
            "F205",
            inviteToken: null,
            userId);

        Assert.Equal(org.Id, returnedOrg.Id);
        Assert.Equal(org.Id, returnedProfile.OrgId);
        Assert.Single(db.Orgs);
        Assert.Single(db.SupplierProfiles);
        Assert.Equal(org.Id, db.Users.Single(u => u.Id == userId).SupplierOrgId);
        Assert.DoesNotContain(db.SupplierProfiles, sp => sp.Email == "new-registration@test.com");
    }

    [Fact]
    public async Task GetActiveByComune_ActiveSupplierWithEmptyCategories_MatchesRequestedCategory()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);
        var org = new OrgEntity
        {
            Name = "Empty Cats Srl",
            Slug = $"empty-cats-{Guid.NewGuid():N}"[..30],
            DisplayName = "Empty Cats Srl",
            ContactEmail = "empty-cats@test.com",
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
            CategoriesJson = "[]",
        });
        await db.SaveChangesAsync();

        var found = await service.GetActiveByComune("Roma", "cleaning");

        Assert.Contains(found, sp => sp.OrgId == org.Id);
    }

    [Fact]
    public async Task FixOrphanedSupplierOrgsAsync_BlankEmailProfiles_DoesNotMergeDistinctSuppliers()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        var firstOrg = new OrgEntity
        {
            Name = "Blank Email Supplier One",
            Slug = $"blank-one-{Guid.NewGuid():N}"[..30],
            DisplayName = "Blank Email Supplier One",
            ContactEmail = string.Empty,
            OrgType = OrgType.Supplier,
            PlanTier = PlanTier.Starter,
        };
        var secondOrg = new OrgEntity
        {
            Name = "Blank Email Supplier Two",
            Slug = $"blank-two-{Guid.NewGuid():N}"[..30],
            DisplayName = "Blank Email Supplier Two",
            ContactEmail = string.Empty,
            OrgType = OrgType.Supplier,
            PlanTier = PlanTier.Starter,
        };
        db.Orgs.AddRange(firstOrg, secondOrg);
        db.SupplierProfiles.AddRange(
            new SupplierProfile
            {
                OrgId = firstOrg.Id,
                Email = string.Empty,
                LegalName = firstOrg.DisplayName,
                Phone = string.Empty,
            },
            new SupplierProfile
            {
                OrgId = secondOrg.Id,
                Email = string.Empty,
                LegalName = secondOrg.DisplayName,
                Phone = string.Empty,
                Bio = "Fully separate supplier",
            });
        db.Users.AddRange(
            new User
            {
                Id = $"auth0|blank-one-{Guid.NewGuid():N}",
                Email = string.Empty,
                FirstName = "Blank",
                LastName = "One",
                SupplierOrgId = firstOrg.Id,
                IsActive = true,
            },
            new User
            {
                Id = $"auth0|blank-two-{Guid.NewGuid():N}",
                Email = string.Empty,
                FirstName = "Blank",
                LastName = "Two",
                SupplierOrgId = secondOrg.Id,
                IsActive = true,
            });
        await db.SaveChangesAsync();

        var report = await service.FixOrphanedSupplierOrgsAsync();

        Assert.Equal(2, report.ProfilesScanned);
        Assert.Equal(0, report.DuplicatesMerged);
        Assert.Equal(2, await db.Orgs.CountAsync(o => o.OrgType == OrgType.Supplier));
        Assert.Equal(2, await db.SupplierProfiles.CountAsync());
        Assert.Contains(await db.SupplierProfiles.Select(sp => sp.OrgId).ToListAsync(), id => id == firstOrg.Id);
        Assert.Contains(await db.SupplierProfiles.Select(sp => sp.OrgId).ToListAsync(), id => id == secondOrg.Id);
    }

    private static SupplierService CreateService(AppDbContext db)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["App:PublicSiteBaseUrl"] = "https://casazen-app.vercel.app",
                ["Email:SendGridApiKey"] = string.Empty,
            })
            .Build();

        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns("Testing");

        return new SupplierService(
            db,
            Mock.Of<IEmailService>(),
            config,
            env.Object,
            NullLogger<SupplierService>.Instance);
    }

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }
}
