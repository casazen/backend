using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-10, decision D9: what the supplier's console reads of a request that came from its public showcase. Until the supplier takes
/// it, only the comune, the postal code, the day and time, the price and "Nome C."; after, the name, the street address, the floor,
/// the access notes, the e-mail and the phone; a request that was refused or cancelled never shows more than a new one does.
/// </summary>
public class SupplierServiceRequestReaderShowcaseTests
{
    private static readonly SupplierInboxQuery Everything = new([], null, null, 1, 100);

    private static readonly string[] Secrets =
    [
        "Rossi", ShowcaseScenario.CustomerEmail, "3331234567", "Segretissima", "Piano 3", "Citofono", "cassetta",
    ];

    [Fact]
    public async Task ListAsync_BeforeTheTake_ShowsTheComuneTheTimeThePriceAndTheShortNameOnly()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, confirmation) = await s.BookedAsync();

        var (items, total) = await s.Reader.ListAsync(s.SupplierOrgId, Everything);

        var view = Assert.Single(items);
        Assert.Equal(1, total);
        Assert.Equal(request.Id, view.Id);
        Assert.Equal(ServiceRequestStatus.Richiesto, view.Status);
        Assert.Equal(ServiceRequestRentalContext.Showcase, view.RentalContext);
        Assert.Equal(SupplierRequestSources.Showcase, view.Source);
        Assert.Equal(ShowcaseScenario.City, view.Location.City);
        Assert.Equal(ShowcaseScenario.PostalCode, view.Location.PostalCode);
        Assert.Null(view.Location.PropertyId);
        Assert.Null(view.Location.PropertyName);
        Assert.Null(view.Location.Address);
        Assert.Null(view.Location.Floor);
        Assert.Null(view.Location.AccessNotes);
        Assert.Equal(ServiceRequestScenario.FridayAt10, view.Schedule.StartUtc);
        Assert.Equal(confirmation.RespondBy, view.Schedule.RespondBy);
        Assert.Equal(ServiceRequestScenario.ServicePriceCents, view.Price.AmountCents);
        Assert.Equal(ServiceRequestScenario.ServiceName, view.ServiceName);
        Assert.Equal("Mario R.", view.Client.Name);
        Assert.Equal(request.CustomerId, view.Client.Id);
        Assert.False(view.ContactDisclosed);
        Assert.Null(view.HostContact);
        Assert.Null(view.Stay);
        Assert.Equal(new DateOnly(2026, 10, 9), view.ScheduledFor);

        // Nothing that identifies the customer is anywhere in what the console receives.
        var text = JsonSerializer.Serialize(items);
        foreach (var secret in Secrets)
            Assert.DoesNotContain(secret, text);
    }

    [Fact]
    public async Task GetAsync_BeforeTheTake_IsTheSameAsTheList()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, _) = await s.BookedAsync();

        var view = await s.Reader.GetAsync(request.Id, s.SupplierOrgId);

        Assert.NotNull(view);
        Assert.Equal("Mario R.", view.Client.Name);
        Assert.Null(view.HostContact);
        Assert.Null(view.Location.Address);
        var text = JsonSerializer.Serialize(view);
        foreach (var secret in Secrets)
            Assert.DoesNotContain(secret, text);
    }

    [Theory]
    [InlineData(ServiceRequestStatus.PresoInCarico)]
    [InlineData(ServiceRequestStatus.InCorso)]
    [InlineData(ServiceRequestStatus.Completato)]
    public async Task GetAsync_AfterTheTake_ShowsTheNameThePlaceAndTheContacts(ServiceRequestStatus status)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, _) = await s.BookedAsync();
        await s.Service.TakeAsync(request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId);
        if (status >= ServiceRequestStatus.InCorso)
            await s.Service.StartAsync(request.Id, s.SupplierOrgId);
        if (status == ServiceRequestStatus.Completato)
            await s.Service.CompleteAsync(request.Id, s.SupplierOrgId);

        var view = await s.Reader.GetAsync(request.Id, s.SupplierOrgId);

        Assert.NotNull(view);
        Assert.Equal(status, view.Status);
        Assert.True(view.ContactDisclosed);
        Assert.Equal("Mario Rossi", view.Client.Name);
        Assert.Equal(new SupplierJobHostContact("Mario Rossi", ShowcaseScenario.CustomerEmail, "+393331234567"), view.HostContact);
        Assert.Equal(ShowcaseScenario.Address, view.Location.Address);
        Assert.Equal(ShowcaseScenario.Floor, view.Location.Floor);
        Assert.Equal(ShowcaseScenario.AccessNotes, view.Location.AccessNotes);
        Assert.Equal(ShowcaseScenario.City, view.Location.City);
        Assert.Null(view.Location.PropertyName);
        Assert.Null(view.Location.PropertyId);
    }

    [Fact]
    public async Task ListAsync_AfterTheTake_ShowsTheSameDetailsAsTheDetail()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, _) = await s.BookedAsync();
        await s.Service.TakeAsync(request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId);

        var (items, _) = await s.Reader.ListAsync(s.SupplierOrgId, Everything);
        var one = await s.Reader.GetAsync(request.Id, s.SupplierOrgId);

        var listed = Assert.Single(items);
        Assert.Equal("Mario Rossi", listed.Client.Name);
        Assert.Equal(ShowcaseScenario.Address, listed.Location.Address);
        Assert.NotNull(one);
        Assert.Equal(listed.HostContact, one.HostContact);
    }

    [Fact]
    public async Task GetAsync_ARefusedRequest_ShowsWhatANewOneShows_NotTheCustomer()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, _) = await s.BookedAsync();
        await s.Service.RejectAsync(request.Id, s.SupplierOrgId, "Non lavoriamo in quella zona");

        var view = await s.Reader.GetAsync(request.Id, s.SupplierOrgId);

        Assert.NotNull(view);
        Assert.Equal(ServiceRequestStatus.Rifiutato, view.Status);
        Assert.False(view.ContactDisclosed);
        Assert.Equal("Mario R.", view.Client.Name);
        Assert.Null(view.HostContact);
        Assert.Null(view.Location.Address);
        Assert.Null(view.Location.Floor);
        Assert.Null(view.Location.AccessNotes);
        var text = JsonSerializer.Serialize(view);
        foreach (var secret in Secrets)
            Assert.DoesNotContain(secret, text);
    }

    [Fact]
    public async Task GetAsync_ARequestCancelledAfterTheTake_NoLongerShowsTheCustomer()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, _) = await s.BookedAsync();
        await s.Service.TakeAsync(request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId);
        await s.Service.CancelAsSupplierAsync(request.Id, s.SupplierOrgId, "Guasto al furgone");

        var view = await s.Reader.GetAsync(request.Id, s.SupplierOrgId);

        Assert.NotNull(view);
        Assert.Equal(ServiceRequestStatus.Annullato, view.Status);
        Assert.Equal("Mario R.", view.Client.Name);
        Assert.Null(view.HostContact);
        Assert.Null(view.Location.Address);
        Assert.Equal(ServiceRequestActorParty.Supplier, view.Cancellation!.By);
        var text = JsonSerializer.Serialize(view);
        foreach (var secret in Secrets)
            Assert.DoesNotContain(secret, text);
    }

    [Fact]
    public async Task GetAsync_TheHistoryNamesTheCustomerAsTheOneWhoAsked()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, _) = await s.BookedAsync();
        await s.Service.TakeAsync(request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId);

        var view = await s.Reader.GetAsync(request.Id, s.SupplierOrgId);

        Assert.NotNull(view?.History);
        Assert.Equal(ServiceRequestStatus.Richiesto, view.History[0].Status);
        Assert.Equal(ServiceRequestActorParty.Customer, view.History[0].Actor);
        Assert.Equal(ServiceRequestStatus.PresoInCarico, view.History[1].Status);
        Assert.Equal(ServiceRequestActorParty.Supplier, view.History[1].Actor);
    }

    [Fact]
    public async Task ListAsync_AHostsRequestAndAShowcaseOne_AreEachWhatTheyAre()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var host = await s.RequestAsync(ServiceRequestScenario.FridayAt14);
        var (showcase, _) = await s.BookedAsync();

        var (items, total) = await s.Reader.ListAsync(s.SupplierOrgId, Everything);

        Assert.Equal(2, total);
        var hostView = Assert.Single(items, v => v.Id == host.Id);
        var showcaseView = Assert.Single(items, v => v.Id == showcase.Id);
        Assert.Equal(SupplierRequestSources.CasaZen, hostView.Source);
        Assert.Equal(s.HostOrgId, hostView.Client.Id);
        Assert.Equal("Casa Rossi", hostView.Client.Name);
        Assert.NotNull(hostView.Location.PropertyId);
        Assert.Equal(SupplierRequestSources.Showcase, showcaseView.Source);
        Assert.Equal(showcase.CustomerId, showcaseView.Client.Id);
        Assert.Null(showcaseView.Location.PropertyId);
    }

    [Fact]
    public async Task ListAsync_AnotherSupplierSeesNothingOfIt()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var other = await s.AddBookableSupplierAsync();
        var (request, _) = await s.BookedAsync();

        var (items, total) = await s.Reader.ListAsync(other.OrgId, Everything);
        var one = await s.Reader.GetAsync(request.Id, other.OrgId);

        Assert.Empty(items);
        Assert.Equal(0, total);
        Assert.Null(one);
    }

    [Fact]
    public async Task ListForAgendaAsync_TheRequestIsThereWithItsHours_AndNothingAboutTheCustomer()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, _) = await s.BookedAsync();

        var items = await s.Reader.ListForAgendaAsync(s.SupplierOrgId, new DateOnly(2026, 10, 9), new DateOnly(2026, 10, 9));

        var item = Assert.Single(items);
        Assert.Equal(request.Id, item.Id);
        Assert.Equal(ServiceRequestStatus.Richiesto, item.Status);
        Assert.True(item.HasHours);
        Assert.Equal(ServiceRequestScenario.FridayAt10, item.StartUtc);
        var text = JsonSerializer.Serialize(items);
        foreach (var secret in Secrets)
            Assert.DoesNotContain(secret, text);
    }

    [Fact]
    public async Task ListForAgendaAsync_ARefusedShowcaseRequest_FreesItsSlot()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var (request, _) = await s.BookedAsync();
        Assert.DoesNotContain(ServiceRequestScenario.FridayAt10, await s.FreeSlotsOfFridayAsync());

        await s.Service.RejectAsync(request.Id, s.SupplierOrgId, "Non posso");

        Assert.Contains(ServiceRequestScenario.FridayAt10, await s.FreeSlotsOfFridayAsync());
        // And the next customer can book it.
        var hold = await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync(email: "altra.persona@example.com"));
        Assert.NotEqual(Guid.Empty, hold.Id);
    }
}
