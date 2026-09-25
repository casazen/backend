using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>SU-08 (A4-14): what the supplier sees before and after the take, the history, the inbox status filter.</summary>
public class SupplierServiceRequestViewTests
{
    private static readonly DateTime Created = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Taken = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Completed = new(2026, 9, 3, 15, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Paid = new(2026, 9, 5, 10, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(ServiceRequestStatus.Richiesto, false)]
    [InlineData(ServiceRequestStatus.Rifiutato, false)]
    [InlineData(ServiceRequestStatus.PresoInCarico, true)]
    [InlineData(ServiceRequestStatus.InCorso, true)]
    [InlineData(ServiceRequestStatus.Completato, true)]
    [InlineData(ServiceRequestStatus.Pagato, true)]
    public void IsDisclosed_EveryStatus_OnlyAfterTheTake(ServiceRequestStatus status, bool expected)
    {
        Assert.Equal(expected, SupplierJobDisclosure.IsDisclosed(status));
    }

    [Fact]
    public void IsDisclosed_EveryStatus_IsCovered()
    {
        // A new status must be placed on one side of the rule on purpose.
        Assert.Equal(6, Enum.GetValues<ServiceRequestStatus>().Length);
    }

    [Fact]
    public void Build_NewRequest_HasOnlyTheHostRequest()
    {
        var history = ServiceRequestHistory.Build(Milestones(ServiceRequestStatus.Richiesto), takenByName: null);

        var step = Assert.Single(history);
        Assert.Equal(ServiceRequestStatus.Richiesto, step.Status);
        Assert.Equal(Created, step.At);
        Assert.Equal(ServiceRequestActorParty.Host, step.Actor);
        Assert.Null(step.ActorName);
    }

    [Fact]
    public void Build_PaidRequest_ListsEveryTransitionWithDateAndActorInOrder()
    {
        var history = ServiceRequestHistory.Build(
            Milestones(ServiceRequestStatus.Pagato, taken: Taken, completed: Completed, paid: Paid),
            takenByName: "  Mario Rossi ");

        var expected = new (ServiceRequestStatus, DateTime, ServiceRequestActorParty, string?)[]
        {
            (ServiceRequestStatus.Richiesto, Created, ServiceRequestActorParty.Host, null),
            (ServiceRequestStatus.PresoInCarico, Taken, ServiceRequestActorParty.Supplier, "Mario Rossi"),
            (ServiceRequestStatus.Completato, Completed, ServiceRequestActorParty.Supplier, null),
            (ServiceRequestStatus.Pagato, Paid, ServiceRequestActorParty.Host, null),
        };
        Assert.Equal(expected, history.Select(h => (h.Status, h.At, h.Actor, h.ActorName)));
    }

    [Fact]
    public void Build_RejectedRequest_EndsWithTheSupplierRejectionAndItsReason()
    {
        var rejectedAt = new DateTime(2026, 9, 2, 7, 30, 0, DateTimeKind.Utc);

        var history = ServiceRequestHistory.Build(
            Milestones(ServiceRequestStatus.Rifiutato, updated: rejectedAt, reason: "Non disponibile"),
            takenByName: null);

        Assert.Equal(2, history.Count);
        var rejection = history[1];
        Assert.Equal(ServiceRequestStatus.Rifiutato, rejection.Status);
        Assert.Equal(rejectedAt, rejection.At);
        Assert.Equal(ServiceRequestActorParty.Supplier, rejection.Actor);
        Assert.Equal("Non disponibile", rejection.Reason);
    }

    [Fact]
    public void Build_TakenByUnknownMember_HasNoActorName()
    {
        var history = ServiceRequestHistory.Build(
            Milestones(ServiceRequestStatus.PresoInCarico, taken: Taken), takenByName: " ");

        Assert.Equal(ServiceRequestStatus.PresoInCarico, history[^1].Status);
        Assert.Null(history[^1].ActorName);
    }

    [Theory]
    [InlineData(null, new[] { ServiceRequestStatus.Richiesto, ServiceRequestStatus.PresoInCarico, ServiceRequestStatus.InCorso })]
    [InlineData("", new[] { ServiceRequestStatus.Richiesto, ServiceRequestStatus.PresoInCarico, ServiceRequestStatus.InCorso })]
    [InlineData("Open", new[] { ServiceRequestStatus.Richiesto, ServiceRequestStatus.PresoInCarico, ServiceRequestStatus.InCorso })]
    [InlineData("history", new[] { ServiceRequestStatus.Completato, ServiceRequestStatus.Pagato, ServiceRequestStatus.Rifiutato })]
    [InlineData("all", new ServiceRequestStatus[0])]
    [InlineData("completato", new[] { ServiceRequestStatus.Completato })]
    [InlineData("Rifiutato", new[] { ServiceRequestStatus.Rifiutato })]
    public void TryParse_KnownValue_ReturnsItsStatuses(string? value, ServiceRequestStatus[] expected)
    {
        Assert.True(SupplierInboxStatusFilter.TryParse(value, out var statuses));
        Assert.Equal(expected, statuses);
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("3")]
    [InlineData("Richiesto,Pagato")]
    public void TryParse_UnknownValue_ReturnsFalse(string value)
    {
        Assert.False(SupplierInboxStatusFilter.TryParse(value, out _));
    }

    private static ServiceRequestMilestones Milestones(
        ServiceRequestStatus status,
        DateTime? taken = null,
        DateTime? completed = null,
        DateTime? paid = null,
        DateTime? updated = null,
        string? reason = null) =>
        new(status, Created, updated ?? paid ?? completed ?? taken ?? Created, taken, completed, paid, reason);
}
