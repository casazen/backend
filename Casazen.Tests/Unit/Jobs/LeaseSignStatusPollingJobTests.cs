using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Web.BackgroundJobs;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Jobs;

public class LeaseSignStatusPollingJobTests
{
    /// <summary>
    /// Known limitation tracked as GitHub #177: this job only logs leases in
    /// <see cref="LeaseStatus.AwaitingSignature"/>. US-009 verifies current behaviour
    /// and does not implement an active e-sign poller.
    /// </summary>
    [Fact]
    public async Task AC5_ExecuteAsync_LogsAwaitingSignatureLeases_DoesNotCallESignProvider_Todo177()
    {
        var pending = new LeaseContract
        {
            Id = Guid.NewGuid(),
            Status = LeaseStatus.AwaitingSignature,
            UpdatedAt = DateTime.UtcNow.AddDays(-2),
        };
        var leases = new Mock<ILeaseContractRepository>();
        leases.Setup(r => r.GetByStatusAsync(LeaseStatus.AwaitingSignature)).ReturnsAsync([pending]);
        var logger = new Mock<ILogger<LeaseSignStatusPollingJob>>();

        var job = new LeaseSignStatusPollingJob(leases.Object, logger.Object);
        await job.ExecuteAsync();

        leases.Verify(r => r.GetByStatusAsync(LeaseStatus.AwaitingSignature), Times.Once);
        leases.Verify(r => r.UpdateAsync(It.IsAny<LeaseContract>()), Times.Never);

        var ctorParams = typeof(LeaseSignStatusPollingJob).GetConstructors()[0].GetParameters();
        Assert.DoesNotContain(ctorParams, p => p.ParameterType == typeof(ILeaseESignService));

        logger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) =>
                    state.ToString()!.Contains("awaiting signature", StringComparison.OrdinalIgnoreCase)
                    || state.ToString()!.Contains("Polling sign status", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }
}
