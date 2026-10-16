using System.Reflection;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>
/// SP-04e: the <c>phase</c> of a request is derived once, here, from the status and the proposed time. The status keeps its seven
/// values (the mobile app does not know new ones); the phase is an open set a client reads with a fallback.
/// </summary>
public class SupplierRequestPhaseTests
{
    [Theory]
    [InlineData(ServiceRequestStatus.Richiesto, false, false, "to_answer")]
    [InlineData(ServiceRequestStatus.Richiesto, true, false, "proposal_pending")]
    [InlineData(ServiceRequestStatus.Richiesto, false, true, "quote_pending")]
    [InlineData(ServiceRequestStatus.PresoInCarico, false, false, "scheduled")]
    [InlineData(ServiceRequestStatus.InCorso, false, false, "in_progress")]
    [InlineData(ServiceRequestStatus.Completato, false, false, "completed")]
    [InlineData(ServiceRequestStatus.Pagato, false, false, "paid")]
    [InlineData(ServiceRequestStatus.Rifiutato, false, false, "rejected")]
    [InlineData(ServiceRequestStatus.Annullato, false, false, "cancelled")]
    public void Of_EveryStatus_GivesItsPhase(ServiceRequestStatus status, bool proposal, bool quote, string expected)
    {
        Assert.Equal(expected, SupplierRequestPhases.Of(status, proposal, quote));
    }

    [Fact]
    public void Of_ProposalAndQuoteAtOnce_ProposalWins()
    {
        // The order is part of the contract shared with the agenda (SP-03b): a proposal, then a quote, else the supplier answers.
        Assert.Equal(SupplierRequestPhases.ProposalPending, SupplierRequestPhases.Of(ServiceRequestStatus.Richiesto, true, true));
    }

    [Theory]
    [InlineData(ServiceRequestStatus.PresoInCarico)]
    [InlineData(ServiceRequestStatus.InCorso)]
    [InlineData(ServiceRequestStatus.Completato)]
    [InlineData(ServiceRequestStatus.Pagato)]
    [InlineData(ServiceRequestStatus.Rifiutato)]
    [InlineData(ServiceRequestStatus.Annullato)]
    public void Of_ProposalOrQuoteMeanNothingOutsideANewRequest(ServiceRequestStatus status)
    {
        Assert.Equal(SupplierRequestPhases.Of(status, false), SupplierRequestPhases.Of(status, true));
        Assert.Equal(SupplierRequestPhases.Of(status, false), SupplierRequestPhases.Of(status, false, true));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(99)]
    [InlineData(int.MaxValue)]
    public void Of_AStatusItDoesNotKnow_IsUnknown_AndNeverThrows(int value)
    {
        // A request forwarded to an external provider (spec #466) would be an eighth status: no exception, a value the client can show.
        Assert.Equal(SupplierRequestPhases.Unknown, SupplierRequestPhases.Of((ServiceRequestStatus)value, false));
        Assert.Equal(SupplierRequestPhases.Unknown, SupplierRequestPhases.Of((ServiceRequestStatus)value, true, true));
        Assert.Equal("unknown", SupplierRequestPhases.Unknown);
    }

    [Fact]
    public void Of_EveryDefinedStatus_HasAPhaseOfItsOwn_SoANewStatusMustBePlacedOnPurpose()
    {
        var phases = Enum.GetValues<ServiceRequestStatus>()
            .Select(status => SupplierRequestPhases.Of(status, hasPendingProposal: false))
            .ToList();

        Assert.DoesNotContain(SupplierRequestPhases.Unknown, phases);
        Assert.Equal(phases.Count, phases.Distinct().Count());
    }

    [Fact]
    public void Constants_AreDistinctLowerSnakeCaseWords()
    {
        var values = typeof(SupplierRequestPhases)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

        Assert.Equal(10, values.Count);
        Assert.Equal(values.Count, values.Distinct().Count());
        Assert.All(values, value => Assert.Matches("^[a-z]+(_[a-z]+)*$", value));
    }
}
