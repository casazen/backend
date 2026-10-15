using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>UI-13a: the last context a user entered is stored on its own row, and only that column (and the update time) is written.</summary>
public class UserServiceLastContextTests
{
    private readonly DbContextOptions<AppDbContext> _options =
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"last-context-{Guid.NewGuid():N}").Options;

    private UserService NewService(AppDbContext db) =>
        new(
            new UserRepository(db),
            Mock.Of<IAuth0ManagementService>(),
            Mock.Of<IOrgService>(),
            Mock.Of<IUserContextMembershipService>(),
            Mock.Of<IOrgMembershipService>(),
            Mock.Of<IUserAuthorizationCache>(),
            NullLogger<UserService>.Instance);

    private async Task<User> SeedAsync(string? lastContext = null)
    {
        await using var db = new AppDbContext(_options);
        var user = new User
        {
            Id = $"auth0|ctx-{Guid.NewGuid():N}",
            Email = "ctx@example.com",
            FirstName = "Ada",
            LastName = "Lovelace",
            LastUsedContextKey = lastContext,
            UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private async Task<User> ReadAsync(string id)
    {
        await using var db = new AppDbContext(_options);
        return await db.Users.AsNoTracking().SingleAsync(u => u.Id == id);
    }

    [Fact]
    public async Task SetLastUsedContext_StoresTheKey_AndTouchesTheUpdateTime()
    {
        var user = await SeedAsync();
        await using var db = new AppDbContext(_options);

        var result = await NewService(db).SetLastUsedContextAsync(user.Id, "short-rent");

        Assert.Equal("short-rent", result.LastUsedContextKey);
        var stored = await ReadAsync(user.Id);
        Assert.Equal("short-rent", stored.LastUsedContextKey);
        Assert.True(stored.UpdatedAt > user.UpdatedAt);
    }

    [Fact]
    public async Task SetLastUsedContext_ChangesTheKey_WhenTheUserEntersAnotherArea()
    {
        var user = await SeedAsync("short-rent");
        await using var db = new AppDbContext(_options);

        await NewService(db).SetLastUsedContextAsync(user.Id, "supplier");

        Assert.Equal("supplier", (await ReadAsync(user.Id)).LastUsedContextKey);
    }

    [Fact]
    public async Task SetLastUsedContext_TheSameKeyAgain_WritesNothing()
    {
        var user = await SeedAsync("long-rent");
        await using var db = new AppDbContext(_options);

        var result = await NewService(db).SetLastUsedContextAsync(user.Id, "long-rent");

        Assert.Equal("long-rent", result.LastUsedContextKey);
        // The client tells it at every change of area: an unchanged key is not a write (the update time stays).
        Assert.Equal(user.UpdatedAt, (await ReadAsync(user.Id)).UpdatedAt);
        Assert.DoesNotContain(db.ChangeTracker.Entries(), entry => entry.State != EntityState.Unchanged);
    }

    [Fact]
    public async Task SetLastUsedContext_WritesNoOtherColumn()
    {
        var user = await SeedAsync();
        await using var db = new AppDbContext(_options);
        var service = NewService(db);

        await service.SetLastUsedContextAsync(user.Id, "admin");

        var stored = await ReadAsync(user.Id);
        Assert.Equal((user.Email, user.FirstName, user.LastName, user.IsActive, user.Role), (stored.Email, stored.FirstName, stored.LastName, stored.IsActive, stored.Role));
        Assert.Equal(user.OrgId, stored.OrgId);
    }

    [Fact]
    public async Task SetLastUsedContext_AUserThatDoesNotExist_IsNotFound()
    {
        await using var db = new AppDbContext(_options);

        await Assert.ThrowsAsync<NotFoundException>(() => NewService(db).SetLastUsedContextAsync("auth0|nobody", "short-rent"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task SetLastUsedContext_ABlankKeyOrUser_IsRefused(string blank)
    {
        await using var db = new AppDbContext(_options);
        var service = NewService(db);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.SetLastUsedContextAsync("auth0|someone", blank));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.SetLastUsedContextAsync(blank, "short-rent"));
    }
}
