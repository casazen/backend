using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

public class ServiceRequestStateMachineTests
{
    private static readonly HashSet<(ServiceRequestStatus From, ServiceRequestStatus To)> Allowed =
    [
        (ServiceRequestStatus.Richiesto, ServiceRequestStatus.PresoInCarico),
        (ServiceRequestStatus.Richiesto, ServiceRequestStatus.Rifiutato),
        (ServiceRequestStatus.PresoInCarico, ServiceRequestStatus.Completato),
        (ServiceRequestStatus.InCorso, ServiceRequestStatus.Completato),
        (ServiceRequestStatus.Completato, ServiceRequestStatus.Pagato),
    ];

    public static TheoryData<ServiceRequestStatus, ServiceRequestStatus> AllPairs()
    {
        var data = new TheoryData<ServiceRequestStatus, ServiceRequestStatus>();
        foreach (var from in Enum.GetValues<ServiceRequestStatus>())
        {
            foreach (var to in Enum.GetValues<ServiceRequestStatus>())
                data.Add(from, to);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void CanTransition_EveryPairOfStatuses_AllowsOnlyTheLifecycleOfARequest(ServiceRequestStatus from, ServiceRequestStatus to)
    {
        Assert.Equal(Allowed.Contains((from, to)), ServiceRequestStateMachine.CanTransition(from, to));
    }

    [Theory]
    [InlineData(ServiceRequestStatus.Rifiutato)]
    [InlineData(ServiceRequestStatus.Pagato)]
    public void CanTransition_FromAFinalStatus_AllowsNothing(ServiceRequestStatus final)
    {
        Assert.All(Enum.GetValues<ServiceRequestStatus>(), to => Assert.False(ServiceRequestStateMachine.CanTransition(final, to)));
    }
}
