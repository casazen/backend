using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>
/// SP-10, decision D9: what the supplier knows of a private customer of its public showcase before and after it takes the request,
/// and who the history names as the party that asked.
/// </summary>
public class ShowcaseDisclosureAndHistoryTests
{
    [Theory]
    [InlineData(ServiceRequestStatus.Richiesto, "Mario R.")]
    [InlineData(ServiceRequestStatus.Rifiutato, "Mario R.")]
    [InlineData(ServiceRequestStatus.Annullato, "Mario R.")]
    [InlineData(ServiceRequestStatus.PresoInCarico, "Mario Rossi")]
    [InlineData(ServiceRequestStatus.InCorso, "Mario Rossi")]
    [InlineData(ServiceRequestStatus.Completato, "Mario Rossi")]
    [InlineData(ServiceRequestStatus.Pagato, "Mario Rossi")]
    public void CustomerName_IsShortenedUntilTheSupplierTakesTheRequest(ServiceRequestStatus status, string expected) =>
        Assert.Equal(expected, SupplierJobDisclosure.CustomerName(status, "Mario Rossi"));

    [Fact]
    public void CustomerName_ARequestRefusedOrCancelledNeverGivesTheFullName()
    {
        // A request the supplier never took, or no longer has a job for, shows what a new request shows.
        Assert.Equal("Anna B.", SupplierJobDisclosure.CustomerName(ServiceRequestStatus.Rifiutato, "Anna Bianchi"));
        Assert.Equal("Anna B.", SupplierJobDisclosure.CustomerName(ServiceRequestStatus.Annullato, "Anna Bianchi"));
        Assert.Equal(string.Empty, SupplierJobDisclosure.CustomerName(ServiceRequestStatus.Richiesto, null));
        Assert.Equal(string.Empty, SupplierJobDisclosure.CustomerName(ServiceRequestStatus.PresoInCarico, null));
    }

    [Fact]
    public void Sources_AShowcaseRequestIsSaidToBeAShowcaseOne()
    {
        Assert.Equal(SupplierRequestSources.Showcase, SupplierRequestSources.Of(ServiceRequestSource.Showcase));
        Assert.Equal(SupplierRequestSources.CasaZen, SupplierRequestSources.Of(ServiceRequestSource.Host));
    }

    [Fact]
    public void History_OfAHostRequest_IsAsItWas_TheHostAskedAndPays()
    {
        var created = new DateTime(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc);
        var paid = created.AddDays(3);

        var steps = ServiceRequestHistory.Build(
            new ServiceRequestMilestones(
                ServiceRequestStatus.Pagato, created, paid, created.AddHours(1), created.AddDays(2), paid, null),
            takenByName: null);

        Assert.Equal(
            new[] { ServiceRequestActorParty.Host, ServiceRequestActorParty.Supplier, ServiceRequestActorParty.Supplier, ServiceRequestActorParty.Host },
            steps.Select(step => step.Actor));
    }

    [Fact]
    public void History_OfAShowcaseRequest_NamesTheCustomerAsTheOneWhoAskedAndWhoPays()
    {
        var created = new DateTime(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc);
        var paid = created.AddDays(3);

        var steps = ServiceRequestHistory.Build(
            new ServiceRequestMilestones(
                ServiceRequestStatus.Pagato,
                created,
                paid,
                created.AddHours(1),
                created.AddDays(2),
                paid,
                null,
                Requester: ServiceRequestActorParty.Customer),
            takenByName: null);

        Assert.Equal(
            new[] { ServiceRequestActorParty.Customer, ServiceRequestActorParty.Supplier, ServiceRequestActorParty.Supplier, ServiceRequestActorParty.Customer },
            steps.Select(step => step.Actor));
    }

    [Fact]
    public void History_ACancellationNobodyAttributed_IsTheRequestersOwn()
    {
        var created = new DateTime(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc);

        var steps = ServiceRequestHistory.Build(
            new ServiceRequestMilestones(
                ServiceRequestStatus.Annullato,
                created,
                created.AddHours(1),
                null,
                null,
                null,
                null,
                CancelledAt: created.AddHours(1),
                Requester: ServiceRequestActorParty.Customer),
            takenByName: null);

        Assert.Equal(ServiceRequestActorParty.Customer, steps[^1].Actor);
    }

    [Fact]
    public void StateMachine_TheCustomerCancelsANewOrATakenRequest_NeverOneInProgressOrClosed()
    {
        // SP-11: the customer is an actor of its own cancellation, like the supplier: before the work starts, never after.
        Assert.All(
            new[] { ServiceRequestStatus.Richiesto, ServiceRequestStatus.PresoInCarico },
            status => Assert.True(ServiceRequestStateMachine.CanCancel(status, ServiceRequestActorParty.Customer)));
        Assert.All(
            Enum.GetValues<ServiceRequestStatus>().Except([ServiceRequestStatus.Richiesto, ServiceRequestStatus.PresoInCarico]),
            status => Assert.False(ServiceRequestStateMachine.CanCancel(status, ServiceRequestActorParty.Customer)));
    }
}
