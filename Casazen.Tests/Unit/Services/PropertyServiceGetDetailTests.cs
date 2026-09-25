using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Enums;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class PropertyServiceGetDetailTests
{
    private readonly Mock<IPropertyRepository> _mockRepository;
    private readonly PropertyService _service;

    public PropertyServiceGetDetailTests()
    {
        _mockRepository = new Mock<IPropertyRepository>();
        _service = new PropertyService(
            _mockRepository.Object,
            Mock.Of<IPropertyComplianceStatusService>(),
            new Mock<ILogger<PropertyService>>().Object);
    }

    /// <summary>
    /// QA-CLOCK: check-in and check-out are date-only values, compared with today's date in Europe/Rome. At 23:30 UTC Rome
    /// is already on the next day: the comparison with the UTC instant counted today's arrival as upcoming and today's
    /// departure as still active, while at noon it did not.
    /// </summary>
    [Theory]
    [InlineData("2026-09-25T12:00:00Z")]
    [InlineData("2026-09-24T23:30:00Z")]
    public async Task GetPropertyDetailAsync_StaysAroundToday_SummarizesByRomeDateAtAnyHourUtc(string utcNow)
    {
        var clock = new FixedTimeProvider(DateTimeOffset.Parse(utcNow, CultureInfo.InvariantCulture));
        var service = new PropertyService(
            _mockRepository.Object,
            Mock.Of<IPropertyComplianceStatusService>(),
            Mock.Of<ILogger<PropertyService>>(),
            clock);
        var today = new DateTime(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc);
        var propertyId = Guid.NewGuid();
        var property = new Property { Id = propertyId, OwnerId = "auth0|owner123", Name = "Villa Roma" };
        Booking Stay(DateTime checkIn, DateTime checkOut, BookingStatus status) => new()
        {
            Id = Guid.NewGuid(),
            PropertyId = propertyId,
            GuestId = Guid.NewGuid(),
            CheckInDate = checkIn,
            CheckOutDate = checkOut,
            Status = status,
        };
        property.Bookings.Add(Stay(today, today.AddDays(2), BookingStatus.Confirmed));
        property.Bookings.Add(Stay(today.AddDays(-3), today, BookingStatus.CheckedIn));
        property.Bookings.Add(Stay(today.AddDays(5), today.AddDays(7), BookingStatus.Confirmed));
        _mockRepository.Setup(x => x.GetPropertyDetailAsync(propertyId)).ReturnsAsync(property);

        var summary = (await service.GetPropertyDetailAsync(propertyId)).BookingsSummary;

        Assert.Equal(1, summary.UpcomingBookings);
        Assert.Equal(0, summary.ActiveBookings);
        Assert.Equal(today.AddDays(5), summary.NextCheckIn);
        Assert.Equal(today.AddDays(2), summary.NextCheckOut);
    }

    [Fact]
    public async Task GetPropertyDetailAsync_WithValidId_ReturnsPropertyDetailResponse()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var property = new Property
        {
            Id = propertyId,
            OwnerId = "auth0|owner123",
            Name = "Villa Roma",
            Description = "Beautiful villa",
            Address = "Via Roma 1",
            City = "Rome",
            PostalCode = "00100",
            Bedrooms = 3,
            Bathrooms = 2,
            MaxGuests = 6,
            NightlyRate = 150m,
            CleaningFee = 50m,
            DamageDeposit = 200m,
            CinCode = "IT058091C27G5FFZDZ",
            IsActive = true,
            CreatedAt = now.AddDays(-30),
            UpdatedAt = now
        };

        property.PropertyDocuments.Add(new PropertyDocument
        {
            Id = Guid.NewGuid(),
            PropertyId = propertyId,
            FileName = "cin-cert.pdf",
            StorageUrl = "/uploads/properties/cin-cert.pdf",
            DocumentType = DocumentType.CinCertificate,
            UploadedBy = "owner@example.com",
            UploadedAt = now.AddDays(-10)
        });

        property.OtaIntegrations.Add(new OtaIntegration
        {
            Id = Guid.NewGuid(),
            PropertyId = propertyId,
            Platform = "Airbnb",
            ExternalPropertyId = "airbnb-123",
            IsActive = true,
            SyncEnabled = true,
            LastSyncAt = now.AddHours(-1),
            SyncStatus = "Success"
        });

        property.Bookings.Add(new Booking
        {
            Id = Guid.NewGuid(),
            PropertyId = propertyId,
            GuestId = Guid.NewGuid(),
            CheckInDate = now.AddDays(10),
            CheckOutDate = now.AddDays(15),
            Status = BookingStatus.Confirmed
        });

        _mockRepository.Setup(x => x.GetPropertyDetailAsync(propertyId)).ReturnsAsync(property);

        // Act
        var result = await _service.GetPropertyDetailAsync(propertyId);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(propertyId, result.Id);
        Assert.Equal("Villa Roma", result.Name);
        Assert.Equal("Rome", result.City);
        Assert.Single(result.Documents);
        Assert.Single(result.OtaIntegrations);
        Assert.Equal(1, result.BookingsSummary.TotalBookings);
        Assert.Equal(1, result.BookingsSummary.UpcomingBookings);
        Assert.False(result.PricingAdapterSummary.IsEnabled);
        _mockRepository.Verify(x => x.GetPropertyDetailAsync(propertyId), Times.Once);
    }

    [Fact]
    public async Task GetPropertyDetailAsync_WithPricingAdapterConfig_ReturnsPricingAdapterSummary()
    {
        var propertyId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var lastComputed = new DateTime(2026, 9, 23, 2, 0, 0, DateTimeKind.Utc);
        var property = new Property
        {
            Id = propertyId,
            OwnerId = "auth0|owner123",
            Name = "Test Property",
            Address = "Via Test 1",
            City = "Rome",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
            PricingAdapterConfig = new PricingAdapterConfig
            {
                PropertyId = propertyId,
                IsEnabled = true,
                AdaptationFrequency = "weekly",
                LastAdaptedAt = lastComputed,
            }
        };

        _mockRepository.Setup(x => x.GetPropertyDetailAsync(propertyId)).ReturnsAsync(property);

        var result = await _service.GetPropertyDetailAsync(propertyId);

        Assert.True(result.PricingAdapterSummary.IsEnabled);
        Assert.Equal(lastComputed, result.PricingAdapterSummary.LastAdaptedAt);
        // PC-15: next computation by Rome date, a week after the last one.
        Assert.Equal(new DateOnly(2026, 9, 30), result.PricingAdapterSummary.NextRunOn);
    }

    [Fact]
    public async Task GetPropertyDetailAsync_MapsDocumentsWithAuthenticatedDownloadPathAndFileType()
    {
        var propertyId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var property = new Property
        {
            Id = propertyId,
            OwnerId = "auth0|owner123",
            Name = "Test Property",
            Address = "Via Test 1",
            City = "Rome",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        property.PropertyDocuments.Add(new PropertyDocument
        {
            Id = documentId,
            PropertyId = propertyId,
            FileName = "cin-cert.pdf",
            StorageUrl = $"properties/{propertyId}/documents/cin-cert.pdf",
            DocumentType = DocumentType.CinCertificate,
            UploadedAt = DateTime.UtcNow
        });

        _mockRepository.Setup(x => x.GetPropertyDetailAsync(propertyId)).ReturnsAsync(property);

        var result = await _service.GetPropertyDetailAsync(propertyId);

        // FD-07 / A2-31: the DTO never exposes the storage reference, only the authenticated download.
        var doc = Assert.Single(result.Documents);
        Assert.Equal("pdf", doc.FileType);
        Assert.Equal($"/api/properties/{propertyId}/documents/{documentId}/download", doc.DownloadUrl);
    }

    [Fact]
    public async Task GetPropertyDetailAsync_WithNonExistentId_ThrowsInvalidOperationException()
    {
        // Arrange
        var propertyId = Guid.NewGuid();

        _mockRepository.Setup(x => x.GetPropertyDetailAsync(propertyId)).ReturnsAsync((Property?)null);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.GetPropertyDetailAsync(propertyId));
        _mockRepository.Verify(x => x.GetPropertyDetailAsync(propertyId), Times.Once);
    }

    [Fact]
    public async Task GetPropertyDetailAsync_OtaIntegrations_DoNotExposeApiKey()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        var property = new Property
        {
            Id = propertyId,
            OwnerId = "auth0|owner123",
            Name = "Test Property",
            Address = "Via Test 1",
            City = "Milan",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        property.OtaIntegrations.Add(new OtaIntegration
        {
            Id = Guid.NewGuid(),
            PropertyId = propertyId,
            Platform = "Booking.com",
            ExternalPropertyId = "booking-456",
            ApiKey = "secret-api-key-12345",
            ApiSecret = "secret-api-secret-67890",
            IsActive = true,
            SyncEnabled = true
        });

        _mockRepository.Setup(x => x.GetPropertyDetailAsync(propertyId)).ReturnsAsync(property);

        // Act
        var result = await _service.GetPropertyDetailAsync(propertyId);

        // Assert
        Assert.NotNull(result);
        Assert.Single(result.OtaIntegrations);

        var otaDto = result.OtaIntegrations[0];
        var otaDtoType = otaDto.GetType();

        Assert.Null(otaDtoType.GetProperty("ApiKey"));
        Assert.Null(otaDtoType.GetProperty("ApiSecret"));
    }

    [Fact]
    public async Task GetPropertyDetailAsync_WithValidCinCode_SetsCinStatusValid()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        var property = new Property
        {
            Id = propertyId,
            OwnerId = "auth0|owner123",
            Name = "Test Property",
            Address = "Via Test 1",
            City = "Rome",
            CinCode = "IT058091C27G5FFZDZ",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _mockRepository.Setup(x => x.GetPropertyDetailAsync(propertyId)).ReturnsAsync(property);

        // Act
        var result = await _service.GetPropertyDetailAsync(propertyId);

        // Assert
        Assert.Equal(CinStatus.Valid, result.CinStatus);
    }

    [Fact]
    public async Task GetPropertyDetailAsync_WithNullCinCode_SetsCinStatusMissing()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        var property = new Property
        {
            Id = propertyId,
            OwnerId = "auth0|owner123",
            Name = "Test Property",
            Address = "Via Test 1",
            City = "Rome",
            CinCode = null,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _mockRepository.Setup(x => x.GetPropertyDetailAsync(propertyId)).ReturnsAsync(property);

        // Act
        var result = await _service.GetPropertyDetailAsync(propertyId);

        // Assert
        Assert.Equal(CinStatus.Missing, result.CinStatus);
    }

    [Fact]
    public async Task GetPropertyDetailAsync_WithInvalidCinCode_SetsCinStatusInvalid()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        var property = new Property
        {
            Id = propertyId,
            OwnerId = "auth0|owner123",
            Name = "Test Property",
            Address = "Via Test 1",
            City = "Rome",
            CinCode = "INVALID",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _mockRepository.Setup(x => x.GetPropertyDetailAsync(propertyId)).ReturnsAsync(property);

        // Act
        var result = await _service.GetPropertyDetailAsync(propertyId);

        // Assert
        Assert.Equal(CinStatus.Invalid, result.CinStatus);
    }
}
