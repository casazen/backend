using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-11: the customer cancels its booking and moves it to another time. Cancelling is a transition of the state machine by the
/// customer (a new request any time, a taken one until its time, never one in progress or closed), free until 24 hours before the
/// work and without charge after it (decision D6), and it frees the slot. Moving is only for a new request, goes through the slot
/// planner under the supplier's calendar lock, and gives the supplier its whole time to answer again. The supplier is told of both
/// (e-mail and push, "Nome C." and the comune only); the customer receives the receipt of a cancellation. The races with the supplier
/// need PostgreSQL (<c>ShowcaseBookingManagementPostgresTests</c>).
/// </summary>
public class ShowcaseBookingManagerCancelTests
{
    private const string SupplierEmail = "supplier@test.com";

    // ─── Cancel ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancel_ANewRequest_IsCancelledByTheCustomer_FreesTheSlot_AndTellsBothParties()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();
        s.Clock.Advance(TimeSpan.FromMinutes(10));
        s.ForgetNotifications();
        Assert.DoesNotContain(ServiceRequestScenario.FridayAt10, await s.FreeSlotsOfFridayAsync());

        var view = await s.Kit.Manager.CancelAsync(credentials, "Cambio programma");

        Assert.Equal(ServiceRequestStatus.Annullato, view.Status);
        Assert.Equal(
            new ShowcaseBookingCancellation(s.Clock.GetUtcNow().UtcDateTime, ServiceRequestActorParty.Customer, "Cambio programma"),
            view.Cancellation);
        Assert.Equal(new ShowcaseBookingActions(false, false, false), view.Actions);
        Assert.Null(view.RespondBy);

        var saved = await s.ReadAsync(request.Id);
        Assert.Equal(ServiceRequestStatus.Annullato, saved.Status);
        Assert.Equal(ServiceRequestActorParty.Customer, saved.CancelledBy);
        Assert.Equal("Cambio programma", saved.CancellationReason);
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime, saved.CancelledAt);
        Assert.Null(saved.ResponseDueAt);
        // The time it took is free again, for anybody.
        Assert.Contains(ServiceRequestScenario.FridayAt10, await s.FreeSlotsOfFridayAsync());

        // The supplier is told: e-mail and push, in Italian, with the comune and "Nome C." only.
        var toSupplier = Assert.Single(s.Emails.Snapshot(), e => e.To == SupplierEmail);
        Assert.Equal(EmailTemplates.Names.SupplierBookingCancelledByCustomer, toSupplier.Template);
        Assert.Equal("Richiesta annullata dal cliente — Monza", toSupplier.Content.Subject);
        Assert.Contains("annullata dal cliente", toSupplier.Content.HtmlBody);
        Assert.Contains("Cliente: <strong>Mario R.</strong>", toSupplier.Content.HtmlBody);
        Assert.Contains("Cambio programma", toSupplier.Content.HtmlBody);
        Assert.Contains("09/10/2026 10:00", toSupplier.Content.HtmlBody);
        var everything = toSupplier.Content.Subject + toSupplier.Content.HtmlBody;
        foreach (var secret in new[] { "Rossi", ShowcaseScenario.CustomerEmail, "3331234567", ShowcaseScenario.Address, ShowcaseScenario.Floor })
            Assert.DoesNotContain(secret, everything);
        // Nothing was agreed: a new request cancelled is not "short notice".
        Assert.DoesNotContain("preavviso", toSupplier.Content.HtmlBody);

        var push = Assert.Single(s.Pushes);
        Assert.Equal(PushAudience.SupplierOrg(s.SupplierOrgId), push.Audience);
        Assert.Equal(PushTypes.ServiceRequestCancelled, push.Payload.Type);
        Assert.Equal("Richiesta annullata dal cliente", push.Payload.Title);
        Assert.Equal("Pulizie a Monza: il cliente ha annullato la richiesta.", push.Payload.Body);
        Assert.Equal(request.Id, push.Payload.ServiceRequestId);
        Assert.Equal(PushDeliveryKeys.ServiceRequestStatus(request.Id, ServiceRequestStatus.Annullato), push.DeliveryKey);

        // The customer gets the receipt.
        var receipt = Assert.Single(s.Emails.Snapshot(), e => e.To == ShowcaseScenario.CustomerEmail);
        Assert.Equal(EmailTemplates.Names.SupplierBookingCancellationReceipt, receipt.Template);
        Assert.Equal("Hai annullato la richiesta a Supplier Srl", receipt.Content.Subject);
        Assert.Contains($"<strong>{BookingCodes.Format(request.PublicCode!)}</strong>", receipt.Content.HtmlBody);
        Assert.Contains("Cambio programma", receipt.Content.HtmlBody);
        Assert.Contains("Non hai pagato nulla e non ti addebitiamo nulla", receipt.Content.HtmlBody);
        Assert.Contains("/fornitori/vetrina-test", receipt.Content.HtmlBody);
        Assert.Equal(2, s.Emails.Snapshot().Count);
    }

    [Fact]
    public async Task Cancel_WithoutAReason_StoresTheCodeNotASentence_AndNoReasonIsToldToAnyone()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();
        s.ForgetNotifications();

        var view = await s.Kit.Manager.CancelAsync(credentials, "   ");

        Assert.Equal(ServiceRequestCancellationReasons.CancelledByCustomer, (await s.ReadAsync(request.Id)).CancellationReason);
        Assert.Null(view.Cancellation!.Reason);
        foreach (var email in s.Emails.Snapshot())
        {
            Assert.DoesNotContain("CancelledByCustomer", email.Content.HtmlBody);
            Assert.DoesNotContain("Il motivo", email.Content.HtmlBody);
            Assert.DoesNotContain("Il motivo che hai indicato", email.Content.HtmlBody);
        }
    }

    [Fact]
    public async Task Cancel_ATakenRequest_WithLessNoticeThanTheFreeOne_TellsTheSupplierTheNoticeWasShort_AndStillCancels()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        // Friday 10:00 is 22 hours away: less than the 24 of the free cancellation.
        var (request, credentials) = await s.BookedForManagementAsync(ServiceRequestScenario.FridayAt10);
        await s.TakeAsSupplierAsync(request.Id);
        s.ForgetNotifications();

        var view = await s.Kit.Manager.CancelAsync(credentials, null);

        Assert.Equal(ServiceRequestStatus.Annullato, view.Status);
        Assert.False(view.CancellationTerms.IsFree);
        var toSupplier = Assert.Single(s.Emails.Snapshot(), e => e.To == SupplierEmail);
        Assert.Contains("con meno di <strong>24 h</strong> di preavviso", toSupplier.Content.HtmlBody);
        Assert.Equal(PushTypes.ServiceRequestCancelled, Assert.Single(s.Pushes).Payload.Type);
        Assert.Contains(ServiceRequestScenario.FridayAt10, await s.FreeSlotsOfFridayAsync());
    }

    [Fact]
    public async Task Cancel_ATakenRequest_WithTheFreeNotice_DoesNotMentionTheNotice()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        // Saturday 09:00 is 45 hours away.
        var (request, credentials) = await s.BookedForManagementAsync(ServiceRequestScenario.SaturdayAt09);
        await s.TakeAsSupplierAsync(request.Id);
        s.ForgetNotifications();

        var view = await s.Kit.Manager.CancelAsync(credentials, null);

        Assert.True(view.CancellationTerms.IsFree);
        var toSupplier = Assert.Single(s.Emails.Snapshot(), e => e.To == SupplierEmail);
        Assert.DoesNotContain("preavviso", toSupplier.Content.HtmlBody);
    }

    [Fact]
    public async Task Cancel_TheReceiptIsInTheLanguageOfTheCustomer_ButTheSupplierIsAlwaysWrittenToInItalian()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (_, credentials) = await s.BookedForManagementAsync(locale: "en");
        s.ForgetNotifications();

        await s.Kit.Manager.CancelAsync(credentials, "Change of plans");

        var receipt = Assert.Single(s.Emails.Snapshot(), e => e.To == ShowcaseScenario.CustomerEmail);
        Assert.Equal("You cancelled your request to Supplier Srl", receipt.Content.Subject);
        Assert.Contains("You have paid nothing and we charge you nothing for cancelling", receipt.Content.HtmlBody);
        Assert.Contains("The reason you gave:", receipt.Content.HtmlBody);
        var toSupplier = Assert.Single(s.Emails.Snapshot(), e => e.To == SupplierEmail);
        Assert.Equal("Richiesta annullata dal cliente — Monza", toSupplier.Content.Subject);
    }

    [Fact]
    public async Task Cancel_ARequestWithAProposalWaiting_DropsTheProposal()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();
        await s.ProposeAsSupplierAsync(request.Id, ServiceRequestScenario.FridayAt14, "Il mattino sono già impegnato");

        var view = await s.Kit.Manager.CancelAsync(credentials, null);

        var saved = await s.ReadAsync(request.Id);
        Assert.Null(view.Proposal);
        Assert.Null(saved.ProposedStartUtc);
        Assert.Null(saved.ProposedEndUtc);
        Assert.Null(saved.ProposalMessage);
        Assert.Null(saved.ResponseDueAt);
    }

    [Fact]
    public async Task Cancel_TheSlotIsFreeAndBookableByAnotherCustomerRightAfter()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var (_, credentials) = await s.BookedForManagementAsync();
        var another = await s.InputAsync(email: "anna.verdi@example.com");
        await Assert.ThrowsAsync<DomainConflictException>(() => s.Kit.Booking.CreateHoldAsync(supplier, another));

        await s.Kit.Manager.CancelAsync(credentials, null);

        var next = await s.Kit.Booking.CreateHoldAsync(supplier, another);
        Assert.NotEqual(Guid.Empty, next.Id);
    }

    [Fact]
    public async Task Cancel_ASecondCallOfACancellationTheCustomerMade_AnswersTheSameBooking_AndDoesNothingAgain()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();
        var first = await s.Kit.Manager.CancelAsync(credentials, "Cambio programma");
        var saved = await s.ReadAsync(request.Id);
        s.Clock.Advance(TimeSpan.FromMinutes(5));
        s.ForgetNotifications();

        var second = await s.Kit.Manager.CancelAsync(credentials, "Un altro motivo");

        // The same booking (the views hold lists, so they are compared as they are written).
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(first), System.Text.Json.JsonSerializer.Serialize(second));
        var again = await s.ReadAsync(request.Id);
        Assert.Equal("Cambio programma", again.CancellationReason);
        Assert.Equal(saved.CancelledAt, again.CancelledAt);
        Assert.Equal(saved.UpdatedAt, again.UpdatedAt);
        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);
    }

    [Fact]
    public async Task Cancel_ANewRequestWhoseTimeHasPassed_CanStillBeCancelled_NothingWasAgreed()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (_, credentials) = await s.BookedForManagementAsync();
        s.Clock.SetUtcNow(new DateTimeOffset(ServiceRequestScenario.FridayAt10.AddHours(1)));

        var view = await s.Kit.Manager.CancelAsync(credentials, null);

        Assert.Equal(ServiceRequestStatus.Annullato, view.Status);
    }

    [Fact]
    public async Task Cancel_ATakenRequestOnceItsTimeHasCome_IsRefused_ThePageDoesNotOfferIt()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();
        await s.TakeAsSupplierAsync(request.Id);
        s.Clock.SetUtcNow(new DateTimeOffset(ServiceRequestScenario.FridayAt10));
        s.ForgetNotifications();

        var view = await s.Kit.Manager.LookupAsync(credentials);
        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => s.Kit.Manager.CancelAsync(credentials, null));

        Assert.False(view.Actions.CanCancel);
        Assert.Equal(ShowcaseBookingManagementErrors.CannotCancel, refused.Code);
        Assert.Equal(ServiceRequestStatus.PresoInCarico, (await s.ReadAsync(request.Id)).Status);
        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);
    }

    [Fact]
    public async Task Cancel_AWorkInProgressDoneRefusedOrCancelledByAnotherParty_Is422_AndNothingChangesOrIsSent()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (inProgress, inProgressCredentials) = await s.BookedForManagementAsync();
        var (done, doneCredentials) = await s.BookedForManagementAsync(ServiceRequestScenario.FridayAt14, "anna.verdi@example.com");
        var (refused, refusedCredentials) = await s.BookedForManagementAsync(ServiceRequestScenario.SaturdayAt09, "luca.bianchi@example.com");
        var (byTheSupplier, byTheSupplierCredentials) = await s.BookedForManagementAsync(
            ServiceRequestScenario.FridayAt10.AddDays(3), "sara.neri@example.com");
        await s.TakeAsSupplierAsync(inProgress.Id);
        await s.Service.StartAsync(inProgress.Id, s.SupplierOrgId);
        await s.TakeAsSupplierAsync(done.Id);
        await s.Service.CompleteAsync(done.Id, s.SupplierOrgId);
        await s.Service.RejectAsync(refused.Id, s.SupplierOrgId, "Siamo in ferie");
        await s.Service.CancelAsSupplierAsync(byTheSupplier.Id, s.SupplierOrgId, "Guasto al furgone");
        s.ForgetNotifications();

        foreach (var (id, credentials, status) in new[]
                 {
                     (inProgress.Id, inProgressCredentials, ServiceRequestStatus.InCorso),
                     (done.Id, doneCredentials, ServiceRequestStatus.Completato),
                     (refused.Id, refusedCredentials, ServiceRequestStatus.Rifiutato),
                     (byTheSupplier.Id, byTheSupplierCredentials, ServiceRequestStatus.Annullato),
                 })
        {
            var view = await s.Kit.Manager.LookupAsync(credentials);
            Assert.False(view.Actions.CanCancel, status.ToString());

            var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Kit.Manager.CancelAsync(credentials, "Cambio idea"));

            Assert.Equal(ShowcaseBookingManagementErrors.CannotCancel, ex.Code);
            Assert.Equal(status, (await s.ReadAsync(id)).Status);
        }

        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);
        // The supplier's own cancellation is still the supplier's.
        Assert.Equal(ServiceRequestActorParty.Supplier, (await s.ReadAsync(byTheSupplier.Id)).CancelledBy);
    }

    [Fact]
    public async Task Cancel_AReasonThatIsNotValid_Is422NamingTheField_AndNothingChanges()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();
        s.ForgetNotifications();

        var tooLong = await Assert.ThrowsAsync<ShowcaseBookingRuleException>(
            () => s.Kit.Manager.CancelAsync(credentials, new string('x', ServiceRequestLimits.CancellationReasonMaxLength + 1)));
        var control = await Assert.ThrowsAsync<ShowcaseBookingRuleException>(() => s.Kit.Manager.CancelAsync(credentials, "a\0b"));

        Assert.Equal(new[] { "reason" }, tooLong.Fields);
        Assert.Equal(new[] { "reason" }, control.Fields);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(request.Id)).Status);
        Assert.Empty(s.Emails.Snapshot());
    }

    [Fact]
    public async Task Cancel_ABookingThatIsNotTheCustomers_IsNotFound_AndStaysAsItWas()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();
        s.ForgetNotifications();

        await Assert.ThrowsAsync<NotFoundException>(() => s.Kit.Manager.CancelAsync(credentials with { Email = "un.altro@example.com" }, null));
        await Assert.ThrowsAsync<NotFoundException>(() => s.Kit.Manager.CancelAsync(credentials with { Slug = "altra-vetrina" }, null));

        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(request.Id)).Status);
        Assert.Empty(s.Emails.Snapshot());
    }

    [Fact]
    public async Task Cancel_ASuspendedSupplier_DoesNotKeepTheCustomerFromCancelling()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();
        await s.TakeAsSupplierAsync(request.Id);
        await s.SetSupplierStatusAsync(SupplierStatus.Suspended);

        var view = await s.Kit.Manager.CancelAsync(credentials, null);

        Assert.Equal(ServiceRequestStatus.Annullato, view.Status);
        Assert.Equal(ServiceRequestActorParty.Customer, (await s.ReadAsync(request.Id)).CancelledBy);
        // The supplier is told whatever its status: a suspended supplier is still owed the news.
        Assert.Single(s.Emails.Snapshot(), e => e.Template == EmailTemplates.Names.SupplierBookingCancelledByCustomer);
    }

    [Fact]
    public async Task Cancel_TheHistoryOfTheRequestNamesTheCustomer_AsTheSupplierReadsIt()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();
        await s.Kit.Manager.CancelAsync(credentials, "Cambio programma");

        var detail = await s.Reader.GetAsync(request.Id, s.SupplierOrgId);

        Assert.NotNull(detail);
        Assert.Equal(ServiceRequestStatus.Annullato, detail.Status);
        Assert.Equal(ServiceRequestActorParty.Customer, detail.Cancellation!.By);
        Assert.Equal("Cambio programma", detail.Cancellation.Reason);
        Assert.Equal(ServiceRequestActorParty.Customer, detail.History![^1].Actor);
        // Cancelled, so the supplier reads the short name only (decision D9).
        Assert.Equal("Mario R.", detail.Client.Name);
        Assert.Null(detail.Location.Address);
    }

    // ─── Reschedule ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reschedule_ANewRequest_MovesToTheNewSlot_FreesTheOldOne_AndGivesTheSupplierItsWholeTimeAgain()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();
        s.Clock.Advance(TimeSpan.FromMinutes(60));
        s.ForgetNotifications();

        var view = await s.Kit.Manager.RescheduleAsync(credentials, ServiceRequestScenario.SaturdayAt09);

        Assert.Equal(ServiceRequestStatus.Richiesto, view.Status);
        Assert.Equal(ServiceRequestScenario.SaturdayAt09, view.StartUtc);
        Assert.Equal(ServiceRequestScenario.SaturdayAt09.AddMinutes(ServiceRequestScenario.ServiceMinutes), view.EndUtc);
        // 180 minutes from now (the minute of the move), not from the first booking: the supplier has the whole time again.
        var answerBy = s.Clock.GetUtcNow().UtcDateTime.AddMinutes(180);
        Assert.Equal(answerBy, view.RespondBy);

        var saved = await s.ReadAsync(request.Id);
        Assert.Equal(ServiceRequestScenario.SaturdayAt09, saved.ScheduledStartUtc);
        Assert.Equal(answerBy, saved.ResponseDueAt);
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime, saved.UpdatedAt);
        Assert.Contains(ServiceRequestScenario.FridayAt10, await s.FreeSlotsOfFridayAsync());

        // The supplier is told, with both times and its new deadline; nobody else is.
        var toSupplier = Assert.Single(s.Emails.Snapshot());
        Assert.Equal(SupplierEmail, toSupplier.To);
        Assert.Equal(EmailTemplates.Names.SupplierBookingRescheduledByCustomer, toSupplier.Template);
        Assert.Equal("Il cliente ha cambiato orario — Monza", toSupplier.Content.Subject);
        Assert.Contains("da <strong>09/10/2026 10:00</strong> a <strong>10/10/2026 09:00</strong>", toSupplier.Content.HtmlBody);
        Assert.Contains("Cliente: <strong>Mario R.</strong>", toSupplier.Content.HtmlBody);
        Assert.Contains("rispondi entro le <strong>08/10/2026 16:00</strong>", toSupplier.Content.HtmlBody);
        Assert.DoesNotContain("Rossi", toSupplier.Content.HtmlBody);
        var push = Assert.Single(s.Pushes);
        Assert.Equal(PushTypes.ServiceRequestRescheduled, push.Payload.Type);
        Assert.Equal("Il cliente ha cambiato orario", push.Payload.Title);
        Assert.Equal(PushDeliveryKeys.ServiceRequestRescheduled(request.Id, saved.UpdatedAt), push.DeliveryKey);
        Assert.Equal(PushAudience.SupplierOrg(s.SupplierOrgId), push.Audience);
    }

    [Fact]
    public async Task Reschedule_TheOldSlotIsBookableByAnotherCustomer_AndTheNewOneIsNot()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var (_, credentials) = await s.BookedForManagementAsync();

        await s.Kit.Manager.RescheduleAsync(credentials, ServiceRequestScenario.SaturdayAt09);

        var old = await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync(email: "anna.verdi@example.com"));
        Assert.NotEqual(Guid.Empty, old.Id);
        await Assert.ThrowsAsync<DomainConflictException>(
            async () => await s.Kit.Booking.CreateHoldAsync(
                supplier, await s.InputAsync(ServiceRequestScenario.SaturdayAt09, "sara.neri@example.com")));
    }

    [Fact]
    public async Task Reschedule_ToASlotThatOverlapsItsOwnOldOne_IsAllowed_TheRequestIsNotInItsOwnWay()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (_, credentials) = await s.BookedForManagementAsync();

        // 11:00-13:00 overlaps 10:00-12:00: free, once the request itself is out of the way.
        var view = await s.Kit.Manager.RescheduleAsync(credentials, ServiceRequestScenario.FridayAt10.AddHours(1));

        Assert.Equal(ServiceRequestScenario.FridayAt10.AddHours(1), view.StartUtc);
    }

    [Fact]
    public async Task Reschedule_ASlotAnotherCustomerTook_Is409_AndTheRequestIsAsItWas()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();
        await s.BookedForManagementAsync(ServiceRequestScenario.FridayAt14, "anna.verdi@example.com");
        var before = await s.ReadAsync(request.Id);
        s.Clock.Advance(TimeSpan.FromMinutes(5));
        s.ForgetNotifications();

        var refused = await Assert.ThrowsAsync<DomainConflictException>(
            () => s.Kit.Manager.RescheduleAsync(credentials, ServiceRequestScenario.FridayAt14));

        Assert.Equal(ServiceRequestErrorCodes.SlotUnavailable, refused.Code);
        var after = await s.ReadAsync(request.Id);
        Assert.Equal(before.ScheduledStartUtc, after.ScheduledStartUtc);
        Assert.Equal(before.ResponseDueAt, after.ResponseDueAt);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);
    }

    [Fact]
    public async Task Reschedule_ATimeNoSlotCanHave_IsTheSame409_NeverAnException()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();

        foreach (var start in new[]
                 {
                     ServiceRequestScenario.SundayAt10, // the supplier does not work on Sundays
                     ServiceRequestScenario.FridayAt10.AddMinutes(60 * 24 * -3), // the past
                     ServiceRequestScenario.FridayAt10.AddHours(14), // after the hours of the day
                     ServiceRequestScenario.FridayAt10.AddDays(60), // beyond the horizon of 35 days
                     new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                     new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                     new DateTime(9999, 12, 31, 22, 30, 0, DateTimeKind.Utc),
                 })
        {
            var refused = await Assert.ThrowsAsync<DomainConflictException>(() => s.Kit.Manager.RescheduleAsync(credentials, start));
            Assert.Equal(ServiceRequestErrorCodes.SlotUnavailable, refused.Code);
        }

        Assert.Equal(ServiceRequestScenario.FridayAt10, (await s.ReadAsync(request.Id)).ScheduledStartUtc);
    }

    [Fact]
    public async Task Reschedule_AStartThatIsNotAWholeMinute_Is422NamingTheField()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (_, credentials) = await s.BookedForManagementAsync();

        var refused = await Assert.ThrowsAsync<ShowcaseBookingRuleException>(
            () => s.Kit.Manager.RescheduleAsync(credentials, ServiceRequestScenario.SaturdayAt09.AddSeconds(30)));

        Assert.Equal(ShowcaseBookingErrors.Invalid, refused.Code);
        Assert.Equal(new[] { "startUtc" }, refused.Fields);
    }

    [Fact]
    public async Task Reschedule_TheTimeItAlreadyHas_ChangesNothing_AndTellsNobody()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();
        var before = await s.ReadAsync(request.Id);
        s.Clock.Advance(TimeSpan.FromMinutes(30));
        s.ForgetNotifications();

        var view = await s.Kit.Manager.RescheduleAsync(credentials, ServiceRequestScenario.FridayAt10);

        Assert.Equal(ServiceRequestScenario.FridayAt10, view.StartUtc);
        var after = await s.ReadAsync(request.Id);
        Assert.Equal(before.ResponseDueAt, after.ResponseDueAt);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);
    }

    [Fact]
    public async Task Reschedule_OnceTheSupplierTookTheRequest_IsRefused_TheTimeIsAgreed()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();
        await s.TakeAsSupplierAsync(request.Id);
        s.ForgetNotifications();

        var view = await s.Kit.Manager.LookupAsync(credentials);
        var refused = await Assert.ThrowsAsync<DomainRuleException>(
            () => s.Kit.Manager.RescheduleAsync(credentials, ServiceRequestScenario.SaturdayAt09));

        Assert.False(view.Actions.CanReschedule);
        Assert.Equal(ShowcaseBookingManagementErrors.CannotReschedule, refused.Code);
        Assert.Equal(ServiceRequestScenario.FridayAt10, (await s.ReadAsync(request.Id)).ScheduledStartUtc);
        Assert.Empty(s.Emails.Snapshot());
    }

    [Theory]
    [InlineData(ServiceRequestStatus.Rifiutato)]
    [InlineData(ServiceRequestStatus.Annullato)]
    [InlineData(ServiceRequestStatus.Completato)]
    public async Task Reschedule_AClosedRequest_IsRefused(ServiceRequestStatus status)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();
        switch (status)
        {
            case ServiceRequestStatus.Rifiutato:
                await s.Service.RejectAsync(request.Id, s.SupplierOrgId, "Siamo in ferie");
                break;
            case ServiceRequestStatus.Annullato:
                await s.Kit.Manager.CancelAsync(credentials, null);
                break;
            default:
                await s.TakeAsSupplierAsync(request.Id);
                await s.Service.CompleteAsync(request.Id, s.SupplierOrgId);
                break;
        }

        var refused = await Assert.ThrowsAsync<DomainRuleException>(
            () => s.Kit.Manager.RescheduleAsync(credentials, ServiceRequestScenario.SaturdayAt09));

        Assert.Equal(ShowcaseBookingManagementErrors.CannotReschedule, refused.Code);
    }

    [Fact]
    public async Task Reschedule_AProposalOfTheSupplier_IsDropped_AndTheSupplierIsToldItNoLongerApplies()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();
        await s.ProposeAsSupplierAsync(request.Id, ServiceRequestScenario.FridayAt14, "Il mattino sono già impegnato");
        s.Clock.Advance(TimeSpan.FromMinutes(10));
        s.ForgetNotifications();

        var view = await s.Kit.Manager.RescheduleAsync(credentials, ServiceRequestScenario.SaturdayAt09);

        var saved = await s.ReadAsync(request.Id);
        Assert.Null(view.Proposal);
        Assert.Null(saved.ProposedStartUtc);
        Assert.Null(saved.ProposalMessage);
        // The deadline is the supplier's again (180 minutes), not the customer's day.
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime.AddMinutes(180), saved.ResponseDueAt);
        Assert.Equal(saved.ResponseDueAt, view.RespondBy);
        var toSupplier = Assert.Single(s.Emails.Snapshot());
        Assert.Contains("L'orario che avevi proposto non vale più.", toSupplier.Content.HtmlBody);
    }

    [Fact]
    public async Task Reschedule_ASuspendedSupplier_OrAServiceThatIsNotPublishedAnymore_IsRefused()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();
        var listing = await s.Db.SupplierServiceListings.SingleAsync(l => l.Id == s.ListingId);
        listing.Status = SupplierServiceListingStatus.Paused;
        await s.Db.SaveChangesAsync();

        var paused = await Assert.ThrowsAsync<DomainRuleException>(
            () => s.Kit.Manager.RescheduleAsync(credentials, ServiceRequestScenario.SaturdayAt09));

        listing.Status = SupplierServiceListingStatus.Active;
        await s.Db.SaveChangesAsync();
        await s.SetSupplierStatusAsync(SupplierStatus.Suspended);
        var suspended = await Assert.ThrowsAsync<DomainRuleException>(
            () => s.Kit.Manager.RescheduleAsync(credentials, ServiceRequestScenario.SaturdayAt09));

        Assert.Equal(ShowcaseBookingManagementErrors.CannotReschedule, paused.Code);
        Assert.Equal(ShowcaseBookingErrors.SupplierUnavailable, suspended.Code);
        Assert.Equal(ServiceRequestScenario.FridayAt10, (await s.ReadAsync(request.Id)).ScheduledStartUtc);
    }

    [Fact]
    public async Task Reschedule_TheNoticeOfTheService_StillApplies()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var withNotice = await s.AddListingAsync("Con preavviso", minNoticeHours: 48);
        // Monday 12 October 10:00 in Rome is four days away: fine. Friday 9 October 10:00 is 22 hours away: too soon for 48.
        var input = await s.InputAsync(ServiceRequestScenario.FridayAt10.AddDays(3), service: await s.ListingSlugAsync(withNotice));
        var (_, confirmation) = await s.BookedAsync(input, supplier);
        var credentials = ShowcaseManageScenario.CredentialsOf(confirmation.PublicCode);

        var refused = await Assert.ThrowsAsync<DomainConflictException>(
            () => s.Kit.Manager.RescheduleAsync(credentials, ServiceRequestScenario.FridayAt10));
        var view = await s.Kit.Manager.RescheduleAsync(credentials, ServiceRequestScenario.FridayAt10.AddDays(4));

        Assert.Equal(ServiceRequestErrorCodes.SlotUnavailable, refused.Code);
        Assert.Equal(ServiceRequestScenario.FridayAt10.AddDays(4), view.StartUtc);
    }

    [Fact]
    public async Task Reschedule_AcrossTheDayTheClocksGoBack_KeepsTheLengthAndShowsTheOffsetOfEachDay()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        // Friday 23 October 10:00 (+02:00) to Monday 26 October 09:00 (+01:00): the clocks went back on Sunday 25 October.
        var friday = RomeCalendar.ToUtc(new DateOnly(2026, 10, 23), new TimeOnly(10, 0));
        var monday = RomeCalendar.ToUtc(new DateOnly(2026, 10, 26), new TimeOnly(9, 0));
        var (_, confirmation) = await s.BookedAsync(await s.InputAsync(friday), supplier);
        var credentials = ShowcaseManageScenario.CredentialsOf(confirmation.PublicCode);

        var view = await s.Kit.Manager.RescheduleAsync(credentials, monday);

        Assert.Equal(new DateTime(2026, 10, 26, 8, 0, 0, DateTimeKind.Utc), view.StartUtc);
        Assert.Equal(TimeSpan.FromMinutes(ServiceRequestScenario.ServiceMinutes), view.EndUtc - view.StartUtc);
        Assert.Equal(TimeSpan.FromHours(1), RomeCalendar.ToRome(view.StartUtc).Offset);
        Assert.Equal(TimeSpan.FromHours(2), RomeCalendar.ToRome(friday).Offset);
        // 24 hours before the new time is Sunday 25 October 08:00 UTC: 09:00 on the clock of that day (+01:00).
        Assert.Equal(new DateTime(2026, 10, 25, 8, 0, 0, DateTimeKind.Utc), view.CancellationTerms.FreeUntilUtc);
    }

    [Fact]
    public async Task Reschedule_TwiceInARow_LeavesTheRequestAtTheLastTime_AndEveryOldSlotFree()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();

        await s.Kit.Manager.RescheduleAsync(credentials, ServiceRequestScenario.FridayAt14);
        await s.Kit.Manager.RescheduleAsync(credentials, ServiceRequestScenario.SaturdayAt09);

        var free = await s.FreeSlotsOfFridayAsync();
        Assert.Contains(ServiceRequestScenario.FridayAt10, free);
        Assert.Contains(ServiceRequestScenario.FridayAt14, free);
        Assert.Equal(ServiceRequestScenario.SaturdayAt09, (await s.ReadAsync(request.Id)).ScheduledStartUtc);
        Assert.Equal(2, s.Pushes.Count(p => p.Payload.Type == PushTypes.ServiceRequestRescheduled));
    }

    [Fact]
    public async Task Reschedule_ABookingThatIsNotTheCustomers_IsNotFound()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();

        await Assert.ThrowsAsync<NotFoundException>(
            () => s.Kit.Manager.RescheduleAsync(credentials with { Email = "un.altro@example.com" }, ServiceRequestScenario.SaturdayAt09));

        Assert.Equal(ServiceRequestScenario.FridayAt10, (await s.ReadAsync(request.Id)).ScheduledStartUtc);
    }
}
