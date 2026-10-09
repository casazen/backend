using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-10: what happens to a request from a supplier's public showcase once the supplier has it. The state machine is the one of the
/// requests of the hosts (SP-04); what changes is who is told: there is no host, the customer hears of it by e-mail, and nothing
/// the host could do reaches a request of the showcase — not even when the supplier org is also a host org.
/// </summary>
public class ShowcaseRequestLifecycleTests
{
    [Fact]
    public async Task Take_TheCustomerIsToldByEmail_AndNoHostIsPushed()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, _) = await s.BookedAsync();
        s.ForgetNotifications();
        s.Clock.Advance(TimeSpan.FromMinutes(20));

        var taken = await s.Service.TakeAsync(request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId);

        Assert.Equal(ServiceRequestStatus.PresoInCarico, taken.Status);
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime, taken.TakenAt);
        Assert.Null(taken.ResponseDueAt);
        var email = Assert.Single(s.Emails.Snapshot());
        Assert.Equal(ShowcaseScenario.CustomerEmail, email.To);
        Assert.Equal(EmailTemplates.Names.SupplierBookingAccepted, email.Template);
        Assert.Contains("Supplier Srl ha accettato la tua richiesta", email.Content.Subject);
        Assert.Contains("09/10/2026 10:00", email.Content.HtmlBody);
        Assert.Contains("Prezzo stimato: <strong>60,00 €</strong>", email.Content.HtmlBody);
        // The work is far enough away for the reminder of the day before to be sent, so the e-mail says so.
        Assert.Contains("promemoria il giorno prima, alle 18:00", email.Content.HtmlBody);
        // There is no host behind the request: nothing is pushed to anybody.
        Assert.Empty(s.Pushes);
    }

    [Fact]
    public async Task Take_ThePriceTheSupplierGivesReplacesTheEstimateInTheEmail()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, _) = await s.BookedAsync();
        s.ForgetNotifications();

        var taken = await s.Service.TakeAsync(
            request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, new TakeServiceRequestCommand(QuotedAmountCents: 7000));

        Assert.Equal(7000, taken.QuotedAmountCents);
        var email = Assert.Single(s.Emails.Snapshot());
        Assert.Contains("Prezzo indicato dal fornitore: <strong>70,00 €</strong>", email.Content.HtmlBody);
        Assert.DoesNotContain("60,00", email.Content.HtmlBody);
    }

    [Fact]
    public async Task Take_AfterTheReminderTimeOfTheDayBefore_TheEmailDoesNotPromiseAReminder()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        // Sunday 11 October, 14:00 in Rome; the supplier has all day to answer; the work is on Monday at 10:00.
        s.Clock.SetUtcNow(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
        var settings = await s.Db.SupplierSettings.SingleAsync(x => x.OrgId == s.SupplierOrgId);
        settings.RespondWithinMinutes = 600;
        await s.Db.SaveChangesAsync();
        var (request, _) = await s.BookedAsync(await s.InputAsync(ServiceRequestScenario.FridayAt10.AddDays(3)));
        // 18:30 in Rome on the Sunday: the reminder time (18:00) has gone.
        s.Clock.SetUtcNow(new DateTimeOffset(2026, 10, 11, 16, 30, 0, TimeSpan.Zero));
        s.ForgetNotifications();

        await s.Service.TakeAsync(request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId);

        var email = Assert.Single(s.Emails.Snapshot());
        Assert.Equal(EmailTemplates.Names.SupplierBookingAccepted, email.Template);
        Assert.DoesNotContain("promemoria", email.Content.HtmlBody);
    }

    [Fact]
    public async Task Take_TheEmailIsInTheLanguageOfTheCustomer()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, _) = await s.BookedAsync(await s.InputAsync(change: i => i with { Locale = "en" }));
        s.ForgetNotifications();

        await s.Service.TakeAsync(request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId);

        var email = Assert.Single(s.Emails.Snapshot());
        Assert.Contains("Supplier Srl accepted your request", email.Content.Subject);
    }

    [Fact]
    public async Task AcceptMany_TakesShowcaseRequestsLikeAnyOther_AndTellsEachCustomer()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (first, _) = await s.BookedAsync();
        var (second, _) = await s.BookedAsync(await s.InputAsync(ServiceRequestScenario.FridayAt14, email: "anna.verdi@example.com"));
        var host = await s.RequestAsync(ServiceRequestScenario.SaturdayAt09);
        s.ForgetNotifications();

        var results = await s.Service.AcceptManyAsync(
            s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, [first.Id, second.Id, host.Id]);

        Assert.All(results, result => Assert.True(result.Accepted));
        var toCustomers = s.Emails.Snapshot().Where(e => e.Template == EmailTemplates.Names.SupplierBookingAccepted).ToList();
        Assert.Equal(
            new[] { "anna.verdi@example.com", ShowcaseScenario.CustomerEmail }.Order(),
            toCustomers.Select(e => e.To!).Order());
    }

    [Fact]
    public async Task Reject_TheCustomerIsToldWithTheReason_AndTheSlotIsFreeAgain()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, _) = await s.BookedAsync();
        s.ForgetNotifications();

        var rejected = await s.Service.RejectAsync(request.Id, s.SupplierOrgId, "Siamo in ferie");

        Assert.Equal(ServiceRequestStatus.Rifiutato, rejected.Status);
        var email = Assert.Single(s.Emails.Snapshot());
        Assert.Equal(ShowcaseScenario.CustomerEmail, email.To);
        Assert.Equal(EmailTemplates.Names.SupplierBookingDeclined, email.Template);
        Assert.Contains("non può accettare la tua richiesta", email.Content.Subject);
        Assert.Contains("Siamo in ferie", email.Content.HtmlBody);
        Assert.Contains("Non hai pagato nulla", email.Content.HtmlBody);
        Assert.Contains("/fornitori/vetrina-test", email.Content.HtmlBody);
        Assert.Empty(s.Pushes);
        Assert.Contains(ServiceRequestScenario.FridayAt10, await s.FreeSlotsOfFridayAsync());
    }

    [Fact]
    public async Task CancelAsSupplier_AfterTheTake_TheCustomerIsTold_AndTheSlotIsFreeAgain()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, _) = await s.BookedAsync();
        await s.Service.TakeAsync(request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId);
        s.ForgetNotifications();

        var cancelled = await s.Service.CancelAsSupplierAsync(request.Id, s.SupplierOrgId, "Guasto al furgone");

        Assert.Equal(ServiceRequestStatus.Annullato, cancelled.Status);
        Assert.Equal(ServiceRequestActorParty.Supplier, cancelled.CancelledBy);
        var email = Assert.Single(s.Emails.Snapshot());
        Assert.Equal(EmailTemplates.Names.SupplierBookingCancelled, email.Template);
        Assert.Equal(ShowcaseScenario.CustomerEmail, email.To);
        Assert.Contains("è stata annullata", email.Content.Subject);
        Assert.Contains("Guasto al furgone", email.Content.HtmlBody);
        Assert.Contains(ServiceRequestScenario.FridayAt10, await s.FreeSlotsOfFridayAsync());
    }

    [Fact]
    public async Task StartAndComplete_SendNothingToTheCustomer()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, _) = await s.BookedAsync();
        await s.Service.TakeAsync(request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId);
        s.ForgetNotifications();

        await s.Service.StartAsync(request.Id, s.SupplierOrgId);
        var completed = await s.Service.CompleteAsync(request.Id, s.SupplierOrgId);

        Assert.Equal(ServiceRequestStatus.Completato, completed.Status);
        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);
    }

    [Fact]
    public async Task ProposeTime_TheCustomerIsToldWithTheDeadlineToAnswer_AndTheRequestStaysNew()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, _) = await s.BookedAsync();
        s.ForgetNotifications();
        s.Clock.Advance(TimeSpan.FromMinutes(30));
        var proposed = ServiceRequestScenario.FridayAt14;

        var changed = await s.Service.ProposeTimeAsync(
            request.Id,
            s.SupplierOrgId,
            ServiceRequestScenario.SupplierUserId,
            new ProposeServiceRequestTimeCommand(proposed, null, "Il mattino sono già impegnato"));

        Assert.Equal(ServiceRequestStatus.Richiesto, changed.Status);
        Assert.Equal(proposed, changed.ProposedStartUtc);
        // Decision D8 for a request with no host: the customer has Suppliers:Showcase:ProposalResponseMinutes (a day) to answer.
        var answerBy = s.Clock.GetUtcNow().UtcDateTime.AddMinutes(24 * 60);
        Assert.Equal(answerBy, changed.ResponseDueAt);

        var email = Assert.Single(s.Emails.Snapshot());
        Assert.Equal(ShowcaseScenario.CustomerEmail, email.To);
        Assert.Equal(EmailTemplates.Names.SupplierBookingTimeProposed, email.Template);
        Assert.Contains("propone un altro orario", email.Content.Subject);
        Assert.Contains("09/10/2026 10:00", email.Content.HtmlBody);
        Assert.Contains("09/10/2026 14:00", email.Content.HtmlBody);
        Assert.Contains("Il mattino sono già impegnato", email.Content.HtmlBody);
        Assert.Contains("09/10/2026 12:30", email.Content.HtmlBody);
        Assert.Empty(s.Pushes);
    }

    [Fact]
    public async Task ProposeTime_TheDeadlineOfTheProposalIsConfigurable()
    {
        using var s = await ServiceRequestScenario.CreateAsync(
            showcaseOptions: new ShowcaseBookingOptions
            {
                PrivacyNoticeVersion = ServiceRequestTestKit.PrivacyNoticeVersion,
                ProposalResponseMinutes = 120,
            });
        await s.EnableBookingAsync();
        var (request, _) = await s.BookedAsync();

        var changed = await s.Service.ProposeTimeAsync(
            request.Id,
            s.SupplierOrgId,
            ServiceRequestScenario.SupplierUserId,
            new ProposeServiceRequestTimeCommand(ServiceRequestScenario.FridayAt14));

        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime.AddMinutes(120), changed.ResponseDueAt);
    }

    // ─── Nothing of the host reaches a showcase request ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task HostActions_OnAShowcaseRequest_AreAnsweredLikeAMissingOne_EvenForTheSupplierOrgItself()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, _) = await s.BookedAsync();
        // The request belongs to the supplier org (OrgId): the org that is also a host, the dual-role case, is the one that would
        // match a check on the org alone.
        var orgId = request.OrgId;
        Assert.Equal(s.SupplierOrgId, orgId);

        await AssertNotFound(() => s.Service.CancelAsHostAsync(request.Id, orgId, "Prova"));
        await AssertNotFound(() => s.Service.RemindAsync(request.Id, orgId));
        await AssertNotFound(() => s.Service.AcceptProposalAsync(request.Id, orgId));
        await AssertNotFound(() => s.Service.RejectProposalAsync(request.Id, orgId));
        await AssertNotFound(() => s.Service.MarkPaidAsync(request.Id, orgId));

        var unchanged = await s.ReadAsync(request.Id);
        Assert.Equal(ServiceRequestStatus.Richiesto, unchanged.Status);
        Assert.Null(unchanged.LastRemindedAt);
    }

    [Fact]
    public async Task HostReads_NeverReturnAShowcaseRequest_WhateverTheContextAsked()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, _) = await s.BookedAsync();
        var scope = new HostScope(s.SupplierOrgId, null);

        foreach (var context in Enum.GetValues<ServiceRequestRentalContext>())
        {
            Assert.Null(await s.Service.GetByIdForHostAsync(request.Id, scope, context));
            var (items, total) = await s.Service.ListForHostAsync(scope, context, null, null, null, 1, 100);
            Assert.Empty(items);
            Assert.Equal(0, total);
        }
    }

    [Fact]
    public async Task SupplierReads_FindTheShowcaseRequestOfTheirOwn_AndNobodyElseDoes()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var other = await s.AddBookableSupplierAsync();
        var (request, _) = await s.BookedAsync();

        var mine = await s.Service.GetByIdForSupplierAsync(request.Id, s.SupplierOrgId);
        var theirs = await s.Service.GetByIdForSupplierAsync(request.Id, other.OrgId);

        Assert.NotNull(mine);
        Assert.Null(mine.Property);
        Assert.Null(theirs);
    }

    [Fact]
    public async Task AnotherSupplier_CannotTakeRejectOrCancelIt()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var other = await s.AddBookableSupplierAsync();
        var (request, _) = await s.BookedAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => s.Service.TakeAsync(request.Id, other.OrgId, "auth0|someone-else"));
        await Assert.ThrowsAnyAsync<Exception>(() => s.Service.RejectAsync(request.Id, other.OrgId, "No"));
        await Assert.ThrowsAnyAsync<Exception>(() => s.Service.CancelAsSupplierAsync(request.Id, other.OrgId, "No"));

        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(request.Id)).Status);
    }

    [Fact]
    public async Task HostCreate_WithTheShowcaseContext_IsRefused()
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        // A host can never create a request that belongs to the showcase: the context is not one a host chooses.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => s.RequestAsync(rentalContext: ServiceRequestRentalContext.Showcase));

        Assert.Empty(await s.ShowcaseRequestsAsync());
    }

    private static async Task AssertNotFound(Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<NotFoundException>(action);
        Assert.Equal(ServiceRequestErrorCodes.NotFound, ex.Code);
    }
}
