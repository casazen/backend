using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Web.DTOs.Supplier;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-10b-b: the offer of the supplier on a request on measure, accepted or turned down by the customer, over the engine of the quote (decision
/// SP-D30). The rules that cost money, a request or trust when they are wrong are the ones tested: the price agreed is the total of the offer
/// and the time is the one of the offer, an acceptance takes the request once (a second one finds nothing to accept), a time that is gone is a
/// 409 with the offer still standing, the offer the customer read is the one it answers (a replaced one is a 409), the customer has a day to
/// answer and the request then closes with the truth told to both, an offer turned down or taken back leaves a conversation that no clock
/// closes, and only a request on measure has an offer at all. The races with the supplier need PostgreSQL
/// (<c>ShowcaseBookingManagementPostgresTests</c>).
/// </summary>
public class ServiceRequestOfferTests
{
    private const string SupplierEmail = "supplier@test.com";

    private static readonly DateTime Start = ServiceRequestScenario.FridayAt10;
    private static readonly DateTime End = ServiceRequestScenario.FridayAt10.AddHours(2);

    /// <summary>Two hours of the work at 45 euro and the materials at 30: 120 euro.</summary>
    private static SendServiceRequestOfferCommand TheOffer(int hours = 2, DateTime? start = null, DateTime? end = null) =>
        new(
            [new ServiceRequestOfferLine("Posa del corrimano", hours, 4500), new ServiceRequestOfferLine("Materiali", 1, 3000)],
            start ?? Start,
            end ?? End,
            "Porto io tutto il materiale");

    private static Task<ServiceRequest> SendAsync(ServiceRequestScenario s, Guid requestId, SendServiceRequestOfferCommand? offer = null) =>
        s.Service.SendOfferAsync(requestId, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, offer ?? TheOffer());

    private static ShowcaseBookingCredentials CredentialsOf(ServiceRequest request, string email = ShowcaseScenario.CustomerEmail) =>
        ShowcaseManageScenario.CredentialsOf(request.PublicCode!, email);

    // ─── Sending ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SendOffer_PutsTheOfferOnTheRequest_GivesTheCustomerADay_AndTellsItWithTheTotalAndTheLines()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var request = await s.InquiredAsync(supplier);
        s.Clock.Advance(TimeSpan.FromMinutes(10));

        var sent = await SendAsync(s, request.Id);

        Assert.Equal(ServiceRequestStatus.Richiesto, sent.Status);
        var saved = await s.ReadAsync(request.Id);
        Assert.Equal(ServiceRequestStatus.Richiesto, saved.Status);
        Assert.Equal(12000, saved.QuoteTotalCents);
        Assert.Equal(Start, saved.QuoteScheduledStartUtc);
        Assert.Equal(End, saved.QuoteScheduledEndUtc);
        Assert.Equal(ServiceRequestScenario.SupplierUserId, saved.QuoteSentByUserId);
        Assert.Equal("Porto io tutto il materiale", saved.QuoteNote);
        var lines = ServiceRequestJson.ReadQuoteLines(saved.QuoteLinesJson);
        Assert.Equal(
            new[] { new ServiceRequestQuoteLine("Posa del corrimano", 9000, 2, 4500), new ServiceRequestQuoteLine("Materiali", 3000, 1, 3000) },
            lines.ToArray());

        // The offer holds no slot and gives the request no time: it keeps none until the customer accepts. The supplier answered, so the clock
        // that stood on it (an hour) is the customer's now: a day.
        Assert.Null(saved.ScheduledStartUtc);
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime.AddHours(24), saved.ResponseDueAt);

        var email = Assert.Single(s.Emails.Snapshot());
        Assert.Equal((ShowcaseScenario.CustomerEmail, EmailTemplates.Names.ServiceRequestQuoteReceived), (email.To, email.Template));
        Assert.Contains("120,00 €", email.Content.HtmlBody);
        Assert.Contains("2 × 45,00 €", email.Content.HtmlBody);
        Assert.Empty(s.Pushes);
        Assert.Empty(await s.ReliabilityEventsAsync());
    }

    [Fact]
    public async Task SendOffer_ANewOfferReplacesTheOldOne_AndTheCustomerWhoReadTheOldOneIsToldOnItsAnswer_WithNothingChanged()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var request = await s.InquiredAsync(supplier);
        await SendAsync(s, request.Id);
        var firstSentAt = (await s.ReadAsync(request.Id)).QuoteSentAt!.Value;
        s.Clock.Advance(TimeSpan.FromMinutes(3));

        await SendAsync(s, request.Id, TheOffer(hours: 1));
        s.ForgetNotifications();

        var saved = await s.ReadAsync(request.Id);
        Assert.Equal(7500, saved.QuoteTotalCents);
        Assert.NotEqual(firstSentAt, saved.QuoteSentAt);
        var stale = await Assert.ThrowsAsync<DomainConflictException>(
            () => s.Kit.Manager.AcceptOfferAsync(CredentialsOf(request), firstSentAt));
        Assert.Equal(ServiceRequestOfferErrors.OfferChanged, stale.Code);
        var staleNo = await Assert.ThrowsAsync<DomainConflictException>(
            () => s.Kit.Manager.DeclineOfferAsync(CredentialsOf(request), firstSentAt));
        Assert.Equal(ServiceRequestOfferErrors.OfferChanged, staleNo.Code);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(request.Id)).Status);
        Assert.Equal(7500, (await s.ReadAsync(request.Id)).QuoteTotalCents);
        Assert.Empty(s.Emails.Snapshot());
    }

    [Fact]
    public async Task ALineTheSupplierWritesBesideItsOffer_DoesNotStopTheCustomersDay_SoTheOfferStillLapses()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var request = await s.InquiredAsync(supplier);
        await SendAsync(s, request.Id);
        var dueBefore = (await s.ReadAsync(request.Id)).ResponseDueAt;
        Assert.NotNull(dueBefore);
        s.Clock.Advance(TimeSpan.FromHours(1));

        await s.Service.PostMessageAsSupplierAsync(request.Id, s.SupplierOrgId, "Se serve posso anticipare di un'ora.");

        Assert.Equal(dueBefore, (await s.ReadAsync(request.Id)).ResponseDueAt);
        s.Clock.Advance(TimeSpan.FromHours(23));
        var run = await s.ExpiryJob().RunAsync();
        Assert.Equal(1, run.RequestsCancelled);
        Assert.Equal(ServiceRequestCancellationReasons.QuoteNotAnswered, (await s.ReadAsync(request.Id)).CancellationReason);
    }

    [Fact]
    public async Task SendOffer_OnlyOnANewRequestOnMeasureOfTheSupplier_AnyOtherIdIsTheSame404_AndAQuoteIsNotHowItIsAnswered()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var request = await s.InquiredAsync(supplier);
        var other = await s.AddBookableSupplierAsync();
        var (booking, _) = await s.BookedAsync();

        var ofAnother = await Assert.ThrowsAsync<NotFoundException>(
            () => s.Service.SendOfferAsync(request.Id, other.OrgId, "auth0|other", TheOffer()));
        var aBooking = await Assert.ThrowsAsync<NotFoundException>(() => SendAsync(s, booking.Id));
        var missing = await Assert.ThrowsAsync<NotFoundException>(() => SendAsync(s, Guid.NewGuid()));
        Assert.All(new[] { aBooking, missing }, ex => Assert.Equal(ofAnother.Code, ex.Code));

        // The quote of SP-04b leans on the time of a request: a request on measure has none, so it is answered with an offer.
        var quote = await Assert.ThrowsAsync<DomainRuleException>(
            () => s.Service.SendQuoteAsync(
                request.Id,
                s.SupplierOrgId,
                ServiceRequestScenario.SupplierUserId,
                new SendServiceRequestQuoteCommand([new ServiceRequestQuoteLine("Posa", 9000)])));
        Assert.Equal(ServiceRequestThreadErrors.OfferNeeded, quote.Code);

        // A request the supplier refused has no offer to wait for.
        await s.Service.RejectAsync(request.Id, s.SupplierOrgId, "Non lavoro in quella zona");
        var closed = await Assert.ThrowsAsync<DomainRuleException>(() => SendAsync(s, request.Id));
        Assert.Equal(ServiceRequestOfferErrors.CannotOffer, closed.Code);
    }

    [Fact]
    public async Task SendOffer_TheTimeIsJudgedByThePlanner_ATimeThatIsNotFreeIs409_AMissingEndIsA422_AndNothingIsSaved()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var request = await s.InquiredAsync(supplier);

        // The supplier does not work on Sundays.
        var sunday = await Assert.ThrowsAsync<DomainConflictException>(
            () => SendAsync(s, request.Id, TheOffer(start: ServiceRequestScenario.SundayAt10, end: ServiceRequestScenario.SundayAt10.AddHours(2))));
        Assert.Equal(ServiceRequestErrorCodes.SlotUnavailable, sunday.Code);

        // A request on measure names no service, so the end of the work cannot be guessed.
        var noEnd = await Assert.ThrowsAsync<DomainRuleException>(
            () => s.Service.SendOfferAsync(
                request.Id,
                s.SupplierOrgId,
                ServiceRequestScenario.SupplierUserId,
                new SendServiceRequestOfferCommand([new ServiceRequestOfferLine("Posa", 1, 5000)], Start)));
        Assert.Equal(ServiceRequestErrorCodes.TimeInvalid, noEnd.Code);

        // An offer outside the limits is refused whole, before anything is read.
        var invalid = await Assert.ThrowsAsync<DomainRuleException>(
            () => s.Service.SendOfferAsync(
                request.Id,
                s.SupplierOrgId,
                ServiceRequestScenario.SupplierUserId,
                new SendServiceRequestOfferCommand([new ServiceRequestOfferLine("Gratis", 1, 0)], Start, End)));
        Assert.Equal(ServiceRequestOfferErrors.Invalid, invalid.Code);

        var saved = await s.ReadAsync(request.Id);
        Assert.Null(saved.QuoteSentAt);
        Assert.Null(saved.QuoteTotalCents);
        Assert.Empty(s.Emails.Snapshot());
    }

    // ─── What the customer reads ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Lookup_ShowsTheOfferNotAQuote_WithTheTimeTheDeadlineAndWhatTheCustomerMayDo()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var request = await s.InquiredAsync(supplier);
        await SendAsync(s, request.Id);
        var sentAt = (await s.ReadAsync(request.Id)).QuoteSentAt!.Value;

        var view = await s.Kit.Manager.LookupAsync(CredentialsOf(request));

        Assert.Equal(ServiceRequestKind.Custom, view.Kind);
        Assert.Equal(ServiceRequestNegotiation.OfferSent, view.Negotiation);
        Assert.Null(view.Quote);
        var offer = Assert.IsType<ServiceRequestOffer>(view.Offer);
        Assert.Equal(sentAt, offer.SentAt);
        Assert.Equal(12000, offer.TotalCents);
        Assert.Equal(Start, offer.StartUtc);
        Assert.Equal(End, offer.EndUtc);
        Assert.Equal(sentAt.AddHours(24), offer.ValidUntil);
        Assert.Equal("Porto io tutto il materiale", offer.Note);
        Assert.Equal(2, offer.Lines.Count);
        Assert.True(view.Actions.CanRespondToOffer);
        Assert.False(view.Actions.CanRespondToQuote);
        Assert.True(view.Actions.CanCancel);
        Assert.False(view.Actions.CanReschedule);
        Assert.Null(view.RespondBy);

        // The API gives it as an offer, with the time in the local time of Rome next to the UTC one.
        var dto = PublicSupplierBookingViewMapper.ToDto(view);
        Assert.Equal(12000, dto.Offer!.TotalCents);
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 10, 0, 0, TimeSpan.FromHours(2)), dto.Offer.StartLocal);
        Assert.Equal(2, dto.Offer.Lines[0].Quantity);
        Assert.Equal(4500, dto.Offer.Lines[0].UnitAmountCents);
        Assert.True(dto.Actions.CanRespondToOffer);
    }

    [Fact]
    public async Task Lookup_OfABookingFromASlot_WithAQuote_IsStillAQuote_NoOfferAndNoOfferAction()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (booking, credentials) = await s.BookedForManagementAsync();
        await s.Service.SendQuoteAsync(
            booking.Id,
            s.SupplierOrgId,
            ServiceRequestScenario.SupplierUserId,
            new SendServiceRequestQuoteCommand([new ServiceRequestQuoteLine("Pulizia profonda", 30000)]));

        var view = await s.Kit.Manager.LookupAsync(credentials);

        Assert.NotNull(view.Quote);
        Assert.Null(view.Offer);
        Assert.True(view.Actions.CanRespondToQuote);
        Assert.False(view.Actions.CanRespondToOffer);

        // And the offer endpoints do not answer it: a booking from a slot is the 404 of the area.
        var refused = await Assert.ThrowsAsync<NotFoundException>(
            () => s.Kit.Manager.AcceptOfferAsync(credentials, view.Quote!.SentAt));
        Assert.Equal(ShowcaseBookingManagementErrors.NotFound, refused.Code);
    }

    // ─── Accepting ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Accept_TakesTheRequestAtTheTimeAndTheTotalOfTheOffer_Once_AndTellsBothParties()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var request = await s.InquiredAsync(supplier);
        await SendAsync(s, request.Id);
        var sentAt = (await s.ReadAsync(request.Id)).QuoteSentAt!.Value;
        s.ForgetNotifications();

        // The answer is about the version the customer read: another one is a 409 and nothing changes.
        var stale = await Assert.ThrowsAsync<DomainConflictException>(
            () => s.Kit.Manager.AcceptOfferAsync(CredentialsOf(request), sentAt.AddMinutes(-1)));
        Assert.Equal(ServiceRequestOfferErrors.OfferChanged, stale.Code);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(request.Id)).Status);
        Assert.Empty(s.Emails.Snapshot());

        var accepted = await s.Kit.Manager.AcceptOfferAsync(CredentialsOf(request), sentAt);

        Assert.Equal(ServiceRequestStatus.PresoInCarico, accepted.Status);
        Assert.Null(accepted.Offer);
        Assert.Equal(ServiceRequestNegotiation.Agreed, accepted.Negotiation);
        Assert.False(accepted.Actions.CanRespondToOffer);
        Assert.Equal((Start, End), (accepted.StartUtc, accepted.EndUtc));
        Assert.Equal(12000, accepted.Price.QuotedAmountCents);
        Assert.Equal(12000, accepted.Price.AmountCents);
        Assert.Equal("quote", accepted.Price.Basis);
        Assert.Equal(
            new[] { ("service", "Posa del corrimano", 2, 4500, 9000), ("service", "Materiali", 1, 3000, 3000) },
            accepted.Price.Lines.Select(l => (l.Kind, l.Label, l.Quantity, l.UnitAmountCents, l.AmountCents)).ToArray());

        var saved = await s.ReadAsync(request.Id);
        Assert.Equal(ServiceRequestStatus.PresoInCarico, saved.Status);
        Assert.Equal(Start, saved.ScheduledStartUtc);
        Assert.Equal(End, saved.ScheduledEndUtc);
        Assert.Equal(12000, saved.QuotedAmountCents);
        Assert.Equal(ServiceRequestPaymentMode.Manual, saved.PaymentMode);
        Assert.Equal(ServiceRequestScenario.SupplierUserId, saved.TakenByUserId);
        Assert.Null(saved.ResponseDueAt);
        Assert.Null(saved.QuoteSentAt);
        Assert.Null(saved.QuoteTotalCents);

        // The supplier is told, e-mail and push, with "Nome C." and never more; the customer gets the e-mail of a request taken, with the price.
        var toSupplier = Assert.Single(s.Emails.Snapshot(), e => e.To == SupplierEmail);
        Assert.Equal(EmailTemplates.Names.ServiceRequestQuoteAnswered, toSupplier.Template);
        Assert.Contains("Mario R.", toSupplier.Content.HtmlBody);
        Assert.DoesNotContain("Rossi", toSupplier.Content.HtmlBody);
        Assert.Equal(PushTypes.ServiceRequestQuoteAccepted, Assert.Single(s.Pushes).Payload.Type);
        var toCustomer = Assert.Single(s.Emails.Snapshot(), e => e.To == ShowcaseScenario.CustomerEmail);
        Assert.Equal(EmailTemplates.Names.SupplierBookingAccepted, toCustomer.Template);
        Assert.Contains("120,00 €", toCustomer.Content.HtmlBody);
        Assert.Equal(2, s.Emails.Snapshot().Count);

        // A second acceptance (a double click, a second tab) finds nothing to accept: one transition, nothing told twice.
        s.ForgetNotifications();
        var again = await Assert.ThrowsAsync<DomainRuleException>(() => s.Kit.Manager.AcceptOfferAsync(CredentialsOf(request), sentAt));
        Assert.Equal(ServiceRequestOfferErrors.NoOffer, again.Code);
        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);
        Assert.Equal(12000, (await s.ReadAsync(request.Id)).QuotedAmountCents);
    }

    [Fact]
    public async Task Accept_WhenTheTimeIsGone_Is409_NothingChanges_AndTheOfferStaysToBeTurnedDown()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var request = await s.InquiredAsync(supplier);
        await SendAsync(s, request.Id);
        var sentAt = (await s.ReadAsync(request.Id)).QuoteSentAt!.Value;

        // The supplier takes another job at that very time: the offer held no slot.
        await s.TakenAsync(Start);
        s.ForgetNotifications();

        var gone = await Assert.ThrowsAsync<DomainConflictException>(() => s.Kit.Manager.AcceptOfferAsync(CredentialsOf(request), sentAt));
        Assert.Equal(ServiceRequestErrorCodes.SlotUnavailable, gone.Code);

        var saved = await s.ReadAsync(request.Id);
        Assert.Equal(ServiceRequestStatus.Richiesto, saved.Status);
        Assert.Equal(sentAt, saved.QuoteSentAt);
        Assert.Equal(12000, saved.QuoteTotalCents);
        Assert.Null(saved.ScheduledStartUtc);
        Assert.Null(saved.QuotedAmountCents);
        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);

        // The customer still has the offer: it can turn it down and go on talking.
        var view = await s.Kit.Manager.LookupAsync(CredentialsOf(request));
        Assert.True(view.Actions.CanRespondToOffer);
        var declined = await s.Kit.Manager.DeclineOfferAsync(CredentialsOf(request), sentAt);
        Assert.Equal(ServiceRequestNegotiation.OfferDeclined, declined.Negotiation);
    }

    [Fact]
    public async Task Accept_AnOfferOfASuppliersThatIsNotActive_Or_AfterTheDeadline_IsRefused()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var request = await s.InquiredAsync(supplier);
        await SendAsync(s, request.Id);
        var sentAt = (await s.ReadAsync(request.Id)).QuoteSentAt!.Value;

        await s.SetSupplierStatusAsync(SupplierStatus.Suspended);
        var suspended = await Assert.ThrowsAsync<DomainRuleException>(() => s.Kit.Manager.AcceptOfferAsync(CredentialsOf(request), sentAt));
        Assert.Equal(ShowcaseBookingErrors.SupplierUnavailable, suspended.Code);
        Assert.False((await s.Kit.Manager.LookupAsync(CredentialsOf(request))).Actions.CanRespondToOffer);
        await s.SetSupplierStatusAsync(SupplierStatus.Active);

        s.Clock.Advance(TimeSpan.FromHours(24));
        var late = await Assert.ThrowsAsync<DomainRuleException>(() => s.Kit.Manager.AcceptOfferAsync(CredentialsOf(request), sentAt));
        Assert.Equal(ServiceRequestOfferErrors.OfferExpired, late.Code);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(request.Id)).Status);
    }

    // ─── Turning down, taking back, letting it lapse ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Decline_TheOfferGoes_TheConversationGoesOn_NoClockRunsOnTheSupplier_AndTheSupplierIsTold()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var request = await s.InquiredAsync(supplier);
        await SendAsync(s, request.Id);
        var sentAt = (await s.ReadAsync(request.Id)).QuoteSentAt!.Value;
        s.Clock.Advance(TimeSpan.FromHours(2));
        s.ForgetNotifications();

        var declined = await s.Kit.Manager.DeclineOfferAsync(CredentialsOf(request), sentAt);

        Assert.Equal(ServiceRequestStatus.Richiesto, declined.Status);
        Assert.Null(declined.Offer);
        Assert.Equal(ServiceRequestNegotiation.OfferDeclined, declined.Negotiation);
        Assert.False(declined.Actions.CanRespondToOffer);
        Assert.Null(declined.RespondBy);
        Assert.True(Assert.IsType<ServiceRequestThread>(declined.Thread).CanWrite);
        var saved = await s.ReadAsync(request.Id);
        Assert.Null(saved.ResponseDueAt);
        Assert.Null(saved.QuoteSentAt);

        var toSupplier = Assert.Single(s.Emails.Snapshot());
        Assert.Equal((SupplierEmail, EmailTemplates.Names.ServiceRequestQuoteAnswered), (toSupplier.To, toSupplier.Template));
        Assert.Equal(PushTypes.ServiceRequestQuoteRejected, Assert.Single(s.Pushes).Payload.Type);

        // Nobody closes the conversation behind the customer's back, however long it is quiet.
        s.Clock.Advance(TimeSpan.FromDays(10));
        var run = await s.ExpiryJob().RunAsync();
        Assert.Equal(0, run.RequestsCancelled);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(request.Id)).Status);

        // The supplier sees where it stands and can send another offer, which makes the negotiation an offer again.
        var thread = await s.Service.GetThreadAsSupplierAsync(request.Id, s.SupplierOrgId);
        Assert.Equal((ServiceRequestNegotiation.OfferDeclined, true), (thread.Negotiation, thread.CanSendOffer));
        Assert.Null(thread.Offer);
        await SendAsync(s, request.Id, TheOffer(start: ServiceRequestScenario.FridayAt14, end: ServiceRequestScenario.FridayAt14.AddHours(2)));
        Assert.Equal(ServiceRequestNegotiation.OfferSent, (await s.Service.GetThreadAsSupplierAsync(request.Id, s.SupplierOrgId)).Negotiation);
    }

    [Fact]
    public async Task Withdraw_TakesTheOfferBack_TheRequestStaysNewWithNoClock_AndThereIsNothingToWithdrawTwice()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var request = await s.InquiredAsync(supplier);
        await SendAsync(s, request.Id);
        var sentAt = (await s.ReadAsync(request.Id)).QuoteSentAt!.Value;

        var withdrawn = await s.Service.WithdrawOfferAsync(request.Id, s.SupplierOrgId);

        Assert.Equal(ServiceRequestStatus.Richiesto, withdrawn.Status);
        var saved = await s.ReadAsync(request.Id);
        Assert.Null(saved.QuoteSentAt);
        Assert.Null(saved.ResponseDueAt);
        var thread = await s.Service.GetThreadAsSupplierAsync(request.Id, s.SupplierOrgId);
        Assert.Equal((ServiceRequestNegotiation.InConversation, true), (thread.Negotiation, thread.CanSendOffer));

        var nothing = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.WithdrawOfferAsync(request.Id, s.SupplierOrgId));
        Assert.Equal(ServiceRequestOfferErrors.NoOfferToWithdraw, nothing.Code);

        // The customer who read it finds it gone.
        var gone = await Assert.ThrowsAsync<DomainRuleException>(() => s.Kit.Manager.AcceptOfferAsync(CredentialsOf(request), sentAt));
        Assert.Equal(ServiceRequestOfferErrors.NoOffer, gone.Code);
    }

    [Fact]
    public async Task TheCustomerWhoDoesNotAnswerInADay_ClosesTheRequest_AndBothAreToldTheTruth_ItIsTheCustomerWhoDidNotAnswer()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var request = await s.InquiredAsync(supplier);
        await SendAsync(s, request.Id);
        s.Clock.Advance(TimeSpan.FromHours(24));
        s.ForgetNotifications();

        var run = await s.ExpiryJob().RunAsync();

        Assert.Equal(1, run.RequestsCancelled);
        var saved = await s.ReadAsync(request.Id);
        Assert.Equal(ServiceRequestStatus.Annullato, saved.Status);
        Assert.Equal(ServiceRequestCancellationReasons.QuoteNotAnswered, saved.CancellationReason);
        Assert.Equal(ServiceRequestActorParty.System, saved.CancelledBy);
        Assert.Null(saved.QuoteSentAt);

        var view = await s.Kit.Manager.LookupAsync(CredentialsOf(request));
        Assert.Equal(ServiceRequestNegotiation.Expired, view.Negotiation);
        Assert.Null(view.Offer);

        // The supplier did answer: the customer is told its offer was not answered, never that the supplier was silent.
        var toCustomer = Assert.Single(s.Emails.Snapshot(), e => e.To == ShowcaseScenario.CustomerEmail);
        Assert.Equal(EmailTemplates.Names.SupplierInquiryOfferExpired, toCustomer.Template);
        var toSupplier = Assert.Single(s.Emails.Snapshot(), e => e.To == SupplierEmail);
        Assert.Equal(EmailTemplates.Names.SupplierBookingQuoteLapsed, toSupplier.Template);
        Assert.Equal(PushTypes.ServiceRequestQuoteExpired, Assert.Single(s.Pushes).Payload.Type);
        Assert.Empty(await s.ReliabilityEventsAsync());
    }

    // ─── What the customer is told when the supplier gives the job up ────────────────────────────────────────────────

    [Fact]
    public async Task TheSupplierWhoCancelsAJobItAgreed_TellsTheCustomer_AndOneWhoGivesUpBeforeAnyOfferDoesToo()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var agreed = await s.InquiredAsync(supplier, "chi.accetta@example.com");
        await SendAsync(s, agreed.Id);
        var sentAt = (await s.ReadAsync(agreed.Id)).QuoteSentAt!.Value;
        await s.Kit.Manager.AcceptOfferAsync(CredentialsOf(agreed, "chi.accetta@example.com"), sentAt);
        s.ForgetNotifications();

        await s.Service.CancelAsSupplierAsync(agreed.Id, s.SupplierOrgId, "Mi si è rotta l'auto");

        var cancelled = Assert.Single(s.Emails.Snapshot(), e => e.To == "chi.accetta@example.com");
        Assert.Equal(EmailTemplates.Names.SupplierBookingCancelled, cancelled.Template);

        var early = await s.InquiredAsync(supplier, "chi.aspetta@example.com");
        s.ForgetNotifications();

        await s.Service.CancelAsSupplierAsync(early.Id, s.SupplierOrgId, "Non posso occuparmene");

        var declined = Assert.Single(s.Emails.Snapshot(), e => e.To == "chi.aspetta@example.com");
        Assert.Equal(EmailTemplates.Names.SupplierInquiryDeclined, declined.Template);
    }

    // ─── Only a request on measure has an offer ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheQuoteEndpointsOfTheCustomer_DoNotAnswerTheOfferOfARequestOnMeasure()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var request = await s.InquiredAsync(supplier);
        await SendAsync(s, request.Id);
        var sentAt = (await s.ReadAsync(request.Id)).QuoteSentAt!.Value;

        var accept = await Assert.ThrowsAsync<DomainRuleException>(() => s.Kit.Manager.AcceptQuoteAsync(CredentialsOf(request), sentAt));
        var reject = await Assert.ThrowsAsync<DomainRuleException>(() => s.Kit.Manager.RejectQuoteAsync(CredentialsOf(request), sentAt));

        Assert.All(new[] { accept, reject }, ex => Assert.Equal(ShowcaseBookingQuoteErrors.NoQuote, ex.Code));
        var saved = await s.ReadAsync(request.Id);
        Assert.Equal(ServiceRequestStatus.Richiesto, saved.Status);
        Assert.Equal(sentAt, saved.QuoteSentAt);
    }

    [Fact]
    public async Task TheOffer_ReachesOnlyTheSupplierOfTheRequest_AndOnlyOnMeasure_InTheDatabaseToo()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var request = await s.InquiredAsync(supplier);
        await SendAsync(s, request.Id);

        var rows = await s.Db.ServiceRequests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.QuoteSentAt != null)
            .Select(r => new { r.Id, r.Kind, r.SupplierOrgId })
            .ToListAsync();

        var row = Assert.Single(rows);
        Assert.Equal((request.Id, ServiceRequestKind.Custom, s.SupplierOrgId), (row.Id, row.Kind, row.SupplierOrgId));
    }
}
