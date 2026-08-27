using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class GuestServiceTests
{
    private readonly Mock<IGuestRepository> _mockRepository;
    private readonly Mock<ILogger<GuestService>> _mockLogger;
    private readonly GuestService _service;

    public GuestServiceTests()
    {
        _mockRepository = new Mock<IGuestRepository>();
        _mockLogger = new Mock<ILogger<GuestService>>();
        _service = new GuestService(_mockRepository.Object, _mockLogger.Object);
    }

    [Fact]
    public async Task GetGuestAsync_WithValidId_ReturnsGuest()
    {
        // Arrange
        var guestId = Guid.NewGuid();
        var guest = new Guest
        {
            Id = guestId,
            FirstName = "John",
            LastName = "Doe",
            Email = "john.doe@example.com"
        };
        _mockRepository.Setup(x => x.GetByIdAsync(guestId)).ReturnsAsync(guest);

        // Act
        var result = await _service.GetGuestAsync(guestId);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(guestId, result.Id);
        Assert.Equal("John", result.FirstName);
        _mockRepository.Verify(x => x.GetByIdAsync(guestId), Times.Once);
    }

    [Fact]
    public async Task GetGuestAsync_WithNonExistentId_ReturnsNull()
    {
        // Arrange
        var guestId = Guid.NewGuid();
        _mockRepository.Setup(x => x.GetByIdAsync(guestId)).ReturnsAsync((Guest?)null);

        // Act
        var result = await _service.GetGuestAsync(guestId);

        // Assert
        Assert.Null(result);
        _mockRepository.Verify(x => x.GetByIdAsync(guestId), Times.Once);
    }

    [Fact]
    public async Task GetGuestByEmailAsync_WithValidEmail_ReturnsGuest()
    {
        // Arrange
        var email = "john.doe@example.com";
        var guest = new Guest
        {
            Id = Guid.NewGuid(),
            FirstName = "John",
            LastName = "Doe",
            Email = email
        };
        _mockRepository.Setup(x => x.GetByEmailAsync(email)).ReturnsAsync(guest);

        // Act
        var result = await _service.GetGuestByEmailAsync(email);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(email, result.Email);
        _mockRepository.Verify(x => x.GetByEmailAsync(email), Times.Once);
    }

    [Fact]
    public async Task GetGuestByEmailAsync_WithNonExistentEmail_ReturnsNull()
    {
        // Arrange
        var email = "nonexistent@example.com";
        _mockRepository.Setup(x => x.GetByEmailAsync(email)).ReturnsAsync((Guest?)null);

        // Act
        var result = await _service.GetGuestByEmailAsync(email);

        // Assert
        Assert.Null(result);
        _mockRepository.Verify(x => x.GetByEmailAsync(email), Times.Once);
    }

    [Fact]
    public async Task GetAllGuestsAsync_ReturnsAllGuests()
    {
        // Arrange
        var guests = new List<Guest>
        {
            new() { Id = Guid.NewGuid(), FirstName = "John", LastName = "Doe", Email = "john@example.com" },
            new() { Id = Guid.NewGuid(), FirstName = "Jane", LastName = "Smith", Email = "jane@example.com" }
        };
        _mockRepository.Setup(x => x.GetAllAsync()).ReturnsAsync(guests);

        // Act
        var result = await _service.GetAllGuestsAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count());
        _mockRepository.Verify(x => x.GetAllAsync(), Times.Once);
    }

    [Fact]
    public async Task GetAllGuestsAsync_WithNoGuests_ReturnsEmpty()
    {
        // Arrange
        _mockRepository.Setup(x => x.GetAllAsync()).ReturnsAsync(new List<Guest>());

        // Act
        var result = await _service.GetAllGuestsAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
        _mockRepository.Verify(x => x.GetAllAsync(), Times.Once);
    }

    [Fact]
    public async Task SearchGuestsAsync_WithSearchTerm_ReturnsMatchingGuests()
    {
        // Arrange
        var searchTerm = "John";
        var guests = new List<Guest>
        {
            new() { Id = Guid.NewGuid(), FirstName = "John", LastName = "Doe", Email = "john@example.com" }
        };
        _mockRepository.Setup(x => x.SearchAsync(searchTerm)).ReturnsAsync(guests);

        // Act
        var result = await _service.SearchGuestsAsync(searchTerm);

        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
        _mockRepository.Verify(x => x.SearchAsync(searchTerm), Times.Once);
    }

    [Fact]
    public async Task SearchGuestsAsync_WithNullSearchTerm_ReturnsAllGuests()
    {
        // Arrange
        var guests = new List<Guest>
        {
            new() { Id = Guid.NewGuid(), FirstName = "John", LastName = "Doe", Email = "john@example.com" },
            new() { Id = Guid.NewGuid(), FirstName = "Jane", LastName = "Smith", Email = "jane@example.com" }
        };
        _mockRepository.Setup(x => x.SearchAsync(null)).ReturnsAsync(guests);

        // Act
        var result = await _service.SearchGuestsAsync(null);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count());
        _mockRepository.Verify(x => x.SearchAsync(null), Times.Once);
    }

    [Fact]
    public async Task CreateGuestAsync_WithNewEmail_ReturnsCreatedGuest()
    {
        // Arrange
        var guest = new Guest
        {
            FirstName = "John",
            LastName = "Doe",
            Email = "john.doe@example.com"
        };
        _mockRepository.Setup(x => x.ExistsByEmailAsync(guest.Email)).ReturnsAsync(false);
        _mockRepository.Setup(x => x.AddAsync(It.IsAny<Guest>())).ReturnsAsync(guest);

        // Act
        var result = await _service.CreateGuestAsync(guest);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(guest.Email, result.Email);
        _mockRepository.Verify(x => x.ExistsByEmailAsync(guest.Email), Times.Once);
        _mockRepository.Verify(x => x.AddAsync(It.IsAny<Guest>()), Times.Once);
    }

    [Fact]
    public async Task CreateGuestAsync_WithExistingEmail_ThrowsInvalidOperationException()
    {
        // Arrange
        var guest = new Guest
        {
            FirstName = "John",
            LastName = "Doe",
            Email = "john.doe@example.com"
        };
        _mockRepository.Setup(x => x.ExistsByEmailAsync(guest.Email)).ReturnsAsync(true);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CreateGuestAsync(guest));

        Assert.Contains("already exists", exception.Message);
        _mockRepository.Verify(x => x.ExistsByEmailAsync(guest.Email), Times.Once);
        _mockRepository.Verify(x => x.AddAsync(It.IsAny<Guest>()), Times.Never);
    }

    [Fact]
    public async Task CreateGuestSnapshotAsync_WithExistingEmail_DoesNotCheckGlobalEmailUniqueness()
    {
        // Arrange
        var guest = new Guest
        {
            FirstName = "John",
            LastName = "Doe",
            Email = "john.doe@example.com"
        };
        _mockRepository.Setup(x => x.AddAsync(It.IsAny<Guest>())).ReturnsAsync(guest);

        // Act
        var result = await _service.CreateGuestSnapshotAsync(guest);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(guest.Email, result.Email);
        _mockRepository.Verify(x => x.ExistsByEmailAsync(It.IsAny<string>()), Times.Never);
        _mockRepository.Verify(x => x.AddAsync(It.IsAny<Guest>()), Times.Once);
    }

    [Fact]
    public async Task UpdateGuestAsync_WithExistingGuest_ReturnsUpdatedGuest()
    {
        // Arrange
        var guest = new Guest
        {
            Id = Guid.NewGuid(),
            FirstName = "John",
            LastName = "Doe",
            Email = "john.doe@example.com"
        };
        _mockRepository.Setup(x => x.ExistsAsync(guest.Id)).ReturnsAsync(true);
        _mockRepository.Setup(x => x.UpdateAsync(It.IsAny<Guest>())).ReturnsAsync(guest);

        // Act
        var result = await _service.UpdateGuestAsync(guest);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(guest.Id, result.Id);
        _mockRepository.Verify(x => x.ExistsAsync(guest.Id), Times.Once);
        _mockRepository.Verify(x => x.UpdateAsync(It.IsAny<Guest>()), Times.Once);
    }

    [Fact]
    public async Task UpdateGuestAsync_WithNonExistentGuest_ThrowsInvalidOperationException()
    {
        // Arrange
        var guest = new Guest
        {
            Id = Guid.NewGuid(),
            FirstName = "John",
            LastName = "Doe",
            Email = "john.doe@example.com"
        };
        _mockRepository.Setup(x => x.ExistsAsync(guest.Id)).ReturnsAsync(false);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.UpdateGuestAsync(guest));

        Assert.Contains("not found", exception.Message);
        _mockRepository.Verify(x => x.ExistsAsync(guest.Id), Times.Once);
        _mockRepository.Verify(x => x.UpdateAsync(It.IsAny<Guest>()), Times.Never);
    }

    [Fact]
    public async Task DeleteGuestAsync_WithExistingGuest_ReturnsTrue()
    {
        // Arrange
        var guestId = Guid.NewGuid();
        _mockRepository.Setup(x => x.ExistsAsync(guestId)).ReturnsAsync(true);
        _mockRepository.Setup(x => x.DeleteAsync(guestId)).Returns(Task.CompletedTask);

        // Act
        var result = await _service.DeleteGuestAsync(guestId);

        // Assert
        Assert.True(result);
        _mockRepository.Verify(x => x.ExistsAsync(guestId), Times.Once);
        _mockRepository.Verify(x => x.DeleteAsync(guestId), Times.Once);
    }

    [Fact]
    public async Task DeleteGuestAsync_WithNonExistentGuest_ReturnsFalse()
    {
        // Arrange
        var guestId = Guid.NewGuid();
        _mockRepository.Setup(x => x.ExistsAsync(guestId)).ReturnsAsync(false);

        // Act
        var result = await _service.DeleteGuestAsync(guestId);

        // Assert
        Assert.False(result);
        _mockRepository.Verify(x => x.ExistsAsync(guestId), Times.Once);
        _mockRepository.Verify(x => x.DeleteAsync(guestId), Times.Never);
    }
}
