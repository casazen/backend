using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Stripe;

namespace Casazen.Infrastructure.GuestPortal;

public sealed partial class GuestSupplierCheckout
{
    public const string ConsentVersion = "guest-service-order-off-session-v1";
    private SetupIntentService Setups => new(client ??= new StripeClient(configuration[$"{Section}:SecretKey"]));
    private CustomerService Customers => new(client ??= new StripeClient(configuration[$"{Section}:SecretKey"]));

    public static DateTime? ExpectedConfirmation(DateTime orderedAt, DateTime serviceAt, int? responseMinutes)
    {
        if (responseMinutes is not > 0) return null;
        var expected = orderedAt.AddMinutes(responseMinutes.Value);
        return expected < serviceAt ? expected : serviceAt;
    }

    // D35: seven days to supplier confirmation. Without a supplier response commitment, only a service
    // occurring within the existing conservative two-day window provides a reliable upper bound.
    public static bool NeedsSavedCard(DateTime orderedAt, DateTime serviceAt, DateTime? confirmationAt) =>
        confirmationAt is { } deadline ? deadline > orderedAt.AddDays(7) : serviceAt > orderedAt + Horizon;

    private Task<PaymentIntent> ReadIntentAsync(string id, CancellationToken ct) =>
        Intents.GetAsync(id, new PaymentIntentGetOptions { Expand = ["latest_charge"] }, cancellationToken: ct);

    private static void ReadCaptureDeadline(GuestSupplierOrder order, PaymentIntent intent)
    {
        if (intent.LatestCharge?.PaymentMethodDetails?.Card?.CaptureBefore is { } deadline)
            order.StripeCaptureBefore = deadline;
    }

    private static void RecoverExpiredHold(GuestSupplierOrder order)
    {
        order.StripeExpiredHoldIntentId = order.StripePaymentIntentId;
        order.StripePaymentIntentId = null;
        order.StripeDeferred = true;
        order.State = GuestSupplierOrderState.Quoted;
    }

    private static Dictionary<string, string> Metadata(GuestSupplierOrder order) =>
        new() { ["kind"] = Kind, ["orderId"] = order.Id.ToString("D") };

    private static bool MatchesSetup(GuestSupplierOrder order, SetupIntent setup) =>
        !setup.Livemode && !string.IsNullOrEmpty(setup.Id) &&
        (order.StripeSetupIntentId is null || setup.Id == order.StripeSetupIntentId) &&
        !string.IsNullOrEmpty(order.StripeCustomerId) && setup.CustomerId == order.StripeCustomerId &&
        setup.Usage == "off_session" && setup.Metadata?.GetValueOrDefault("kind") == Kind &&
        setup.Metadata.GetValueOrDefault("orderId") == order.Id.ToString("D");

    private async Task<Session?> StartDeferredAsync(GuestSupplierOrder order, int total, string? consentVersion, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(order, ct);
        if (!order.StripeDeferred || order.RequestedFor <= Now || total < MinTotalCents ||
            total != order.SupplierPriceCents + order.CommissionCents) return null;

        var key = configuration[$"{Section}:PublishableKey"]!;
        Session? session = null;
        if (order.State == GuestSupplierOrderState.Quoted)
        {
            if (consentVersion != ConsentVersion) return null;
            order.StripeSetupConsentAt ??= Now;
            order.StripeSetupConsentVersion = consentVersion;
            if (order.StripeCustomerId is null)
            {
                var customer = await Customers.CreateAsync(new CustomerCreateOptions { Metadata = Metadata(order) }, Key(order, "customer"), ct);
                if (customer.Livemode || string.IsNullOrEmpty(customer.Id) ||
                    customer.Metadata?.GetValueOrDefault("orderId") != order.Id.ToString("D")) return null;
                order.StripeCustomerId = customer.Id;
            }
            var setup = order.StripeSetupIntentId is null
                ? await Setups.CreateAsync(new SetupIntentCreateOptions
                {
                    Customer = order.StripeCustomerId,
                    Usage = "off_session",
                    PaymentMethodTypes = ["card"],
                    Metadata = Metadata(order)
                }, Key(order, "setup"), ct)
                : await Setups.GetAsync(order.StripeSetupIntentId, cancellationToken: ct);
            if (!MatchesSetup(order, setup)) return null;
            order.StripeSetupIntentId = setup.Id;
            if (setup.Status == "succeeded") order.State = GuestSupplierOrderState.CardSaved;
            else if (setup.Status is "requires_payment_method" or "requires_confirmation" or "requires_action")
                session = new Session(setup.ClientSecret, key, "setup");
        }
        else if (order.State is GuestSupplierOrderState.PaymentActionRequired or GuestSupplierOrderState.PaymentFailed &&
            order.StripePaymentIntentId is { } id)
        {
            var intent = await ReadIntentAsync(id, ct);
            if (!Matches(order, intent)) return null;
            ApplyDeferred(order, intent);
            if (intent.Status is "requires_payment_method" or "requires_confirmation" or "requires_action")
                session = new Session(intent.ClientSecret, key, intent.Status == "requires_action" ? "next-action" : "payment");
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return session;
    }

    // The caller commits the prepared, unconfirmed intent before performing the first off-session confirmation.
    private async Task<bool> PrepareSupplierDecisionAsync(GuestSupplierOrder order, string action, CancellationToken ct)
    {
        if (action == "decline")
        {
            if (order.State == GuestSupplierOrderState.Declined) return true;
            if (order.State is not (GuestSupplierOrderState.Quoted or GuestSupplierOrderState.CardSaved
                or GuestSupplierOrderState.PaymentFailed or GuestSupplierOrderState.PaymentActionRequired or GuestSupplierOrderState.PaymentProcessing)) return false;
            if (order.StripePaymentIntentId is { } id)
            {
                var intent = await ReadIntentAsync(id, ct);
                if (!Matches(order, intent) || intent.Status == "succeeded") return false;
                if (intent.Status != "canceled") intent = await ChangeAsync(order, capture: false, ct);
                if (!Matches(order, intent) || intent.Status != "canceled") return false;
            }
            else if (order.StripeSetupIntentId is { } setupId)
            {
                var setup = await Setups.GetAsync(setupId, cancellationToken: ct);
                if (!MatchesSetup(order, setup)) return false;
                if (setup.Status is not ("canceled" or "succeeded"))
                    await Setups.CancelAsync(setupId, null, Key(order, "cancel-setup"), ct);
            }
            order.State = GuestSupplierOrderState.Declined;
            return true;
        }
        if (order.State is GuestSupplierOrderState.Accepted or GuestSupplierOrderState.PaymentProcessing
            or GuestSupplierOrderState.PaymentFailed or GuestSupplierOrderState.PaymentActionRequired) return true;
        if (order.State != GuestSupplierOrderState.CardSaved || order.RequestedFor <= Now ||
            order.StripeSetupConsentAt is null || order.StripeSetupConsentVersion != ConsentVersion ||
            order.StripeSetupIntentId is null) return false;
        var saved = await Setups.GetAsync(order.StripeSetupIntentId, cancellationToken: ct);
        if (!MatchesSetup(order, saved) || saved.Status != "succeeded" || string.IsNullOrEmpty(saved.PaymentMethodId)) return false;
        var charge = await Intents.CreateAsync(new PaymentIntentCreateOptions
        {
            Amount = order.SupplierPriceCents + order.CommissionCents,
            Currency = "eur",
            Customer = order.StripeCustomerId,
            PaymentMethod = saved.PaymentMethodId,
            PaymentMethodTypes = ["card"],
            CaptureMethod = "automatic",
            Metadata = Metadata(order)
        }, Key(order, "deferred-create"), ct);
        if (!Matches(order, charge) || charge.Status != "requires_confirmation") return false;
        order.StripePaymentIntentId = charge.Id;
        order.State = GuestSupplierOrderState.PaymentProcessing;
        return true;
    }

    private async Task<bool> RefreshDeferredAsync(GuestSupplierOrder order, CancellationToken ct)
    {
        if (order.StripePaymentIntentId is null)
        {
            if (order.StripeSetupIntentId is null || order.StripeSetupConsentVersion != ConsentVersion) return false;
            var setup = await Setups.GetAsync(order.StripeSetupIntentId, cancellationToken: ct);
            if (!MatchesSetup(order, setup)) return false;
            if (order.State == GuestSupplierOrderState.Quoted && setup.Status == "succeeded" && !string.IsNullOrEmpty(setup.PaymentMethodId))
                order.State = GuestSupplierOrderState.CardSaved;
            return true;
        }
        var intent = await ReadIntentAsync(order.StripePaymentIntentId, ct);
        if (!Matches(order, intent)) return false;
        if (intent.Status == "requires_confirmation" && order.State == GuestSupplierOrderState.PaymentProcessing)
        {
            if (order.RequestedFor <= Now)
                intent = await ChangeAsync(order, capture: false, ct);
            else
            {
                try
                {
                    intent = await Intents.ConfirmAsync(intent.Id, new PaymentIntentConfirmOptions
                    {
                        OffSession = true,
                        UseStripeSdk = true
                    }, Key(order, "deferred-confirm"), ct);
                }
                catch (StripeException ex) when (ex.StripeError?.Type == "card_error")
                {
                    intent = await ReadIntentAsync(order.StripePaymentIntentId, ct);
                }
            }
            if (!Matches(order, intent)) return false;
        }
        ApplyDeferred(order, intent);
        return true;
    }

    private static void ApplyDeferred(GuestSupplierOrder order, PaymentIntent intent)
    {
        if (order.State is not (GuestSupplierOrderState.PaymentProcessing or GuestSupplierOrderState.PaymentFailed
            or GuestSupplierOrderState.PaymentActionRequired)) return;
        order.State = intent.Status switch
        {
            "succeeded" when intent.AmountReceived == intent.Amount => GuestSupplierOrderState.Accepted,
            "requires_action" => GuestSupplierOrderState.PaymentActionRequired,
            "requires_payment_method" when intent.LastPaymentError?.Code == "authentication_required" => GuestSupplierOrderState.PaymentActionRequired,
            "requires_payment_method" => GuestSupplierOrderState.PaymentFailed,
            "processing" => GuestSupplierOrderState.PaymentProcessing,
            "canceled" => GuestSupplierOrderState.Declined,
            _ => order.State,
        };
    }
}
