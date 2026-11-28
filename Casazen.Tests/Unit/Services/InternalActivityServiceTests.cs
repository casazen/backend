using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

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

    [Fact]
    public async Task StaffActivity_PerimeterIsolation_StaffSeesOnlyTheirProperties()
    {
        await using var db = NewDb();
        var service = Service(db);

        var org = new OrgEntity { Id = Guid.NewGuid(), Name = "Test Agency", PlanTier = PlanTier.Agency };
        db.Orgs.Add(org);

        var grantedProperty = new Property
        {
            Id = Guid.NewGuid(),
            OrgId = org.Id,
            Name = "Granted Property",
            OwnerId = OwnerId,
            IsActive = true,
            RentalMode = RentalMode.Short
        };
        var hiddenProperty = new Property
        {
            Id = Guid.NewGuid(),
            OrgId = org.Id,
            Name = "Hidden Property",
            OwnerId = OwnerId,
            IsActive = true,
            RentalMode = RentalMode.Short
        };
        db.Properties.AddRange(grantedProperty, hiddenProperty);

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

        var restrictedScope = new HostScope(
            OrgId: org.Id,
            OwnerId: null,
            GrantedToUserId: StaffUserId,
            GrantedMode: RentalMode.Short);

        var visibleActivities = await service.GetActivitiesAsync(restrictedScope);

        Assert.Single(visibleActivities);
        Assert.Equal(grantedProperty.Id, visibleActivities[0].PropertyId);
        Assert.Equal("Cleaning", visibleActivities[0].ActivityType);
        Assert.Equal(50m, visibleActivities[0].InternalRate);
        Assert.True(visibleActivities[0].RechargeToOwner);

        var orgWideScope = new HostScope(
            OrgId: org.Id,
            OwnerId: null,
            GrantedToUserId: null,
            GrantedMode: null);

        var allActivities = await service.GetActivitiesAsync(orgWideScope);

        Assert.Equal(2, allActivities.Count);
        Assert.Contains(allActivities, a => a.PropertyId == grantedProperty.Id);
        Assert.Contains(allActivities, a => a.PropertyId == hiddenProperty.Id);
    }

    [Fact]
    public async Task CreateActivityAsync_InheritsOrgIdFromProperty()
    {
        await using var db = NewDb();
        var service = Service(db);

        var org = new OrgEntity { Id = Guid.NewGuid(), Name = "Test Org", PlanTier = PlanTier.Agency };
        db.Orgs.Add(org);

        var property = new Property
        {
            Id = Guid.NewGuid(),
            OrgId = org.Id,
            Name = "Test Property",
            OwnerId = OwnerId,
            IsActive = true,
            RentalMode = RentalMode.Short
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();

        var activity = await service.CreateActivityAsync(
            property.Id,
            StaffUserId,
            "CheckIn",
            DateTime.UtcNow.AddHours(3),
            internalRate: 30m,
            rechargeToOwner: true);

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
        await using var db = NewDb();
        var service = Service(db);

        var org = new OrgEntity { Id = Guid.NewGuid(), Name = "Test Org", PlanTier = PlanTier.Agency };
        db.Orgs.Add(org);

        var property = new Property
        {
            Id = Guid.NewGuid(),
            OrgId = org.Id,
            Name = "Test Property",
            OwnerId = OwnerId,
            IsActive = true,
            RentalMode = RentalMode.Short
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();

        var activity = await service.CreateActivityAsync(
            property.Id,
            StaffUserId,
            "Maintenance",
            DateTime.UtcNow.AddDays(1));

        var updated = await service.UpdateStatusAsync(activity.Id, "Completed", "All done");

        Assert.Equal("Completed", updated.Status);
        Assert.Equal("All done", updated.Notes);
        Assert.NotNull(updated.CompletedAt);
    }

    [Fact]
    public async Task GetMyActivitiesAsync_ReturnsOnlyStaffUsersActivitiesWithinPerimeter()
    {
        await using var db = NewDb();
        var service = Service(db);

        var org = new OrgEntity { Id = Guid.NewGuid(), Name = "Test Org", PlanTier = PlanTier.Agency };
        db.Orgs.Add(org);

        var property = new Property
        {
            Id = Guid.NewGuid(),
            OrgId = org.Id,
            Name = "Test Property",
            OwnerId = OwnerId,
            IsActive = true,
            RentalMode = RentalMode.Short
        };
        db.Properties.Add(property);

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

        await service.CreateActivityAsync(property.Id, StaffUserId, "Cleaning", DateTime.UtcNow.AddDays(1));
        await service.CreateActivityAsync(property.Id, "auth0|other-staff", "Maintenance", DateTime.UtcNow.AddDays(2));

        var scope = new HostScope(org.Id, null, StaffUserId, RentalMode.Short);
        var myActivities = await service.GetMyActivitiesAsync(scope, StaffUserId);

        Assert.Single(myActivities);
        Assert.Equal(StaffUserId, myActivities[0].StaffUserId);
        Assert.Equal("Cleaning", myActivities[0].ActivityType);
    }
}
