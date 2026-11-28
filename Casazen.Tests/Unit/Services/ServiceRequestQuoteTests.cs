using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-04b: the supplier answers a new request with a quote that the host accepts or turns down. The rules that cost money or give a
/// permission, one proof each: who may send and who may answer; the total becomes the price agreed, and the payment is made of it with the
/// commission; an answer is about the quote the customer read (a quote replaced meanwhile is a 409 and nothing changes); a time that is
/// gone keeps the quote; a request is taken once; a host's quote has no deadline. The customer of a showcase request answers through
/// <c>ShowcaseBookingManagerQuoteTests</c>. The races (two acceptances at once) need PostgreSQL: the xmin check is the one of every
/// transition (<c>ServiceRequestLifecyclePostgresTests</c>).
/// </summary>
public class ServiceRequestQuoteTests
{
    private const string SupplierEmail = "supplier@test.com";

    [Fact]
    public async Task Send_OnlyTheActiveSupplierOfThatRequestMayQuote_AndOnlyWhileItIsNew()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        var otherSupplier = await s.AddOtherSupplierAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => SendAsync(s, request.Id, orgId: otherSupplier));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => SendAsync(s, request.Id, orgId: s.HostOrgId));
        Assert.Null((await s.ReadAsync(request.Id)).QuoteSentAt);

        await s.SetSupplierStatusAsync(SupplierStatus.Suspended);
        var suspended = await Assert.ThrowsAsync<DomainRuleException>(() => SendAsync(s, request.Id));
        Assert.Equal(ServiceRequestErrorCodes.SupplierNotActive, suspended.Code);
        await s.SetSupplierStatusAsync(SupplierStatus.Active);

        var taken = await s.TakenAsync(ServiceRequestScenario.FridayAt14);
        var late = await Assert.ThrowsAsync<DomainRuleException>(() => SendAsync(s, taken.Id));
        Assert.Equal(ServiceRequestErrorCodes.InvalidTransition, late.Code);
        Assert.Null((await s.ReadAsync(taken.Id)).QuoteSentAt);
    }

    [Fact]
    public async Task Answer_OnlyTheHostOrgOfTheRequestMayAcceptOrReject_NotAStrangerNorTheSupplier()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        await SendAsync(s, request.Id);
        var sentAt = (await s.ReadAsync(request.Id)).QuoteSentAt!.Value;

        foreach (var notTheHost in new[] { Guid.NewGuid(), s.SupplierOrgId })
        {
            var accept = await Assert.ThrowsAsync<NotFoundException>(() => s.Service.AcceptQuoteAsync(request.Id, notTheHost, sentAt));
            var reject = await Assert.ThrowsAsync<NotFoundException>(() => s.Service.RejectQuoteAsync(request.Id, notTheHost, sentAt));
            Assert.Equal(ServiceRequestErrorCodes.NotFound, accept.Code);
            Assert.Equal(ServiceRequestErrorCodes.NotFound, reject.Code);
        }

        var saved = await s.ReadAsync(request.Id);
        Assert.Equal(ServiceRequestStatus.Richiesto, saved.Status);
        Assert.Equal(sentAt, saved.QuoteSentAt);
        Assert.Null(saved.QuotedAmountCents);
    }

    [Fact]
    public async Task Accept_TheTotalIsThePriceAgreed_AndTheCompletionIsPaidWithTheCommissionOnIt()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnablePaymentsAsync();
        var request = await s.RequestAsync();
        s.Clock.Advance(TimeSpan.FromMinutes(5));
        await SendAsync(s, request.Id, ("Pulizia profonda", 20000), ("Materiali", 7500), ("Trasferta", 2500));
        var sentAt = (await s.ReadAsync(request.Id)).QuoteSentAt!.Value;
        s.ForgetNotifications();

        var taken = await s.Service.AcceptQuoteAsync(request.Id, s.HostOrgId, sentAt);

        Assert.Equal(ServiceRequestStatus.PresoInCarico, taken.Status);
        var saved = await s.ReadAsync(request.Id);
        Assert.Equal(30000, saved.QuotedAmountCents);
        Assert.Equal(ServiceRequestPaymentMode.Online, saved.PaymentMode);
        Assert.Equal(ServiceRequestScenario.SupplierUserId, saved.TakenByUserId);
        Assert.Equal(ServiceRequestScenario.FridayAt10, saved.ScheduledStartUtc);
        Assert.Null(saved.ResponseDueAt);
        var lines = ServiceRequestJson.ReadPriceLines(saved.PriceLinesJson);
        Assert.Equal([20000, 7500, 2500], lines.Select(line => line.AmountCents));
        Assert.All(lines, line => Assert.Equal(ServiceRequestPriceLineKinds.Quote, line.Kind));
        Assert.Null(saved.QuoteSentAt);
        Assert.Null(saved.QuoteTotalCents);
        Assert.Equal("[]", saved.QuoteLinesJson);
        Assert.Equal(
            [ServiceRequestQuoteEventKinds.Sent, ServiceRequestQuoteEventKinds.Accepted],
            ServiceRequestJson.ReadQuoteEvents(saved.QuoteEventsJson).Select(e => e.Kind));

        var email = Assert.Single(s.Emails.Snapshot());
        Assert.Equal(SupplierEmail, email.To);
        Assert.Equal(EmailTemplates.Names.ServiceRequestQuoteAnswered, email.Template);
        var push = Assert.Single(s.Pushes);
        Assert.Equal(PushTypes.ServiceRequestQuoteAccepted, push.Payload.Type);
        Assert.Equal(PushAudience.SupplierOrg(s.SupplierOrgId), push.Audience);

        var completed = await s.Service.CompleteAsync(request.Id, s.SupplierOrgId);
        Assert.Equal(30000, completed.FinalAmountCents);
        Assert.False(completed.FinalAmountNeedsConfirmation);
        Assert.Equal(3, ServiceRequestJson.ReadPriceLines(completed.PriceLinesJson).Count);
        var payment = Assert.Single(await s.PaymentsOfAsync(request.Id));
        Assert.Equal(
            (30000, 10m, 3000, 27000),
            (payment.AmountCents, payment.CommissionPercent, payment.ApplicationFeeCents, payment.NetCents));
    }

    [Fact]
    public async Task Complete_AboveTheQuoteByMoreThanTwentyPercent_TheCustomerHasToConfirm()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        await SendAsync(s, request.Id, ("Pulizia", 30000));
        var sentAt = (await s.ReadAsync(request.Id)).QuoteSentAt!.Value;
        await s.Service.AcceptQuoteAsync(request.Id, s.HostOrgId, sentAt);

        var completed = await s.Service.CompleteAsync(request.Id, s.SupplierOrgId, new CompleteServiceRequestCommand(FinalAmountCents: 36001));

        Assert.True(completed.FinalAmountNeedsConfirmation);
    }

    [Fact]
    public async Task Accept_AQuoteTheSupplierReplacedMeanwhile_Is409AndNothingChanges_ThenTheNewOneIsAccepted()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        await SendAsync(s, request.Id, ("Pulizia", 10000));
        var first = (await s.ReadAsync(request.Id)).QuoteSentAt!.Value;
        s.Clock.Advance(TimeSpan.FromMinutes(10));
        await SendAsync(s, request.Id, ("Pulizia", 25000));
        var second = (await s.ReadAsync(request.Id)).QuoteSentAt!.Value;
        Assert.NotEqual(first, second);
        s.ForgetNotifications();

        var accept = await Assert.ThrowsAsync<DomainConflictException>(() => s.Service.AcceptQuoteAsync(request.Id, s.HostOrgId, first));
        var reject = await Assert.ThrowsAsync<DomainConflictException>(() => s.Service.RejectQuoteAsync(request.Id, s.HostOrgId, first));

        Assert.Equal(ServiceRequestErrorCodes.QuoteChanged, accept.Code);
        Assert.Equal(ServiceRequestErrorCodes.QuoteChanged, reject.Code);
        var saved = await s.ReadAsync(request.Id);
        Assert.Equal(ServiceRequestStatus.Richiesto, saved.Status);
        Assert.Equal(25000, saved.QuoteTotalCents);
        Assert.Null(saved.QuotedAmountCents);
        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);

        await s.Service.AcceptQuoteAsync(request.Id, s.HostOrgId, second);
        Assert.Equal(25000, (await s.ReadAsync(request.Id)).QuotedAmountCents);
    }

    [Fact]
    public async Task Accept_TheOtherTimeOfTheQuoteGoneMeanwhile_Is409_TheQuoteStays_AndTheHostCanTurnItDown()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        await SendAsync(s, request.Id, otherStart: ServiceRequestScenario.FridayAt14);
        var sentAt = (await s.ReadAsync(request.Id)).QuoteSentAt!.Value;
        await s.TakenAsync(ServiceRequestScenario.FridayAt14);
        s.ForgetNotifications();

        var refused = await Assert.ThrowsAsync<DomainConflictException>(() => s.Service.AcceptQuoteAsync(request.Id, s.HostOrgId, sentAt));

        Assert.Equal(ServiceRequestErrorCodes.SlotUnavailable, refused.Code);
        var saved = await s.ReadAsync(request.Id);
        Assert.Equal(ServiceRequestStatus.Richiesto, saved.Status);
        Assert.Equal(sentAt, saved.QuoteSentAt);
        Assert.Equal(ServiceRequestScenario.FridayAt10, saved.ScheduledStartUtc);
        Assert.Empty(s.Emails.Snapshot());

        var rejected = await s.Service.RejectQuoteAsync(request.Id, s.HostOrgId, sentAt);
        Assert.Null(rejected.QuoteSentAt);
    }

    [Fact]
    public async Task Accept_ARequestAlreadyTaken_NeverTakesItAgain_AndNoSecondQuoteCanBeSent()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        await SendAsync(s, request.Id, ("Pulizia", 30000));
        var sentAt = (await s.ReadAsync(request.Id)).QuoteSentAt!.Value;
        await s.Service.AcceptQuoteAsync(request.Id, s.HostOrgId, sentAt);
        var takenAt = (await s.ReadAsync(request.Id)).TakenAt;
        s.ForgetNotifications();

        var again = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.AcceptQuoteAsync(request.Id, s.HostOrgId, sentAt));
        var another = await Assert.ThrowsAsync<DomainRuleException>(() => SendAsync(s, request.Id, ("Pulizia", 1000)));

        Assert.Equal(ServiceRequestErrorCodes.NoQuote, again.Code);
        Assert.Equal(ServiceRequestErrorCodes.InvalidTransition, another.Code);
        var saved = await s.ReadAsync(request.Id);
        Assert.Equal(30000, saved.QuotedAmountCents);
        Assert.Equal(takenAt, saved.TakenAt);
        Assert.Null(saved.QuoteSentAt);
        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);
    }

    [Fact]
    public async Task Reject_TheRequestIsToAnswerAgain_TheSupplierIsToldTheStoryIsKept_AndTheOldAnswerFindsNoQuote()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        await SendAsync(s, request.Id, ("Pulizia", 30000));
        var sentAt = (await s.ReadAsync(request.Id)).QuoteSentAt!.Value;
        var pending = await s.Reader.GetAsync(request.Id, s.SupplierOrgId);
        Assert.Equal(SupplierRequestPhases.QuotePending, pending!.Phase);
        Assert.Equal(30000, pending.Quote!.TotalCents);
        s.Clock.Advance(TimeSpan.FromMinutes(30));
        s.ForgetNotifications();

        await s.Service.RejectQuoteAsync(request.Id, s.HostOrgId, sentAt);

        var saved = await s.ReadAsync(request.Id);
        Assert.Equal(ServiceRequestStatus.Richiesto, saved.Status);
        Assert.Null(saved.QuoteSentAt);
        Assert.Null(saved.QuotedAmountCents);
        var email = Assert.Single(s.Emails.Snapshot());
        Assert.Equal(EmailTemplates.Names.ServiceRequestQuoteAnswered, email.Template);
        Assert.Equal(PushTypes.ServiceRequestQuoteRejected, Assert.Single(s.Pushes).Payload.Type);

        var view = await s.Reader.GetAsync(request.Id, s.SupplierOrgId);
        Assert.Equal(SupplierRequestPhases.ToAnswer, view!.Phase);
        Assert.Null(view.Quote);
        Assert.Equal(
            [null, ServiceRequestHistoryEvents.QuoteSent, ServiceRequestHistoryEvents.QuoteRejected],
            view.History!.Select(step => step.Event));

        var late = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.AcceptQuoteAsync(request.Id, s.HostOrgId, sentAt));
        Assert.Equal(ServiceRequestErrorCodes.NoQuote, late.Code);
    }

    [Fact]
    public async Task AutoCancel_AHostsQuoteHasNoDeadline_ButARequestWithoutOneIsStillCancelled()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var quoted = await s.RequestAsync();
        var silent = await s.RequestAsync(ServiceRequestScenario.FridayAt14);
        await SendAsync(s, quoted.Id);
        s.Clock.Advance(TimeSpan.FromDays(3));
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.SupplierRequestAutoCancel)).Returns(true);

        var run = await new ServiceRequestAutoCancelService(s.Db, s.Notifier, flags.Object, NullLogger<ServiceRequestAutoCancelService>.Instance, s.Clock)
            .CancelUnansweredAsync();

        Assert.Equal(1, run.Cancelled);
        Assert.Equal(ServiceRequestStatus.Annullato, (await s.ReadAsync(silent.Id)).Status);
        var saved = await s.ReadAsync(quoted.Id);
        Assert.Equal(ServiceRequestStatus.Richiesto, saved.Status);
        Assert.NotNull(saved.QuoteSentAt);
    }

    private static Task<ServiceRequest> SendAsync(
        ServiceRequestScenario s,
        Guid requestId,
        params (string Label, int AmountCents)[] lines) =>
        SendAsync(s, requestId, orgId: null, otherStart: null, lines);

    private static Task<ServiceRequest> SendAsync(ServiceRequestScenario s, Guid requestId, Guid orgId) =>
        SendAsync(s, requestId, orgId, otherStart: null, []);

    private static Task<ServiceRequest> SendAsync(ServiceRequestScenario s, Guid requestId, DateTime otherStart) =>
        SendAsync(s, requestId, orgId: null, otherStart, []);

    private static Task<ServiceRequest> SendAsync(
        ServiceRequestScenario s,
        Guid requestId,
        Guid? orgId,
        DateTime? otherStart,
        (string Label, int AmountCents)[] lines)
    {
        var quoteLines = lines.Length == 0
            ? new List<ServiceRequestQuoteLine> { new("Pulizia profonda", 30000) }
            : lines.Select(line => new ServiceRequestQuoteLine(line.Label, line.AmountCents)).ToList();
        return s.Service.SendQuoteAsync(
            requestId,
            orgId ?? s.SupplierOrgId,
            ServiceRequestScenario.SupplierUserId,
            new SendServiceRequestQuoteCommand(quoteLines, "Materiali compresi", otherStart));
    }
}
