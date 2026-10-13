using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class GuestServiceTests
{
    private static readonly Guid OrgId = Guid.Parse("00000000-0000-0000-0000-0000000000aa");

    // 2026-09-23 22:30 UTC is already 2026-09-24 in Rome (CEST).
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 22, 30, 0, TimeSpan.Zero);
    private static readonly DateTime RomeToday = new(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IGuestRepository> _mockRepository;
    private readonly Mock<IGdprService> _mockGdprService;
    private readonly GuestService _service;

    public GuestServiceTests()
    {
        _mockRepository = new Mock<IGuestRepository>();
        _mockGdprService = new Mock<IGdprService>();
        _service = new GuestService(
            _mockRepository.Object,
            _mockGdprService.Object,
            new FixedTimeProvider(Now),
            new Mock<ILogger<GuestService>>().Object);
    }

    [Fact]
    public async Task GetGuestAsync_WithGuestOfOrg_ReturnsGuest()
    {
        // Arrange
        var guestId = Guid.NewGuid();
        var guest = new Guest { Id = guestId, OrgId = OrgId, FirstName = "John", LastName = "Doe", Email = "john.doe@example.com" };
        _mockRepository.Setup(x => x.GetByIdInOrgAsync(OrgId, guestId, It.IsAny<CancellationToken>())).ReturnsAsync(guest);

        // Act
        var result = await _service.GetGuestAsync(OrgId, guestId);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(guestId, result.Id);
        _mockRepository.Verify(x => x.GetByIdAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task GetGuestAsync_WithGuestNotInOrg_ReturnsNull()
    {
        // Arrange
        var guestId = Guid.NewGuid();
        _mockRepository.Setup(x => x.GetByIdInOrgAsync(OrgId, guestId, It.IsAny<CancellationToken>())).ReturnsAsync((Guest?)null);

        // Act
        var result = await _service.GetGuestAsync(OrgId, guestId);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetGuestByEmailAsync_LooksUpOnlyInCallerOrg()
    {
        // Arrange
        var email = "john.doe@example.com";
        var guest = new Guest { Id = Guid.NewGuid(), OrgId = OrgId, Email = email };
        _mockRepository.Setup(x => x.GetByEmailAsync(OrgId, email, It.IsAny<CancellationToken>())).ReturnsAsync(guest);

        // Act
        var result = await _service.GetGuestByEmailAsync(OrgId, email);

        // Assert
        Assert.Same(guest, result);
        _mockRepository.Verify(x => x.GetByEmailAsync(OrgId, email, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetGuestsPageAsync_ForwardsOrgSearchAndPage()
    {
        // Arrange
        IReadOnlyList<Guest> guests = [new Guest { Id = Guid.NewGuid(), OrgId = OrgId, FirstName = "John" }];
        _mockRepository
            .Setup(x => x.GetPageAsync(OrgId, "John", 2, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync((guests, 11));

        // Act
        var (items, total) = await _service.GetGuestsPageAsync(OrgId, "John", 2, 10);

        // Assert
        Assert.Single(items);
        Assert.Equal(11, total);
    }

    [Fact]
    public async Task CreateGuestAsync_WithNewEmail_CreatesGuestInCallerOrg()
    {
        // Arrange
        var guest = new Guest { FirstName = "John", LastName = "Doe", Email = "john.doe@example.com" };
        _mockRepository.Setup(x => x.ExistsByEmailAsync(OrgId, guest.Email, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _mockRepository.Setup(x => x.AddAsync(It.IsAny<Guest>())).ReturnsAsync((Guest g) => g);

        // Act
        var result = await _service.CreateGuestAsync(OrgId, guest);

        // Assert
        Assert.Equal(OrgId, result.OrgId);
        _mockRepository.Verify(x => x.AddAsync(It.Is<Guest>(g => g.OrgId == OrgId)), Times.Once);
    }

    [Fact]
    public async Task CreateGuestAsync_WithEmailOfSameOrg_ThrowsDomainConflict()
    {
        // Arrange
        var guest = new Guest { FirstName = "John", LastName = "Doe", Email = "john.doe@example.com" };
        _mockRepository.Setup(x => x.ExistsByEmailAsync(OrgId, guest.Email, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<DomainConflictException>(() => _service.CreateGuestAsync(OrgId, guest));

        Assert.Equal("guest_email_exists", exception.Code);
        Assert.Equal("GuestEmailAlreadyExists", exception.MessageKey);
        Assert.DoesNotContain(guest.Email, exception.Message);
        _mockRepository.Verify(x => x.AddAsync(It.IsAny<Guest>()), Times.Never);
    }

    [Fact]
    public async Task CreateGuestSnapshotAsync_WithOrg_DoesNotCheckEmailUniqueness()
    {
        // Arrange
        var guest = new Guest { OrgId = OrgId, FirstName = "John", LastName = "Doe", Email = "john.doe@example.com" };
        _mockRepository.Setup(x => x.AddAsync(It.IsAny<Guest>())).ReturnsAsync(guest);

        // Act
        var result = await _service.CreateGuestSnapshotAsync(guest);

        // Assert
        Assert.Equal(guest.Email, result.Email);
        _mockRepository.Verify(x => x.ExistsByEmailAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockRepository.Verify(x => x.AddAsync(It.IsAny<Guest>()), Times.Once);
    }

    [Fact]
    public async Task CreateGuestSnapshotAsync_WithoutOrg_ThrowsArgumentException()
    {
        // Arrange
        var guest = new Guest { FirstName = "John", LastName = "Doe", Email = "john.doe@example.com" };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateGuestSnapshotAsync(guest));
        _mockRepository.Verify(x => x.AddAsync(It.IsAny<Guest>()), Times.Never);
    }

    [Fact]
    public async Task UpdateGuestAsync_WithExistingGuest_ReturnsUpdatedGuest()
    {
        // Arrange
        var guest = new Guest { Id = Guid.NewGuid(), OrgId = OrgId, FirstName = "John", LastName = "Doe", Email = "john.doe@example.com" };
        _mockRepository.Setup(x => x.ExistsAsync(guest.Id)).ReturnsAsync(true);
        _mockRepository.Setup(x => x.UpdateAsync(It.IsAny<Guest>())).ReturnsAsync(guest);

        // Act
        var result = await _service.UpdateGuestAsync(guest);

        // Assert
        Assert.Equal(guest.Id, result.Id);
        _mockRepository.Verify(x => x.UpdateAsync(It.IsAny<Guest>()), Times.Once);
    }

    [Fact]
    public async Task UpdateGuestAsync_WithNonExistentGuest_ThrowsNotFoundException()
    {
        // Arrange
        var guest = new Guest { Id = Guid.NewGuid(), OrgId = OrgId, FirstName = "John", LastName = "Doe", Email = "john.doe@example.com" };
        _mockRepository.Setup(x => x.ExistsAsync(guest.Id)).ReturnsAsync(false);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<NotFoundException>(() => _service.UpdateGuestAsync(guest));

        Assert.Equal("guest_not_found", exception.Code);
        _mockRepository.Verify(x => x.UpdateAsync(It.IsAny<Guest>()), Times.Never);
    }

    [Fact]
    public async Task DeleteGuestAsync_GuestWithoutReferences_DeletesStoredFilesThenRow()
    {
        // Arrange
        var guestId = SetupGuestInOrg();
        _mockRepository.Setup(x => x.GetUsageAsync(guestId, RomeToday, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GuestUsage(HasReferences: false, HasOpenBookings: false));

        // Act
        var result = await _service.DeleteGuestAsync(OrgId, guestId);

        // Assert
        Assert.Equal(GuestDeletionResult.Deleted, result);
        _mockRepository.Verify(x => x.DeleteAsync(guestId), Times.Once);
        _mockGdprService.Verify(
            x => x.EraseStoredFilesBeforeRemovalAsync(OrgId, guestId, It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _mockGdprService.Verify(
            x => x.EraseGuestDataAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DeleteGuestAsync_GuestWithPastBookings_SoftDeletesAndAnonymizesInsteadOfRemovingRow()
    {
        // Arrange
        var guestId = SetupGuestInOrg();
        _mockRepository.Setup(x => x.GetUsageAsync(guestId, RomeToday, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GuestUsage(HasReferences: true, HasOpenBookings: false));

        // Act
        var result = await _service.DeleteGuestAsync(OrgId, guestId);

        // Assert
        Assert.Equal(GuestDeletionResult.Anonymized, result);
        _mockRepository.Verify(x => x.DeleteAsync(It.IsAny<Guid>()), Times.Never);
        _mockGdprService.Verify(
            x => x.EraseGuestDataAsync(OrgId, guestId, GuestService.HostDeletionReason, It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DeleteGuestAsync_GuestWithOpenBooking_ThrowsDomainConflict()
    {
        // Arrange
        var guestId = SetupGuestInOrg();
        _mockRepository.Setup(x => x.GetUsageAsync(guestId, RomeToday, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GuestUsage(HasReferences: true, HasOpenBookings: true));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<DomainConflictException>(() => _service.DeleteGuestAsync(OrgId, guestId));

        Assert.Equal("guest_has_open_bookings", exception.Code);
        _mockRepository.Verify(x => x.DeleteAsync(It.IsAny<Guid>()), Times.Never);
        _mockGdprService.Verify(
            x => x.EraseGuestDataAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DeleteGuestAsync_GuestNotInOrg_ThrowsNotFoundWithoutTouchingIt()
    {
        // Arrange
        var guestId = Guid.NewGuid();
        _mockRepository.Setup(x => x.GetByIdInOrgAsync(OrgId, guestId, It.IsAny<CancellationToken>())).ReturnsAsync((Guest?)null);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<NotFoundException>(() => _service.DeleteGuestAsync(OrgId, guestId));

        Assert.Equal("guest_not_found", exception.Code);
        _mockRepository.Verify(x => x.GetUsageAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockRepository.Verify(x => x.DeleteAsync(It.IsAny<Guid>()), Times.Never);
    }

    private Guid SetupGuestInOrg()
    {
        var guestId = Guid.NewGuid();
        _mockRepository
            .Setup(x => x.GetByIdInOrgAsync(OrgId, guestId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Guest { Id = guestId, OrgId = OrgId, FirstName = "John", LastName = "Doe", Email = "john.doe@example.com" });
        return guestId;
    }
}
