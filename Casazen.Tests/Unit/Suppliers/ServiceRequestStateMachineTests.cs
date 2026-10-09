using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>SU-10 (A4-19) and SP-04: the table of the transitions of a service request, pair by pair, and who may cancel from where.</summary>
public class ServiceRequestStateMachineTests
{
    private static readonly HashSet<(ServiceRequestStatus From, ServiceRequestStatus To)> Allowed =
    [
        (ServiceRequestStatus.Richiesto, ServiceRequestStatus.PresoInCarico),
        (ServiceRequestStatus.Richiesto, ServiceRequestStatus.Rifiutato),
        (ServiceRequestStatus.Richiesto, ServiceRequestStatus.Annullato),
        (ServiceRequestStatus.PresoInCarico, ServiceRequestStatus.InCorso),
        (ServiceRequestStatus.PresoInCarico, ServiceRequestStatus.Completato),
        (ServiceRequestStatus.PresoInCarico, ServiceRequestStatus.Annullato),
        (ServiceRequestStatus.InCorso, ServiceRequestStatus.Completato),
        (ServiceRequestStatus.InCorso, ServiceRequestStatus.Annullato),
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
    [InlineData(ServiceRequestStatus.Annullato)]
    public void CanTransition_FromAFinalStatus_AllowsNothing(ServiceRequestStatus final)
    {
        Assert.True(ServiceRequestStateMachine.IsFinal(final));
        Assert.All(Enum.GetValues<ServiceRequestStatus>(), to => Assert.False(ServiceRequestStateMachine.CanTransition(final, to)));
    }

    [Theory]
    [InlineData(ServiceRequestStatus.Richiesto)]
    [InlineData(ServiceRequestStatus.PresoInCarico)]
    [InlineData(ServiceRequestStatus.InCorso)]
    [InlineData(ServiceRequestStatus.Completato)]
    public void IsFinal_AStatusThatIsNotFinal_IsFalse(ServiceRequestStatus status)
    {
        Assert.False(ServiceRequestStateMachine.IsFinal(status));
    }

    [Fact]
    public void Status_ValuesAreStoredAsIntegers_SoTheyNeverMove()
    {
        // ServiceRequests.Status is an integer column and the mobile app reads names: never reorder, only append (SP-04).
        Assert.Equal(
            new[] { 0, 1, 2, 3, 4, 5, 6 },
            new[]
            {
                (int)ServiceRequestStatus.Richiesto,
                (int)ServiceRequestStatus.PresoInCarico,
                (int)ServiceRequestStatus.InCorso,
                (int)ServiceRequestStatus.Completato,
                (int)ServiceRequestStatus.Pagato,
                (int)ServiceRequestStatus.Rifiutato,
                (int)ServiceRequestStatus.Annullato,
            });
    }

    [Fact]
    public void ActorParty_ValuesAreStored_SoTheyNeverMove()
    {
        Assert.Equal(new[] { 0, 1, 2 }, new[] { (int)ServiceRequestActorParty.Host, (int)ServiceRequestActorParty.Supplier, (int)ServiceRequestActorParty.System });
    }

    // ─── Who may cancel from where ───

    [Theory]
    [InlineData(ServiceRequestStatus.Richiesto, true)]
    [InlineData(ServiceRequestStatus.PresoInCarico, true)]
    [InlineData(ServiceRequestStatus.InCorso, true)]
    [InlineData(ServiceRequestStatus.Completato, false)]
    [InlineData(ServiceRequestStatus.Pagato, false)]
    [InlineData(ServiceRequestStatus.Rifiutato, false)]
    [InlineData(ServiceRequestStatus.Annullato, false)]
    public void CanCancel_Host_UpToAndIncludingTheWorkInProgress(ServiceRequestStatus from, bool expected)
    {
        Assert.Equal(expected, ServiceRequestStateMachine.CanCancel(from, ServiceRequestActorParty.Host));
    }

    [Theory]
    [InlineData(ServiceRequestStatus.Richiesto, true)]
    [InlineData(ServiceRequestStatus.PresoInCarico, true)]
    [InlineData(ServiceRequestStatus.InCorso, false)]
    [InlineData(ServiceRequestStatus.Completato, false)]
    [InlineData(ServiceRequestStatus.Pagato, false)]
    [InlineData(ServiceRequestStatus.Rifiutato, false)]
    [InlineData(ServiceRequestStatus.Annullato, false)]
    public void CanCancel_Supplier_OnlyBeforeTheWorkStarted(ServiceRequestStatus from, bool expected)
    {
        Assert.Equal(expected, ServiceRequestStateMachine.CanCancel(from, ServiceRequestActorParty.Supplier));
    }

    [Theory]
    [InlineData(ServiceRequestStatus.Richiesto, true)]
    [InlineData(ServiceRequestStatus.PresoInCarico, false)]
    [InlineData(ServiceRequestStatus.InCorso, false)]
    [InlineData(ServiceRequestStatus.Completato, false)]
    [InlineData(ServiceRequestStatus.Pagato, false)]
    [InlineData(ServiceRequestStatus.Rifiutato, false)]
    [InlineData(ServiceRequestStatus.Annullato, false)]
    public void CanCancel_System_OnlyARequestNobodyAnswered(ServiceRequestStatus from, bool expected)
    {
        Assert.Equal(expected, ServiceRequestStateMachine.CanCancel(from, ServiceRequestActorParty.System));
    }

    [Fact]
    public void CanCancel_NeverContradictsTheTable_AnActorCannotCancelWhereNobodyCan()
    {
        foreach (var actor in Enum.GetValues<ServiceRequestActorParty>())
        {
            foreach (var from in Enum.GetValues<ServiceRequestStatus>())
            {
                if (ServiceRequestStateMachine.CanCancel(from, actor))
                    Assert.True(ServiceRequestStateMachine.CanTransition(from, ServiceRequestStatus.Annullato), $"{actor} cancels {from}");
            }
        }
    }
}
