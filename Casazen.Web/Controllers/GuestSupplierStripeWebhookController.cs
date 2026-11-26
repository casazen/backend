using System.Text.Json;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.GuestPortal;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stripe;

namespace Casazen.Web.Controllers;

[ApiController]
[AllowAnonymous]
[Route("webhooks/stripe/guest-supplier-sandbox")]
public sealed class GuestSupplierStripeWebhookController(
    AppDbContext db, GuestSupplierCheckout checkout, IConfiguration configuration) : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(512 * 1024)]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        if (!GuestSupplierCheckout.Enabled(configuration)) return NotFound();
        if (string.IsNullOrEmpty(Request.Headers["Stripe-Signature"])) return BadRequest();
        using var reader = new StreamReader(Request.Body);
        var json = await reader.ReadToEndAsync(ct);
        Envelope? stripeEvent;
        try
        {
            EventUtility.ValidateSignature(json, Request.Headers["Stripe-Signature"],
                configuration[$"{GuestSupplierCheckout.Section}:WebhookSecret"], 300);
            stripeEvent = JsonSerializer.Deserialize<Envelope>(json, JsonSerializerOptions.Web);
        }
        catch (StripeException) { return BadRequest(); }
        catch (JsonException) { return BadRequest(); }

        if (stripeEvent is not { Object: "event", Livemode: false } || string.IsNullOrEmpty(stripeEvent.Id) ||
            string.IsNullOrEmpty(stripeEvent.Type) || !string.IsNullOrEmpty(stripeEvent.Account)) return BadRequest();
        var bank = stripeEvent.Type is "charge.dispute.created" or "charge.dispute.updated" or "charge.dispute.closed" or
            "charge.dispute.funds_withdrawn" or "charge.dispute.funds_reinstated";
        if (bank || stripeEvent.Type is "refund.created" or "refund.updated" or "refund.failed")
        {
            var reference = stripeEvent.Data?.Object;
            if (reference?.Object != (bank ? "dispute" : "refund") || string.IsNullOrEmpty(reference.Id)) return BadRequest();
            if (string.IsNullOrEmpty(reference.PaymentIntent)) return Ok();
            var paid = await db.GuestSupplierOrders.SingleOrDefaultAsync(x => x.StripeSandbox && x.StripePaymentIntentId == reference.PaymentIntent, ct);
            if (paid == null) return Ok();
            if (bank) return await checkout.ReconcileBankCaseAsync(paid, reference.Id, ct) ? Ok() : Conflict();
            await checkout.ReconcileRefundAsync(paid, ct);
            return Ok();
        }
        var setup = stripeEvent.Type is "setup_intent.succeeded" or "setup_intent.canceled" or "setup_intent.setup_failed";
        if (!setup && stripeEvent.Type is not ("payment_intent.amount_capturable_updated" or "payment_intent.canceled"
            or "payment_intent.succeeded" or "payment_intent.requires_action" or "payment_intent.payment_failed" or "payment_intent.processing"))
            return Ok();
        if (stripeEvent.Data?.Object is not { Id.Length: > 0 } intent || intent.Object != (setup ? "setup_intent" : "payment_intent")) return BadRequest();
        if (intent.Metadata?.GetValueOrDefault("kind") != GuestSupplierCheckout.Kind) return Ok();
        if (!Guid.TryParse(intent.Metadata.GetValueOrDefault("orderId"), out var id)) return BadRequest();
        var order = await db.GuestSupplierOrders.SingleOrDefaultAsync(x => x.Id == id && x.StripeSandbox && (setup ? x.StripeSetupIntentId == intent.Id : x.StripePaymentIntentId == intent.Id), ct);
        if (order is null) return BadRequest();
        // Fetch Stripe's current state so a delayed event cannot revert a capture or revive a canceled hold.
        return await checkout.ReconcileAsync(order, ct) ? Ok() : Conflict();
    }

    private sealed record Envelope(string? Id, string? Object, bool? Livemode, string? Type, string? Account, EventData? Data);
    private sealed record EventData(IntentReference? Object);
    private sealed record IntentReference(string? Id, string? Object, Dictionary<string, string>? Metadata,
        [property: System.Text.Json.Serialization.JsonPropertyName("payment_intent")] string? PaymentIntent);
}
