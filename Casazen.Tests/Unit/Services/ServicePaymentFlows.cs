using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Tests.Unit.Services;

/// <summary>A request paid inside CasaZen whose payer opened the payment session: the PaymentIntent exists on the fake Stripe.</summary>
internal sealed record OpenedPayment(ServiceRequest Request, ServiceRequestPayment Payment, string Token, ServiceChargeIntent Intent)
{
    public Guid PaymentId => Payment.Id;

    public string IntentId => Intent.Id;
}

/// <summary>The flows of the payments that the SP-15b tests start from, and the events Stripe sends about them.</summary>
internal static class ServicePaymentFlows
{
    public const string Succeeded = "payment_intent.succeeded";
    public const string Processing = "payment_intent.processing";
    public const string PaymentFailed = "payment_intent.payment_failed";
    public const string Canceled = "payment_intent.canceled";

    /// <summary>The raw token of the last link emailed for the payment (request, reminder or new link after a failure).</summary>
    public static string TokenOf(this ServiceRequestScenario scenario, Guid paymentId) =>
        scenario.Emails.PaymentEmails()
            .Where(e => e.Template is "service-payment-request" or "service-payment-reminder" or "service-payment-failed")
            .Select(e => ServicePaymentTestSupport.LinkOf(e.Content))
            .Last(link => link.PaymentId == paymentId)
            .Token;

    /// <summary>
    /// A request completed online (the payments flag on, the supplier connected, 60 euro, the commission 10 %), with its payment and
    /// the link emailed, whose payer opened the payment session: the payment has its PaymentIntent and the supplier's account.
    /// </summary>
    public static async Task<OpenedPayment> OpenedAsync(this ServiceRequestScenario scenario, int amountCents = ServiceRequestScenario.ServicePriceCents)
    {
        var request = await scenario.CompletedOnlineAsync(amountCents);
        var payment = await scenario.OnlyPaymentOfAsync(request.Id);
        var token = scenario.TokenOf(payment.Id);
        await scenario.Payments.CreatePublicSessionAsync(payment.Id, token);

        payment = await scenario.OnlyPaymentOfAsync(request.Id);
        return new OpenedPayment(request, payment, token, scenario.Gateway.Intent(payment.StripePaymentIntentId!));
    }

    /// <summary>
    /// The event Stripe sends about the PaymentIntent of <paramref name="opened"/>, as the webhook hands it over: the account, the
    /// amounts, the currency and the commission of the PaymentIntent, and <paramref name="change"/> to make any of them differ.
    /// </summary>
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

    /// <summary>The payment as saved.</summary>
    public static async Task<ServiceRequestPayment> ReloadAsync(this ServiceRequestScenario scenario, Guid paymentId) =>
        await scenario.Db.ServiceRequestPayments.AsNoTracking().SingleAsync(p => p.Id == paymentId);

    /// <summary>The refunds of a payment as saved, in the order they were recorded.</summary>
    public static async Task<List<ServiceRequestPaymentRefund>> RefundsOfAsync(this ServiceRequestScenario scenario, Guid paymentId) =>
        await scenario.Db.ServiceRequestPaymentRefunds.AsNoTracking().Where(r => r.ServiceRequestPaymentId == paymentId).OrderBy(r => r.Sequence).ToListAsync();

    /// <summary>Applies a succeeded PaymentIntent the way the webhook does and sends what follows the commit.</summary>
    public static async Task<IReadOnlyList<ServicePaymentNotice>> PayAsync(this ServiceRequestScenario scenario, OpenedPayment opened)
    {
        scenario.Gateway.SetStatus(opened.IntentId, "succeeded");
        var notices = await scenario.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(Succeeded));
        await scenario.Payments.CompleteAsync(notices);
        return notices;
    }

    /// <summary>The emails of the payments queued so far to <paramref name="to"/>, of one template.</summary>
    public static List<(string? To, Casazen.Infrastructure.Email.EmailContent Content, string Template)> EmailsOf(
        this ServiceRequestScenario scenario, string template, string? to = null) =>
        scenario.Emails.Snapshot().Where(e => e.Template == template && (to is null || e.To == to)).ToList();
}
