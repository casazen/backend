using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email.Templates;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-11: the customer answers the other time the supplier proposed (SP-04's <c>propose-time</c>, with the same meaning for a showcase
/// request as for a host's: an offer, not a booking). Accepting takes the request on the supplier's behalf at that time, after the slot
/// was checked again under the calendar lock (409 and the proposal stays when it is gone); turning it down drops the proposal and leaves
/// the request <c>Richiesto</c> at its time, with the supplier's whole time to answer again. The customer has a day to answer: after it
/// the upkeep job cancels the request with a reason of its own (<c>ProposalNotAnswered</c>) and both parties are told the truth — that
/// it was the customer who did not answer. The races with the supplier need PostgreSQL (<c>ShowcaseBookingManagementPostgresTests</c>).
/// </summary>
public class ShowcaseBookingManagerProposalTests
{
    private const string SupplierEmail = "supplier@test.com";

    // ─── Accept ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Accept_TakesTheRequestAtTheProposedTime_FreesTheOldSlot_AndTellsBothParties()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();
        s.Clock.Advance(TimeSpan.FromMinutes(20));
        await s.ProposeAsSupplierAsync(request.Id, ServiceRequestScenario.FridayAt14, "Il mattino sono già impegnato");
        var proposedAt = s.Clock.GetUtcNow().UtcDateTime;
        s.Clock.Advance(TimeSpan.FromMinutes(40));
        s.ForgetNotifications();

        var view = await s.Kit.Manager.AcceptProposalAsync(credentials);

        Assert.Equal(ServiceRequestStatus.PresoInCarico, view.Status);
        Assert.Equal(ServiceRequestScenario.FridayAt14, view.StartUtc);
        Assert.Equal(ServiceRequestScenario.FridayAt14.AddMinutes(ServiceRequestScenario.ServiceMinutes), view.EndUtc);
        Assert.Null(view.Proposal);
        Assert.Null(view.RespondBy);
        // Taken now: the exact address is there, and the time can no longer be moved.
        Assert.Equal(ShowcaseScenario.Address, view.Place.Address);
        Assert.Equal(new ShowcaseBookingActions(CanCancel: true, CanReschedule: false, CanRespondToProposal: false), view.Actions);

        var saved = await s.ReadAsync(request.Id);
        Assert.Equal(ServiceRequestStatus.PresoInCarico, saved.Status);
        Assert.Equal(ServiceRequestScenario.FridayAt14, saved.ScheduledStartUtc);
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime, saved.TakenAt);
        // Taken on behalf of the member of the supplier who proposed it.
        Assert.Equal(ServiceRequestScenario.SupplierUserId, saved.TakenByUserId);
        Assert.Null(saved.ResponseDueAt);
        Assert.Null(saved.ProposedStartUtc);
        Assert.Null(saved.ProposalMessage);
        var free = await s.FreeSlotsOfFridayAsync();
        Assert.Contains(ServiceRequestScenario.FridayAt10, free);
        Assert.DoesNotContain(ServiceRequestScenario.FridayAt14, free);

        // The supplier is told, e-mail and push.
        var toSupplier = Assert.Single(s.Emails.Snapshot(), e => e.To == SupplierEmail);
        Assert.Equal(EmailTemplates.Names.SupplierBookingProposalAnsweredByCustomer, toSupplier.Template);
        Assert.Equal("Il cliente ha accettato il nuovo orario — Monza", toSupplier.Content.Subject);
        Assert.Contains("<strong>09/10/2026 14:00</strong>", toSupplier.Content.HtmlBody);
        Assert.Contains("La richiesta è ora presa in carico.", toSupplier.Content.HtmlBody);
        Assert.Contains("Cliente: <strong>Mario R.</strong>", toSupplier.Content.HtmlBody);
        Assert.DoesNotContain("Rossi", toSupplier.Content.HtmlBody);
        var push = Assert.Single(s.Pushes);
        Assert.Equal(PushTypes.ServiceRequestProposalAccepted, push.Payload.Type);
        Assert.Equal("Nuovo orario accettato", push.Payload.Title);
        Assert.Equal(PushDeliveryKeys.ServiceRequestProposalAnswered(request.Id, proposedAt, accepted: true), push.DeliveryKey);

        // The customer gets the e-mail of a request taken, with the new time and the estimate.
        var toCustomer = Assert.Single(s.Emails.Snapshot(), e => e.To == ShowcaseScenario.CustomerEmail);
        Assert.Equal(EmailTemplates.Names.SupplierBookingAccepted, toCustomer.Template);
        Assert.Contains("09/10/2026 14:00", toCustomer.Content.HtmlBody);
        Assert.Contains("Prezzo stimato: <strong>60,00 €</strong>", toCustomer.Content.HtmlBody);
        Assert.Equal(2, s.Emails.Snapshot().Count);
    }

    [Fact]
    public async Task Accept_TheTimeGoneMeanwhile_Is409_TheProposalStays_AndTheCustomerCanStillTurnItDownOrCancel()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();
        await s.ProposeAsSupplierAsync(request.Id, ServiceRequestScenario.FridayAt14);
        // A proposal is an offer, not a booking: it holds nothing, so somebody else can take the slot.
        await s.BookedForManagementAsync(ServiceRequestScenario.FridayAt14, "anna.verdi@example.com");
        s.ForgetNotifications();

        var refused = await Assert.ThrowsAsync<DomainConflictException>(() => s.Kit.Manager.AcceptProposalAsync(credentials));

        Assert.Equal(ServiceRequestErrorCodes.SlotUnavailable, refused.Code);
        var saved = await s.ReadAsync(request.Id);
        Assert.Equal(ServiceRequestStatus.Richiesto, saved.Status);
        Assert.Equal(ServiceRequestScenario.FridayAt14, saved.ProposedStartUtc);
        Assert.Equal(ServiceRequestScenario.FridayAt10, saved.ScheduledStartUtc);
        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);

        var view = await s.Kit.Manager.LookupAsync(credentials);
        Assert.True(view.Actions.CanRespondToProposal);
        var rejected = await s.Kit.Manager.RejectProposalAsync(credentials);
        Assert.Null(rejected.Proposal);
        Assert.Equal(ServiceRequestStatus.Richiesto, rejected.Status);
    }

    [Fact]
    public async Task Accept_AfterTheDeadline_IsRefused_AndTheJobThenCancelsTheRequestForTheCustomersSilence()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();
        await s.ProposeAsSupplierAsync(request.Id, ServiceRequestScenario.FridayAt14);
        s.Clock.Advance(TimeSpan.FromHours(24));
        s.ForgetNotifications();

        var accept = await Assert.ThrowsAsync<DomainRuleException>(() => s.Kit.Manager.AcceptProposalAsync(credentials));
        var reject = await Assert.ThrowsAsync<DomainRuleException>(() => s.Kit.Manager.RejectProposalAsync(credentials));

        Assert.Equal(ShowcaseBookingManagementErrors.ProposalExpired, accept.Code);
        Assert.Equal(ShowcaseBookingManagementErrors.ProposalExpired, reject.Code);
        Assert.Equal(ServiceRequestScenario.FridayAt14, (await s.ReadAsync(request.Id)).ProposedStartUtc);

        // The upkeep does what the page says it will.
        var run = await s.ExpiryJob().RunAsync();

        Assert.Equal(1, run.RequestsCancelled);
        var saved = await s.ReadAsync(request.Id);
        Assert.Equal(ServiceRequestStatus.Annullato, saved.Status);
        Assert.Equal(ServiceRequestCancellationReasons.ProposalNotAnswered, saved.CancellationReason);
        Assert.Equal(ServiceRequestActorParty.System, saved.CancelledBy);
        var view = await s.Kit.Manager.LookupAsync(credentials);
        Assert.Equal(ServiceRequestStatus.Annullato, view.Status);
        Assert.Equal(ServiceRequestActorParty.System, view.Cancellation!.By);
        Assert.Null(view.Cancellation.Reason);
    }

    [Fact]
    public async Task Accept_OneSecondBeforeTheDeadline_Works()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();
        await s.ProposeAsSupplierAsync(request.Id, ServiceRequestScenario.FridayAt14);
        s.Clock.Advance(TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1));

        var view = await s.Kit.Manager.AcceptProposalAsync(credentials);

        Assert.Equal(ServiceRequestStatus.PresoInCarico, view.Status);
    }

    [Fact]
    public async Task Accept_WithoutAProposal_OnATakenRequest_OrAfterItWasAnswered_Is422()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();
        var (taken, takenCredentials) = await s.BookedForManagementAsync(ServiceRequestScenario.FridayAt14, "anna.verdi@example.com");
        await s.TakeAsSupplierAsync(taken.Id);
        s.ForgetNotifications();

        var none = await Assert.ThrowsAsync<DomainRuleException>(() => s.Kit.Manager.AcceptProposalAsync(credentials));
        var alreadyTaken = await Assert.ThrowsAsync<DomainRuleException>(() => s.Kit.Manager.AcceptProposalAsync(takenCredentials));
        var noneToTurnDown = await Assert.ThrowsAsync<DomainRuleException>(() => s.Kit.Manager.RejectProposalAsync(credentials));

        Assert.All(
            new[] { none, alreadyTaken, noneToTurnDown },
            ex => Assert.Equal(ShowcaseBookingManagementErrors.NoProposal, ex.Code));
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(request.Id)).Status);
        Assert.Empty(s.Emails.Snapshot());

        // After an answer, a second answer is the same refusal.
        await s.ProposeAsSupplierAsync(request.Id, ServiceRequestScenario.SaturdayAt09);
        await s.Kit.Manager.AcceptProposalAsync(credentials);
        var again = await Assert.ThrowsAsync<DomainRuleException>(() => s.Kit.Manager.AcceptProposalAsync(credentials));
        Assert.Equal(ShowcaseBookingManagementErrors.NoProposal, again.Code);
    }

    [Fact]
    public async Task Accept_ASuspendedSupplier_CannotTakeWork_TheProposalStays()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();
        await s.ProposeAsSupplierAsync(request.Id, ServiceRequestScenario.FridayAt14);
        await s.SetSupplierStatusAsync(SupplierStatus.Suspended);
        s.ForgetNotifications();

        var accept = await Assert.ThrowsAsync<DomainRuleException>(() => s.Kit.Manager.AcceptProposalAsync(credentials));
        var reject = await Assert.ThrowsAsync<DomainRuleException>(() => s.Kit.Manager.RejectProposalAsync(credentials));

        Assert.Equal(ShowcaseBookingErrors.SupplierUnavailable, accept.Code);
        Assert.Equal(ShowcaseBookingErrors.SupplierUnavailable, reject.Code);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(request.Id)).Status);
        Assert.Empty(s.Emails.Snapshot());
    }

    [Fact]
    public async Task Accept_TheNoticeOfTheService_StillAppliesToTheProposedTime()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var withNotice = await s.AddListingAsync("Con preavviso", minNoticeHours: 24);
        // Booked for Monday 12 October; the supplier proposes Saturday 10 October 09:00, 45 hours away (24 hours of notice: fine).
        var input = await s.InputAsync(ServiceRequestScenario.FridayAt10.AddDays(3), service: await s.ListingSlugAsync(withNotice));
        var (request, confirmation) = await s.BookedAsync(input, supplier);
        await s.ProposeAsSupplierAsync(request.Id, ServiceRequestScenario.SaturdayAt09);
        var credentials = ShowcaseManageScenario.CredentialsOf(confirmation.PublicCode);
        // The customer answers a minute before its day is over: Saturday 09:00 is then 21 hours away, less than the 24 of notice.
        s.Clock.Advance(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59));
        Assert.True((await s.Kit.Manager.LookupAsync(credentials)).Actions.CanRespondToProposal);

        var refused = await Assert.ThrowsAsync<DomainConflictException>(() => s.Kit.Manager.AcceptProposalAsync(credentials));

        Assert.Equal(ServiceRequestErrorCodes.SlotUnavailable, refused.Code);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(request.Id)).Status);
    }

    // ─── Reject ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reject_DropsTheProposal_TheRequestStaysNewAtItsTime_AndTheSupplierHasItsWholeTimeAgain()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();
        s.Clock.Advance(TimeSpan.FromMinutes(20));
        await s.ProposeAsSupplierAsync(request.Id, ServiceRequestScenario.FridayAt14, "Il mattino sono già impegnato");
        var proposedAt = s.Clock.GetUtcNow().UtcDateTime;
        s.Clock.Advance(TimeSpan.FromHours(2));
        s.ForgetNotifications();

        var view = await s.Kit.Manager.RejectProposalAsync(credentials);

        Assert.Equal(ServiceRequestStatus.Richiesto, view.Status);
        Assert.Null(view.Proposal);
        Assert.Equal(ServiceRequestScenario.FridayAt10, view.StartUtc);
        var answerBy = s.Clock.GetUtcNow().UtcDateTime.AddMinutes(180);
        Assert.Equal(answerBy, view.RespondBy);
        Assert.Equal(new ShowcaseBookingActions(true, true, false), view.Actions);

        var saved = await s.ReadAsync(request.Id);
        Assert.Equal(ServiceRequestScenario.FridayAt10, saved.ScheduledStartUtc);
        Assert.Equal(answerBy, saved.ResponseDueAt);
        Assert.Null(saved.ProposedStartUtc);
        Assert.Null(saved.ProposedEndUtc);
        Assert.Null(saved.ProposedAt);
        Assert.Null(saved.ProposedByUserId);
        Assert.Null(saved.ProposalMessage);
        Assert.Null(saved.TakenAt);
        // The slot it kept the whole time is still its.
        Assert.DoesNotContain(ServiceRequestScenario.FridayAt10, await s.FreeSlotsOfFridayAsync());

        var toSupplier = Assert.Single(s.Emails.Snapshot());
        Assert.Equal(SupplierEmail, toSupplier.To);
        Assert.Equal("Il cliente ha rifiutato il nuovo orario — Monza", toSupplier.Content.Subject);
        Assert.Contains("La richiesta resta in attesa dell'orario richiesto, <strong>09/10/2026 10:00</strong>", toSupplier.Content.HtmlBody);
        // 12:20 UTC plus the supplier's 180 minutes is 15:20 UTC, 17:20 in Rome.
        Assert.Contains("rispondi entro le <strong>08/10/2026 17:20</strong>", toSupplier.Content.HtmlBody);
        var push = Assert.Single(s.Pushes);
        Assert.Equal(PushTypes.ServiceRequestProposalRejected, push.Payload.Type);
        Assert.Equal("Nuovo orario rifiutato", push.Payload.Title);
        Assert.Equal(PushDeliveryKeys.ServiceRequestProposalAnswered(request.Id, proposedAt, accepted: false), push.DeliveryKey);
    }

    [Fact]
    public async Task Reject_ThenTheSupplierProposesAgain_AndTheCustomerAccepts()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();
        await s.ProposeAsSupplierAsync(request.Id, ServiceRequestScenario.FridayAt14);
        await s.Kit.Manager.RejectProposalAsync(credentials);
        s.Clock.Advance(TimeSpan.FromMinutes(15));
        await s.ProposeAsSupplierAsync(request.Id, ServiceRequestScenario.SaturdayAt09);

        var view = await s.Kit.Manager.AcceptProposalAsync(credentials);

        Assert.Equal(ServiceRequestStatus.PresoInCarico, view.Status);
        Assert.Equal(ServiceRequestScenario.SaturdayAt09, view.StartUtc);
    }

    // ─── The proposal that nobody answers ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Lapse_AProposalTheCustomerLetsLapse_IsToldToBothAsWhatItIs_TheCustomersSilence()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync(locale: "en");
        await s.ProposeAsSupplierAsync(request.Id, ServiceRequestScenario.FridayAt14);
        s.Clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1));
        s.ForgetNotifications();

        var run = await s.ExpiryJob().RunAsync();

        Assert.Equal(1, run.RequestsCancelled);
        var toCustomer = Assert.Single(s.Emails.Snapshot(), e => e.To == ShowcaseScenario.CustomerEmail);
        Assert.Equal(EmailTemplates.Names.SupplierBookingProposalExpired, toCustomer.Template);
        Assert.Equal("Request to Supplier Srl cancelled: new time not answered", toCustomer.Content.Subject);
        Assert.Contains("but you did not answer in time, so we cancelled the request", toCustomer.Content.HtmlBody);
        Assert.Contains("9 October 2026, 10:00", toCustomer.Content.HtmlBody); // the time it had asked for
        Assert.DoesNotContain("did not answer your request", toCustomer.Content.HtmlBody);
        var toSupplier = Assert.Single(s.Emails.Snapshot(), e => e.To == SupplierEmail);
        Assert.Equal(EmailTemplates.Names.SupplierBookingProposalLapsed, toSupplier.Template);
        Assert.Equal("Richiesta annullata: il cliente non ha risposto — Monza", toSupplier.Content.Subject);
        Assert.DoesNotContain("non hai risposto entro il termine", toSupplier.Content.HtmlBody);
        Assert.Equal(2, s.Emails.Snapshot().Count);
        Assert.Equal(ServiceRequestStatus.Annullato, (await s.Kit.Manager.LookupAsync(credentials)).Status);
        Assert.Equal(ServiceRequestCancellationReasons.ProposalNotAnswered, (await s.ReadAsync(request.Id)).CancellationReason);
    }

    [Fact]
    public async Task Lapse_ARequestTheSupplierNeverAnswered_IsStillTheSuppliersSilence_Unchanged()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, _) = await s.BookedForManagementAsync();
        s.Clock.Advance(TimeSpan.FromMinutes(181));
        s.ForgetNotifications();

        await s.ExpiryJob().RunAsync();

        Assert.Equal(ServiceRequestCancellationReasons.NoResponse, (await s.ReadAsync(request.Id)).CancellationReason);
        var toCustomer = Assert.Single(s.Emails.Snapshot(), e => e.To == ShowcaseScenario.CustomerEmail);
        Assert.Equal(EmailTemplates.Names.SupplierBookingExpired, toCustomer.Template);
        var toSupplier = Assert.Single(s.Emails.Snapshot(), e => e.To == SupplierEmail);
        Assert.Contains("non hai risposto entro il termine", toSupplier.Content.HtmlBody);
    }

    [Fact]
    public async Task Lapse_TheJobIsIdempotent_ASecondRunCancelsAndTellsNothingMore()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, _) = await s.BookedForManagementAsync();
        await s.ProposeAsSupplierAsync(request.Id, ServiceRequestScenario.FridayAt14);
        s.Clock.Advance(TimeSpan.FromHours(25));
        await s.ExpiryJob().RunAsync();
        var saved = await s.ReadAsync(request.Id);
        s.ForgetNotifications();

        var second = await s.ExpiryJob().RunAsync();

        Assert.Equal(0, second.RequestsCancelled);
        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);
        Assert.Equal(saved.UpdatedAt, (await s.ReadAsync(request.Id)).UpdatedAt);
    }

    [Fact]
    public async Task Lapse_AProposalTheCustomerAnsweredInTime_IsLeftAloneByTheJob()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();
        await s.ProposeAsSupplierAsync(request.Id, ServiceRequestScenario.FridayAt14);
        s.Clock.Advance(TimeSpan.FromHours(23));
        await s.Kit.Manager.AcceptProposalAsync(credentials);
        s.Clock.Advance(TimeSpan.FromHours(2));

        var run = await s.ExpiryJob().RunAsync();

        Assert.Equal(0, run.RequestsCancelled);
        Assert.Equal(ServiceRequestStatus.PresoInCarico, (await s.ReadAsync(request.Id)).Status);
    }
}
