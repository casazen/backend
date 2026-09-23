using System.Reflection;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Web.BackgroundJobs;
using Hangfire;
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

    [Fact]
    public async Task AC3_WhenPollConfirms_MarksRegisteredAndEmitsEvent()
    {
        var lease = new LeaseContract
        {
            Id = Guid.NewGuid(),
            Status = LeaseStatus.SentToProvider,
        };
        var pending = new LeaseRegistration
        {
            LeaseContractId = lease.Id,
            Status = RegistrationStatus.SentToProvider,
            ExternalRegistrationId = "RLI-WAIT-1",
        };

        var leases = new Mock<ILeaseContractRepository>();
        leases.Setup(r => r.GetByIdAsync(lease.Id)).ReturnsAsync(lease);
        leases.Setup(r => r.UpdateAsync(It.IsAny<LeaseContract>()))
            .ReturnsAsync((LeaseContract l) => l);

        var regs = new Mock<ILeaseRegistrationRepository>();
        regs.Setup(r => r.GetByStatusAsync(RegistrationStatus.SentToProvider)).ReturnsAsync([pending]);
        regs.Setup(r => r.UpdateAsync(It.IsAny<LeaseRegistration>()))
            .ReturnsAsync((LeaseRegistration r) => r);

        var provider = new Mock<ILeaseRegistrationService>();
        provider.Setup(s => s.PollStatusAsync("RLI-WAIT-1"))
            .ReturnsAsync(new RegistrationStatusResult("RLI-WAIT-1", "Registered", "RLI-CODE-9", true));

        var events = new Mock<ILeaseEventRepository>();
        events.Setup(r => r.GetByLeaseIdAsync(lease.Id)).ReturnsAsync([]);
        events.Setup(r => r.AddAsync(It.IsAny<LeaseEvent>())).ReturnsAsync((LeaseEvent e) => e);

        var job = new LeaseRegistrationStatusPollingJob(
            leases.Object,
            regs.Object,
            provider.Object,
            events.Object,
            Mock.Of<ILogger<LeaseRegistrationStatusPollingJob>>());

        await job.ExecuteAsync();

        Assert.Equal(RegistrationStatus.Registered, pending.Status);
        Assert.Equal("RLI-CODE-9", pending.RegistrationCode);
        Assert.NotNull(pending.ConfirmedAt);
        Assert.Equal(LeaseStatus.Registered, lease.Status);
        events.Verify(r => r.AddAsync(It.Is<LeaseEvent>(e =>
            e.LeaseContractId == lease.Id
            && e.EventType == LeaseEventType.RegistrationConfirmed
            && e.Payload == "RLI-CODE-9")), Times.Once);
        provider.Verify(s => s.SubmitRegistrationAsync(It.IsAny<LeaseContract>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenLeaseUpdateFails_LeavesRegistrationPendingForRetry()
    {
        var lease = new LeaseContract
        {
            Id = Guid.NewGuid(),
            Status = LeaseStatus.SentToProvider,
        };
        var pending = new LeaseRegistration
        {
            LeaseContractId = lease.Id,
            Status = RegistrationStatus.SentToProvider,
            ExternalRegistrationId = "RLI-WAIT-RETRY",
        };

        var leases = new Mock<ILeaseContractRepository>();
        leases.Setup(r => r.GetByIdAsync(lease.Id)).ReturnsAsync(lease);
        leases.Setup(r => r.UpdateAsync(It.IsAny<LeaseContract>()))
            .ThrowsAsync(new InvalidOperationException("transient lease save failure"));

        var regs = new Mock<ILeaseRegistrationRepository>();
        regs.Setup(r => r.GetByStatusAsync(RegistrationStatus.SentToProvider)).ReturnsAsync([pending]);

        var provider = new Mock<ILeaseRegistrationService>();
        provider.Setup(s => s.PollStatusAsync("RLI-WAIT-RETRY"))
            .ReturnsAsync(new RegistrationStatusResult("RLI-WAIT-RETRY", "Registered", "RLI-CODE-RETRY", true));

        var events = new Mock<ILeaseEventRepository>();

        var job = new LeaseRegistrationStatusPollingJob(
            leases.Object,
            regs.Object,
            provider.Object,
            events.Object,
            Mock.Of<ILogger<LeaseRegistrationStatusPollingJob>>());

        await job.ExecuteAsync();

        Assert.Equal(RegistrationStatus.SentToProvider, pending.Status);
        Assert.Null(pending.RegistrationCode);
        Assert.Null(pending.ConfirmedAt);
        regs.Verify(r => r.UpdateAsync(It.IsAny<LeaseRegistration>()), Times.Never);
        events.Verify(r => r.AddAsync(It.IsAny<LeaseEvent>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenPollReturnsTerminalFailure_MarksFailedAndRestoresSignedLease()
    {
        var lease = new LeaseContract
        {
            Id = Guid.NewGuid(),
            Status = LeaseStatus.SentToProvider,
        };
        var pending = new LeaseRegistration
        {
            LeaseContractId = lease.Id,
            Status = RegistrationStatus.SentToProvider,
            ExternalRegistrationId = "RLI-FAIL-1",
        };

        var leases = new Mock<ILeaseContractRepository>();
        leases.Setup(r => r.GetByIdAsync(lease.Id)).ReturnsAsync(lease);
        leases.Setup(r => r.UpdateAsync(It.IsAny<LeaseContract>()))
            .ReturnsAsync((LeaseContract l) => l);

        var regs = new Mock<ILeaseRegistrationRepository>();
        regs.Setup(r => r.GetByStatusAsync(RegistrationStatus.SentToProvider)).ReturnsAsync([pending]);
        regs.Setup(r => r.UpdateAsync(It.IsAny<LeaseRegistration>()))
            .ReturnsAsync((LeaseRegistration r) => r);

        var provider = new Mock<ILeaseRegistrationService>();
        provider.Setup(s => s.PollStatusAsync("RLI-FAIL-1"))
            .ReturnsAsync(new RegistrationStatusResult("RLI-FAIL-1", "Rejected", null, false));

        var events = new Mock<ILeaseEventRepository>();
        events.Setup(r => r.AddAsync(It.IsAny<LeaseEvent>())).ReturnsAsync((LeaseEvent e) => e);

        var job = new LeaseRegistrationStatusPollingJob(
            leases.Object,
            regs.Object,
            provider.Object,
            events.Object,
            Mock.Of<ILogger<LeaseRegistrationStatusPollingJob>>());

        await job.ExecuteAsync();

        Assert.Equal(RegistrationStatus.Failed, pending.Status);
        Assert.Equal(LeaseStatus.Signed, lease.Status);
        events.Verify(r => r.AddAsync(It.Is<LeaseEvent>(e =>
            e.LeaseContractId == lease.Id
            && e.EventType == LeaseEventType.RegistrationFailed
            && e.Payload == "Rejected")), Times.Once);
    }

    [Fact]
    public void AC3_ExecuteAsync_HasDisableConcurrentExecution()
    {
        var method = typeof(LeaseRegistrationStatusPollingJob)
            .GetMethod(nameof(LeaseRegistrationStatusPollingJob.ExecuteAsync));
        Assert.NotNull(method);
        var attr = method!.GetCustomAttribute<DisableConcurrentExecutionAttribute>();
        Assert.NotNull(attr);
    }
}
