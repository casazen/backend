using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Repositories;

public class GuestRepositoryTests
{
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
    public async Task GetByEmailAsync_WithExistingEmail_ReturnsGuest()
    {
        // Arrange
        await _repository.AddAsync(new Guest
        {
            FirstName = "Alice",
            LastName = "Johnson",
            Email = "alice.johnson@example.com"
        });

        // Act
        var result = await _repository.GetByEmailAsync("alice.johnson@example.com");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("alice.johnson@example.com", result.Email);
        Assert.Equal("Alice", result.FirstName);
    }

    [Fact]
    public async Task GetByEmailAsync_WithNonExistentEmail_ReturnsNull()
    {
        // Arrange
        var nonExistentEmail = "nonexistent@example.com";

        // Act
        var result = await _repository.GetByEmailAsync(nonExistentEmail);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetByEmailAsync_IsCaseInsensitive()
    {
        // Arrange
        await _repository.AddAsync(new Guest
        {
            FirstName = "Bob",
            LastName = "Wilson",
            Email = "Bob.Wilson@Example.COM"
        });

        // Act
        var result = await _repository.GetByEmailAsync("bob.wilson@example.com");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Bob.Wilson@Example.COM", result.Email);
    }

    [Fact]
    public async Task GetAllAsync_ReturnsAllGuests()
    {
        // Arrange
        await _repository.AddAsync(new Guest
        {
            FirstName = "Guest1",
            LastName = "Test",
            Email = "guest1@example.com"
        });

        await _repository.AddAsync(new Guest
        {
            FirstName = "Guest2",
            LastName = "Test",
            Email = "guest2@example.com"
        });

        await _repository.AddAsync(new Guest
        {
            FirstName = "Guest3",
            LastName = "Test",
            Email = "guest3@example.com"
        });

        // Act
        var result = await _repository.GetAllAsync();

        // Assert
        Assert.Equal(3, result.Count());
    }

    [Fact]
    public async Task GetAllAsync_WithNoGuests_ReturnsEmpty()
    {
        // Act
        var result = await _repository.GetAllAsync();

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task SearchAsync_WithFirstNameMatch_ReturnsMatchingGuests()
    {
        // Arrange
        await _repository.AddAsync(new Guest
        {
            FirstName = "John",
            LastName = "Doe",
            Email = "john.doe@example.com"
        });

        await _repository.AddAsync(new Guest
        {
            FirstName = "Jane",
            LastName = "Smith",
            Email = "jane.smith@example.com"
        });

        // Act
        var result = await _repository.SearchAsync("John");

        // Assert
        Assert.Single(result);
        Assert.Equal("John", result.First().FirstName);
    }

    [Fact]
    public async Task SearchAsync_WithLastNameMatch_ReturnsMatchingGuests()
    {
        // Arrange
        await _repository.AddAsync(new Guest
        {
            FirstName = "John",
            LastName = "Doe",
            Email = "john.doe@example.com"
        });

        await _repository.AddAsync(new Guest
        {
            FirstName = "Jane",
            LastName = "Doe",
            Email = "jane.doe@example.com"
        });

        await _repository.AddAsync(new Guest
        {
            FirstName = "Bob",
            LastName = "Smith",
            Email = "bob.smith@example.com"
        });

        // Act
        var result = await _repository.SearchAsync("Doe");

        // Assert
        Assert.Equal(2, result.Count());
        Assert.All(result, g => Assert.Equal("Doe", g.LastName));
    }

    [Fact]
    public async Task SearchAsync_WithEmailMatch_ReturnsMatchingGuests()
    {
        // Arrange
        await _repository.AddAsync(new Guest
        {
            FirstName = "John",
            LastName = "Doe",
            Email = "john.doe@example.com"
        });

        await _repository.AddAsync(new Guest
        {
            FirstName = "Jane",
            LastName = "Smith",
            Email = "jane.smith@test.com"
        });

        // Act
        var result = await _repository.SearchAsync("example.com");

        // Assert
        Assert.Single(result);
        Assert.Contains("example.com", result.First().Email);
    }

    [Fact]
    public async Task SearchAsync_WithPhoneMatch_ReturnsMatchingGuests()
    {
        // Arrange
        await _repository.AddAsync(new Guest
        {
            FirstName = "John",
            LastName = "Doe",
            Email = "john.doe@example.com",
            PhoneNumber = "+39 123456789"
        });

        await _repository.AddAsync(new Guest
        {
            FirstName = "Jane",
            LastName = "Smith",
            Email = "jane.smith@example.com",
            PhoneNumber = "+39 987654321"
        });

        // Act
        var result = await _repository.SearchAsync("123456");

        // Assert
        Assert.Single(result);
        Assert.Contains("123456", result.First().PhoneNumber);
    }

    [Fact]
    public async Task SearchAsync_WithNullSearchTerm_ReturnsAllGuests()
    {
        // Arrange
        await _repository.AddAsync(new Guest
        {
            FirstName = "John",
            LastName = "Doe",
            Email = "john.doe@example.com"
        });

        await _repository.AddAsync(new Guest
        {
            FirstName = "Jane",
            LastName = "Smith",
            Email = "jane.smith@example.com"
        });

        // Act
        var result = await _repository.SearchAsync(null);

        // Assert
        Assert.Equal(2, result.Count());
    }

    [Fact]
    public async Task SearchAsync_WithEmptySearchTerm_ReturnsAllGuests()
    {
        // Arrange
        await _repository.AddAsync(new Guest
        {
            FirstName = "John",
            LastName = "Doe",
            Email = "john.doe@example.com"
        });

        // Act
        var result = await _repository.SearchAsync("");

        // Assert
        Assert.Single(result);
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
        await _repository.AddAsync(new Guest
        {
            FirstName = "John",
            LastName = "Doe",
            Email = "john.doe@example.com"
        });

        // Act
        var result = await _repository.ExistsByEmailAsync("john.doe@example.com");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task ExistsByEmailAsync_WithNonExistentEmail_ReturnsFalse()
    {
        // Act
        var result = await _repository.ExistsByEmailAsync("nonexistent@example.com");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task ExistsByEmailAsync_IsCaseInsensitive()
    {
        // Arrange
        await _repository.AddAsync(new Guest
        {
            FirstName = "John",
            LastName = "Doe",
            Email = "John.Doe@Example.COM"
        });

        // Act
        var result = await _repository.ExistsByEmailAsync("john.doe@example.com");

        // Assert
        Assert.True(result);
    }
}
