using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Repositories;

public class GuestRepositoryTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();

    private static readonly HostScope ScopeA = new(OrgA);
    private static readonly HostScope ScopeB = new(OrgB);
    private static readonly DateTime Today = new(2026, 9, 23, 0, 0, 0, DateTimeKind.Utc);

    private readonly AppDbContext _context;
    private readonly GuestRepository _repository;

    public GuestRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _context = new AppDbContext(options);
        _repository = new GuestRepository(_context);
    }

    [Fact]
    public async Task AddAsync_WithValidGuest_AddsGuest()
    {
        var guest = new Guest
        {
            FirstName = "John",
            LastName = "Doe",
            Email = "john.doe@example.com",
            PhoneNumber = "+39 123456789"
        };

        var result = await _repository.AddAsync(guest);

        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.Id);
        var savedGuest = await _context.Guests.FindAsync(result.Id);
        Assert.NotNull(savedGuest);
        Assert.Equal("john.doe@example.com", savedGuest.Email);
    }

    [Fact]
    public async Task GetByIdAsync_WithExistingId_ReturnsGuest()
    {
        var guest = await _repository.AddAsync(new Guest
        {
            FirstName = "Jane",
            LastName = "Smith",
            Email = "jane.smith@example.com"
        });

        var result = await _repository.GetByIdAsync(guest.Id);

        Assert.NotNull(result);
        Assert.Equal(guest.Id, result.Id);
        Assert.Equal("Jane", result.FirstName);
        Assert.Equal("jane.smith@example.com", result.Email);
    }

    [Fact]
    public async Task GetByIdAsync_WithNonExistentId_ReturnsNull()
    {
        var nonExistentId = Guid.NewGuid();

        var result = await _repository.GetByIdAsync(nonExistentId);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetByIdInOrgAsync_GuestOfSameOrg_ReturnsGuest()
    {
        var guest = await _repository.AddAsync(NewGuest("Jane", "Smith", "jane.smith@example.com", OrgA));

        var result = await _repository.GetByIdInScopeAsync(ScopeA, guest.Id);

        Assert.NotNull(result);
        Assert.Equal(guest.Id, result.Id);
    }

    [Fact]
    public async Task GetByIdInOrgAsync_GuestOfOtherOrg_ReturnsNull()
    {
        var guest = await _repository.AddAsync(NewGuest("Jane", "Smith", "jane.smith@example.com", OrgA));

        var result = await _repository.GetByIdInScopeAsync(ScopeB, guest.Id);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetByEmailAsync_WithExistingEmail_ReturnsGuest()
    {
        await _repository.AddAsync(NewGuest("Alice", "Johnson", "alice.johnson@example.com", OrgA));

        var result = await _repository.GetByEmailAsync(ScopeA, "alice.johnson@example.com");

        Assert.NotNull(result);
        Assert.Equal("alice.johnson@example.com", result.Email);
        Assert.Equal("Alice", result.FirstName);
    }

    [Fact]
    public async Task GetByEmailAsync_WithNonExistentEmail_ReturnsNull()
    {
        var result = await _repository.GetByEmailAsync(ScopeA, "nonexistent@example.com");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetByEmailAsync_IsCaseInsensitive()
    {
        await _repository.AddAsync(NewGuest("Bob", "Wilson", "Bob.Wilson@Example.COM", OrgA));

        var result = await _repository.GetByEmailAsync(ScopeA, "bob.wilson@example.com");

        Assert.NotNull(result);
        Assert.Equal("Bob.Wilson@Example.COM", result.Email);
    }

    [Fact]
    public async Task GetByEmailAsync_SameEmailOnlyInOtherOrg_ReturnsNull()
    {
        await _repository.AddAsync(NewGuest("Mario", "Rossi", "mario@example.com", OrgA));

        var result = await _repository.GetByEmailAsync(ScopeB, "mario@example.com");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetPageAsync_GuestsOfTwoOrgs_ReturnsOnlyRequestedOrg()
    {
        await AddBooker("Guest1", "Test", "guest1@example.com", OrgA);
        await AddBooker("Guest2", "Test", "guest2@example.com", OrgA);
        await AddBooker("Other", "Test", "guest1@example.com", OrgB);

        var (items, total) = await _repository.GetPageAsync(ScopeA, null, 1, 20);

        Assert.Equal(2, total);
        Assert.All(items, g => Assert.Equal(OrgA, g.OrgId));
    }

    [Fact]
    public async Task GetPageAsync_WithNoGuests_ReturnsEmpty()
    {
        var (items, total) = await _repository.GetPageAsync(ScopeA, null, 1, 20);

        Assert.Empty(items);
        Assert.Equal(0, total);
    }

    [Fact]
    public async Task GetPageAsync_DeletedGuest_IsExcluded()
    {
        await AddBooker("Kept", "Test", "kept@example.com", OrgA);
        var deleted = NewGuest("Deleted", "Test", "deleted@example.com", OrgA);
        deleted.IsDeleted = true;
        await _repository.AddAsync(deleted);
        await AttachBooking(deleted);

        var (items, total) = await _repository.GetPageAsync(ScopeA, null, 1, 20);

        Assert.Equal(1, total);
        Assert.Equal("Kept", Assert.Single(items).FirstName);
    }

    [Fact]
    public async Task GetPageAsync_SecondPage_ReturnsRemainingGuestsNewestFirst()
    {
        var now = DateTime.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            var guest = NewGuest($"Guest{i}", "Test", $"guest{i}@example.com", OrgA);
            guest.CreatedAt = now.AddMinutes(i);
            await _repository.AddAsync(guest);
            await AttachBooking(guest);
        }

        var (items, total) = await _repository.GetPageAsync(ScopeA, null, 2, 2);

        Assert.Equal(5, total);
        Assert.Equal(["Guest2", "Guest1"], items.Select(g => g.FirstName));
    }

    [Theory]
    [InlineData("John", "John")]
    [InlineData("doe", "John")]
    [InlineData("EXAMPLE.com", "John")]
    [InlineData("123456", "John")]
    public async Task GetPageAsync_WithSearchTerm_ReturnsMatchingGuestsOfOrg(string searchTerm, string expectedFirstName)
    {
        var john = NewGuest("John", "Doe", "john.doe@example.com", OrgA);
        john.PhoneNumber = "+39 123456789";
        await _repository.AddAsync(john);
        await AttachBooking(john);
        await _repository.AddAsync(NewGuest("Jane", "Smith", "jane.smith@test.com", OrgA));
        var otherOrgJohn = NewGuest("John", "Doe", "john.doe@example.com", OrgB);
        otherOrgJohn.PhoneNumber = "+39 123456789";
        await _repository.AddAsync(otherOrgJohn);
        await AttachBooking(otherOrgJohn);

        var (items, total) = await _repository.GetPageAsync(ScopeA, searchTerm, 1, 20);

        Assert.Equal(1, total);
        var match = Assert.Single(items);
        Assert.Equal(expectedFirstName, match.FirstName);
        Assert.Equal(OrgA, match.OrgId);
    }

    [Fact]
    public async Task UpdateAsync_WithValidGuest_UpdatesGuest()
    {
        var guest = await _repository.AddAsync(new Guest
        {
            FirstName = "John",
            LastName = "Doe",
            Email = "john.doe@example.com"
        });

        guest.FirstName = "Johnny";
        guest.PhoneNumber = "+39 123456789";

        var result = await _repository.UpdateAsync(guest);

        Assert.Equal("Johnny", result.FirstName);
        Assert.Equal("+39 123456789", result.PhoneNumber);

        var updated = await _repository.GetByIdAsync(guest.Id);
        Assert.Equal("Johnny", updated?.FirstName);
    }

    [Fact]
    public async Task UpdateAsync_UpdatesUpdatedAtTimestamp()
    {
        var guest = await _repository.AddAsync(new Guest
        {
            FirstName = "John",
            LastName = "Doe",
            Email = "john.doe@example.com"
        });

        var originalUpdatedAt = guest.UpdatedAt;
        await Task.Delay(100);

        guest.FirstName = "Johnny";

        var result = await _repository.UpdateAsync(guest);

        Assert.True(result.UpdatedAt > originalUpdatedAt);
    }

    [Fact]
    public async Task DeleteAsync_WithExistingId_DeletesGuest()
    {
        var guest = await _repository.AddAsync(new Guest
        {
            FirstName = "John",
            LastName = "Doe",
            Email = "john.doe@example.com"
        });

        await _repository.DeleteAsync(guest.Id);

        var deleted = await _repository.GetByIdAsync(guest.Id);
        Assert.Null(deleted);
    }

    [Fact]
    public async Task DeleteAsync_WithNonExistentId_DoesNotThrow()
    {
        var nonExistentId = Guid.NewGuid();

        await _repository.DeleteAsync(nonExistentId);
    }

    [Fact]
    public async Task ExistsAsync_WithExistingId_ReturnsTrue()
    {
        var guest = await _repository.AddAsync(new Guest
        {
            FirstName = "John",
            LastName = "Doe",
            Email = "john.doe@example.com"
        });

        var result = await _repository.ExistsAsync(guest.Id);

        Assert.True(result);
    }

    [Fact]
    public async Task ExistsAsync_WithNonExistentId_ReturnsFalse()
    {
        var nonExistentId = Guid.NewGuid();

        var result = await _repository.ExistsAsync(nonExistentId);

        Assert.False(result);
    }

    [Fact]
    public async Task ExistsByEmailAsync_WithExistingEmail_ReturnsTrue()
    {
        await _repository.AddAsync(NewGuest("John", "Doe", "john.doe@example.com", OrgA));

        var result = await _repository.ExistsByEmailAsync(OrgA, "john.doe@example.com");

        Assert.True(result);
    }

    [Fact]
    public async Task ExistsByEmailAsync_WithNonExistentEmail_ReturnsFalse()
    {
        var result = await _repository.ExistsByEmailAsync(OrgA, "nonexistent@example.com");

        Assert.False(result);
    }

    [Fact]
    public async Task ExistsByEmailAsync_IsCaseInsensitive()
    {
        await _repository.AddAsync(NewGuest("John", "Doe", "John.Doe@Example.COM", OrgA));

        var result = await _repository.ExistsByEmailAsync(OrgA, "john.doe@example.com");

        Assert.True(result);
    }

    [Fact]
    public async Task ExistsByEmailAsync_EmailOnlyInOtherOrg_ReturnsFalse()
    {
        await _repository.AddAsync(NewGuest("John", "Doe", "john.doe@example.com", OrgA));

        var result = await _repository.ExistsByEmailAsync(OrgB, "john.doe@example.com");

        Assert.False(result);
    }

    [Fact]
    public async Task GetUsageAsync_GuestWithoutBookings_HasNoReferences()
    {
        var guest = await _repository.AddAsync(NewGuest("John", "Doe", "john.doe@example.com", OrgA));

        var usage = await _repository.GetUsageAsync(guest.Id, Today);

        Assert.False(usage.HasReferences);
        Assert.False(usage.HasOpenBookings);
    }

    [Theory]
    [InlineData(BookingStatus.CheckedOut, -5, false)]
    [InlineData(BookingStatus.Cancelled, 10, false)]
    [InlineData(BookingStatus.Confirmed, -1, true)]
    [InlineData(BookingStatus.Confirmed, 0, true)]
    [InlineData(BookingStatus.Pending, 10, true)]
    [InlineData(BookingStatus.CheckedIn, -1, true)]
    [InlineData(BookingStatus.CheckedIn, 2, true)]
    public async Task GetUsageAsync_GuestWithBooking_ReportsReferenceAndOpenState(
        BookingStatus status, int checkOutOffsetDays, bool expectedOpen)
    {
        var guest = await _repository.AddAsync(NewGuest("John", "Doe", "john.doe@example.com", OrgA));
        _context.Bookings.Add(new Booking
        {
            PropertyId = Guid.NewGuid(),
            OrgId = OrgA,
            GuestId = guest.Id,
            CheckInDate = Today.AddDays(checkOutOffsetDays - 2),
            CheckOutDate = Today.AddDays(checkOutOffsetDays),
            Status = status,
        });
        await _context.SaveChangesAsync();

        var usage = await _repository.GetUsageAsync(guest.Id, Today);

        Assert.True(usage.HasReferences);
        Assert.Equal(expectedOpen, usage.HasOpenBookings);
    }

    [Fact]
    public async Task GetPageAsync_GuestWithoutBooking_IsExcluded()
    {
        await _repository.AddAsync(NewGuest("Companion", "Only", "companion@example.com", OrgA));
        await AddBooker("Booker", "Test", "booker@example.com", OrgA);

        var (items, total) = await _repository.GetPageAsync(ScopeA, null, 1, 20);

        Assert.Equal(1, total);
        Assert.Equal("Booker", Assert.Single(items).FirstName);
    }

    private async Task<Guest> AddBooker(string firstName, string lastName, string email, Guid orgId)
    {
        var guest = await _repository.AddAsync(NewGuest(firstName, lastName, email, orgId));
        await AttachBooking(guest);
        return guest;
    }

    private async Task AttachBooking(Guest guest)
    {
        _context.Bookings.Add(new Booking
        {
            PropertyId = Guid.NewGuid(),
            OrgId = guest.OrgId,
            GuestId = guest.Id,
            CheckInDate = Today,
            CheckOutDate = Today.AddDays(2),
            Status = BookingStatus.Confirmed,
        });
        await _context.SaveChangesAsync();
    }

    private static Guest NewGuest(string firstName, string lastName, string email, Guid orgId) => new()
    {
        OrgId = orgId,
        FirstName = firstName,
        LastName = lastName,
        Email = email,
    };
}
