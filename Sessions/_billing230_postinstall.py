#!/usr/bin/env python3
"""Post-install patches for billing230 (merged handler, DI, fixes)."""
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def git_show(path: str) -> str | None:
    r = subprocess.run(["git", "show", f"stash@{{1}}:{path}"], cwd=ROOT, capture_output=True)
    if r.returncode != 0:
        return None
    t = r.stdout.decode("utf-8")
    return t[1:] if t.startswith("\ufeff") else t


def write(path: str, content: str) -> None:
    p = ROOT / path
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(content, encoding="utf-8", newline="\n")
    print(f"patched {path}")


for f in [
    "Casazen.Infrastructure/External/StripeWebhookHandler.cs",
    "Casazen.Web/Extensions/ServiceCollectionExtensions.cs",
    "Casazen.Infrastructure/Data/AppDbContext.cs",
]:
    c = git_show(f)
    if c:
        write(f, c)

h = (ROOT / "Casazen.Infrastructure/External/StripeWebhookHandler.cs").read_text(encoding="utf-8")
if "IRentBillingService rentBillingService" not in h:
    h = h.replace(
        "    ISdiEInvoiceService sdiEInvoiceService,\n    ILogger<StripeWebhookHandler> logger)",
        "    ISdiEInvoiceService sdiEInvoiceService,\n    IRentBillingService rentBillingService,\n    ILogger<StripeWebhookHandler> logger)",
    )
if "RentChargeKind" not in h:
    h = h.replace(
        '    private const string DirectBookingKind = "direct-booking";',
        '    private const string DirectBookingKind = "direct-booking";\n    private const string RentChargeKind = "rent-charge";',
    )
h = h.replace(
    "await HandlePaymentFailedAsync(stripeEvent.Data.Object as PaymentIntent, source);",
    "await HandlePaymentFailedAsync(stripeEvent.Data.Object as PaymentIntent, source, stripeEvent.Type);",
)
h = h.replace(
    "org.CurrentPeriodEnd = subscription.CurrentPeriodEnd;",
    "org.CurrentPeriodEnd = subscription.Items?.Data?.FirstOrDefault()?.CurrentPeriodEnd;",
)
if "TryGetMetadataKind" not in h:
    h = h.replace(
        """    private async Task HandlePaymentSucceededAsync(PaymentIntent? paymentIntent, WebhookSource source)
    {
        if (paymentIntent is null)
            return;

        if (IsDirectBookingEvent(paymentIntent, source))
        {
            await HandleDirectBookingPaymentSucceededAsync(paymentIntent);
            return;
        }

        if (source != WebhookSource.Platform)
            return;

        logger.LogInformation("Platform payment succeeded: {PaymentIntentId}", paymentIntent.Id);
        await UpdatePaymentStatusAsync(paymentIntent.Id, PaymentStatus.Completed);
    }""",
        """    private async Task HandlePaymentSucceededAsync(PaymentIntent? paymentIntent, WebhookSource source)
    {
        if (paymentIntent is null)
            return;

        if (TryGetMetadataKind(paymentIntent, out var kind))
        {
            if (string.Equals(kind, RentChargeKind, StringComparison.Ordinal))
            {
                if (source == WebhookSource.Connected &&
                    paymentIntent.Metadata.TryGetValue("rentLedgerEntryId", out var entryIdRaw) &&
                    Guid.TryParse(entryIdRaw, out var entryId))
                    await rentBillingService.HandleRentPaymentSucceededAsync(entryId);
                return;
            }
            if (string.Equals(kind, DirectBookingKind, StringComparison.Ordinal) && source == WebhookSource.Connected)
            {
                await HandleDirectBookingPaymentSucceededAsync(paymentIntent);
                return;
            }
        }

        if (source != WebhookSource.Platform)
            return;

        logger.LogInformation("Platform payment succeeded: {PaymentIntentId}", paymentIntent.Id);
        await UpdatePaymentStatusAsync(paymentIntent.Id, PaymentStatus.Completed);
    }""",
    )
    h = h.replace(
        """    private async Task HandlePaymentFailedAsync(PaymentIntent? paymentIntent, WebhookSource source)
    {
        if (paymentIntent is null)
            return;

        if (IsDirectBookingEvent(paymentIntent, source))
        {
            logger.LogInformation("Direct booking payment failed/canceled: {PaymentIntentId}", paymentIntent.Id);
            await UpdatePaymentStatusAsync(paymentIntent.Id, PaymentStatus.Failed);
            return;
        }

        if (source != WebhookSource.Platform)
            return;

        logger.LogInformation("Platform payment failed: {PaymentIntentId}", paymentIntent.Id);
        await UpdatePaymentStatusAsync(paymentIntent.Id, PaymentStatus.Failed);
    }""",
        """    private async Task HandlePaymentFailedAsync(PaymentIntent? paymentIntent, WebhookSource source, string eventType)
    {
        if (paymentIntent is null)
            return;

        if (TryGetMetadataKind(paymentIntent, out var kind))
        {
            if (string.Equals(kind, RentChargeKind, StringComparison.Ordinal))
            {
                if (source == WebhookSource.Connected &&
                    paymentIntent.Metadata.TryGetValue("rentLedgerEntryId", out var entryIdRaw) &&
                    Guid.TryParse(entryIdRaw, out var entryId))
                    await rentBillingService.HandleRentPaymentFailedAsync(entryId, eventType == "payment_intent.canceled");
                return;
            }
            if (string.Equals(kind, DirectBookingKind, StringComparison.Ordinal) && source == WebhookSource.Connected)
            {
                logger.LogInformation("Direct booking payment failed/canceled: {PaymentIntentId}", paymentIntent.Id);
                await UpdatePaymentStatusAsync(paymentIntent.Id, PaymentStatus.Failed);
                return;
            }
        }

        if (source != WebhookSource.Platform)
            return;

        logger.LogInformation("Platform payment failed: {PaymentIntentId}", paymentIntent.Id);
        await UpdatePaymentStatusAsync(paymentIntent.Id, PaymentStatus.Failed);
    }""",
    )
    h = h.replace(
        """    private static bool IsDirectBookingEvent(PaymentIntent paymentIntent, WebhookSource source)
    {
        if (source != WebhookSource.Connected)
            return false;

        paymentIntent.Metadata.TryGetValue("kind", out var kind);
        return string.Equals(kind, DirectBookingKind, StringComparison.Ordinal);
    }

    private async Task UpdatePaymentStatusAsync""",
        """    private static bool TryGetMetadataKind(PaymentIntent paymentIntent, out string? kind) =>
        paymentIntent.Metadata.TryGetValue("kind", out kind) && !string.IsNullOrWhiteSpace(kind);

    private async Task UpdatePaymentStatusAsync""",
    )
if "invoice.Parent?.SubscriptionDetails?.SubscriptionId" not in h:
    h = h.replace(
        """        var customerId = invoice.CustomerId;
        if (!string.IsNullOrWhiteSpace(customerId))
            return await dbContext.Orgs.FirstOrDefaultAsync(o => o.StripeCustomerId == customerId);

        return null;
    }

    private static SubscriptionStatus MapSubscriptionStatus""",
        """        var customerId = invoice.CustomerId;
        if (!string.IsNullOrWhiteSpace(customerId))
        {
            var org = await dbContext.Orgs.FirstOrDefaultAsync(o => o.StripeCustomerId == customerId);
            if (org is not null)
                return org;
        }

        var subscriptionId = invoice.Parent?.SubscriptionDetails?.SubscriptionId;
        if (!string.IsNullOrWhiteSpace(subscriptionId))
            return await dbContext.Orgs.FirstOrDefaultAsync(o => o.SubscriptionId == subscriptionId);

        return null;
    }

    private static SubscriptionStatus MapSubscriptionStatus""",
    )
write("Casazen.Infrastructure/External/StripeWebhookHandler.cs", h)

sce = (ROOT / "Casazen.Web/Extensions/ServiceCollectionExtensions.cs").read_text(encoding="utf-8")
if "IRentBillingService" not in sce:
    sce = sce.replace(
        "        services.AddScoped<IOssRevenueTracker, OssRevenueTracker>();\n",
        "        services.AddScoped<IOssRevenueTracker, OssRevenueTracker>();\n        services.AddScoped<IRentBillingService, NullRentBillingService>();\n",
    )
    write("Casazen.Web/Extensions/ServiceCollectionExtensions.cs", sce)

lc_path = ROOT / "Casazen.Core/Entities/LeaseContract.cs"
lct = lc_path.read_text(encoding="utf-8")
if "RentSchedule" not in lct:
    lct = lct.replace(
        "    public virtual LeaseRegistration? Registration { get; set; }\n    public virtual ICollection<LeaseEvent> Events",
        "    public virtual LeaseRegistration? Registration { get; set; }\n    public virtual RentSchedule? RentSchedule { get; set; }\n    public virtual ICollection<LeaseEvent> Events",
    )
    write("Casazen.Core/Entities/LeaseContract.cs", lct)

sbs_path = ROOT / "Casazen.Infrastructure/Services/StripeBillingService.cs"
sbst = sbs_path.read_text(encoding="utf-8")
if "using Stripe;" not in sbst:
    write("Casazen.Infrastructure/Services/StripeBillingService.cs", "using Stripe;\n" + sbst)

adb_path = ROOT / "Casazen.Infrastructure/Data/AppDbContext.cs"
adb_text = adb_path.read_text(encoding="utf-8")
if (ROOT / "Casazen.Core/Entities/ConsentRecord.cs").exists() and "ConsentRecords" not in adb_text:
    adb_text = adb_text.replace(
        "    public DbSet<PlatformBillingMetrics> PlatformBillingMetrics { get; set; } = null!;\n",
        "    public DbSet<PlatformBillingMetrics> PlatformBillingMetrics { get; set; } = null!;\n    public DbSet<ConsentRecord> ConsentRecords { get; set; } = null!;\n",
    )
    write("Casazen.Infrastructure/Data/AppDbContext.cs", adb_text)

ws = ROOT / "Casazen.Infrastructure/External/WebhookSource.cs"
if ws.exists():
    ws.unlink()
    print("deleted WebhookSource.cs")

print("post-install complete")
