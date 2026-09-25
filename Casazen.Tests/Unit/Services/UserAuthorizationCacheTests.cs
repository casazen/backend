using System.Security.Claims;
using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// A4-30 / A9-31: per-request and short-lived per-user caching of authorization data, and
/// A1-02 / A1-29: DB context memberships kept aligned with role changes.
/// </summary>
public class UserAuthorizationCacheTests
{
    private const string UserId = "auth0|cache-user";

    /// <summary>Current legal document versions: the defaults, no configuration.</summary>
    private static readonly LegalDocumentService Legal = new(new ConfigurationBuilder().Build());

    [Fact]
    public async Task GetAsync_SameRequestTwice_ReadsDatabaseOnce()
    {
        await using var db = await CreateSeededDbAsync();
        var accessor = Accessor();
        var store = CreateStore(db, accessor, new MemoryCache(new MemoryCacheOptions()), cacheSeconds: 0);

        var first = await store.GetAsync(UserId);
        await DeactivateAsync(db);
        var second = await store.GetAsync(UserId);

        Assert.True(first.IsActive);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task GetAsync_NextRequestWithinTtl_ServedFromMemoryCache()
    {
        await using var db = await CreateSeededDbAsync();
        var cache = new MemoryCache(new MemoryCacheOptions());
        await CreateStore(db, Accessor(), cache).GetAsync(UserId);
        await DeactivateAsync(db);

        var nextRequest = await CreateStore(db, Accessor(), cache).GetAsync(UserId);

        Assert.True(nextRequest.IsActive);
    }

    [Fact]
    public async Task Invalidate_AfterChange_NextRequestReadsFreshData()
    {
        await using var db = await CreateSeededDbAsync();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var accessor = Accessor();
        var store = CreateStore(db, accessor, cache);
        await store.GetAsync(UserId);
        await DeactivateAsync(db);

        store.Invalidate(UserId);
        var sameRequest = await store.GetAsync(UserId);
        var nextRequest = await CreateStore(db, Accessor(), cache).GetAsync(UserId);

        Assert.False(sameRequest.IsActive);
        Assert.False(nextRequest.IsActive);
    }

    [Fact]
    public async Task GetAsync_CacheDisabled_NextRequestReadsFreshData()
    {
        await using var db = await CreateSeededDbAsync();
        var cache = new MemoryCache(new MemoryCacheOptions());
        await CreateStore(db, Accessor(), cache, cacheSeconds: 0).GetAsync(UserId);
        await DeactivateAsync(db);

        var nextRequest = await CreateStore(db, Accessor(), cache, cacheSeconds: 0).GetAsync(UserId);

        Assert.False(nextRequest.IsActive);
    }

    [Fact]
    public async Task GetAsync_UserWithSupplierLinkAndMembership_ReturnsBoth()
    {
        await using var db = await CreateSeededDbAsync();
        var supplierOrgId = Guid.NewGuid();
        var user = await db.Users.SingleAsync(u => u.Id == UserId);
        user.SupplierOrgId = supplierOrgId;
        db.UserContextMemberships.Add(new UserContextMembership { UserId = UserId, ContextKey = "short-rent", RoleId = 1 });
        await db.SaveChangesAsync();

        var snapshot = await CreateStore(db, Accessor(), new MemoryCache(new MemoryCacheOptions())).GetAsync(UserId);

        Assert.Equal(supplierOrgId, snapshot.SupplierOrgId);
        var membership = Assert.Single(snapshot.Memberships);
        Assert.Equal("short-rent", membership.ContextKey);
        Assert.Contains("booking.read", membership.Permissions);
    }

    [Fact]
    public async Task GrantAsync_BothRentalRoles_WritesMembershipForEachRole()
    {
        await using var db = await CreateSeededDbAsync();
        var store = CreateStore(db, Accessor(), new MemoryCache(new MemoryCacheOptions()));
        var service = new UserContextMembershipService(db, store, NullLogger<UserContextMembershipService>.Instance);

        await service.GrantAsync(UserId, [UserRole.PropertyOwner, UserRole.LongTermLandlord, UserRole.Supplier]);

        var contexts = await db.UserContextMemberships.Where(m => m.UserId == UserId)
            .Select(m => m.ContextKey).OrderBy(k => k).ToListAsync();
        Assert.Equal(["long-rent", "short-rent"], contexts);
    }

    [Fact]
    public async Task GrantAsync_CalledTwice_IsIdempotent()
    {
        await using var db = await CreateSeededDbAsync();
        var store = CreateStore(db, Accessor(), new MemoryCache(new MemoryCacheOptions()));
        var service = new UserContextMembershipService(db, store, NullLogger<UserContextMembershipService>.Instance);

        await service.GrantAsync(UserId, [UserRole.Admin]);
        await service.GrantAsync(UserId, [UserRole.Admin]);

        Assert.Equal(1, await db.UserContextMemberships.CountAsync(m => m.UserId == UserId && m.ContextKey == "admin"));
    }

    [Fact]
    public async Task RevokeAsync_RemovedRole_DeletesOnlyItsContextAndInvalidatesCache()
    {
        await using var db = await CreateSeededDbAsync();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var store = CreateStore(db, Accessor(), cache);
        var service = new UserContextMembershipService(db, store, NullLogger<UserContextMembershipService>.Instance);
        await service.GrantAsync(UserId, [UserRole.PropertyOwner, UserRole.Admin]);
        var before = await store.GetAsync(UserId);

        await service.RevokeAsync(UserId, [UserRole.PropertyOwner]);
        var after = await CreateStore(db, Accessor(), cache).GetAsync(UserId);

        Assert.Equal(2, before.Memberships.Count);
        var remaining = Assert.Single(after.Memberships);
        Assert.Equal("admin", remaining.ContextKey);
    }

    [Fact]
    public async Task HasPermissionAsync_RevokedLegacyMembership_NoLongerGrantsAccess()
    {
        await using var db = await CreateSeededDbAsync();
        // Legacy seed (pre-June): short-rent membership for every PropertyOwner row.
        db.UserContextMemberships.Add(new UserContextMembership { UserId = UserId, ContextKey = "short-rent", RoleId = 1 });
        // An onboarded host (PL-02): otherwise no host context is granted at all.
        var org = new OrgEntity { Name = "Cache org", Slug = $"cache-{Guid.NewGuid():N}" };
        db.Orgs.Add(org);
        var user = await db.Users.SingleAsync(u => u.Id == UserId);
        user.OrgId = org.Id;
        await HostOnboardingSeed.MarkOnboardedAsync(db, user, org.Id, Legal);
        await db.SaveChangesAsync();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var accessor = Accessor(jwtRoles: ["LongTermLandlord"]);
        var store = CreateStore(db, accessor, cache);
        var authorization = new ContextAuthorizationService(store, Legal, accessor, NullLogger<ContextAuthorizationService>.Instance);
        Assert.True(await authorization.HasPermissionAsync(UserId, "short-rent", "booking.read"));

        var memberships = new UserContextMembershipService(db, store, NullLogger<UserContextMembershipService>.Instance);
        await memberships.RevokeAsync(UserId, [UserRole.PropertyOwner]);

        var nextAccessor = Accessor(jwtRoles: ["LongTermLandlord"]);
        var nextRequest = new ContextAuthorizationService(
            CreateStore(db, nextAccessor, cache), Legal, nextAccessor, NullLogger<ContextAuthorizationService>.Instance);
        Assert.False(await nextRequest.HasPermissionAsync(UserId, "short-rent", "booking.read"));
    }

    private static async Task<AppDbContext> CreateSeededDbAsync()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new AppDbContext(options);
        // Applies the HasData seed: contexts short-rent/long-rent/admin and roles 1..3 with permissions.
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new User
        {
            Id = UserId,
            Email = "cache@test.com",
            FirstName = "Cache",
            LastName = "User",
            Role = UserRole.PropertyOwner,
            IsActive = true,
        });
        await db.SaveChangesAsync();
        return db;
    }

    private static async Task DeactivateAsync(AppDbContext db)
    {
        var user = await db.Users.SingleAsync(u => u.Id == UserId);
        user.IsActive = false;
        await db.SaveChangesAsync();
    }

    private static UserAuthorizationSnapshotStore CreateStore(
        AppDbContext db,
        IHttpContextAccessor accessor,
        IMemoryCache cache,
        int? cacheSeconds = null)
    {
        var settings = new Dictionary<string, string?>();
        if (cacheSeconds is not null)
            settings["Authorization:UserCacheSeconds"] = cacheSeconds.Value.ToString();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new UserAuthorizationSnapshotStore(db, cache, accessor, configuration);
    }

    private static HttpContextAccessor Accessor(string[]? jwtRoles = null)
    {
        var claims = new List<Claim> { new("sub", UserId) };
        claims.AddRange((jwtRoles ?? []).Select(r => new Claim("https://casazen.app/roles", r)));
        return new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")),
            },
        };
    }
}
