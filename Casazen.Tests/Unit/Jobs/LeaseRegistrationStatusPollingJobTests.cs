using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Web.BackgroundJobs;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Jobs;

public class LeaseRegistrationStatusPollingJobTests
{
    [Fact]
    public async Task ExecuteAsync_NeverCallsSubmitRegistration()
    {
        var pending = new LeaseRegistration
        {
            LeaseContractId = Guid.NewGuid(),
            Status = RegistrationStatus.SentToProvider,
            ExternalRegistrationId = "RLI-STUB-1",
        };
        var regs = new Mock<ILeaseRegistrationRepository>();
        regs.Setup(r => r.GetByStatusAsync(RegistrationStatus.SentToProvider)).ReturnsAsync([pending]);
        var provider = new Mock<ILeaseRegistrationService>();
        provider.Setup(s => s.PollStatusAsync("RLI-STUB-1"))
            .ReturnsAsync(new RegistrationStatusResult("RLI-STUB-1", "Pending", null, false));

        var job = new LeaseRegistrationStatusPollingJob(
            Mock.Of<ILeaseContractRepository>(),
            regs.Object,
            provider.Object,
            Mock.Of<ILeaseEventRepository>(),
            Mock.Of<ILogger<LeaseRegistrationStatusPollingJob>>());

        await job.ExecuteAsync();

        provider.Verify(s => s.PollStatusAsync("RLI-STUB-1"), Times.Once);
        provider.Verify(s => s.SubmitRegistrationAsync(It.IsAny<LeaseContract>()), Times.Never);
    }
}
