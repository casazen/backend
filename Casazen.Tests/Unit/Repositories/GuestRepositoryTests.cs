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
        // Arrange
        var guest = new Guest
        {
            FirstName = "John",
            LastName = "Doe",
            Email = "john.doe@example.com",
            PhoneNumber = "+39 123456789"
        };

        // Act
        var result = await _repository.AddAsync(guest);

        // Assert
        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.Id);
        var savedGuest = await _context.Guests.FindAsync(result.Id);
        Assert.NotNull(savedGuest);
        Assert.Equal("john.doe@example.com", savedGuest.Email);
    }

    [Fact]
    public async Task GetByIdAsync_WithExistingId_ReturnsGuest()
    {
        // Arrange
        var guest = await _repository.AddAsync(new Guest
        {
            FirstName = "Jane",
            LastName = "Smith",
            Email = "jane.smith@example.com"
        });

        // Act
        var result = await _repository.GetByIdAsync(guest.Id);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(guest.Id, result.Id);
        Assert.Equal("Jane", result.FirstName);
        Assert.Equal("jane.smith@example.com", result.Email);
    }

    [Fact]
    public async Task GetByIdAsync_WithNonExistentId_ReturnsNull()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();

        // Act
        var result = await _repository.GetByIdAsync(nonExistentId);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetByIdInOrgAsync_GuestOfSameOrg_ReturnsGuest()
    {
        // Arrange
        var guest = await _repository.AddAsync(NewGuest("Jane", "Smith", "jane.smith@example.com", OrgA));

        // Act
        var result = await _repository.GetByIdInOrgAsync(OrgA, guest.Id);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(guest.Id, result.Id);
    }

    [Fact]
    public async Task GetByIdInOrgAsync_GuestOfOtherOrg_ReturnsNull()
    {
        // Arrange
        var guest = await _repository.AddAsync(NewGuest("Jane", "Smith", "jane.smith@example.com", OrgA));

        // Act
        var result = await _repository.GetByIdInOrgAsync(OrgB, guest.Id);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetByEmailAsync_WithExistingEmail_ReturnsGuest()
    {
        // Arrange
        await _repository.AddAsync(NewGuest("Alice", "Johnson", "alice.johnson@example.com", OrgA));

        // Act
        var result = await _repository.GetByEmailAsync(OrgA, "alice.johnson@example.com");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("alice.johnson@example.com", result.Email);
        Assert.Equal("Alice", result.FirstName);
    }

    [Fact]
    public async Task GetByEmailAsync_WithNonExistentEmail_ReturnsNull()
    {
        // Act
        var result = await _repository.GetByEmailAsync(OrgA, "nonexistent@example.com");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetByEmailAsync_IsCaseInsensitive()
    {
        // Arrange
        await _repository.AddAsync(NewGuest("Bob", "Wilson", "Bob.Wilson@Example.COM", OrgA));

        // Act
        var result = await _repository.GetByEmailAsync(OrgA, "bob.wilson@example.com");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Bob.Wilson@Example.COM", result.Email);
    }

    [Fact]
    public async Task GetByEmailAsync_SameEmailOnlyInOtherOrg_ReturnsNull()
    {
        // Arrange
        await _repository.AddAsync(NewGuest("Mario", "Rossi", "mario@example.com", OrgA));

        // Act
        var result = await _repository.GetByEmailAsync(OrgB, "mario@example.com");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetPageAsync_GuestsOfTwoOrgs_ReturnsOnlyRequestedOrg()
    {
        // Arrange
        await _repository.AddAsync(NewGuest("Guest1", "Test", "guest1@example.com", OrgA));
        await _repository.AddAsync(NewGuest("Guest2", "Test", "guest2@example.com", OrgA));
        await _repository.AddAsync(NewGuest("Other", "Test", "guest1@example.com", OrgB));

        // Act
        var (items, total) = await _repository.GetPageAsync(OrgA, null, 1, 20);

        // Assert
        Assert.Equal(2, total);
        Assert.All(items, g => Assert.Equal(OrgA, g.OrgId));
    }

    [Fact]
    public async Task GetPageAsync_WithNoGuests_ReturnsEmpty()
    {
        // Act
        var (items, total) = await _repository.GetPageAsync(OrgA, null, 1, 20);

        // Assert
        Assert.Empty(items);
        Assert.Equal(0, total);
    }

    [Fact]
    public async Task GetPageAsync_DeletedGuest_IsExcluded()
    {
        // Arrange
        await _repository.AddAsync(NewGuest("Kept", "Test", "kept@example.com", OrgA));
        var deleted = NewGuest("Deleted", "Test", "deleted@example.com", OrgA);
        deleted.IsDeleted = true;
        await _repository.AddAsync(deleted);

        // Act
        var (items, total) = await _repository.GetPageAsync(OrgA, null, 1, 20);

        // Assert
        Assert.Equal(1, total);
        Assert.Equal("Kept", Assert.Single(items).FirstName);
    }

    [Fact]
    public async Task GetPageAsync_SecondPage_ReturnsRemainingGuestsNewestFirst()
    {
        // Arrange
        var now = DateTime.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            var guest = NewGuest($"Guest{i}", "Test", $"guest{i}@example.com", OrgA);
            guest.CreatedAt = now.AddMinutes(i);
            await _repository.AddAsync(guest);
        }

        // Act
        var (items, total) = await _repository.GetPageAsync(OrgA, null, 2, 2);

        // Assert
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
        // Arrange
        var john = NewGuest("John", "Doe", "john.doe@example.com", OrgA);
        john.PhoneNumber = "+39 123456789";
        await _repository.AddAsync(john);
        await _repository.AddAsync(NewGuest("Jane", "Smith", "jane.smith@test.com", OrgA));
        var otherOrgJohn = NewGuest("John", "Doe", "john.doe@example.com", OrgB);
        otherOrgJohn.PhoneNumber = "+39 123456789";
        await _repository.AddAsync(otherOrgJohn);

        // Act
        var (items, total) = await _repository.GetPageAsync(OrgA, searchTerm, 1, 20);

        // Assert
        Assert.Equal(1, total);
        var match = Assert.Single(items);
        Assert.Equal(expectedFirstName, match.FirstName);
        Assert.Equal(OrgA, match.OrgId);
    }

    [Fact]
    public async Task UpdateAsync_WithValidGuest_UpdatesGuest()
    {
        // Arrange
        var guest = await _repository.AddAsync(new Guest
        {
            FirstName = "John",
            LastName = "Doe",
            Email = "john.doe@example.com"
        });

        guest.FirstName = "Johnny";
        guest.PhoneNumber = "+39 123456789";

        // Act
        var result = await _repository.UpdateAsync(guest);

        // Assert
        Assert.Equal("Johnny", result.FirstName);
        Assert.Equal("+39 123456789", result.PhoneNumber);

        var updated = await _repository.GetByIdAsync(guest.Id);
        Assert.Equal("Johnny", updated?.FirstName);
    }

    [Fact]
    public async Task UpdateAsync_UpdatesUpdatedAtTimestamp()
    {
        // Arrange
        var guest = await _repository.AddAsync(new Guest
        {
            FirstName = "John",
            LastName = "Doe",
            Email = "john.doe@example.com"
        });

        var originalUpdatedAt = guest.UpdatedAt;
        await Task.Delay(100);

        guest.FirstName = "Johnny";

        // Act
        var result = await _repository.UpdateAsync(guest);

        // Assert
        Assert.True(result.UpdatedAt > originalUpdatedAt);
    }

    [Fact]
    public async Task DeleteAsync_WithExistingId_DeletesGuest()
    {
        // Arrange
        var guest = await _repository.AddAsync(new Guest
        {
            FirstName = "John",
            LastName = "Doe",
            Email = "john.doe@example.com"
        });

        // Act
        await _repository.DeleteAsync(guest.Id);

        // Assert
        var deleted = await _repository.GetByIdAsync(guest.Id);
        Assert.Null(deleted);
    }

    [Fact]
    public async Task DeleteAsync_WithNonExistentId_DoesNotThrow()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();

        // Act & Assert (should not throw)
        await _repository.DeleteAsync(nonExistentId);
    }

    [Fact]
    public async Task ExistsAsync_WithExistingId_ReturnsTrue()
    {
        // Arrange
        var guest = await _repository.AddAsync(new Guest
        {
            FirstName = "John",
            LastName = "Doe",
            Email = "john.doe@example.com"
        });

        // Act
        var result = await _repository.ExistsAsync(guest.Id);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task ExistsAsync_WithNonExistentId_ReturnsFalse()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();

        // Act
        var result = await _repository.ExistsAsync(nonExistentId);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task ExistsByEmailAsync_WithExistingEmail_ReturnsTrue()
    {
        // Arrange
        await _repository.AddAsync(NewGuest("John", "Doe", "john.doe@example.com", OrgA));

        // Act
        var result = await _repository.ExistsByEmailAsync(OrgA, "john.doe@example.com");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task ExistsByEmailAsync_WithNonExistentEmail_ReturnsFalse()
    {
        // Act
        var result = await _repository.ExistsByEmailAsync(OrgA, "nonexistent@example.com");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task ExistsByEmailAsync_IsCaseInsensitive()
    {
        // Arrange
        await _repository.AddAsync(NewGuest("John", "Doe", "John.Doe@Example.COM", OrgA));

        // Act
        var result = await _repository.ExistsByEmailAsync(OrgA, "john.doe@example.com");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task ExistsByEmailAsync_EmailOnlyInOtherOrg_ReturnsFalse()
    {
        // Arrange
        await _repository.AddAsync(NewGuest("John", "Doe", "john.doe@example.com", OrgA));

        // Act
        var result = await _repository.ExistsByEmailAsync(OrgB, "john.doe@example.com");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task GetUsageAsync_GuestWithoutBookings_HasNoReferences()
    {
        // Arrange
        var guest = await _repository.AddAsync(NewGuest("John", "Doe", "john.doe@example.com", OrgA));

        // Act
        var usage = await _repository.GetUsageAsync(guest.Id, Today);

        // Assert
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
        // Arrange
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

        // Act
        var usage = await _repository.GetUsageAsync(guest.Id, Today);

        // Assert
        Assert.True(usage.HasReferences);
        Assert.Equal(expectedOpen, usage.HasOpenBookings);
    }

    private static Guest NewGuest(string firstName, string lastName, string email, Guid orgId) => new()
    {
        OrgId = orgId,
        FirstName = firstName,
        LastName = lastName,
        Email = email,
    };
}
