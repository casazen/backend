using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class PropertyServiceCinTests
{
    private readonly Mock<IPropertyRepository> _repository = new();
    private readonly Mock<IPropertyComplianceStatusService> _complianceStatus = new();
    private readonly PropertyService _service;

    public PropertyServiceCinTests()
    {
        _service = NewService(new CinOptions());
    }

    private PropertyService NewService(CinOptions cinOptions, DateTimeOffset? utcNow = null) =>
        new(
            _repository.Object,
            _complianceStatus.Object,
            new CinDeadlineCalendar(
                Options.Create(cinOptions), new FakeTimeProvider(utcNow ?? new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero))),
            Mock.Of<ILogger<PropertyService>>());

    [Fact]
    public async Task GetOwnerCinComplianceAsync_ReturnsSummaryCounts()
    {
        var ownerId = "owner-1";
        _repository.Setup(r => r.GetByOwnerForComplianceAsync(ownerId)).ReturnsAsync([
            new Property { Id = Guid.NewGuid(), OwnerId = ownerId, Name = "A", Address = "a", City = "Roma", CinCode = "IT058091C27G5FFZDZ" },
            new Property { Id = Guid.NewGuid(), OwnerId = ownerId, Name = "B", Address = "b", City = "Roma", CinCode = null },
            new Property { Id = Guid.NewGuid(), OwnerId = ownerId, Name = "C", Address = "c", City = "Roma", CinCode = "BAD" },
            new Property { Id = Guid.NewGuid(), OwnerId = ownerId, Name = "D", Address = "d", City = "Roma", CinCode = "IT123450123456789" },
        ]);

        var result = await _service.GetOwnerCinComplianceAsync(ownerId, null, 1, 50);

        Assert.Equal(4, result.TotalCount);
        Assert.Equal(1, result.Summary.Valid);
        Assert.Equal(1, result.Summary.Missing);
        Assert.Equal(2, result.Summary.Invalid); // "BAD" and a migrated old-format CIN
        Assert.True(result.Summary.HasNonCompliant);
    }

    [Theory]
    [InlineData("2026-03-11T09:00:00Z", CinDeadlinePhase.Upcoming, 10)]
    [InlineData("2026-03-20T23:30:00Z", CinDeadlinePhase.DueToday, 0)] // 23:30 UTC on the 20th is 00:30 of the 21st in Rome
    [InlineData("2026-10-07T09:00:00Z", CinDeadlinePhase.Passed, -200)]
    public async Task GetOwnerCinComplianceAsync_ConfiguredDeadline_ReportsPhaseAndSignedDays(
        string utcNow, CinDeadlinePhase phase, int days)
    {
        _repository.Setup(r => r.GetByOwnerForComplianceAsync("owner-1")).ReturnsAsync([]);
        var service = NewService(new CinOptions { ExposureDeadline = "2026-03-21" }, DateTimeOffset.Parse(utcNow, CultureInfo.InvariantCulture));

        var result = await service.GetOwnerCinComplianceAsync("owner-1", null, 1, 50);

        Assert.Equal(new DateOnly(2026, 3, 21), result.Summary.Deadline.Deadline);
        Assert.Equal(phase, result.Summary.Deadline.Phase);
        Assert.Equal(days, result.Summary.Deadline.DaysUntilDeadline);
    }

    [Fact]
    public async Task GetOwnerCinComplianceAsync_NoDeadlineConfigured_ReportsNoDateAndNoDays()
    {
        _repository.Setup(r => r.GetByOwnerForComplianceAsync("owner-1")).ReturnsAsync([]);

        var result = await _service.GetOwnerCinComplianceAsync("owner-1", null, 1, 50);

        Assert.Equal(CinDeadlinePhase.NotConfigured, result.Summary.Deadline.Phase);
        Assert.Null(result.Summary.Deadline.Deadline);
        Assert.Null(result.Summary.Deadline.DaysUntilDeadline);
        Assert.Equal("none", result.Summary.Deadline.PhaseApiValue);
    }

    [Theory]
    [InlineData("INVALID")]
    [InlineData("IT-12345-1234567890")] // old invented format
    public async Task UpdatePropertyCinAsync_NotOfficialFormat_ThrowsInvalidCinFormat(string cin)
    {
        var propertyId = Guid.NewGuid();
        var property = NewProperty(propertyId);
        _repository.Setup(r => r.GetByIdAsync(propertyId)).ReturnsAsync(property);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            _service.UpdatePropertyCinAsync(propertyId, cin));

        Assert.Equal(CinFormat.InvalidFormatCode, ex.Code);
        Assert.Equal(CinFormat.InvalidFormatMessageKey, ex.MessageKey);
        Assert.Null(property.CinCode);
        _repository.Verify(r => r.UpdateAsync(It.IsAny<Property>()), Times.Never);
    }

    [Fact]
    public async Task UpdatePropertyCinAsync_DuplicateCin_ThrowsConflictOnNormalizedValue()
    {
        var propertyId = Guid.NewGuid();
        _repository.Setup(r => r.GetByIdAsync(propertyId)).ReturnsAsync(NewProperty(propertyId));
        _repository.Setup(r => r.CinCodeExistsOnOtherPropertyAsync("IT058091C27G5FFZDZ", propertyId)).ReturnsAsync(true);

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() =>
            _service.UpdatePropertyCinAsync(propertyId, "IT-058091-C2-7G5FFZDZ"));

        Assert.Equal("duplicate_cin", ex.Code);
    }

    [Theory]
    [InlineData("IT058091C27G5FFZDZ")]
    [InlineData("IT-058091-C2-7G5FFZDZ")]
    [InlineData(" it 058091 c2 7g5ffzdz ")]
    public async Task UpdatePropertyCinAsync_ValidCin_SavesNormalizedValue(string input)
    {
        var propertyId = Guid.NewGuid();
        var property = NewProperty(propertyId);
        _repository.Setup(r => r.GetByIdAsync(propertyId)).ReturnsAsync(property);
        _repository.Setup(r => r.CinCodeExistsOnOtherPropertyAsync(It.IsAny<string>(), propertyId)).ReturnsAsync(false);
        _repository.Setup(r => r.UpdateAsync(It.IsAny<Property>())).ReturnsAsync((Property p) => p);

        await _service.UpdatePropertyCinAsync(propertyId, input);

        Assert.Equal("IT058091C27G5FFZDZ", property.CinCode);
        _repository.Verify(r => r.UpdateAsync(property), Times.Once);
    }

    [Fact]
    public async Task UpdatePropertyCinAsync_Blank_ClearsCin()
    {
        var propertyId = Guid.NewGuid();
        var property = NewProperty(propertyId);
        property.CinCode = "IT058091C27G5FFZDZ";
        _repository.Setup(r => r.GetByIdAsync(propertyId)).ReturnsAsync(property);
        _repository.Setup(r => r.UpdateAsync(It.IsAny<Property>())).ReturnsAsync((Property p) => p);

        await _service.UpdatePropertyCinAsync(propertyId, "  ");

        Assert.Null(property.CinCode);
        // CO-06 (A5-20): the removed CIN is re-evaluated after the save, so an active property is suspended.
        _complianceStatus.Verify(s => s.ReevaluateAsync(propertyId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreatePropertyAsync_CinWithSeparators_StoresNormalizedCin()
    {
        var property = NewProperty(Guid.NewGuid());
        property.CinCode = "it-015146-a1-2holv2mz";
        _repository.Setup(r => r.SlugExistsInOrgAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid?>())).ReturnsAsync(false);
        _repository.Setup(r => r.AddAsync(It.IsAny<Property>())).ReturnsAsync((Property p) => p);

        var created = await _service.CreatePropertyAsync(property);

        Assert.Equal("IT015146A12HOLV2MZ", created.CinCode);
    }

    [Fact]
    public async Task UpdatePropertyAsync_CinWithSeparators_StoresNormalizedCin()
    {
        var property = NewProperty(Guid.NewGuid());
        property.CinCode = "IT 048017 B4 2742QNBZ";
        _repository.Setup(r => r.UpdateAsync(It.IsAny<Property>())).ReturnsAsync((Property p) => p);

        var updated = await _service.UpdatePropertyAsync(property);

        Assert.Equal("IT048017B42742QNBZ", updated.CinCode);
    }

    private static Property NewProperty(Guid id) => new()
    {
        Id = id,
        OwnerId = "o1",
        Name = "P",
        Address = "a",
        City = "Roma",
    };
}
