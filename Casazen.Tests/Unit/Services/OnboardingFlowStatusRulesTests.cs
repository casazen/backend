using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The flow status (NF-106, E10/F01/US01/T02): the pure rules that order the states of a flow on a property, and the proof that the counts
/// and the lists of the service stay within what the caller reaches.
/// </summary>
public class OnboardingFlowStatusRulesTests
{
    private static readonly FlowPropertyFacts ShortRentProperty = new(Guid.NewGuid(), "Trullo", "Ostuni", RentalMode.Short, true);

    private static readonly FlowPrerequisite ConnectionMet = FlowPrerequisite.Holds(OnboardingPrerequisiteKeys.Connection);

    private static readonly FlowPrerequisite MappingMet = FlowPrerequisite.Holds(OnboardingPrerequisiteKeys.Mapping);

    private static PropertyFlowActivation Activation(DateTime? deactivatedAt = null) => new()
    {
        OrgId = Guid.NewGuid(),
        PropertyId = ShortRentProperty.PropertyId,
        Flow = OnboardingFlow.Bookings,
        ActivatedByUserId = "auth0|host",
        DeactivatedAt = deactivatedAt,
    };

    // ─── The proof: what the caller reaches ───────────────────────────────────────────────────────────

    /// <summary>
    /// Permission rule: the counts and the lists of the flow status include only the properties the caller's <see cref="Casazen.Core.Authorization.HostScope"/>
    /// reaches and never another org's. A collaborator «Solo alcuni» is counted and listed for the property it was given and for no other;
    /// the whole org for the two of its properties; and no provider is ever asked about a property outside the scope.
    /// </summary>
    [Fact]
    public async Task FlowStatus_HostScope_NarrowsToReachableProperties_Test()
    {
        await using var db = HostScopeScenario.NewInMemoryDb();
        var world = await HostScopeScenario.SeedAsync(db);
        db.PropertyFlowActivations.Add(new PropertyFlowActivation
        {
            OrgId = world.OrgId,
            PropertyId = world.Granted.Id,
            Flow = OnboardingFlow.Bookings,
            ActivatedByUserId = world.OwnerUserId,
        });
        // An activation of the other org: it must never be counted for this one.
        db.PropertyFlowActivations.Add(new PropertyFlowActivation
        {
            OrgId = world.OtherOrgId,
            PropertyId = world.OtherOrgProperty.Id,
            Flow = OnboardingFlow.Bookings,
            ActivatedByUserId = "auth0|altro",
        });
        await db.SaveChangesAsync();

        var providers = Enum.GetValues<OnboardingFlow>().Select(flow => new RecordingProvider(flow)).ToList();
        var service = new OnboardingFlowStatusService(db, providers);
        var ct = CancellationToken.None;

        // The collaborator «Solo alcuni» reaches one property: one in each count of each flow.
        var restricted = await service.GetSummariesAsync(world.Restricted, ct);
        Assert.Equal(Enum.GetValues<OnboardingFlow>(), restricted.Select(summary => summary.Flow));
        Assert.All(restricted, summary => Assert.Equal(1, summary.Total));
        var restrictedBookings = restricted.Single(summary => summary.Flow == OnboardingFlow.Bookings);
        Assert.Equal((1, 0, 0), (restrictedBookings.Active, restrictedBookings.ToComplete, restrictedBookings.NotAvailable));
        var restrictedListings = restricted.Single(summary => summary.Flow == OnboardingFlow.Listings);
        Assert.Equal((0, 1, 0), (restrictedListings.Active, restrictedListings.ToComplete, restrictedListings.NotAvailable));

        var restrictedPage = await service.GetPropertiesAsync(world.Restricted, OnboardingFlow.Bookings, null, null, 1, 50, ct);
        Assert.Equal(world.Granted.Id, Assert.Single(restrictedPage.Items).PropertyId);
        Assert.Equal(1, restrictedPage.TotalCount);

        // The whole org: its two properties, never the one of the other org, by name.
        var orgWide = await service.GetSummariesAsync(world.OrgWide, ct);
        Assert.All(orgWide, summary => Assert.Equal(2, summary.Total));
        var orgWideBookings = orgWide.Single(summary => summary.Flow == OnboardingFlow.Bookings);
        Assert.Equal((1, 1, 0), (orgWideBookings.Active, orgWideBookings.ToComplete, orgWideBookings.NotAvailable));

        var orgWidePage = await service.GetPropertiesAsync(world.OrgWide, OnboardingFlow.Bookings, null, null, 1, 50, ct);
        Assert.Equal([world.Hidden.Id, world.Granted.Id], orgWidePage.Items.Select(item => item.PropertyId));
        Assert.Equal(2, orgWidePage.TotalCount);
        Assert.DoesNotContain(orgWidePage.Items, item => item.PropertyId == world.OtherOrgProperty.Id);

        // A state and a search narrow the list, never widen it: the property of the other org is not found by its own name.
        var toComplete = await service.GetPropertiesAsync(world.OrgWide, OnboardingFlow.Bookings, OnboardingFlowStates.ToComplete, null, 1, 50, ct);
        Assert.Equal(world.Hidden.Id, Assert.Single(toComplete.Items).PropertyId);
        var foreign = await service.GetPropertiesAsync(world.OrgWide, OnboardingFlow.Bookings, null, "Villa Altrove", 1, 50, ct);
        Assert.Empty(foreign.Items);
        Assert.Equal(0, foreign.TotalCount);
        var hiddenFromTheCollaborator = await service.GetPropertiesAsync(world.Restricted, OnboardingFlow.Bookings, null, "Casa Bianca", 1, 50, ct);
        Assert.Empty(hiddenFromTheCollaborator.Items);

        // No provider was asked about a property outside the scope that asked.
        Assert.DoesNotContain(providers.SelectMany(provider => provider.Asked), id => id == world.OtherOrgProperty.Id);
    }

    // ─── The rules ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_LongRentOrInactiveProperty_IsNotAvailableBeforeAnyPrerequisite()
    {
        var unmet = FlowReadiness.Needing(FlowPrerequisite.Lacks(OnboardingPrerequisiteKeys.Connection, OnboardingFlowReasons.ConnectionMissing));

        var longRent = OnboardingFlowStatusRules.Evaluate(ShortRentProperty with { RentalMode = RentalMode.Long }, OnboardingFlow.Bookings, Activation(), unmet);
        var inactive = OnboardingFlowStatusRules.Evaluate(ShortRentProperty with { IsActive = false }, OnboardingFlow.Bookings, Activation(), unmet);

        Assert.Equal((OnboardingFlowStates.NotAvailable, OnboardingFlowReasons.PropertyLongRent, (string?)null), (longRent.State, longRent.Reason, longRent.Action));
        Assert.Equal((OnboardingFlowStates.NotAvailable, OnboardingFlowReasons.PropertyInactive, (string?)null), (inactive.State, inactive.Reason, inactive.Action));
        Assert.Empty(longRent.Prerequisites);
        Assert.Empty(inactive.Prerequisites);
    }

    [Fact]
    public void Evaluate_NoProviderOrProviderSaysNotAvailable_IsNotAvailableWithItsReason()
    {
        var nobody = OnboardingFlowStatusRules.Evaluate(ShortRentProperty, OnboardingFlow.Reports, null, null);
        var notProvided = OnboardingFlowStatusRules.Evaluate(ShortRentProperty, OnboardingFlow.Messages, null, FlowReadiness.NotProvided);
        var onChannel = OnboardingFlowStatusRules.Evaluate(
            ShortRentProperty, OnboardingFlow.Listings, Activation(), FlowReadiness.NotAvailable(OnboardingFlowReasons.ContentManagedOnChannel));

        Assert.Equal((OnboardingFlowStates.NotAvailable, OnboardingFlowReasons.NotProvided), (nobody.State, nobody.Reason));
        Assert.Equal((OnboardingFlowStates.NotAvailable, OnboardingFlowReasons.NotProvided), (notProvided.State, notProvided.Reason));
        Assert.Equal((OnboardingFlowStates.NotAvailable, OnboardingFlowReasons.ContentManagedOnChannel), (onChannel.State, onChannel.Reason));
    }

    [Fact]
    public void Evaluate_AnyUnmetPrerequisite_IsToCompleteWithTheFirstOnesReasonAndAction()
    {
        var readiness = FlowReadiness.Needing(
            ConnectionMet,
            FlowPrerequisite.Lacks(OnboardingPrerequisiteKeys.Mapping, OnboardingFlowReasons.MappingUnconfirmed),
            FlowPrerequisite.Lacks(OnboardingPrerequisiteKeys.Cin, OnboardingFlowReasons.CinMissingOrInvalid));

        var status = OnboardingFlowStatusRules.Evaluate(ShortRentProperty, OnboardingFlow.CheckInCompliance, null, readiness);

        Assert.Equal(
            (OnboardingFlowStates.ToComplete, OnboardingFlowReasons.MappingUnconfirmed, OnboardingFlowActions.ConfirmMapping),
            (status.State, status.Reason, status.Action));
        Assert.Equal([true, false, false], status.Prerequisites.Select(prerequisite => prerequisite.Met));
    }

    [Fact]
    public void Evaluate_UnmetPrerequisiteWithoutReason_IsNeverTakenForMet()
    {
        var readiness = FlowReadiness.Needing(new FlowPrerequisite(OnboardingPrerequisiteKeys.Mapping, false));

        var status = OnboardingFlowStatusRules.Evaluate(ShortRentProperty, OnboardingFlow.Bookings, Activation(), readiness);

        Assert.Equal(OnboardingFlowStates.ToComplete, status.State);
        Assert.Equal(OnboardingFlowReasons.MappingUnavailable, status.Reason);
        Assert.Null(status.Action);
    }

    [Fact]
    public void Evaluate_AllMetWithoutAnActiveActivation_IsToCompleteWithActivate()
    {
        var readiness = FlowReadiness.Needing(ConnectionMet, MappingMet);

        var neverActivated = OnboardingFlowStatusRules.Evaluate(ShortRentProperty, OnboardingFlow.Bookings, null, readiness);
        var switchedOff = OnboardingFlowStatusRules.Evaluate(ShortRentProperty, OnboardingFlow.Bookings, Activation(DateTime.UtcNow), readiness);

        Assert.Equal(
            (OnboardingFlowStates.ToComplete, OnboardingFlowReasons.FlowNotActivated, OnboardingFlowActions.Activate),
            (neverActivated.State, neverActivated.Reason, neverActivated.Action));
        Assert.Equal((neverActivated.State, neverActivated.Reason, neverActivated.Action), (switchedOff.State, switchedOff.Reason, switchedOff.Action));
    }

    [Fact]
    public void Evaluate_AllMetWithAnActivation_IsActive()
    {
        var status = OnboardingFlowStatusRules.Evaluate(
            ShortRentProperty, OnboardingFlow.Bookings, Activation(), FlowReadiness.Needing(ConnectionMet, MappingMet));

        Assert.Equal(OnboardingFlowStates.Active, status.State);
        Assert.Null(status.Reason);
        Assert.Null(status.Action);
    }

    [Fact]
    public void Evaluate_ActivatedFlowWhoseConnectionIsLost_IsToCompleteAgainAndActiveWhenItHolds()
    {
        var activation = Activation();
        var lost = FlowReadiness.Needing(
            FlowPrerequisite.Lacks(OnboardingPrerequisiteKeys.Connection, OnboardingFlowReasons.ConnectionLost),
            MappingMet);

        var whileLost = OnboardingFlowStatusRules.Evaluate(ShortRentProperty, OnboardingFlow.Bookings, activation, lost);
        var afterReconnect = OnboardingFlowStatusRules.Evaluate(
            ShortRentProperty, OnboardingFlow.Bookings, activation, FlowReadiness.Needing(ConnectionMet, MappingMet));

        Assert.Equal(
            (OnboardingFlowStates.ToComplete, OnboardingFlowReasons.ConnectionLost, OnboardingFlowActions.Reconnect),
            (whileLost.State, whileLost.Reason, whileLost.Action));
        Assert.Equal(OnboardingFlowStates.Active, afterReconnect.State);
    }

    [Fact]
    public void IsShortRent_ForEveryRentalMode_AgreesWithThePredicateOfThePropertyRentalModeRules()
    {
        var predicate = PropertyRentalModeRules.IsShortRent.Compile();

        Assert.All(
            Enum.GetValues<RentalMode>(),
            mode => Assert.Equal(predicate(new Property { RentalMode = mode }), OnboardingFlowStatusRules.IsShortRent(mode)));
    }

    /// <summary>A provider that finds every prerequisite met and remembers the properties it was asked about.</summary>
    private sealed class RecordingProvider(OnboardingFlow flow) : IFlowReadinessProvider
    {
        public List<Guid> Asked { get; } = [];

        public OnboardingFlow Flow { get; } = flow;

        public int Order => FlowProviderOrders.Provider;

        public Task<IReadOnlyDictionary<Guid, FlowReadiness>> EvaluateAsync(
            Guid orgId,
            IReadOnlyCollection<Guid> propertyIds,
            CancellationToken ct)
        {
            Asked.AddRange(propertyIds);
            IReadOnlyDictionary<Guid, FlowReadiness> readiness = propertyIds.ToDictionary(
                id => id,
                _ => FlowReadiness.Needing(FlowPrerequisite.Holds(OnboardingPrerequisiteKeys.Connection)));
            return Task.FromResult(readiness);
        }
    }
}
