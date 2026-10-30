using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email.Templates;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-04b: the private customer of a supplier's public showcase answers the quote the supplier sent, with the code and the e-mail
/// address of its booking and the version of the quote it read. Accepting takes the request on the supplier's behalf at the total of
/// the quote (never paid online: a showcase customer has no account); the quote is about the version the customer read; the customer
/// has a day to answer, then the upkeep cancels the request with a reason of its own and both parties are told the truth — that it
/// is the customer who did not answer. The races with the supplier need PostgreSQL (<c>ShowcaseBookingManagementPostgresTests</c>).
/// </summary>
public class ShowcaseBookingManagerQuoteTests
{
    private const string SupplierEmail = "supplier@test.com";

    [Fact]
    public async Task Accept_TakesTheRequestAtTheTotal_ForTheVersionTheCustomerRead_AndTellsBothParties()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();
        s.Clock.Advance(TimeSpan.FromMinutes(20));
        await QuoteAsSupplierAsync(s, request.Id, 30000);
        var sentAt = (await s.ReadAsync(request.Id)).QuoteSentAt!.Value;

        // The customer reads the quote it was e-mailed: the lines, the total, the deadline; and it cannot move the time while it waits.
        var view = await s.Kit.Manager.LookupAsync(credentials);
        Assert.Equal(ServiceRequestStatus.Richiesto, view.Status);
        Assert.Equal(30000, view.Quote!.TotalCents);
        Assert.Equal(sentAt, view.Quote.SentAt);
        Assert.Equal(sentAt.AddHours(24), view.Quote.AnswerBy);
        Assert.Equal("Materiali compresi", view.Quote.Note);
        Assert.True(view.Actions.CanRespondToQuote);
        Assert.False(view.Actions.CanReschedule);
        Assert.Null(view.RespondBy);
        var received = Assert.Single(s.Emails.Snapshot(), e => e.Template == EmailTemplates.Names.ServiceRequestQuoteReceived);
        Assert.Equal(ShowcaseScenario.CustomerEmail, received.To);
        Assert.Contains("300,00 €", received.Content.HtmlBody);
        var cannotMove = await Assert.ThrowsAsync<DomainRuleException>(
            () => s.Kit.Manager.RescheduleAsync(credentials, ServiceRequestScenario.FridayAt14));
        Assert.Equal(ShowcaseBookingManagementErrors.CannotReschedule, cannotMove.Code);
        s.ForgetNotifications();

        // The answer is about the version the customer read: another one is a 409 and nothing changes.
        var stale = await Assert.ThrowsAsync<DomainConflictException>(
            () => s.Kit.Manager.AcceptQuoteAsync(credentials, sentAt.AddMinutes(-1)));
        Assert.Equal(ServiceRequestErrorCodes.QuoteChanged, stale.Code);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(request.Id)).Status);
        Assert.Empty(s.Emails.Snapshot());

        var accepted = await s.Kit.Manager.AcceptQuoteAsync(credentials, sentAt);

        Assert.Equal(ServiceRequestStatus.PresoInCarico, accepted.Status);
        Assert.Null(accepted.Quote);
        Assert.Equal(30000, accepted.Price.QuotedAmountCents);
        Assert.Equal(30000, accepted.Price.AmountCents);
        Assert.Equal("quote", accepted.Price.Basis);
        var line = Assert.Single(accepted.Price.Lines);
        Assert.Equal(("service", "Pulizia profonda", 30000), (line.Kind, line.Label, line.AmountCents));
        // Taken now: the exact address is there.
        Assert.Equal(ShowcaseScenario.Address, accepted.Place.Address);

        var saved = await s.ReadAsync(request.Id);
        Assert.Equal(ServiceRequestStatus.PresoInCarico, saved.Status);
        Assert.Equal(30000, saved.QuotedAmountCents);
        Assert.Equal(ServiceRequestPaymentMode.Manual, saved.PaymentMode);
        Assert.Equal(ServiceRequestScenario.SupplierUserId, saved.TakenByUserId);
        Assert.Null(saved.ResponseDueAt);
        Assert.Null(saved.QuoteSentAt);

        // The supplier is told, e-mail and push, with "Nome C." and never more; the customer gets the e-mail of a request taken.
        var toSupplier = Assert.Single(s.Emails.Snapshot(), e => e.To == SupplierEmail);
        Assert.Equal(EmailTemplates.Names.ServiceRequestQuoteAnswered, toSupplier.Template);
        Assert.Contains("Cliente: <strong>Mario R.</strong>", toSupplier.Content.HtmlBody);
        Assert.DoesNotContain("Rossi", toSupplier.Content.HtmlBody);
        var push = Assert.Single(s.Pushes);
        Assert.Equal(PushTypes.ServiceRequestQuoteAccepted, push.Payload.Type);
        var toCustomer = Assert.Single(s.Emails.Snapshot(), e => e.To == ShowcaseScenario.CustomerEmail);
        Assert.Equal(EmailTemplates.Names.SupplierBookingAccepted, toCustomer.Template);
        Assert.Contains("300,00 €", toCustomer.Content.HtmlBody);
        Assert.Equal(2, s.Emails.Snapshot().Count);
    }

    [Fact]
    public async Task Reject_TheQuoteGoes_TheRequestIsToAnswerAgainWithTheSuppliersWholeTime_AndTheSupplierIsTold()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();
        await QuoteAsSupplierAsync(s, request.Id);
        var sentAt = (await s.ReadAsync(request.Id)).QuoteSentAt!.Value;
        s.Clock.Advance(TimeSpan.FromHours(2));
        s.ForgetNotifications();

        var rejected = await s.Kit.Manager.RejectQuoteAsync(credentials, sentAt);

        Assert.Equal(ServiceRequestStatus.Richiesto, rejected.Status);
        Assert.Null(rejected.Quote);
        Assert.False(rejected.Actions.CanRespondToQuote);
        Assert.True(rejected.Actions.CanReschedule);
        // The deadline of the customer is gone; the supplier has its whole time to answer again.
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime.AddMinutes(180), rejected.RespondBy);
        var toSupplier = Assert.Single(s.Emails.Snapshot());
        Assert.Equal((SupplierEmail, EmailTemplates.Names.ServiceRequestQuoteAnswered), (toSupplier.To, toSupplier.Template));
        Assert.Equal(PushTypes.ServiceRequestQuoteRejected, Assert.Single(s.Pushes).Payload.Type);

        var again = await Assert.ThrowsAsync<DomainRuleException>(() => s.Kit.Manager.RejectQuoteAsync(credentials, sentAt));
        Assert.Equal(ShowcaseBookingQuoteErrors.NoQuote, again.Code);
    }

    [Fact]
    public async Task Accept_AfterTheDeadline_IsRefused_AndTheJobThenCancelsTheRequestForTheCustomersSilence()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();
        await QuoteAsSupplierAsync(s, request.Id);
        var sentAt = (await s.ReadAsync(request.Id)).QuoteSentAt!.Value;
        s.Clock.Advance(TimeSpan.FromHours(24));
        s.ForgetNotifications();

        var late = await Assert.ThrowsAsync<DomainRuleException>(() => s.Kit.Manager.AcceptQuoteAsync(credentials, sentAt));
        Assert.Equal(ShowcaseBookingQuoteErrors.QuoteExpired, late.Code);
        Assert.False((await s.Kit.Manager.LookupAsync(credentials)).Actions.CanRespondToQuote);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(request.Id)).Status);

        // The upkeep does what the page says it will.
        var run = await s.ExpiryJob().RunAsync();

        Assert.Equal(1, run.RequestsCancelled);
        var saved = await s.ReadAsync(request.Id);
        Assert.Equal(ServiceRequestStatus.Annullato, saved.Status);
        Assert.Equal(ServiceRequestCancellationReasons.QuoteNotAnswered, saved.CancellationReason);
        Assert.Equal(ServiceRequestActorParty.System, saved.CancelledBy);
        Assert.Null(saved.QuoteSentAt);
        var view = await s.Kit.Manager.LookupAsync(credentials);
        Assert.Equal(ServiceRequestStatus.Annullato, view.Status);
        Assert.Null(view.Cancellation!.Reason);

        // The supplier did answer: it is not told it did not, and the customer is told it is the one who did not.
        var toCustomer = Assert.Single(s.Emails.Snapshot(), e => e.To == ShowcaseScenario.CustomerEmail);
        Assert.Equal(EmailTemplates.Names.SupplierBookingQuoteExpired, toCustomer.Template);
        var toSupplier = Assert.Single(s.Emails.Snapshot(), e => e.To == SupplierEmail);
        Assert.Equal(EmailTemplates.Names.SupplierBookingQuoteLapsed, toSupplier.Template);
        var push = Assert.Single(s.Pushes);
        Assert.Equal(PushTypes.ServiceRequestQuoteExpired, push.Payload.Type);
        Assert.Equal(PushDeliveryKeys.ServiceRequestStatus(request.Id, ServiceRequestStatus.Annullato), push.DeliveryKey);
    }

    /// <summary>The supplier answers a booking with a quote of one line, as its console does.</summary>
    private static Task<ServiceRequest> QuoteAsSupplierAsync(ServiceRequestScenario s, Guid requestId, int amountCents = 30000) =>
        s.Service.SendQuoteAsync(
            requestId,
            s.SupplierOrgId,
            ServiceRequestScenario.SupplierUserId,
            new SendServiceRequestQuoteCommand([new ServiceRequestQuoteLine("Pulizia profonda", amountCents)], "Materiali compresi"));
}
