using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class RliAuthorizationGateTests
{
    private const string OwnerId = "auth0|owner123";
    private static readonly RegistrationAuthorizationRequest ValidAuth =
        new("2026-08-rli-delega-bozza", true);

    [Fact]
    public async Task TriggerRegistrationAsync_WithoutAttestation_DoesNotCallProvider()
    {
        var (sut, regService, authRepo) = CreateSut();
        var lease = SignedLease();
        SetupLease(sut.LeaseRepo, lease);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.Workflow.TriggerRegistrationAsync(lease.Id, OwnerId, new RegistrationAuthorizationRequest("2026-08-rli-delega-bozza", false)));

        regService.Verify(s => s.SubmitRegistrationAsync(It.IsAny<LeaseContract>()), Times.Never);
        authRepo.Verify(r => r.AddAsync(It.IsAny<LeaseRegistrationAuthorization>()), Times.Never);
    }

    [Fact]
    public async Task TriggerRegistrationAsync_WrongTosVersion_DoesNotCallProvider()
    {
        var (sut, regService, _) = CreateSut();
        var lease = SignedLease();
        SetupLease(sut.LeaseRepo, lease);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.Workflow.TriggerRegistrationAsync(lease.Id, OwnerId, new RegistrationAuthorizationRequest("wrong", true)));

        regService.Verify(s => s.SubmitRegistrationAsync(It.IsAny<LeaseContract>()), Times.Never);
    }

    [Fact]
    public async Task TriggerRegistrationAsync_ValidDelega_PersistsAuthorizationThenSubmits()
    {
        var (sut, regService, authRepo) = CreateSut();
        var lease = SignedLease();
        SetupLease(sut.LeaseRepo, lease);
        sut.RegRepo.Setup(r => r.GetByLeaseIdAsync(lease.Id)).ReturnsAsync((LeaseRegistration?)null);
        sut.RegRepo.Setup(r => r.AddAsync(It.IsAny<LeaseRegistration>())).ReturnsAsync((LeaseRegistration r) => r);
        sut.LeaseRepo.Setup(r => r.UpdateAsync(It.IsAny<LeaseContract>())).ReturnsAsync((LeaseContract l) => l);
        sut.Events.Setup(r => r.AddAsync(It.IsAny<LeaseEvent>())).ReturnsAsync((LeaseEvent e) => e);
        authRepo.Setup(r => r.AddAsync(It.IsAny<LeaseRegistrationAuthorization>()))
            .ReturnsAsync((LeaseRegistrationAuthorization a) => a);
        regService.Setup(s => s.SubmitRegistrationAsync(lease)).ReturnsAsync("RLI-STUB-OK");

        var registration = await sut.Workflow.TriggerRegistrationAsync(lease.Id, OwnerId, ValidAuth);

        Assert.Equal("RLI-STUB-OK", registration.ExternalRegistrationId);
        authRepo.Verify(r => r.AddAsync(It.Is<LeaseRegistrationAuthorization>(a =>
            a.LeaseContractId == lease.Id
            && a.AttestationAccepted
            && a.TosVersion == "2026-08-rli-delega-bozza"
            && a.OrgId == lease.OrgId)), Times.Once);
        sut.Events.Verify(r => r.AddAsync(It.Is<LeaseEvent>(e => e.EventType == LeaseEventType.RegistrationAuthorized)), Times.Once);
        sut.Events.Verify(r => r.AddAsync(It.Is<LeaseEvent>(e => e.EventType == LeaseEventType.RegistrationSubmitted)), Times.Once);
        regService.Verify(s => s.SubmitRegistrationAsync(lease), Times.Once);
    }

    private static void SetupLease(Mock<ILeaseContractRepository> repo, LeaseContract lease) =>
        repo.Setup(r => r.GetByIdWithDetailsAsync(lease.Id)).ReturnsAsync(lease);

    private static LeaseContract SignedLease()
    {
        var property = new Property { Id = Guid.NewGuid(), OwnerId = OwnerId, Name = "P" };
        return new LeaseContract
        {
            Id = Guid.NewGuid(),
            OrgId = Guid.NewGuid(),
            PropertyId = property.Id,
            Property = property,
            Status = LeaseStatus.Signed,
            FiscalRegime = FiscalRegime.CedolareSecca,
            StartDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2030, 8, 31, 0, 0, 0, DateTimeKind.Utc),
            MonthlyRent = 1000m,
        };
    }

    private static (SutBundle Sut, Mock<ILeaseRegistrationService> RegService, Mock<ILeaseRegistrationAuthorizationRepository> AuthRepo) CreateSut()
    {
        var leaseRepo = new Mock<ILeaseContractRepository>();
        var regRepo = new Mock<ILeaseRegistrationRepository>();
        var events = new Mock<ILeaseEventRepository>();
        var templates = new Mock<ILeaseTemplateService>();
        var esign = new Mock<ILeaseESignService>();
        var regService = new Mock<ILeaseRegistrationService>();
        var properties = new Mock<IPropertyRepository>();
        var authRepo = new Mock<ILeaseRegistrationAuthorizationRepository>();
        var workflow = new LeaseWorkflowService(
            leaseRepo.Object,
            regRepo.Object,
            events.Object,
            templates.Object,
            esign.Object,
            regService.Object,
            properties.Object,
            authRepo.Object,
            Options.Create(new RliOptions { TosVersion = "2026-08-rli-delega-bozza" }),
            Mock.Of<ILogger<LeaseWorkflowService>>());
        return (new SutBundle(workflow, leaseRepo, regRepo, events), regService, authRepo);
    }

    private sealed record SutBundle(
        LeaseWorkflowService Workflow,
        Mock<ILeaseContractRepository> LeaseRepo,
        Mock<ILeaseRegistrationRepository> RegRepo,
        Mock<ILeaseEventRepository> Events);
}
