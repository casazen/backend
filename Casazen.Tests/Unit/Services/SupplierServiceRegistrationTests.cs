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
