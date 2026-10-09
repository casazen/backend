using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-04: what the service requests tell the two parties. The host hears of what the supplier does and sees its own property;
/// the supplier hears of what the host does and sees only the comune (decision D9), never the property, the street or the host's
/// notes. A status with no message (a new request, a paid one, one the host cancelled itself, an unknown one) sends nothing and
/// is not an error (the old code sent an email for any unknown state).
/// </summary>
public class ServiceRequestNotifierTests
{
    [Theory]
    [InlineData(ServiceRequestStatus.Richiesto)]
    [InlineData(ServiceRequestStatus.Pagato)]
    [InlineData((ServiceRequestStatus)99)]
    public async Task NotifyHostAsync_StatusTheHostIsNotToldAbout_SendsNothing(ServiceRequestStatus status)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await LoadAsync(s, status);

        await s.Notifier.NotifyHostAsync(request, CancellationToken.None);

        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);
    }

    [Fact]
    public async Task NotifyHostAsync_RequestTheHostCancelledItself_SendsNothing()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await LoadAsync(s, ServiceRequestStatus.Annullato, r => r.CancelledBy = ServiceRequestActorParty.Host);

        await s.Notifier.NotifyHostAsync(request, CancellationToken.None);

        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);
    }

    [Theory]
    [InlineData(ServiceRequestStatus.PresoInCarico, "service-request-taken")]
    [InlineData(ServiceRequestStatus.InCorso, "service-request-started")]
    [InlineData(ServiceRequestStatus.Completato, "service-request-completed")]
    [InlineData(ServiceRequestStatus.Rifiutato, "service-request-rejected")]
    public async Task NotifyHostAsync_EveryStatusTheSupplierMovesTo_HasItsEmailAndItsPushWithTheHostsPropertyName(
        ServiceRequestStatus status, string pushType)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await LoadAsync(s, status);

        await s.Notifier.NotifyHostAsync(request, CancellationToken.None);

        var email = Assert.Single(s.Emails.Snapshot());
        Assert.Equal("host@test.com", email.To);
        Assert.Contains(ServiceRequestScenario.PropertyName, email.Content.Subject);
        var push = Assert.Single(s.Pushes);
        Assert.Equal(pushType, push.Payload.Type);
        Assert.Equal(PushAudience.PropertyHosts(s.PropertyId), push.Audience);
        Assert.Equal(PushDeliveryKeys.ServiceRequestStatus(request.Id, status), push.DeliveryKey);
        Assert.Equal(PushRoutes.Booking(s.BookingId), push.Payload.Route);
    }

    [Fact]
    public async Task NotifyHostAsync_RequestOfALongRentProperty_OpensThePropertyListBecauseThereIsNoStay()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await LoadAsync(s, ServiceRequestStatus.PresoInCarico, r =>
        {
            r.BookingId = null;
            r.RentalContext = ServiceRequestRentalContext.LongRent;
        });

        await s.Notifier.NotifyHostAsync(request, CancellationToken.None);

        Assert.Equal(PushRoutes.Properties, Assert.Single(s.Pushes).Payload.Route);
    }

    [Fact]
    public async Task NotifyHostAsync_CancelledByTheSupplierOrByCasaZen_SaysWhichOne()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var bySupplier = await LoadAsync(s, ServiceRequestStatus.Annullato, r =>
        {
            r.CancelledBy = ServiceRequestActorParty.Supplier;
            r.CancellationReason = "Furgone in panne";
        });
        var bySystem = await LoadAsync(s, ServiceRequestStatus.Annullato, r =>
        {
            r.CancelledBy = ServiceRequestActorParty.System;
            r.CancellationReason = ServiceRequestCancellationReasons.NoResponse;
        });

        await s.Notifier.NotifyHostAsync(bySupplier, CancellationToken.None);
        await s.Notifier.NotifyHostAsync(bySystem, CancellationToken.None);

        var emails = s.Emails.Snapshot();
        Assert.Equal(2, emails.Count);
        Assert.Contains("annullata dal fornitore", emails[0].Content.Subject);
        Assert.Contains("Furgone in panne", emails[0].Content.HtmlBody);
        Assert.Contains("nessuna risposta", emails[1].Content.Subject);
        Assert.DoesNotContain(ServiceRequestCancellationReasons.NoResponse, emails[1].Content.HtmlBody);
        Assert.All(s.Pushes, push => Assert.Equal(PushTypes.ServiceRequestCancelled, push.Payload.Type));
    }

    [Fact]
    public async Task NotifySupplierCancelledAsync_CancelledByTheSupplierItself_SendsNothing()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await LoadAsync(s, ServiceRequestStatus.Annullato, r => r.CancelledBy = ServiceRequestActorParty.Supplier);

        await s.Notifier.NotifySupplierCancelledAsync(request, CancellationToken.None);

        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);
    }

    [Fact]
    public async Task NotifyTimeProposedAsync_RequestWithoutProposal_SendsNothing()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await LoadAsync(s, ServiceRequestStatus.Richiesto);

        await s.Notifier.NotifyTimeProposedAsync(request, CancellationToken.None);

        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);
    }

    [Fact]
    public async Task NotifyReminderAsync_RequestNeverReminded_SendsNothing()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await LoadAsync(s, ServiceRequestStatus.Richiesto);

        await s.Notifier.NotifyReminderAsync(request, CancellationToken.None);

        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);
    }

    [Fact]
    public async Task EverythingTheSupplierIsTold_NamesTheComuneButNeverThePropertyTheStreetNorTheHostsNotes()
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        // A new request, a reminder, a cancellation by the host and by CasaZen, a proposal accepted and turned down.
        var created = await s.RequestAsync(ServiceRequestScenario.FridayAt10);
        await s.Service.RemindAsync(created.Id, s.HostOrgId);
        var proposed = await s.RequestAsync();
        await s.Service.ProposeTimeAsync(
            proposed.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, new ProposeServiceRequestTimeCommand(ServiceRequestScenario.FridayAt14));
        await s.Service.RejectProposalAsync(proposed.Id, s.HostOrgId);
        await s.Service.ProposeTimeAsync(
            proposed.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, new ProposeServiceRequestTimeCommand(ServiceRequestScenario.FridayAt14));
        await s.Service.AcceptProposalAsync(proposed.Id, s.HostOrgId);
        await s.Service.CancelAsHostAsync(created.Id, s.HostOrgId, "Cambio programma");
        var expired = await LoadAsync(s, ServiceRequestStatus.Annullato, r =>
        {
            r.CancelledBy = ServiceRequestActorParty.System;
            r.CancellationReason = ServiceRequestCancellationReasons.NoResponse;
        });
        await s.Notifier.NotifySupplierCancelledAsync(expired, CancellationToken.None);

        var toTheSupplier = s.Emails.Snapshot().Where(e => e.To == "supplier@test.com").ToList();
        var pushesToTheSupplier = s.Pushes.Where(p => p.Audience == PushAudience.SupplierOrg(s.SupplierOrgId)).ToList();
        // Two new requests, the reminder, the proposal turned down and the one accepted, the cancellation by the host and the one for no answer.
        Assert.Equal(7, toTheSupplier.Count);
        Assert.Equal(toTheSupplier.Count, pushesToTheSupplier.Count);
        var texts = toTheSupplier.SelectMany(e => new[] { e.Content.Subject, e.Content.HtmlBody })
            .Concat(pushesToTheSupplier.SelectMany(p => new[] { p.Payload.Title, p.Payload.Body }))
            .ToList();
        Assert.All(texts, text =>
        {
            Assert.DoesNotContain(ServiceRequestScenario.PropertyName, text);
            Assert.DoesNotContain(ServiceRequestScenario.PropertyAddress, text);
            Assert.DoesNotContain(ServiceRequestScenario.HostNotes, text);
        });
        Assert.All(toTheSupplier, email => Assert.Contains(ServiceRequestScenario.Comune, email.Content.Subject + email.Content.HtmlBody));
    }

    [Fact]
    public async Task QueueCreated_ForARequestWithATimeAndAPrice_GivesTheSupplierBothAndNothingElseAboutTheHost()
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        await s.RequestAsync(ServiceRequestScenario.FridayAt10);

        var email = Assert.Single(s.Emails.Snapshot());
        Assert.Equal("supplier@test.com", email.To);
        Assert.Equal(EmailTemplates.Names.ServiceRequestCreated, email.Template);
        Assert.Contains("60,00 €", email.Content.HtmlBody);
        Assert.Contains("Quando: <strong>09/10/2026 10:00</strong>.", email.Content.HtmlBody);
        Assert.DoesNotContain("Casa Rossi", email.Content.HtmlBody);
    }

    private static async Task<ServiceRequest> LoadAsync(
        ServiceRequestScenario s,
        ServiceRequestStatus status,
        Action<ServiceRequest>? change = null)
    {
        var seeded = await s.SeedAsync(status, change);
        return await s.Db.ServiceRequests.IgnoreQueryFilters().Include(r => r.Property).SingleAsync(r => r.Id == seeded.Id);
    }
}
