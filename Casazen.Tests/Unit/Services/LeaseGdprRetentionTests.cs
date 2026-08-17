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

public class LeaseGdprRetentionTests
{
    private const string OwnerId = "auth0|gdpr-lease-owner";
    private static readonly Guid PropertyId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    [Fact]
    public async Task AC7_CreateDraft_SetsTenYearRetentionAndThirtyDayDeadline()
    {
        var start = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var property = new Property { Id = PropertyId, OwnerId = OwnerId, OrgId = Guid.NewGuid(), Name = "GDPR Property" };
        var properties = new Mock<IPropertyRepository>();
        properties.Setup(r => r.GetByIdAsync(PropertyId)).ReturnsAsync(property);
        var ape = new Mock<IApeComplianceService>();
        ape.Setup(s => s.EnsurePropertyHasValidApeAsync(PropertyId)).Returns(Task.CompletedTask);
        var leases = new Mock<ILeaseContractRepository>();
        leases.Setup(r => r.AddAsync(It.IsAny<LeaseContract>())).ReturnsAsync((LeaseContract l) => l);
        var events = new Mock<ILeaseEventRepository>();
        events.Setup(r => r.AddAsync(It.IsAny<LeaseEvent>())).ReturnsAsync((LeaseEvent e) => e);

        var sut = new LeaseWorkflowService(
            leases.Object,
            Mock.Of<ILeaseRegistrationRepository>(),
            events.Object,
            Mock.Of<ILeaseTemplateService>(),
            Mock.Of<ILeaseESignService>(),
            Mock.Of<ILeaseRegistrationService>(),
            properties.Object,
            Mock.Of<ILeaseRegistrationAuthorizationRepository>(),
            ape.Object,
            Options.Create(new RliOptions()),
            Mock.Of<ILogger<LeaseWorkflowService>>());

        var result = await sut.CreateDraftAsync(PropertyId, OwnerId, new CreateLeaseRequest(
            FiscalRegime.CedolareSecca,
            start,
            start.AddYears(4),
            1100m,
            [
                new CreatePartyRequest(PartyRole.Landlord, "Mario", "Rossi", "RSSMRA80A01H501Z", "IT", "mario@example.com"),
                new CreatePartyRequest(PartyRole.Tenant, "Giulia", "Verdi", "VRDGLI85B02F205X", "IT", "giulia@example.com"),
            ]));

        Assert.Equal(start.AddYears(10), result.DataRetentionUntil);
        Assert.Equal(start.AddDays(30), result.RegistrationDeadline);
        Assert.False(result.ErasureRequested);
    }

    [Fact]
    public void AC7_LeaseErasureApi_IsNotImplemented_TrackedGap()
    {
        // Production has LeaseEventType.ErasureRequested and LeaseContract.ErasureRequested,
        // but ILeaseWorkflowService / IGdprService expose no lease-erasure operation (guest-only GDPR).
        // US-009 verifies current behaviour; implementing lease erasure is a separate issue.
        Assert.DoesNotContain(
            typeof(ILeaseWorkflowService).GetMethods(),
            m => m.Name.Contains("Erasur", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(IGdprService).GetMethods(),
            m => m.Name.Contains("Lease", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(Enum.GetNames<LeaseEventType>(), n => n == nameof(LeaseEventType.ErasureRequested));
    }
}
