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
    [InlineData(ServiceRequestStatus.Annullato, false)]
    public void IsDisclosed_EveryStatus_OnlyAfterTheTake(ServiceRequestStatus status, bool expected)
    {
        Assert.Equal(expected, SupplierJobDisclosure.IsDisclosed(status));
    }

    [Fact]
    public void IsDisclosed_EveryStatus_IsCovered()
    {
        // A new status must be placed on one side of the rule on purpose.
        Assert.Equal(7, Enum.GetValues<ServiceRequestStatus>().Length);
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
    public void Build_PaidRequest_CreditsThePaymentToWhoMadeIt()
    {
        // SP-15a: the supplier can record a payment received outside CasaZen (decision D5); a request paid before it has no author
        // and keeps showing the host.
        var bySupplier = ServiceRequestHistory.Build(
            Milestones(ServiceRequestStatus.Pagato, taken: Taken, completed: Completed, paid: Paid, paidBy: ServiceRequestActorParty.Supplier),
            takenByName: null);
        var byHost = ServiceRequestHistory.Build(
            Milestones(ServiceRequestStatus.Pagato, taken: Taken, completed: Completed, paid: Paid, paidBy: ServiceRequestActorParty.Host),
            takenByName: null);
        var unknown = ServiceRequestHistory.Build(
            Milestones(ServiceRequestStatus.Pagato, taken: Taken, completed: Completed, paid: Paid),
            takenByName: null);

        Assert.Equal(ServiceRequestActorParty.Supplier, bySupplier.Single(h => h.Status == ServiceRequestStatus.Pagato).Actor);
        Assert.Equal(ServiceRequestActorParty.Host, byHost.Single(h => h.Status == ServiceRequestStatus.Pagato).Actor);
        Assert.Equal(ServiceRequestActorParty.Host, unknown.Single(h => h.Status == ServiceRequestStatus.Pagato).Actor);
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
    [InlineData("history", new[] { ServiceRequestStatus.Completato, ServiceRequestStatus.Pagato, ServiceRequestStatus.Rifiutato, ServiceRequestStatus.Annullato })]
    [InlineData("all", new ServiceRequestStatus[0])]
    [InlineData("completato", new[] { ServiceRequestStatus.Completato })]
    [InlineData("Rifiutato", new[] { ServiceRequestStatus.Rifiutato })]
    [InlineData("annullato", new[] { ServiceRequestStatus.Annullato })]
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

    // ─── SP-04: start and cancellation in the history ───

    [Fact]
    public void Build_StartedRequest_HasTheStartBetweenTheTakeAndTheCompletion()
    {
        var started = new DateTime(2026, 9, 3, 9, 0, 0, DateTimeKind.Utc);

        var history = ServiceRequestHistory.Build(
            Milestones(ServiceRequestStatus.Completato, taken: Taken, completed: Completed, started: started), takenByName: null);

        Assert.Equal(
            new[] { ServiceRequestStatus.Richiesto, ServiceRequestStatus.PresoInCarico, ServiceRequestStatus.InCorso, ServiceRequestStatus.Completato },
            history.Select(h => h.Status));
        var step = history[2];
        Assert.Equal(started, step.At);
        Assert.Equal(ServiceRequestActorParty.Supplier, step.Actor);
    }

    [Theory]
    [InlineData(ServiceRequestActorParty.Host, "Ospiti partiti prima")]
    [InlineData(ServiceRequestActorParty.Supplier, "Furgone in panne")]
    [InlineData(ServiceRequestActorParty.System, ServiceRequestCancellationReasons.NoResponse)]
    public void Build_CancelledRequest_EndsWithTheCancellationByWhoMadeItAndItsReason(ServiceRequestActorParty by, string reason)
    {
        var cancelledAt = new DateTime(2026, 9, 2, 7, 30, 0, DateTimeKind.Utc);

        var history = ServiceRequestHistory.Build(
            Milestones(ServiceRequestStatus.Annullato, updated: cancelledAt, cancelledAt: cancelledAt, cancelledBy: by, cancellationReason: reason),
            takenByName: null);

        Assert.Equal(2, history.Count);
        var cancellation = history[^1];
        Assert.Equal(ServiceRequestStatus.Annullato, cancellation.Status);
        Assert.Equal(cancelledAt, cancellation.At);
        Assert.Equal(by, cancellation.Actor);
        Assert.Equal(reason, cancellation.Reason);
    }

    [Fact]
    public void Build_CancelledAfterTheTake_KeepsTheTakeAndAddsTheCancellation()
    {
        var cancelledAt = new DateTime(2026, 9, 2, 7, 30, 0, DateTimeKind.Utc);

        var history = ServiceRequestHistory.Build(
            Milestones(
                ServiceRequestStatus.Annullato,
                taken: Taken,
                updated: cancelledAt,
                cancelledAt: cancelledAt,
                cancelledBy: ServiceRequestActorParty.Host,
                cancellationReason: "Ospiti partiti"),
            takenByName: "Mario Rossi");

        Assert.Equal(
            new[] { ServiceRequestStatus.Richiesto, ServiceRequestStatus.PresoInCarico, ServiceRequestStatus.Annullato },
            history.Select(h => h.Status));
        Assert.Equal("Mario Rossi", history[1].ActorName);
    }

    [Fact]
    public void Build_CancelledWithoutRecordedAuthor_FallsBackToTheHostAndTheUpdateDate()
    {
        var updated = new DateTime(2026, 9, 2, 7, 30, 0, DateTimeKind.Utc);

        var history = ServiceRequestHistory.Build(Milestones(ServiceRequestStatus.Annullato, updated: updated), takenByName: null);

        Assert.Equal(updated, history[^1].At);
        Assert.Equal(ServiceRequestActorParty.Host, history[^1].Actor);
        Assert.Null(history[^1].Reason);
    }

    // ─── SP-04: tabs and period filters of the inbox ───

    [Theory]
    [InlineData("nuove", SupplierInboxTab.New)]
    [InlineData("Nuove", SupplierInboxTab.New)]
    [InlineData(" programmate ", SupplierInboxTab.Scheduled)]
    [InlineData("da-incassare", SupplierInboxTab.ToCollect)]
    [InlineData("DA_INCASSARE", SupplierInboxTab.ToCollect)]
    [InlineData("archivio", SupplierInboxTab.Archive)]
    public void InboxTabs_TryParse_KnownValue_ReturnsTheTab(string value, SupplierInboxTab expected)
    {
        Assert.True(SupplierInboxTabs.TryParse(value, out var tab));
        Assert.Equal(expected, tab);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("open")]
    [InlineData("0")]
    [InlineData("nuove,archivio")]
    public void InboxTabs_TryParse_UnknownValue_ReturnsFalse(string? value)
    {
        Assert.False(SupplierInboxTabs.TryParse(value, out _));
    }

    [Fact]
    public void InboxTabs_EveryStatusIsInExactlyOneTab_SoNoRequestIsLostFromTheInbox()
    {
        var all = Enum.GetValues<SupplierInboxTab>().SelectMany(SupplierInboxTabs.StatusesOf).ToList();

        Assert.Equal(Enum.GetValues<ServiceRequestStatus>().Order(), all.Order());
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Theory]
    [InlineData(SupplierInboxTab.New, SupplierInboxSort.Urgency)]
    [InlineData(SupplierInboxTab.Scheduled, SupplierInboxSort.WorkTime)]
    [InlineData(SupplierInboxTab.ToCollect, SupplierInboxSort.Activity)]
    [InlineData(SupplierInboxTab.Archive, SupplierInboxSort.Activity)]
    public void InboxTabs_SortOf_TheUrgentFirstTheNextJobFirstTheLatestFirst(SupplierInboxTab tab, SupplierInboxSort expected)
    {
        Assert.Equal(expected, SupplierInboxTabs.SortOf(tab));
    }

    [Theory]
    [InlineData("oggi", SupplierInboxWhen.Today)]
    [InlineData("Settimana", SupplierInboxWhen.Week)]
    [InlineData(" mese ", SupplierInboxWhen.Month)]
    public void InboxWhens_TryParse_KnownValue_ReturnsThePeriod(string value, SupplierInboxWhen expected)
    {
        Assert.True(SupplierInboxWhens.TryParse(value, out var when));
        Assert.Equal(expected, when);
    }

    [Theory]
    [InlineData("domani")]
    [InlineData("today")]
    [InlineData("1")]
    public void InboxWhens_TryParse_UnknownValue_ReturnsFalse(string value)
    {
        Assert.False(SupplierInboxWhens.TryParse(value, out _));
    }

    [Fact]
    public void InboxWhens_RangeOf_TodayTheNextSevenDaysAndTheCurrentMonth()
    {
        var today = new DateOnly(2026, 10, 8);

        Assert.Equal((today, today), SupplierInboxWhens.RangeOf(SupplierInboxWhen.Today, today));
        Assert.Equal((today, new DateOnly(2026, 10, 14)), SupplierInboxWhens.RangeOf(SupplierInboxWhen.Week, today));
        Assert.Equal((new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)), SupplierInboxWhens.RangeOf(SupplierInboxWhen.Month, today));
        Assert.Equal(
            (new DateOnly(2028, 2, 1), new DateOnly(2028, 2, 29)),
            SupplierInboxWhens.RangeOf(SupplierInboxWhen.Month, new DateOnly(2028, 2, 10)));
        // The week crosses the end of the year.
        Assert.Equal(
            (new DateOnly(2026, 12, 29), new DateOnly(2027, 1, 4)),
            SupplierInboxWhens.RangeOf(SupplierInboxWhen.Week, new DateOnly(2026, 12, 29)));
    }

    private static ServiceRequestMilestones Milestones(
        ServiceRequestStatus status,
        DateTime? taken = null,
        DateTime? completed = null,
        DateTime? paid = null,
        DateTime? updated = null,
        string? reason = null,
        DateTime? started = null,
        DateTime? cancelledAt = null,
        string? cancellationReason = null,
        ServiceRequestActorParty? cancelledBy = null,
        ServiceRequestActorParty? paidBy = null) =>
        new(
            status,
            Created,
            updated ?? paid ?? completed ?? taken ?? Created,
            taken,
            completed,
            paid,
            reason,
            started,
            cancelledAt,
            cancellationReason,
            cancelledBy,
            paidBy);
}
