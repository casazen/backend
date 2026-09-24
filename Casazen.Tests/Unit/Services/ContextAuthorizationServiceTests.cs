using Casazen.Core.Authorization;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Claims;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class ContextAuthorizationServiceTests
{
    [Fact]
    public async Task HasPermissionAsync_UserNotInDatabaseWithJwtPropertyOwner_DeniesHostContext()
    {
        // PL-02 (A1-05): the JWT role alone (Auth0 sign-up, API or app without the onboarding) grants nothing host-side.
        await using var db = CreateDbContext();
        var httpContext = BuildHttpContext("auth0|jwt-only", ["PropertyOwner"]);
        var service = CreateService(db, httpContext);

        var allowed = await service.HasPermissionAsync("auth0|jwt-only", "short-rent", "property.read");

        Assert.False(allowed);
    }

    [Fact]
    public async Task HasPermissionAsync_OnboardedHostWithJwtPropertyOwner_GrantsShortRent()
    {
        await using var db = CreateDbContext();
        await SeedOnboardedUserAsync(db, "auth0|jwt-onboarded");
        var service = CreateService(db, BuildHttpContext("auth0|jwt-onboarded", ["PropertyOwner"]));

        Assert.True(await service.HasPermissionAsync("auth0|jwt-onboarded", "short-rent", "property.write"));
    }

    [Fact]
    public async Task HasPermissionAsync_DefaultPropertyOwnerRoleWithoutOnboarding_DeniesShortRentWrite()
    {
        // The pre-PL-02 default: DB role PropertyOwner, no JWT role, no onboarding, no consents.
        await using var db = CreateDbContext();
        db.Users.Add(new Core.Entities.User
        {
            Id = "auth0|legacy-default",
            Email = "legacy@test.com",
            FirstName = "Legacy",
            LastName = "Default",
            Role = Core.Entities.UserRole.PropertyOwner,
            IsActive = true,
        });
        await db.SaveChangesAsync();
        var service = CreateService(db, BuildHttpContext("auth0|legacy-default", []));

        Assert.False(await service.HasPermissionAsync("auth0|legacy-default", "short-rent", "property.write"));
        Assert.False(await service.HasPermissionAsync("auth0|legacy-default", "short-rent", "guest.write"));
        Assert.False(await service.HasPermissionAsync("auth0|legacy-default", "short-rent", "booking.write"));
    }

    [Fact]
    public async Task HasPermissionAsync_OnboardedButConsentsOfOldVersion_DeniesHostContext()
    {
        await using var db = CreateDbContext();
        await SeedOnboardedUserAsync(db, "auth0|old-consents");
        var service = CreateService(
            db,
            BuildHttpContext("auth0|old-consents", ["PropertyOwner"]),
            new Dictionary<string, string?> { ["Legal:Documents:Dpa:Version"] = "2026-10-v2" });

        Assert.False(await service.HasPermissionAsync("auth0|old-consents", "short-rent", "property.read"));
    }

    [Fact]
    public async Task HasPermissionAsync_OnboardingCompletedWithoutConsents_DeniesHostContext()
    {
        await using var db = CreateDbContext();
        await SeedOnboardedUserAsync(db, "auth0|no-consents", withConsents: false);
        var service = CreateService(db, BuildHttpContext("auth0|no-consents", ["PropertyOwner"]));

        Assert.False(await service.HasPermissionAsync("auth0|no-consents", "short-rent", "property.read"));
    }

    [Fact]
    public async Task GetUserContextsAsync_AdminAndSupplierWithoutOnboarding_KeepTheirContexts()
    {
        await using var db = CreateDbContext();
        db.Users.Add(new Core.Entities.User
        {
            Id = "auth0|admin-supplier",
            Email = "admin@test.com",
            FirstName = "Admin",
            LastName = "User",
            Role = Core.Entities.UserRole.Admin,
            IsActive = true,
        });
        await db.SaveChangesAsync();
        var service = CreateService(db, BuildHttpContext("auth0|admin-supplier", ["Admin", "Supplier", "PropertyOwner"]));

        var contexts = await service.GetUserContextsAsync("auth0|admin-supplier");

        Assert.Contains(contexts, c => c.ContextKey == "admin");
        Assert.Contains(contexts, c => c.ContextKey == "supplier");
        Assert.DoesNotContain(contexts, c => c.ContextKey == "short-rent");
        Assert.True(await service.HasPermissionAsync("auth0|admin-supplier", "admin", "admin.users.manage"));
    }

    [Fact]
    public async Task GetUserContextsAsync_NewUserWithRoleNone_HasNoContext()
    {
        await using var db = CreateDbContext();
        db.Users.Add(new Core.Entities.User
        {
            Id = "auth0|new-user",
            Email = "new@test.com",
            FirstName = "New",
            LastName = "User",
            IsActive = true,
        });
        await db.SaveChangesAsync();
        var service = CreateService(db, BuildHttpContext("auth0|new-user", []));

        Assert.Empty(await service.GetUserContextsAsync("auth0|new-user"));
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
        await SeedOnboardedUserAsync(db, "auth0|dual");
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
        await SeedOnboardedUserAsync(db, "auth0|db-role");

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
    public void BuildFallbackAccess_LongTermLandlord_HasLongRentPropertyPermissions()
    {
        var contexts = ContextAccessBootstrap.BuildFallbackAccess(["LongTermLandlord"]);

        var longRent = Assert.Single(contexts, c => c.ContextKey == "long-rent");
        Assert.Contains(longRent.Permissions, p => p == "property.read");
        Assert.Contains(longRent.Permissions, p => p == "property.write");
        Assert.DoesNotContain(longRent.Permissions, p => p == "booking.read");
    }

    [Fact]
    public async Task HasPermissionAsync_LongTermLandlord_HasPropertyPermissionsOnlyInLongRent()
    {
        await using var db = CreateDbContext();
        await SeedOnboardedUserAsync(db, "auth0|long-only", Core.Entities.UserRole.LongTermLandlord);
        var httpContext = BuildHttpContext("auth0|long-only", ["LongTermLandlord"]);
        var service = CreateService(db, httpContext);

        // property.* counts in the context that grants it (LT-05): the shared property endpoints accept either
        // context in their policy, the short-rent ones (pricing, iCal, photos...) stay closed to a landlord.
        Assert.True(await service.HasPermissionAsync("auth0|long-only", "long-rent", "property.read"));
        Assert.True(await service.HasPermissionAsync("auth0|long-only", "long-rent", "property.write"));
        Assert.False(await service.HasPermissionAsync("auth0|long-only", "short-rent", "property.read"));
        Assert.False(await service.HasPermissionAsync("auth0|long-only", "short-rent", "property.write"));
        Assert.False(await service.HasPermissionAsync("auth0|long-only", "short-rent", "booking.read"));
    }

    [Fact]
    public async Task HasPermissionAsync_LongRentMembership_DoesNotGrantShortRentPropertyPermissions()
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
        await SeedOnboardedUserAsync(db, "auth0|long-membership", Core.Entities.UserRole.LongTermLandlord);
        db.UserContextMemberships.Add(new Core.Entities.UserContextMembership
        {
            UserId = "auth0|long-membership",
            ContextKey = "long-rent",
            RoleId = 20,
        });
        await db.SaveChangesAsync();

        var httpContext = BuildHttpContext("auth0|long-membership", []);
        var service = CreateService(db, httpContext);

        Assert.True(await service.HasPermissionAsync("auth0|long-membership", "long-rent", "property.write"));
        Assert.False(await service.HasPermissionAsync("auth0|long-membership", "short-rent", "property.write"));
        Assert.False(await service.HasPermissionAsync("auth0|long-membership", "short-rent", "booking.read"));
    }

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static ContextAuthorizationService CreateService(
        AppDbContext db,
        HttpContext httpContext,
        IDictionary<string, string?>? settings = null)
    {
        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings ?? new Dictionary<string, string?>()).Build();
        var store = new UserAuthorizationSnapshotStore(
            db,
            new MemoryCache(new MemoryCacheOptions()),
            accessor,
            configuration);
        return new ContextAuthorizationService(
            store,
            new LegalDocumentService(configuration),
            accessor,
            NullLogger<ContextAuthorizationService>.Instance);
    }

    /// <summary>
    /// A host who completed the onboarding (PL-02): org, <c>OnboardingCompletedAt</c> and, unless told otherwise, the
    /// consents of the current (default) document versions.
    /// </summary>
    private static async Task SeedOnboardedUserAsync(
        AppDbContext db,
        string userId,
        Core.Entities.UserRole role = Core.Entities.UserRole.PropertyOwner,
        bool withConsents = true)
    {
        var org = new Core.Entities.Org { Name = $"Org {userId}", Slug = $"org-{Guid.NewGuid():N}" };
        db.Orgs.Add(org);
        var user = new Core.Entities.User
        {
            Id = userId,
            Email = $"{Guid.NewGuid():N}@test.com",
            FirstName = "Host",
            LastName = "User",
            Role = role,
            OrgId = org.Id,
            IsActive = true,
        };
        db.Users.Add(user);
        if (withConsents)
            await HostOnboardingSeed.MarkOnboardedAsync(db, user, org.Id, new LegalDocumentService(new ConfigurationBuilder().Build()));
        else
            user.OnboardingCompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
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
