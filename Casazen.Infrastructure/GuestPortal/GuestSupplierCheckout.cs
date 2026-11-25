using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Stripe;

namespace Casazen.Infrastructure.GuestPortal;

/// <summary>Platform-account card holds for guest supplier orders in a dedicated Stripe test environment.</summary>
public sealed class GuestSupplierCheckout(AppDbContext db, TimeProvider clock, IConfiguration configuration)
{
    public const string Section = "GuestPortal:SupplierStripeSandbox";
    public const int MinTotalCents = 50;
    public const string Kind = "guest_supplier_order";
    public static readonly TimeSpan Horizon = TimeSpan.FromHours(48);
    public sealed record Session(string ClientSecret, string PublishableKey);

    public static bool Enabled(IConfiguration? config) =>
        config?.GetValue<bool>($"{Section}:Enabled") == true &&
        config[$"{Section}:SecretKey"]?.StartsWith("sk_test_", StringComparison.Ordinal) == true &&
        config[$"{Section}:PublishableKey"]?.StartsWith("pk_test_", StringComparison.Ordinal) == true &&
        config[$"{Section}:WebhookSecret"]?.StartsWith("whsec_", StringComparison.Ordinal) == true;

    public static bool OrdersEnabled(IConfiguration? config) =>
        config?.GetValue<bool>($"{Section}:Enabled") == true
            ? Enabled(config) : config?.GetValue<bool>(GuestSupplierOrders.SimulationSetting) == true;

    private IStripeClient? client;

    internal GuestSupplierCheckout(AppDbContext db, TimeProvider clock, IConfiguration configuration, IStripeClient testClient)
        : this(db, clock, configuration) => client = testClient;

    private PaymentIntentService Intents => new(client ??= new StripeClient(configuration[$"{Section}:SecretKey"]));
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<Session?> StartAsync(GuestSupplierOrder order, int total, CancellationToken ct)
    {
        if (!Enabled(configuration) || !order.StripeSandbox) return null;
        if (total != order.SupplierPriceCents + order.CommissionCents) return null;
        var startedAt = Now;
        // Commit the first attempt timestamp before contacting Stripe, even if the SDK succeeds and the process dies.
        await db.GuestSupplierOrders.Where(x => x.Id == order.Id && x.StripeSandbox && x.StripeCheckoutStartedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.StripeCheckoutStartedAt, startedAt), ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(order, ct);
        if (order.State != GuestSupplierOrderState.Quoted || total != order.SupplierPriceCents + order.CommissionCents ||
            total < MinTotalCents || order.RequestedFor <= Now || order.RequestedFor > Now + Horizon)
            return null;

        PaymentIntent intent;
        if (order.StripePaymentIntentId is null)
        {
            // Stripe can forget an idempotency key after 24h. An orphaned create must never result in a second hold.
            if (order.StripeCheckoutStartedAt is not { } first || Now >= first.AddHours(23)) return null;
            intent = await Intents.CreateAsync(new PaymentIntentCreateOptions
            {
                Amount = total,
                Currency = "eur",
                CaptureMethod = "manual",
                PaymentMethodTypes = ["card"],
                Metadata = new() { ["kind"] = Kind, ["orderId"] = order.Id.ToString("D") }
            }, Key(order, "create"), ct);
            if (!Matches(order, intent)) return null;
            order.StripePaymentIntentId = intent.Id;
        }
        else
        {
            intent = await Intents.GetAsync(order.StripePaymentIntentId, cancellationToken: ct);
            if (!Matches(order, intent)) return null;
        }
        Apply(order, intent);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return intent.Status is "requires_payment_method" or "requires_confirmation" or "requires_action"
            ? new Session(intent.ClientSecret, configuration[$"{Section}:PublishableKey"]!) : null;
    }

    public async Task<bool> ReconcileAsync(GuestSupplierOrder order, CancellationToken ct)
    {
        if (!Enabled(configuration) || !order.StripeSandbox) return false;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(order, ct);
        if (order.StripePaymentIntentId is null) return false;
        var intent = await Intents.GetAsync(order.StripePaymentIntentId, cancellationToken: ct);
        if (!Matches(order, intent)) return false;
        if (intent.Status == "requires_capture" && order.RequestedFor <= Now)
            intent = await ChangeAsync(order, capture: false, ct);
        if (!Matches(order, intent)) return false;
        Apply(order, intent);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<bool> SupplierActionAsync(GuestSupplierOrder order, string action, CancellationToken ct)
    {
        if (!Enabled(configuration) || !order.StripeSandbox || action is not ("accept" or "decline")) return false;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(order, ct);
        var target = action == "accept" ? GuestSupplierOrderState.Accepted : GuestSupplierOrderState.Declined;
        if (order.State == target) return true;
        if (order.State is not (GuestSupplierOrderState.Quoted or GuestSupplierOrderState.Authorized)) return false;
        if (order.StripePaymentIntentId is null)
        {
            if (action != "decline") return false;
            order.State = GuestSupplierOrderState.Declined;
        }
        else
        {
            var intent = await Intents.GetAsync(order.StripePaymentIntentId, cancellationToken: ct);
            if (!Matches(order, intent)) return false;
            if (action == "decline" && intent.Status is not ("canceled" or "succeeded") ||
                intent.Status == "requires_capture" && order.RequestedFor <= Now)
                intent = await ChangeAsync(order, capture: false, ct);
            else if (action == "accept" && intent.Status == "requires_capture" && intent.AmountCapturable == intent.Amount)
                intent = await ChangeAsync(order, capture: true, ct);
            if (!Matches(order, intent)) return false;
            Apply(order, intent);
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return order.State == target;
    }

    private async Task LockAsync(GuestSupplierOrder order, CancellationToken ct)
    {
        // Serialize capture, cancellation and asynchronous callbacks, including requests from different application nodes.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"GuestSupplierOrders\" WHERE \"Id\" = {order.Id} FOR UPDATE", ct);
        await db.Entry(order).ReloadAsync(ct);
    }

    private async Task<PaymentIntent> ChangeAsync(GuestSupplierOrder order, bool capture, CancellationToken ct)
    {
        try
        {
            return capture
                ? await Intents.CaptureAsync(order.StripePaymentIntentId!, null, Key(order, "capture"), ct)
                : await Intents.CancelAsync(order.StripePaymentIntentId!, null, Key(order, "cancel"), ct);
        }
        catch (StripeException ex) when (ex.StripeError?.Code == "payment_intent_unexpected_state")
        {
            return await Intents.GetAsync(order.StripePaymentIntentId!, cancellationToken: ct);
        }
    }

    private static RequestOptions Key(GuestSupplierOrder order, string action) =>
        new() { IdempotencyKey = $"guest-supplier:{order.Id:D}:{action}" };

    internal static bool Matches(GuestSupplierOrder order, PaymentIntent intent) =>
        order.StripeSandbox && !intent.Livemode && !string.IsNullOrEmpty(intent.Id) &&
        intent.TransferData is null && intent.ApplicationFeeAmount is null && string.IsNullOrEmpty(intent.OnBehalfOfId) &&
        (order.StripePaymentIntentId is null || intent.Id == order.StripePaymentIntentId) &&
        intent.Metadata?.GetValueOrDefault("kind") == Kind && intent.Metadata.GetValueOrDefault("orderId") == order.Id.ToString("D") &&
        intent.CaptureMethod == "manual" && intent.Currency == "eur" && intent.Amount == order.SupplierPriceCents + order.CommissionCents;

    internal static void Apply(GuestSupplierOrder order, PaymentIntent intent)
    {
        if (order.State is not (GuestSupplierOrderState.Quoted or GuestSupplierOrderState.Authorized)) return;
        if (intent.Status == "requires_capture" && intent.AmountCapturable == intent.Amount)
            order.State = GuestSupplierOrderState.Authorized;
        else if (intent.Status == "succeeded" && intent.AmountReceived == intent.Amount)
            order.State = GuestSupplierOrderState.Accepted;
        else if (intent.Status == "canceled")
            order.State = GuestSupplierOrderState.Declined;
    }
}
