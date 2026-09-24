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
    /// LT-02: the job (registered only with <c>Features:ESignProvider</c> on) logs the leases waiting for the provider,
    /// AwaitingSignature and PartiallySigned; it never changes a lease and never calls the provider.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_LeasesWaitingForProvider_LogsWithoutChangingOrCallingTheProvider()
    {
        var awaiting = new LeaseContract { Id = Guid.NewGuid(), Status = LeaseStatus.AwaitingSignature, UpdatedAt = DateTime.UtcNow.AddDays(-2) };
        var partial = new LeaseContract { Id = Guid.NewGuid(), Status = LeaseStatus.PartiallySigned, UpdatedAt = DateTime.UtcNow.AddDays(-1) };
        var leases = new Mock<ILeaseContractRepository>();
        leases.Setup(r => r.GetByStatusAsync(LeaseStatus.AwaitingSignature)).ReturnsAsync([awaiting]);
        leases.Setup(r => r.GetByStatusAsync(LeaseStatus.PartiallySigned)).ReturnsAsync([partial]);
        var logger = new Mock<ILogger<LeaseSignStatusPollingJob>>();

        await new LeaseSignStatusPollingJob(leases.Object, logger.Object).ExecuteAsync();

        leases.Verify(r => r.UpdateAsync(It.IsAny<LeaseContract>()), Times.Never);
        var ctorParams = typeof(LeaseSignStatusPollingJob).GetConstructors()[0].GetParameters();
        Assert.DoesNotContain(ctorParams, p => p.ParameterType == typeof(ILeaseESignService));
        logger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("waiting for the e-signature provider", StringComparison.Ordinal)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Exactly(3));
    }
}
