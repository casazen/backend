using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class PropertyServiceCinTests
{
    private readonly Mock<IPropertyRepository> _repository = new();
    private readonly PropertyService _service;

    public PropertyServiceCinTests()
    {
        _service = new PropertyService(_repository.Object, Mock.Of<ILogger<PropertyService>>());
    }

    [Fact]
    public async Task GetOwnerCinComplianceAsync_ReturnsSummaryCounts()
    {
        var ownerId = "owner-1";
        _repository.Setup(r => r.GetByOwnerForComplianceAsync(ownerId)).ReturnsAsync([
            new Property { Id = Guid.NewGuid(), OwnerId = ownerId, Name = "A", Address = "a", City = "Roma", CinCode = "IT-12345-0123456789" },
            new Property { Id = Guid.NewGuid(), OwnerId = ownerId, Name = "B", Address = "b", City = "Roma", CinCode = null },
            new Property { Id = Guid.NewGuid(), OwnerId = ownerId, Name = "C", Address = "c", City = "Roma", CinCode = "BAD" },
        ]);

        var result = await _service.GetOwnerCinComplianceAsync(ownerId, null, 1, 50);

        Assert.Equal(3, result.TotalCount);
        Assert.Equal(1, result.Summary.Valid);
        Assert.Equal(1, result.Summary.Missing);
        Assert.Equal(1, result.Summary.Invalid);
        Assert.True(result.Summary.HasNonCompliant);
    }

    [Fact]
    public async Task UpdatePropertyCinAsync_RejectsInvalidFormat()
    {
        var propertyId = Guid.NewGuid();
        _repository.Setup(r => r.GetByIdAsync(propertyId)).ReturnsAsync(new Property
        {
            Id = propertyId,
            OwnerId = "o1",
            Name = "P",
            Address = "a",
            City = "Roma",
        });

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.UpdatePropertyCinAsync(propertyId, "INVALID"));
    }

    [Fact]
    public async Task UpdatePropertyCinAsync_RejectsDuplicateCin()
    {
        var propertyId = Guid.NewGuid();
        const string cin = "IT-12345-0123456789";
        _repository.Setup(r => r.GetByIdAsync(propertyId)).ReturnsAsync(new Property
        {
            Id = propertyId,
            OwnerId = "o1",
            Name = "P",
            Address = "a",
            City = "Roma",
        });
        _repository.Setup(r => r.CinCodeExistsOnOtherPropertyAsync(cin, propertyId)).ReturnsAsync(true);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.UpdatePropertyCinAsync(propertyId, cin));
    }

    [Fact]
    public async Task UpdatePropertyCinAsync_SavesValidCin()
    {
        var propertyId = Guid.NewGuid();
        const string cin = "IT-12345-0123456789";
        var property = new Property
        {
            Id = propertyId,
            OwnerId = "o1",
            Name = "P",
            Address = "a",
            City = "Roma",
        };
        _repository.Setup(r => r.GetByIdAsync(propertyId)).ReturnsAsync(property);
        _repository.Setup(r => r.CinCodeExistsOnOtherPropertyAsync(cin, propertyId)).ReturnsAsync(false);
        _repository.Setup(r => r.UpdateAsync(It.IsAny<Property>())).ReturnsAsync((Property p) => p);

        await _service.UpdatePropertyCinAsync(propertyId, cin);

        Assert.Equal(cin, property.CinCode);
        _repository.Verify(r => r.UpdateAsync(property), Times.Once);
    }
}
