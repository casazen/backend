using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.BackgroundJobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using Casazen.Core.Options;

namespace Casazen.Tests.Unit.Jobs;

public class ESignWebhookJobTests
{
    [Fact]
    public async Task AC4_ValidPayload_CallsHandleESignEventAsync()
    {
        var workflow = new Mock<ILeaseWorkflowService>();
        workflow.Setup(s => s.HandleESignEventAsync("payload")).Returns(Task.CompletedTask);
        var job = new ESignWebhookJob(workflow.Object, Mock.Of<ILogger<ESignWebhookJob>>());

        await job.ProcessEventAsync("payload");

        workflow.Verify(s => s.HandleESignEventAsync("payload"), Times.Once);
    }

    [Fact]
    public async Task AC4_AllSigned_SetsSignedPathAndEmitsAllPartiesSigned()
    {
        var lease = BuildLease();
        lease.ExternalSigningSessionId = "session-all";
        var (sut, leaseRepo, events) = CreateWorkflow(lease, new ESignEvent(
            "session-all", "all_signed", null, true, "/path/signed.pdf"));

        await new ESignWebhookJob(sut, Mock.Of<ILogger<ESignWebhookJob>>())
            .ProcessEventAsync("payload");

        Assert.Equal(LeaseStatus.Signed, lease.Status);
        Assert.Equal("/path/signed.pdf", lease.SignedPdfStoragePath);
        events.Verify(r => r.AddAsync(It.Is<LeaseEvent>(e =>
            e.EventType == LeaseEventType.AllPartiesSigned)), Times.Once);
        leaseRepo.Verify(r => r.UpdateAsync(lease), Times.Once);
    }

    [Fact]
    public async Task AC4_Partial_EmitsPartySignedDocument_DoesNotMarkSigned()
    {
        var lease = BuildLease();
        lease.ExternalSigningSessionId = "session-partial";
        var (sut, leaseRepo, events) = CreateWorkflow(lease, new ESignEvent(
            "session-partial", "party_signed", "tenant@example.com", false, null));

        await new ESignWebhookJob(sut, Mock.Of<ILogger<ESignWebhookJob>>())
            .ProcessEventAsync("payload");

        Assert.Equal(LeaseStatus.AwaitingSignature, lease.Status);
        Assert.Null(lease.SignedPdfStoragePath);
        events.Verify(r => r.AddAsync(It.Is<LeaseEvent>(e =>
            e.EventType == LeaseEventType.PartySignedDocument
            && e.Payload == "tenant@example.com")), Times.Once);
        leaseRepo.Verify(r => r.UpdateAsync(It.IsAny<LeaseContract>()), Times.Never);
    }

    [Fact]
    public async Task AC4_UnknownSession_IsNoOp()
    {
        var eSign = new Mock<ILeaseESignService>();
        eSign.Setup(s => s.ParseWebhookEventAsync("payload"))
            .ReturnsAsync(new ESignEvent("unknown", "all_signed", null, true, null));
        var leaseRepo = new Mock<ILeaseContractRepository>();
        leaseRepo.Setup(r => r.GetByExternalSigningSessionIdAsync("unknown"))
            .ReturnsAsync((LeaseContract?)null);

        var sut = new LeaseWorkflowService(
            leaseRepo.Object,
            Mock.Of<ILeaseRegistrationRepository>(),
            Mock.Of<ILeaseEventRepository>(),
            Mock.Of<ILeaseTemplateService>(),
            eSign.Object,
            Mock.Of<ILeaseRegistrationService>(),
            Mock.Of<IPropertyRepository>(),
            Mock.Of<ILeaseRegistrationAuthorizationRepository>(),
            Mock.Of<IApeComplianceService>(),
            Options.Create(new RliOptions()),
            Mock.Of<ILogger<LeaseWorkflowService>>());

        await new ESignWebhookJob(sut, Mock.Of<ILogger<ESignWebhookJob>>())
            .ProcessEventAsync("payload");

        leaseRepo.Verify(r => r.UpdateAsync(It.IsAny<LeaseContract>()), Times.Never);
    }

    private static (LeaseWorkflowService Sut, Mock<ILeaseContractRepository> Leases, Mock<ILeaseEventRepository> Events)
        CreateWorkflow(LeaseContract lease, ESignEvent parsed)
    {
        var eSign = new Mock<ILeaseESignService>();
        eSign.Setup(s => s.ParseWebhookEventAsync("payload")).ReturnsAsync(parsed);
        var leaseRepo = new Mock<ILeaseContractRepository>();
        leaseRepo.Setup(r => r.GetByExternalSigningSessionIdAsync(parsed.ExternalSessionId)).ReturnsAsync(lease);
        leaseRepo.Setup(r => r.UpdateAsync(It.IsAny<LeaseContract>())).ReturnsAsync((LeaseContract l) => l);
        var events = new Mock<ILeaseEventRepository>();
        events.Setup(r => r.AddAsync(It.IsAny<LeaseEvent>())).ReturnsAsync((LeaseEvent e) => e);

        var sut = new LeaseWorkflowService(
            leaseRepo.Object,
            Mock.Of<ILeaseRegistrationRepository>(),
            events.Object,
            Mock.Of<ILeaseTemplateService>(),
            eSign.Object,
            Mock.Of<ILeaseRegistrationService>(),
            Mock.Of<IPropertyRepository>(),
            Mock.Of<ILeaseRegistrationAuthorizationRepository>(),
            Mock.Of<IApeComplianceService>(),
            Options.Create(new RliOptions()),
            Mock.Of<ILogger<LeaseWorkflowService>>());
        return (sut, leaseRepo, events);
    }

    private static LeaseContract BuildLease() => new()
    {
        Id = Guid.NewGuid(),
        Status = LeaseStatus.AwaitingSignature,
        FiscalRegime = FiscalRegime.CedolareSecca,
        StartDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        EndDate = new DateTime(2030, 8, 31, 0, 0, 0, DateTimeKind.Utc),
        MonthlyRent = 1200m,
        Parties = [],
    };
}
