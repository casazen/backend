using Casazen.Core.Authorization;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
    public void BuildFallbackAccess_PropertyOwner_HasShortRentPermissions()
    {
        var contexts = ContextAccessBootstrap.BuildFallbackAccess(["PropertyOwner"]);

        Assert.Contains(contexts, c => c.ContextKey == "short-rent");
        var shortRent = contexts.Single(c => c.ContextKey == "short-rent");
        Assert.Contains(shortRent.Permissions, p => p == "property.read");
        Assert.Contains(shortRent.Permissions, p => p == "booking.read");
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
        return new ContextAuthorizationService(db, accessor);
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
