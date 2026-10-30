using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>
/// SR-08a: what the host can do about a request is worked out in one place and the page of the interventions shows it as it is.
/// The table below is the whole rule (status x proposal x amount to confirm x how it is paid x time of the last reminder), and the
/// last test ties it to the state machine so the two cannot drift apart.
/// </summary>
public class HostServiceRequestActionsTests
{
    private static readonly DateTime Now = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

    private static HostServiceRequestAction Of(
        ServiceRequestStatus status,
        bool proposal = false,
        bool needsConfirmation = false,
        ServiceRequestPaymentMode mode = ServiceRequestPaymentMode.Manual,
        DateTime? lastReminded = null) =>
        HostServiceRequestActions.Of(new HostRequestFacts(status, proposal, needsConfirmation, mode, lastReminded), Now, remindIntervalHours: 6);

    public static TheoryData<ServiceRequestStatus, bool, bool, ServiceRequestPaymentMode, string, string[]> Table => new()
    {
        { ServiceRequestStatus.Richiesto, false, false, ServiceRequestPaymentMode.Manual, "remind", ["remind", "cancel"] },
        { ServiceRequestStatus.Richiesto, true, false, ServiceRequestPaymentMode.Manual, "acceptProposal", ["acceptProposal", "rejectProposal", "cancel"] },
        { ServiceRequestStatus.PresoInCarico, false, false, ServiceRequestPaymentMode.Manual, "none", ["cancel"] },
        { ServiceRequestStatus.InCorso, false, false, ServiceRequestPaymentMode.Online, "none", ["cancel"] },
        { ServiceRequestStatus.Completato, false, false, ServiceRequestPaymentMode.Online, "pay", ["pay"] },
        { ServiceRequestStatus.Completato, false, false, ServiceRequestPaymentMode.Manual, "markPaid", ["markPaid"] },
        { ServiceRequestStatus.Completato, false, true, ServiceRequestPaymentMode.Online, "confirmFinalAmount", ["confirmFinalAmount"] },
        { ServiceRequestStatus.Completato, false, true, ServiceRequestPaymentMode.Manual, "confirmFinalAmount", ["confirmFinalAmount"] },
        { ServiceRequestStatus.Pagato, false, false, ServiceRequestPaymentMode.Manual, "none", ["requestAgain"] },
        { ServiceRequestStatus.Rifiutato, false, false, ServiceRequestPaymentMode.Manual, "requestAgain", ["requestAgain"] },
        { ServiceRequestStatus.Annullato, false, false, ServiceRequestPaymentMode.Manual, "requestAgain", ["requestAgain"] },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void Of_ReadsTheStatusTheProposalTheAmountAndTheWayItIsPaid(
        ServiceRequestStatus status,
        bool proposal,
        bool needsConfirmation,
        ServiceRequestPaymentMode mode,
        string kind,
        string[] available)
    {
        var action = Of(status, proposal, needsConfirmation, mode);

        Assert.Equal(kind, action.Kind);
        Assert.Equal(available, action.Available);
        Assert.Null(action.RemindAvailableAt);
    }

    [Fact]
    public void Of_ANewRequestThatWasRemindedLessThanTheIntervalAgo_HasNothingToDoYetAndSaysWhenItCanRemindAgain()
    {
        var last = Now.AddHours(-1);

        var action = Of(ServiceRequestStatus.Richiesto, lastReminded: last);

        Assert.Equal("none", action.Kind);
        Assert.Equal(last.AddHours(6), action.RemindAvailableAt);
        Assert.Equal(new[] { "cancel" }, action.Available);
    }

    [Fact]
    public void Of_ANewRequestRemindedExactlyTheIntervalAgo_CanBeRemindedAgain()
    {
        var action = Of(ServiceRequestStatus.Richiesto, lastReminded: Now.AddHours(-6));

        Assert.Equal("remind", action.Kind);
        Assert.Null(action.RemindAvailableAt);
    }

    [Fact]
    public void Of_AProposalIsAnsweredBeforeAnythingElse_EvenRightAfterAReminder()
    {
        var action = Of(ServiceRequestStatus.Richiesto, proposal: true, lastReminded: Now.AddMinutes(-5));

        Assert.Equal("acceptProposal", action.Kind);
        Assert.Null(action.RemindAvailableAt);
        Assert.DoesNotContain("remind", action.Available);
    }

    [Fact]
    public void Of_EveryStatus_OffersCancelExactlyWhereTheStateMachineLetsTheHostCancel_AndPaymentOnlyWhereItLetsTheHostPay()
    {
        foreach (var status in Enum.GetValues<ServiceRequestStatus>())
        {
            foreach (var mode in Enum.GetValues<ServiceRequestPaymentMode>())
            {
                var action = Of(status, mode: mode);

                Assert.Equal(
                    ServiceRequestStateMachine.CanCancel(status, ServiceRequestActorParty.Host),
                    action.Available.Contains("cancel"));

                var pays = action.Available.Contains("pay") || action.Available.Contains("markPaid");
                Assert.Equal(ServiceRequestStateMachine.CanTransition(status, ServiceRequestStatus.Pagato), pays);

                // A job is paid inside CasaZen with "pay" and outside with "markPaid", never both.
                if (pays)
                    Assert.Equal(mode == ServiceRequestPaymentMode.Online, action.Available.Contains("pay"));

                // The one action of the row is the first of the list, or there is none.
                Assert.True(action.Kind == "none" || action.Available[0] == action.Kind, $"{status}/{mode}: {action.Kind}");
            }
        }
    }
}
