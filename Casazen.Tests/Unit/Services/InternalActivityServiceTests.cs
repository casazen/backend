using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// NF-22: Internal activities performed by staff (collaborators with "Only some" perimeter) on properties.
/// The proof tests that staff sees only activities within their perimeter.
/// </summary>
public class InternalActivityServiceTests
{
    private const string OwnerId = "auth0|owner";
    private const string StaffUserId = "auth0|staff";

    private AppDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private InternalActivityService Service(AppDbContext db) =>
        new(db, NullLogger<InternalActivityService>.Instance);

    /// <summary>
    /// Proof: Staff with "Only some" perimeter sees only activities on their assigned properties (NF-22, AC-NF22-5).
    /// </summary>
    [Fact]
    public async Task StaffActivity_PerimeterIsolation_StaffSeesOnlyTheirProperties()
    {
        // Arrange
        await using var db = NewDb();
        var service = Service(db);

        var org = new Org { Id = Guid.NewGuid(), Name = "Test Agency", PlanTier = PlanTier.Agency };
        db.Orgs.Add(org);

        // Two properties: one granted to staff, one hidden
        var grantedProperty = new Property
        {
            Id = Guid.NewGuid(),
            OrgId = org.Id,
            Name = "Granted Property",
            OwnerId = OwnerId,
            IsActive = true,
            RentalMode = RentalMode.ShortRent
        };
        var hiddenProperty = new Property
        {
            Id = Guid.NewGuid(),
            OrgId = org.Id,
            Name = "Hidden Property",
            OwnerId = OwnerId,
            IsActive = true,
            RentalMode = RentalMode.ShortRent
        };
        db.Properties.AddRange(grantedProperty, hiddenProperty);

        // Staff member with "Only some" perimeter, granted access to only one property
        var memberAccess = new PropertyMemberAccess
        {
            Id = Guid.NewGuid(),
            OrgId = org.Id,
            UserId = StaffUserId,
            PropertyId = grantedProperty.Id,
            CreatedAt = DateTime.UtcNow
        };
        db.PropertyMemberAccesses.Add(memberAccess);
        await db.SaveChangesAsync();

        // Create activities on both properties
        var activityOnGrantedProperty = await service.CreateActivityAsync(
            grantedProperty.Id,
            StaffUserId,
            "Cleaning",
            DateTime.UtcNow.AddDays(1),
            internalRate: 50m,
            rechargeToOwner: true,
            createdByUserId: OwnerId);

        var activityOnHiddenProperty = await service.CreateActivityAsync(
            hiddenProperty.Id,
            StaffUserId,
            "Maintenance",
            DateTime.UtcNow.AddDays(2),
            internalRate: 75m,
            rechargeToOwner: false,
            createdByUserId: OwnerId);

        // Act: Get activities with a restricted scope (staff with "Only some" perimeter)
        var restrictedScope = new HostScope(
            OrgId: org.Id,
            OwnerId: null,
            GrantedToUserId: StaffUserId,
            GrantedMode: RentalMode.ShortRent);

        var visibleActivities = await service.GetActivitiesAsync(restrictedScope);

        // Assert: Staff sees only the activity on their granted property
        Assert.Single(visibleActivities);
        Assert.Equal(grantedProperty.Id, visibleActivities[0].PropertyId);
        Assert.Equal("Cleaning", visibleActivities[0].ActivityType);
        Assert.Equal(50m, visibleActivities[0].InternalRate);
        Assert.True(visibleActivities[0].RechargeToOwner);

        // Act: Get activities with an org-wide scope (owner sees all)
        var orgWideScope = new HostScope(
            OrgId: org.Id,
            OwnerId: null,
            GrantedToUserId: null,
            GrantedMode: null);

        var allActivities = await service.GetActivitiesAsync(orgWideScope);

        // Assert: Owner sees both activities
        Assert.Equal(2, allActivities.Count);
        Assert.Contains(allActivities, a => a.PropertyId == grantedProperty.Id);
        Assert.Contains(allActivities, a => a.PropertyId == hiddenProperty.Id);
    }

    [Fact]
    public async Task CreateActivityAsync_InheritsOrgIdFromProperty()
    {
        // Arrange
        await using var db = NewDb();
        var service = Service(db);

        var org = new Org { Id = Guid.NewGuid(), Name = "Test Org", PlanTier = PlanTier.Agency };
        db.Orgs.Add(org);

        var property = new Property
        {
            Id = Guid.NewGuid(),
            OrgId = org.Id,
            Name = "Test Property",
            OwnerId = OwnerId,
            IsActive = true,
            RentalMode = RentalMode.ShortRent
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();

        // Act
        var activity = await service.CreateActivityAsync(
            property.Id,
            StaffUserId,
            "CheckIn",
            DateTime.UtcNow.AddHours(3),
            internalRate: 30m,
            rechargeToOwner: true);

        // Assert
        Assert.Equal(org.Id, activity.OrgId);
        Assert.Equal(property.Id, activity.PropertyId);
        Assert.Equal(StaffUserId, activity.StaffUserId);
        Assert.Equal("CheckIn", activity.ActivityType);
        Assert.Equal("Pending", activity.Status);
        Assert.Null(activity.CompletedAt);
    }

    [Fact]
    public async Task UpdateStatusAsync_SetsCompletedAtWhenCompleted()
    {
        // Arrange
        await using var db = NewDb();
        var service = Service(db);

        var org = new Org { Id = Guid.NewGuid(), Name = "Test Org", PlanTier = PlanTier.Agency };
        db.Orgs.Add(org);

        var property = new Property
        {
            Id = Guid.NewGuid(),
            OrgId = org.Id,
            Name = "Test Property",
            OwnerId = OwnerId,
            IsActive = true,
            RentalMode = RentalMode.ShortRent
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();

        var activity = await service.CreateActivityAsync(
            property.Id,
            StaffUserId,
            "Maintenance",
            DateTime.UtcNow.AddDays(1));

        // Act
        var updated = await service.UpdateStatusAsync(activity.Id, "Completed", "All done");

        // Assert
        Assert.Equal("Completed", updated.Status);
        Assert.Equal("All done", updated.Notes);
        Assert.NotNull(updated.CompletedAt);
    }

    [Fact]
    public async Task GetMyActivitiesAsync_ReturnsOnlyStaffUsersActivitiesWithinPerimeter()
    {
        // Arrange
        await using var db = NewDb();
        var service = Service(db);

        var org = new Org { Id = Guid.NewGuid(), Name = "Test Org", PlanTier = PlanTier.Agency };
        db.Orgs.Add(org);

        var property = new Property
        {
            Id = Guid.NewGuid(),
            OrgId = org.Id,
            Name = "Test Property",
            OwnerId = OwnerId,
            IsActive = true,
            RentalMode = RentalMode.ShortRent
        };
        db.Properties.Add(property);

        // Staff access
        var memberAccess = new PropertyMemberAccess
        {
            Id = Guid.NewGuid(),
            OrgId = org.Id,
            UserId = StaffUserId,
            PropertyId = property.Id,
            CreatedAt = DateTime.UtcNow
        };
        db.PropertyMemberAccesses.Add(memberAccess);
        await db.SaveChangesAsync();

        // Create activities for different staff members
        await service.CreateActivityAsync(property.Id, StaffUserId, "Cleaning", DateTime.UtcNow.AddDays(1));
        await service.CreateActivityAsync(property.Id, "auth0|other-staff", "Maintenance", DateTime.UtcNow.AddDays(2));

        // Act
        var scope = new HostScope(org.Id, null, StaffUserId, RentalMode.ShortRent);
        var myActivities = await service.GetMyActivitiesAsync(scope, StaffUserId);

        // Assert
        Assert.Single(myActivities);
        Assert.Equal(StaffUserId, myActivities[0].StaffUserId);
        Assert.Equal("Cleaning", myActivities[0].ActivityType);
    }
}
