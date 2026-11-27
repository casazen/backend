using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Tests.Unit.Services;

internal sealed record OpenedPayment(ServiceRequest Request, ServiceRequestPayment Payment, string Token, ServiceChargeIntent Intent)
{
    public Guid PaymentId => Payment.Id;

    public string IntentId => Intent.Id;
}

internal static class ServicePaymentFlows
{
    public const string Succeeded = "payment_intent.succeeded";
    public const string Processing = "payment_intent.processing";
    public const string PaymentFailed = "payment_intent.payment_failed";
    public const string Canceled = "payment_intent.canceled";

    public static string TokenOf(this ServiceRequestScenario scenario, Guid paymentId) =>
        scenario.Emails.PaymentEmails()
            .Where(e => e.Template is "service-payment-request" or "service-payment-reminder" or "service-payment-failed")
            .Select(e => ServicePaymentTestSupport.LinkOf(e.Content))
            .Last(link => link.PaymentId == paymentId)
            .Token;

    public static async Task<OpenedPayment> OpenedAsync(this ServiceRequestScenario scenario, int amountCents = ServiceRequestScenario.ServicePriceCents)
    {
        var request = await scenario.CompletedOnlineAsync(amountCents);
        var payment = await scenario.OnlyPaymentOfAsync(request.Id);
        var token = scenario.TokenOf(payment.Id);
        await scenario.Payments.CreatePublicSessionAsync(payment.Id, token);

        payment = await scenario.OnlyPaymentOfAsync(request.Id);
        return new OpenedPayment(request, payment, token, scenario.Gateway.Intent(payment.StripePaymentIntentId!));
    }

    public static ServicePaymentIntentEvent EventOf(
        this OpenedPayment opened,
        string type,
        Func<ServicePaymentIntentEvent, ServicePaymentIntentEvent>? change = null,
        DateTime? at = null)
    {
        var intent = opened.Intent;
        var paymentEvent = new ServicePaymentIntentEvent(
            $"evt_{Guid.NewGuid():N}",
            type,
            intent.Id,
            intent.ConnectedAccountId,
            opened.PaymentId,
            intent.AmountCents,
            intent.AmountReceivedCents ?? intent.AmountCents,
            intent.Currency,
            intent.ApplicationFeeCents,
            type == PaymentFailed ? "card_declined" : null,
            at);
        return change is null ? paymentEvent : change(paymentEvent);
    }

    public static async Task<ServiceRequestPayment> ReloadAsync(this ServiceRequestScenario scenario, Guid paymentId) =>
        await scenario.Db.ServiceRequestPayments.AsNoTracking().SingleAsync(p => p.Id == paymentId);

    public static async Task<List<ServiceRequestPaymentRefund>> RefundsOfAsync(this ServiceRequestScenario scenario, Guid paymentId) =>
        await scenario.Db.ServiceRequestPaymentRefunds.AsNoTracking().Where(r => r.ServiceRequestPaymentId == paymentId).OrderBy(r => r.Sequence).ToListAsync();

    public static async Task<IReadOnlyList<ServicePaymentNotice>> PayAsync(this ServiceRequestScenario scenario, OpenedPayment opened)
    {
        scenario.Gateway.SetStatus(opened.IntentId, "succeeded");
        var notices = await scenario.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(Succeeded));
        await scenario.Payments.CompleteAsync(notices);
        return notices;
    }

    public static List<(string? To, Casazen.Infrastructure.Email.EmailContent Content, string Template)> EmailsOf(
        this ServiceRequestScenario scenario, string template, string? to = null) =>
        scenario.Emails.Snapshot().Where(e => e.Template == template && (to is null || e.To == to)).ToList();
}
