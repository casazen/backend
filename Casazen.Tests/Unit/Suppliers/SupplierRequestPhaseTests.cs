using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>
/// SP-03b: the phase of a request, derived in one place. Every status has its phase, a proposal or a quote that waits only matters
/// for a request still <c>Richiesto</c>, and a status nobody knows yet is <c>unknown</c>, never an exception.
/// </summary>
public class SupplierRequestPhaseTests
{
    [Theory]
    [InlineData(ServiceRequestStatus.Richiesto, "to_answer")]
    [InlineData(ServiceRequestStatus.PresoInCarico, "scheduled")]
    [InlineData(ServiceRequestStatus.InCorso, "in_progress")]
    [InlineData(ServiceRequestStatus.Completato, "completed")]
    [InlineData(ServiceRequestStatus.Pagato, "paid")]
    [InlineData(ServiceRequestStatus.Rifiutato, "rejected")]
    [InlineData(ServiceRequestStatus.Annullato, "cancelled")]
    public void Of_EveryStatus_HasItsPhase(ServiceRequestStatus status, string phase)
    {
        Assert.Equal(phase, SupplierRequestPhases.Of(status, hasPendingProposal: false));
    }

    [Fact]
    public void Of_EveryStatusOfTheEnum_HasAPhaseThatIsNotUnknown()
    {
        // A new status added to the enum without a phase would show up here as "unknown": the task that adds it adds its phase too.
        Assert.All(
            Enum.GetValues<ServiceRequestStatus>(),
            status => Assert.NotEqual(SupplierRequestPhases.Unknown, SupplierRequestPhases.Of(status, hasPendingProposal: false)));
    }

    [Fact]
    public void Of_AProposalWaitingForAnAnswer_OnlyChangesARequestStillWaiting()
    {
        Assert.Equal("proposal_pending", SupplierRequestPhases.Of(ServiceRequestStatus.Richiesto, hasPendingProposal: true));
        Assert.Equal("scheduled", SupplierRequestPhases.Of(ServiceRequestStatus.PresoInCarico, hasPendingProposal: true));
        Assert.Equal("cancelled", SupplierRequestPhases.Of(ServiceRequestStatus.Annullato, hasPendingProposal: true));
    }

    [Fact]
    public void Of_AQuoteWaitingForTheCustomer_IsQuotePending_AProposalComesFirst()
    {
        Assert.Equal("quote_pending", SupplierRequestPhases.Of(ServiceRequestStatus.Richiesto, hasPendingProposal: false, hasPendingQuote: true));
        Assert.Equal("proposal_pending", SupplierRequestPhases.Of(ServiceRequestStatus.Richiesto, hasPendingProposal: true, hasPendingQuote: true));
        Assert.Equal("completed", SupplierRequestPhases.Of(ServiceRequestStatus.Completato, hasPendingProposal: false, hasPendingQuote: true));
    }

    [Fact]
    public void Of_AStatusThisVersionDoesNotKnow_IsUnknown_NeverAnException()
    {
        // The enum travels by name and may grow (a request handed to an external provider): the console must not break on it.
        Assert.Equal("unknown", SupplierRequestPhases.Of((ServiceRequestStatus)99, hasPendingProposal: false));
        Assert.Equal("unknown", SupplierRequestPhases.Of((ServiceRequestStatus)(-1), hasPendingProposal: true));
    }

    [Fact]
    public void Phases_TheValuesOnTheWire_AreStable()
    {
        Assert.Equal(
            ["to_answer", "proposal_pending", "quote_pending", "scheduled", "in_progress", "completed", "paid", "rejected", "cancelled", "unknown"],
            new[]
            {
                SupplierRequestPhases.ToAnswer, SupplierRequestPhases.ProposalPending, SupplierRequestPhases.QuotePending, SupplierRequestPhases.Scheduled,
                SupplierRequestPhases.InProgress, SupplierRequestPhases.Completed, SupplierRequestPhases.Paid, SupplierRequestPhases.Rejected,
                SupplierRequestPhases.Cancelled, SupplierRequestPhases.Unknown,
            });
    }
}
