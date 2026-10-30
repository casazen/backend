using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Casazen.Tests.Unit.Services.ServicePaymentFlows;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-16d: the three switches of «Notifiche» of the supplier do what they say. «Nuove richieste» governs the e-mail and the push of a new request
/// (a host's, an automatic acceptance, a booking from the public showcase), «Promemoria del giorno prima» the reminder (see
/// <c>SupplierDayBeforeReminderTests</c>), «Pagamenti ricevuti» the notice of a job paid; the operational notices (a cancellation, an answer to a
/// proposal or a quote, an expiry, a refund) are never hidden by a switch. A supplier that has saved nothing has no row and gets the defaults: all on.
/// </summary>
public class SupplierNotificationSwitchesTests
{
    /// <summary>Monday 12 October 2026, 10:00 in Rome.</summary>
    private static readonly DateTime MondayAt10 = ServiceRequestScenario.FridayAt10.AddDays(3);

    [Fact]
    public void SupplierNotificationRules_WithoutARow_AreAllOn_AndEachSwitchGovernsItsOwnKind()
    {
        foreach (var kind in Enum.GetValues<SupplierNotificationKind>())
            Assert.True(SupplierNotificationRules.Allows(null, kind), $"{kind} without a row");

        var allOff = new SupplierSettings { NotifyNewRequests = false, NotifyDayBeforeReminder = false, NotifyPaymentMarked = false };
        foreach (var kind in Enum.GetValues<SupplierNotificationKind>())
            Assert.False(SupplierNotificationRules.Allows(allOff, kind), $"{kind} off");

        var onlyThePayment = new SupplierSettings { NotifyNewRequests = false, NotifyDayBeforeReminder = false, NotifyPaymentMarked = true };
        Assert.False(SupplierNotificationRules.Allows(onlyThePayment, SupplierNotificationKind.NewRequest));
        Assert.False(SupplierNotificationRules.Allows(onlyThePayment, SupplierNotificationKind.DayBeforeReminder));
        Assert.True(SupplierNotificationRules.Allows(onlyThePayment, SupplierNotificationKind.PaymentReceived));
    }

    [Fact]
    public async Task NewRequest_ASupplierWhoTurnedItOff_IsNotToldOfANewRequest_ButStillOfItsCancellation()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await TurnOffAsync(s, settings => settings.NotifyNewRequests = false);
        s.ForgetNotifications();

        var request = await s.RequestAsync();

        Assert.DoesNotContain(s.Emails.Snapshot(), e => e.Template == EmailTemplates.Names.ServiceRequestCreated);
        Assert.DoesNotContain(s.Pushes, p => p.Payload.Type == PushTypes.ServiceRequestCreated);

        // A cancellation is operational: no switch hides it.
        await s.Service.CancelAsHostAsync(request.Id, s.HostOrgId, "Non serve più");

        Assert.Contains(s.Emails.Snapshot(), e => e.Template == EmailTemplates.Names.ServiceRequestCancelledToSupplier);
        Assert.Contains(s.Pushes, p => p.Payload.Type == PushTypes.ServiceRequestCancelled);
    }

    [Fact]
    public async Task NewRequest_ABookingFromTheShowcase_ReachesTheCustomerEvenWhenTheSupplierTurnedTheNoticeOff()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        await TurnOffAsync(s, settings => settings.NotifyNewRequests = false);
        s.ForgetNotifications();

        await s.BookedAsync(await s.InputAsync(MondayAt10));

        Assert.Contains(s.Emails.Snapshot(), e => e.Template == EmailTemplates.Names.SupplierBookingReceipt);
        Assert.DoesNotContain(s.Emails.Snapshot(), e => e.Template == EmailTemplates.Names.SupplierBookingNewRequest);
        Assert.DoesNotContain(s.Pushes, p => p.Payload.Type == PushTypes.ServiceRequestCreated);
    }

    [Fact]
    public async Task PaymentMarked_ASupplierWhoTurnedItOff_IsNotToldThatTheCustomerMarkedTheJobAsPaid()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.TakenAsync();
        await TurnOffAsync(s, settings => settings.NotifyPaymentMarked = false);
        s.ForgetNotifications();

        await s.Notifier.NotifySupplierPaidAsync(request, CancellationToken.None);

        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);

        await TurnOffAsync(s, settings => settings.NotifyPaymentMarked = true);
        await s.Notifier.NotifySupplierPaidAsync(request, CancellationToken.None);

        Assert.Single(s.Emails.Snapshot(), e => e.Template == EmailTemplates.Names.ServiceRequestPaid);
        Assert.Single(s.Pushes, p => p.Payload.Type == PushTypes.ServiceRequestPaid);
    }

    [Fact]
    public async Task PaymentReceived_ThroughCasaZen_TellsTheSupplierTheNet_UnlessItTurnedTheNoticeOff()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();
        var received = new[] { new ServicePaymentNotice(ServicePaymentNoticeKind.Received, opened.PaymentId) };
        await TurnOffAsync(s, settings => settings.NotifyPaymentMarked = false);
        s.ForgetNotifications();

        await s.Payments.CompleteAsync(received);

        Assert.Empty(s.EmailsOf(EmailTemplates.Names.ServicePaymentReceived));
        Assert.Empty(s.Pushes);

        await TurnOffAsync(s, settings => settings.NotifyPaymentMarked = true);
        await s.Payments.CompleteAsync(received);

        Assert.Single(s.EmailsOf(EmailTemplates.Names.ServicePaymentReceived));
        var push = Assert.Single(s.Pushes);
        Assert.Equal(PushTypes.ServicePaymentReceived, push.Payload.Type);
        Assert.Equal(PushAudience.SupplierOrg(s.SupplierOrgId), push.Audience);
        Assert.Equal(opened.Request.Id, push.Payload.ServiceRequestId);
        // The net, before the fees of Stripe, and no word about when the money arrives.
        Assert.Contains("netti per te", push.Payload.Body);
        Assert.DoesNotContain("giorni", push.Payload.Body);
    }

    /// <summary>Writes a change to the settings row of the supplier of the scenario (it has one), whatever the context tracks.</summary>
    private static async Task TurnOffAsync(ServiceRequestScenario s, Action<SupplierSettings> change)
    {
        var settings = await s.Db.SupplierSettings.SingleAsync(x => x.OrgId == s.SupplierOrgId);
        change(settings);
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
    }
}
