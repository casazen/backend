using Casazen.Core.Services;
using Casazen.Web.BackgroundJobs;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.BackgroundJobs;

public class OtaSyncJobTests
{
    private readonly Mock<IOtaManager> _otaManagerMock;
    private readonly Mock<ILogger<OtaSyncJob>> _loggerMock;
    private readonly OtaSyncJob _job;

    public OtaSyncJobTests()
    {
        _otaManagerMock = new Mock<IOtaManager>();
        _loggerMock = new Mock<ILogger<OtaSyncJob>>();
        _job = new OtaSyncJob(_otaManagerMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task ExecuteAsync_ValidPropertyId_CallsOtaManager()
    {
        var propertyId = Guid.NewGuid();
        _otaManagerMock.Setup(m => m.SyncAllAsync(propertyId))
            .ReturnsAsync(true);

        await _job.ExecuteAsync(propertyId);

        _otaManagerMock.Verify(m => m.SyncAllAsync(propertyId), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_SuccessfulSync_LogsInformation()
    {
        var propertyId = Guid.NewGuid();
        _otaManagerMock.Setup(m => m.SyncAllAsync(propertyId))
            .ReturnsAsync(true);

        await _job.ExecuteAsync(propertyId);

        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("completed successfully")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_FailedSync_LogsWarning()
    {
        var propertyId = Guid.NewGuid();
        _otaManagerMock.Setup(m => m.SyncAllAsync(propertyId))
            .ReturnsAsync(false);

        await _job.ExecuteAsync(propertyId);

        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("completed with errors")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_OtaManagerThrowsException_RethrowsException()
    {
        var propertyId = Guid.NewGuid();
        var expectedException = new Exception("OTA sync failed");
        _otaManagerMock.Setup(m => m.SyncAllAsync(propertyId))
            .ThrowsAsync(expectedException);

        var exception = await Assert.ThrowsAsync<Exception>(() => _job.ExecuteAsync(propertyId));
        Assert.Equal(expectedException.Message, exception.Message);
    }
}
