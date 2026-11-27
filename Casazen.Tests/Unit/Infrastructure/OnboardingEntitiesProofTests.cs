using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// Proof of the tables of the Octorate activation (NF-104..NF-107, E10/F01/US01/T01): what an organization has written there is not visible
/// to another one (data and tenant isolation rule, TN-2).
/// </summary>
public class OnboardingEntitiesProofTests
{
    private sealed class FixedTenantContext(Guid orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }

    /// <summary>
    /// Data rule: each of the six tables of the activation (the state of a part, the activation of a flow, the guided panel step, the
    /// «Do it for me» request, the pilot group and the per-property data) written by organization A is not read by organization B, whether
    /// B asks for every row or for the very property of A, and A still reads its own. A row of another org is never returned.
    /// </summary>
    [Fact]
    public async Task OnboardingEntities_TenantFilter_HidesOtherOrgRows_Test()
    {
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var propertyOfA = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"OnboardingEntitiesTenancy_{Guid.NewGuid()}")
            .Options;

        await using (var writer = new AppDbContext(options, new FixedTenantContext(orgA)))
        {
            writer.OnboardingPartStates.Add(new OnboardingPartState
            {
                OrgId = orgA,
                PartKey = OnboardingPartKey.Compliance,
                Status = OnboardingPartStatus.Deferred,
                UpdatedByUserId = "auth0|user-a",
            });
            writer.PropertyFlowActivations.Add(new PropertyFlowActivation
            {
                OrgId = orgA,
                PropertyId = propertyOfA,
                Flow = OnboardingFlow.Bookings,
                ActivatedByUserId = "auth0|user-a",
            });
            writer.OnboardingPanelSteps.Add(new OnboardingPanelStep
            {
                OrgId = orgA,
                PropertyId = propertyOfA,
                Kind = PanelStepKind.PoliceConnection,
                HandoffNonceHash = new string('a', OnboardingPanelStep.HandoffNonceHashLength),
            });
            writer.OnboardingAssistRequests.Add(new OnboardingAssistRequest
            {
                OrgId = orgA,
                PartKey = OnboardingPartKey.Compliance,
                PropertyId = propertyOfA,
                Context = OnboardingAssistContexts.PanelPolice,
                DraftWizardType = WizardType.OnboardingActivation,
                DraftEntityId = propertyOfA,
                RequestedByUserId = "auth0|user-a",
            });
            writer.OnboardingPilotMembers.Add(new OnboardingPilotMember
            {
                OrgId = orgA,
                PropertyId = propertyOfA,
                AddedByUserId = "auth0|user-a",
            });
            writer.PropertyOnboardingData.Add(new PropertyOnboardingData
            {
                OrgId = orgA,
                PropertyId = propertyOfA,
                AlloggiatiRoomNumbersJson = """["101","102"]""",
                UpdatedByUserId = "auth0|user-a",
            });
            await writer.SaveChangesAsync();
        }

        await using (var asOrgB = new AppDbContext(options, new FixedTenantContext(orgB)))
        {
            Assert.Empty(await asOrgB.OnboardingPartStates.ToListAsync());
            Assert.Empty(await asOrgB.PropertyFlowActivations.ToListAsync());
            Assert.Empty(await asOrgB.PropertyFlowActivations.Where(a => a.PropertyId == propertyOfA).ToListAsync());
            Assert.Empty(await asOrgB.OnboardingPanelSteps.ToListAsync());
            Assert.Empty(await asOrgB.OnboardingPanelSteps.Where(s => s.PropertyId == propertyOfA).ToListAsync());
            Assert.Empty(await asOrgB.OnboardingAssistRequests.ToListAsync());
            Assert.Empty(await asOrgB.OnboardingAssistRequests.Where(r => r.PropertyId == propertyOfA).ToListAsync());
            Assert.Empty(await asOrgB.OnboardingPilotMembers.ToListAsync());
            Assert.Empty(await asOrgB.OnboardingPilotMembers.Where(m => m.PropertyId == propertyOfA).ToListAsync());
            Assert.Empty(await asOrgB.PropertyOnboardingData.ToListAsync());
            Assert.Empty(await asOrgB.PropertyOnboardingData.Where(d => d.PropertyId == propertyOfA).ToListAsync());
        }

        await using var asOrgA = new AppDbContext(options, new FixedTenantContext(orgA));
        Assert.Equal(OnboardingPartKey.Compliance, Assert.Single(await asOrgA.OnboardingPartStates.ToListAsync()).PartKey);
        Assert.Equal(OnboardingFlow.Bookings, Assert.Single(await asOrgA.PropertyFlowActivations.ToListAsync()).Flow);
        Assert.Equal(PanelStepKind.PoliceConnection, Assert.Single(await asOrgA.OnboardingPanelSteps.ToListAsync()).Kind);

        // The request holds the reference to the draft of the wizard, and nothing of its content.
        var request = Assert.Single(await asOrgA.OnboardingAssistRequests.ToListAsync());
        Assert.Equal((WizardType.OnboardingActivation, (Guid?)propertyOfA), (request.DraftWizardType, request.DraftEntityId));

        Assert.Equal(propertyOfA, Assert.Single(await asOrgA.OnboardingPilotMembers.ToListAsync()).PropertyId);
        Assert.Equal("""["101","102"]""", Assert.Single(await asOrgA.PropertyOnboardingData.ToListAsync()).AlloggiatiRoomNumbersJson);
    }
}
