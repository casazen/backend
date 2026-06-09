using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class OrgServiceTests
{
    private static AppDbContext CreateDb(string dbName)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task EnsureOrgForUserAsync_CreatesOrgWithSelectedPlanTier()
    {
        await using var db = CreateDb(nameof(EnsureOrgForUserAsync_CreatesOrgWithSelectedPlanTier));
        var userId = "auth0|new-user";
        db.Users.Add(new User
        {
            Id = userId,
            Email = "owner@example.com",
            FirstName = "Mario",
            LastName = "Rossi",
            Role = UserRole.PropertyOwner,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var service = new OrgService(db);
        var org = await service.EnsureOrgForUserAsync(
            userId, "owner@example.com", "Mario Rossi", PlanTier.Pro);

        Assert.Equal(PlanTier.Pro, org.PlanTier);
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        Assert.Equal(org.Id, user.OrgId);
    }

    [Fact]
    public async Task EnsureOrgForUserAsync_IsIdempotent_DoesNotChangeExistingPlan()
    {
        await using var db = CreateDb(nameof(EnsureOrgForUserAsync_IsIdempotent_DoesNotChangeExistingPlan));
        var userId = "auth0|existing";
        var orgId = Guid.NewGuid();
        db.Orgs.Add(new Org
        {
            Id = orgId,
            Name = "Existing",
            Slug = "org-existing",
            DisplayName = "Existing",
            ContactEmail = "x@y.it",
            PlanTier = PlanTier.Starter,
            IsActive = true,
        });
        db.Users.Add(new User
        {
            Id = userId,
            Email = "x@y.it",
            FirstName = "A",
            LastName = "B",
            Role = UserRole.PropertyOwner,
            OrgId = orgId,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var service = new OrgService(db);
        var org = await service.EnsureOrgForUserAsync(
            userId, "x@y.it", "A B", PlanTier.Scale);

        Assert.Equal(orgId, org.Id);
        Assert.Equal(PlanTier.Starter, org.PlanTier);
    }

    [Fact]
    public async Task UpdatePlanTierAsync_ChangesTier()
    {
        await using var db = CreateDb(nameof(UpdatePlanTierAsync_ChangesTier));
        var orgId = Guid.NewGuid();
        db.Orgs.Add(new Org
        {
            Id = orgId,
            Name = "Org",
            Slug = "org-test",
            DisplayName = "Org",
            ContactEmail = "x@y.it",
            PlanTier = PlanTier.Starter,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var service = new OrgService(db);
        var updated = await service.UpdatePlanTierAsync(orgId, PlanTier.Scale);

        Assert.NotNull(updated);
        Assert.Equal(PlanTier.Scale, updated!.PlanTier);
    }
}
