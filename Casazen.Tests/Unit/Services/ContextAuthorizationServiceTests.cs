using Casazen.Core.Authorization;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Claims;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class ContextAuthorizationServiceTests
{
    [Fact]
    public async Task HasPermissionAsync_WhenUserNotInDatabase_UsesJwtRoleFallback()
    {
        await using var db = CreateDbContext();
        var httpContext = BuildHttpContext("auth0|jwt-only", ["PropertyOwner"]);
        var service = CreateService(db, httpContext);

        var allowed = await service.HasPermissionAsync("auth0|jwt-only", "short-rent", "property.read");

        Assert.True(allowed);
    }

    [Fact]
    public async Task HasPermissionAsync_WhenUserInactive_ReturnsFalse()
    {
        await using var db = CreateDbContext();
        db.Users.Add(new Core.Entities.User
        {
            Id = "auth0|inactive",
            Email = "inactive@test.com",
            FirstName = "Inactive",
            LastName = "User",
            IsActive = false,
        });
        await db.SaveChangesAsync();

        var httpContext = BuildHttpContext("auth0|inactive", ["PropertyOwner"]);
        var service = CreateService(db, httpContext);

        var allowed = await service.HasPermissionAsync("auth0|inactive", "short-rent", "property.read");

        Assert.False(allowed);
    }

    [Fact]
    public async Task GetUserContextsAsync_MergesJwtSupplier_WhenDbHasHostMembership()
    {
        await using var db = CreateDbContext();
        db.Users.Add(new Core.Entities.User
        {
            Id = "auth0|dual",
            Email = "dual@test.com",
            FirstName = "Dual",
            LastName = "User",
            IsActive = true,
        });
        db.UserContextMemberships.Add(new Core.Entities.UserContextMembership
        {
            UserId = "auth0|dual",
            ContextKey = "short-rent",
            RoleId = 1,
        });
        await db.SaveChangesAsync();

        var httpContext = BuildHttpContext("auth0|dual", ["PropertyOwner", "Supplier"]);
        var service = CreateService(db, httpContext);

        var contexts = await service.GetUserContextsAsync("auth0|dual");

        Assert.Contains(contexts, c => c.ContextKey == "short-rent");
        Assert.Contains(contexts, c => c.ContextKey == "supplier");
    }

    [Fact]
    public async Task GetUserContextsAsync_WhenNoMembershipAndNoJwt_FallsBackToUserRoleEnum()
    {
        await using var db = CreateDbContext();
        db.Users.Add(new Core.Entities.User
        {
            Id = "auth0|db-role",
            Email = "owner@test.com",
            FirstName = "Owner",
            LastName = "User",
            Role = Core.Entities.UserRole.PropertyOwner,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var httpContext = BuildHttpContext("auth0|db-role", []);
        var service = CreateService(db, httpContext);

        var contexts = await service.GetUserContextsAsync("auth0|db-role");

        Assert.Contains(contexts, c => c.ContextKey == "short-rent");
    }

    [Fact]
    public void BuildFallbackAccess_PropertyOwner_HasShortRentPermissions()
    {
        var contexts = ContextAccessBootstrap.BuildFallbackAccess(["PropertyOwner"]);

        Assert.Contains(contexts, c => c.ContextKey == "short-rent");
        var shortRent = contexts.Single(c => c.ContextKey == "short-rent");
        Assert.Contains(shortRent.Permissions, p => p == "property.read");
        Assert.Contains(shortRent.Permissions, p => p == "booking.read");
    }

    [Fact]
    public void BuildFallbackAccess_LongTermLandlord_HasSharedPropertyPermissions()
    {
        var contexts = ContextAccessBootstrap.BuildFallbackAccess(["LongTermLandlord"]);

        var longRent = Assert.Single(contexts, c => c.ContextKey == "long-rent");
        Assert.Contains(longRent.Permissions, p => p == "property.read");
        Assert.Contains(longRent.Permissions, p => p == "property.write");
        Assert.DoesNotContain(longRent.Permissions, p => p == "booking.read");
    }

    [Fact]
    public async Task HasPermissionAsync_LongTermLandlord_CanSatisfyExistingPropertyPolicies()
    {
        await using var db = CreateDbContext();
        var httpContext = BuildHttpContext("auth0|long-only", ["LongTermLandlord"]);
        var service = CreateService(db, httpContext);

        var canReadProperty = await service.HasPermissionAsync("auth0|long-only", "short-rent", "property.read");
        var canWriteProperty = await service.HasPermissionAsync("auth0|long-only", "short-rent", "property.write");
        var canReadBookings = await service.HasPermissionAsync("auth0|long-only", "short-rent", "booking.read");

        Assert.True(canReadProperty);
        Assert.True(canWriteProperty);
        Assert.False(canReadBookings);
    }

    [Fact]
    public async Task HasPermissionAsync_LongRentMembership_CanSatisfyExistingPropertyPolicies()
    {
        await using var db = CreateDbContext();
        if (!await db.AppContexts.AnyAsync(c => c.Key == "long-rent"))
        {
            db.AppContexts.Add(new Core.Entities.AppContext { Key = "long-rent", DisplayName = "Affitti lungo termine" });
        }

        db.Roles.Add(new Core.Entities.Role
        {
            Id = 20,
            ContextKey = "long-rent",
            RoleKey = "long_term_landlord",
            Permissions =
            [
                new Core.Entities.RolePermission { RoleId = 20, PermissionKey = "property.read" },
                new Core.Entities.RolePermission { RoleId = 20, PermissionKey = "property.write" },
                new Core.Entities.RolePermission { RoleId = 20, PermissionKey = "lease.read" },
            ],
        });
        db.Users.Add(new Core.Entities.User
        {
            Id = "auth0|long-membership",
            Email = "long@test.com",
            FirstName = "Long",
            LastName = "User",
            IsActive = true,
        });
        db.UserContextMemberships.Add(new Core.Entities.UserContextMembership
        {
            UserId = "auth0|long-membership",
            ContextKey = "long-rent",
            RoleId = 20,
        });
        await db.SaveChangesAsync();

        var httpContext = BuildHttpContext("auth0|long-membership", []);
        var service = CreateService(db, httpContext);

        var canWriteProperty = await service.HasPermissionAsync("auth0|long-membership", "short-rent", "property.write");
        var canReadBookings = await service.HasPermissionAsync("auth0|long-membership", "short-rent", "booking.read");

        Assert.True(canWriteProperty);
        Assert.False(canReadBookings);
    }

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static ContextAuthorizationService CreateService(AppDbContext db, HttpContext httpContext)
    {
        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        return new ContextAuthorizationService(db, accessor, NullLogger<ContextAuthorizationService>.Instance);
    }

    private static HttpContext BuildHttpContext(string userId, string[] roles)
    {
        var claims = new List<Claim> { new("sub", userId) };
        claims.AddRange(roles.Select(r => new Claim("https://casazen.app/roles", r)));

        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")),
        };
        return context;
    }
}
