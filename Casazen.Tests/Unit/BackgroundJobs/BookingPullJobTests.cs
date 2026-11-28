using Casazen.Core.Services;
using Casazen.Web.BackgroundJobs;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.BackgroundJobs;

public class BookingPullJobTests
{
    private readonly Mock<IOtaManager> _otaManagerMock;
    private readonly Mock<ILogger<BookingPullJob>> _loggerMock;
    private readonly BookingPullJob _job;

    public BookingPullJobTests()
    {
        _otaManagerMock = new Mock<IOtaManager>();
        _loggerMock = new Mock<ILogger<BookingPullJob>>();
        _job = new BookingPullJob(_otaManagerMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task ExecuteAsync_ValidPropertyId_CallsOtaManager()
    {
        var propertyId = Guid.NewGuid();
        _otaManagerMock.Setup(m => m.PullBookingsAsync(propertyId))
            .ReturnsAsync(true);

        await _job.ExecuteAsync(propertyId);

        _otaManagerMock.Verify(m => m.PullBookingsAsync(propertyId), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_SuccessfulPull_LogsInformation()
    {
        var propertyId = Guid.NewGuid();
        _otaManagerMock.Setup(m => m.PullBookingsAsync(propertyId))
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
    public async Task ExecuteAsync_FailedPull_LogsWarning()
    {
        var propertyId = Guid.NewGuid();
        _otaManagerMock.Setup(m => m.PullBookingsAsync(propertyId))
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
        var expectedException = new Exception("Booking pull failed");
        _otaManagerMock.Setup(m => m.PullBookingsAsync(propertyId))
            .ThrowsAsync(expectedException);

        var exception = await Assert.ThrowsAsync<Exception>(() => _job.ExecuteAsync(propertyId));
        Assert.Equal(expectedException.Message, exception.Message);
    }

    [Fact]
    public async Task ExecuteAsync_Exception_LogsError()
    {
        var propertyId = Guid.NewGuid();
        var expectedException = new Exception("Booking pull failed");
        _otaManagerMock.Setup(m => m.PullBookingsAsync(propertyId))
            .ThrowsAsync(expectedException);

        await Assert.ThrowsAsync<Exception>(() => _job.ExecuteAsync(propertyId));

        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("failed")),
                expectedException,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
