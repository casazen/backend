using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.GuestPortal;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Stripe;

namespace Casazen.Infrastructure.GuestPortal;

public sealed record GuestHostExtraOffer(Guid Id, string Name, string Description, decimal UnitPrice,
    string Currency, bool ByQuantity, bool RequiresApproval);
public sealed record GuestHostExtraPayment(string ClientSecret, string PublishableKey, string StripeAccountId);
public sealed record GuestHostExtraOrderView(Guid Id, string Name, int Quantity, decimal Amount, string Currency, GuestHostExtraStatus Status)
{
    public static GuestHostExtraOrderView From(GuestHostExtraOrder order) =>
        new(order.Id, order.Name, order.Quantity, order.Amount, order.Currency, order.Status);
}

public sealed class GuestHostExtraService(AppDbContext db, IStripeService stripe, IConfiguration configuration,
    IEnumerable<IGuestPortalPolicy> policies, TimeProvider clock, ILogger<GuestHostExtraService> logger)
{
    public async Task<(Guid? Id, bool Forbidden)> SaveAsync(Guid orgId, Guid propertyId, Guid id,
        GuestHostExtra values, bool published, Func<Guid, Task<bool>> canEdit, CancellationToken ct)
    {
        await using var tx = await LockAsync(id == Guid.Empty ? orgId : id, ct);
        var extra = await db.GuestHostExtras.SingleOrDefaultAsync(x => x.OrgId == orgId && x.Id == id, ct);
        if (extra is null)
        {
            if (id != Guid.Empty) return (null, false);
            extra = new GuestHostExtra { OrgId = orgId };
            db.GuestHostExtras.Add(extra);
        }
        else
        {
            var updatesSharedCatalog = extra.Name != values.Name || extra.Description != values.Description ||
                extra.Price != values.Price || extra.Pricing != values.Pricing || extra.Inventory != values.Inventory ||
                extra.CutoffHours != values.CutoffHours || extra.DuringStay != values.DuringStay ||
                extra.RequiresApproval != values.RequiresApproval || extra.Calendar != values.Calendar;
            if (updatesSharedCatalog)
                foreach (var otherId in extra.PropertyIds.Where(x => x != propertyId))
                    if (!await canEdit(otherId)) return (null, true);
        }
        if (extra.Name != values.Name || extra.Description != values.Description)
            extra.TranslationsJson = null;
        extra.Name = values.Name;
        extra.Description = values.Description;
        extra.Price = values.Price;
        extra.Pricing = values.Pricing;
        extra.Inventory = values.Inventory;
        extra.CutoffHours = values.CutoffHours;
        extra.DuringStay = values.DuringStay;
        extra.RequiresApproval = values.RequiresApproval;
        extra.Calendar = values.Calendar;
        extra.PropertyIds = published ? extra.PropertyIds.Append(propertyId).Distinct().ToArray()
            : extra.PropertyIds.Where(x => x != propertyId).ToArray();
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return (extra.Id, false);
    }

    public async Task<(IReadOnlyList<GuestHostExtraOffer> Offers, IReadOnlyList<GuestHostExtraOrderView> Orders)> ListAsync(
        PortalContext portal, CancellationToken ct, string? language = null)
    {
        var displayLanguage = GuestLanguages.Normalize(language) ?? portal.Language;
        var offers = new List<GuestHostExtraOffer>();
        var orders = await Orders(portal).OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
        var booking = await BookingAsync(portal, ct);
        if (CanSell(booking) && await AllowsSalesAsync(portal, ct))
        {
            var extras = await db.GuestHostExtras.AsNoTracking()
                .Where(x => x.OrgId == portal.OrgId && x.PropertyIds.Contains(portal.PropertyId))
                .OrderBy(x => x.Name).ToListAsync(ct);
            foreach (var extra in extras)
            {
                if (!orders.Any(x => x.ExtraId == extra.Id && IsActive(x.Status)) &&
                    await IsAvailableAsync(extra, booking!, 1, null, ct))
                {
                    var copy = GuestHostExtraTranslator.ForLanguage(extra.TranslationsJson, displayLanguage);
                    offers.Add(new(extra.Id, copy?.Name ?? extra.Name, copy?.Description ?? extra.Description, Price(extra, booking!, 1),
                        StayCurrency.ForProperty(booking!.Property), extra.Pricing == GuestHostExtraPricing.PerUnit, extra.RequiresApproval));
                }
            }
        }
        return (offers, orders.Select(GuestHostExtraOrderView.From).ToList());
    }

    public async Task<GuestHostExtraOrderView> OrderAsync(PortalContext portal, Guid extraId, int quantity, Guid requestId,
        CancellationToken ct)
    {
        var booking = await BookingAsync(portal, ct);
        if (!CanSell(booking) || !await AllowsSalesAsync(portal, ct) || requestId == Guid.Empty)
            throw Unavailable();

        await using var tx = await LockAsync(extraId, ct);
        var orders = Orders(portal);
        var scopedId = new Guid(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{portal.OrgId:N}:{portal.BookingId:N}:{extraId:N}:{requestId:N}")).AsSpan(0, 16));
        var retry = await orders.SingleOrDefaultAsync(x => x.Id == scopedId && x.ExtraId == extraId, ct);
        if (retry is not null)
            return GuestHostExtraOrderView.From(retry);

        var extra = await db.GuestHostExtras.SingleOrDefaultAsync(x =>
            x.OrgId == portal.OrgId && x.Id == extraId && x.PropertyIds.Contains(portal.PropertyId), ct);
        if (extra is null || quantity is < 1 or > 100 || (extra.Pricing != GuestHostExtraPricing.PerUnit && quantity != 1) ||
            await orders.AnyAsync(x => x.ExtraId == extraId && x.Status != GuestHostExtraStatus.Declined &&
                x.Status != GuestHostExtraStatus.Cancelled, ct) ||
            !await IsAvailableAsync(extra, booking!, quantity, null, ct))
            throw Unavailable();

        var amount = Price(extra, booking!, quantity);
        if (amount > 999_999.99m) throw Unavailable();

        var order = new GuestHostExtraOrder
        {
            Id = scopedId,
            OrgId = portal.OrgId,
            BookingId = portal.BookingId,
            ExtraId = extraId,
            Name = extra.Name,
            Quantity = quantity,
            Amount = amount,
            Currency = StayCurrency.ForProperty(booking!.Property),
            CreatedAt = clock.GetUtcNow().UtcDateTime,
            Status = extra.RequiresApproval ? GuestHostExtraStatus.AwaitingAuthorization : GuestHostExtraStatus.ReadyToPay
        };
        db.GuestHostExtraOrders.Add(order);
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return GuestHostExtraOrderView.From(order);
    }

    public async Task<GuestHostExtraPayment> PayAsync(PortalContext portal, Guid orderId, CancellationToken ct)
    {
        var booking = await BookingAsync(portal, ct);
        var publicKey = configuration["Stripe:PublishableKey"];
        if (!CanSell(booking) || !await AllowsSalesAsync(portal, ct) || string.IsNullOrWhiteSpace(publicKey))
            throw Unavailable();
        var orderKey = await Orders(portal).Where(x => x.Id == orderId).Select(x => (Guid?)x.ExtraId).SingleOrDefaultAsync(ct);
        if (orderKey is null) throw Unavailable();

        await using var tx = await LockAsync(orderKey.Value, ct);
        var order = await Orders(portal).SingleAsync(x => x.Id == orderId, ct);
        var authorization = order.Status == GuestHostExtraStatus.AwaitingAuthorization;
        if (!authorization && order.Status != GuestHostExtraStatus.ReadyToPay)
            throw Unavailable();

        var account = order.StripeAccountId ?? booking!.Org.StripeConnectedAccountId!;
        PaymentIntent intent;
        if (order.StripeIntentId is null)
        {
            var extra = await db.GuestHostExtras.SingleOrDefaultAsync(x => x.OrgId == portal.OrgId &&
                x.Id == order.ExtraId && x.PropertyIds.Contains(portal.PropertyId), ct);
            if (extra is null || !await IsAvailableAsync(extra, booking!, order.Quantity, order.Id, ct))
                throw Unavailable();

            var metadata = new Dictionary<string, string>
            {
                ["kind"] = "guest-host-extra",
                ["order_id"] = order.Id.ToString("D"),
                ["org_id"] = portal.OrgId.ToString("D"),
            };
            var key = $"guest-host-extra:{order.Id:N}";
            var currency = StayCurrency.ForStripe(order.Currency);
            intent = authorization
                ? await stripe.CreateConnectedAuthorizationAsync(account, Minor(order.Amount), currency, metadata, key, order.Name, ct)
                : await stripe.CreateConnectedAccountPaymentIntentAsync(account, Minor(order.Amount), currency, metadata, key, order.Name, ct);
            order.StripeIntentId = intent.Id;
            order.StripeAccountId = account;
            await db.SaveChangesAsync(ct);
        }
        else
        {
            intent = await stripe.GetPaymentIntentAsync(order.StripeIntentId, account, ct);
        }
        await ApplyObservationAsync(order, intent, account, ct);
        if (tx is not null) await tx.CommitAsync(ct);
        if (intent.Status is not ("requires_payment_method" or "requires_confirmation" or "requires_action"))
            throw Unavailable();
        return new(intent.ClientSecret, publicKey, account);
    }

    public async Task<bool> ChangeStatusAsync(Guid orgId, Guid bookingId, Guid id, GuestHostExtraStatus target,
        bool isHost, CancellationToken ct)
    {
        var orders = db.GuestHostExtraOrders.Where(x => x.OrgId == orgId && x.BookingId == bookingId);
        var key = await orders.Where(x => x.Id == id).Select(x => (Guid?)x.ExtraId).SingleOrDefaultAsync(ct);
        if (key is null) return false;

        await using var tx = await LockAsync(key.Value, ct);
        var order = await orders.SingleAsync(x => x.Id == id, ct);
        if (order.Status == target || (target == GuestHostExtraStatus.CapturePending &&
            order.Status is GuestHostExtraStatus.Paid or GuestHostExtraStatus.Fulfilled)) return true;

        var allowed = (isHost, order.Status, target) switch
        {
            (true, GuestHostExtraStatus.Requested, GuestHostExtraStatus.CapturePending or GuestHostExtraStatus.Declined) => true,
            (true, GuestHostExtraStatus.Paid, GuestHostExtraStatus.Fulfilled) => true,
            (_, GuestHostExtraStatus.Requested or GuestHostExtraStatus.ReadyToPay or GuestHostExtraStatus.AwaitingAuthorization,
                GuestHostExtraStatus.Cancelled) => true,
            _ => false,
        };
        if (!allowed) throw Unavailable();
        if (target == GuestHostExtraStatus.CapturePending)
        {
            if (order.StripeIntentId is null || order.StripeAccountId is null) throw Unavailable();
            var observed = await stripe.GetPaymentIntentAsync(order.StripeIntentId, order.StripeAccountId, ct);
            await ApplyObservationAsync(order, observed, order.StripeAccountId, ct);
            if (observed.Status == "canceled" || order.Status == GuestHostExtraStatus.Paid)
            {
                if (tx is not null) await tx.CommitAsync(ct);
                return true;
            }
            if (observed.Status != "processing" && !IsCapturable(order, observed)) throw Unavailable();
            if (observed.Status != "processing")
            {
                var booking = await db.Bookings.AsNoTracking().Include(x => x.Property).Include(x => x.Org)
                    .SingleOrDefaultAsync(x => x.Id == bookingId && x.OrgId == orgId, ct);
                var extra = await db.GuestHostExtras.SingleOrDefaultAsync(x => x.OrgId == orgId && x.Id == order.ExtraId, ct);
                if (!CanSell(booking) || extra is null || !extra.PropertyIds.Contains(booking!.PropertyId) ||
                    !await IsAvailableAsync(extra, booking, order.Quantity, order.Id, ct))
                    throw Unavailable();
                var captured = await stripe.CaptureConnectedAuthorizationAsync(order.StripeAccountId, order.StripeIntentId,
                    $"guest-host-extra-capture:{order.Id:N}", ct);
                if (captured.Status is not ("processing" or "succeeded")) throw Unavailable();
                await ApplyObservationAsync(order, captured, order.StripeAccountId, ct);
                if (order.Status == GuestHostExtraStatus.Paid)
                {
                    if (tx is not null) await tx.CommitAsync(ct);
                    return true;
                }
            }
        }
        if ((target is GuestHostExtraStatus.Cancelled or GuestHostExtraStatus.Declined) && order.StripeIntentId is { } intentId)
        {
            var intent = await stripe.GetPaymentIntentAsync(intentId, order.StripeAccountId, ct);
            if (intent.Status is "processing" or "succeeded") throw Unavailable();
            if (intent.Status != "canceled")
                await stripe.CancelPaymentIntentAsync(intent.Id, order.StripeAccountId, $"guest-host-extra-cancel:{order.Id:N}", ct);
        }
        order.Status = target;
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return true;
    }

    // The signed Connect event and the initial order must agree on account, amount, currency and identity.
    public async Task ObserveAsync(PaymentIntent intent, string account, CancellationToken ct)
    {
        if (intent.Metadata?.TryGetValue("order_id", out var id) != true || !Guid.TryParse(id, out var orderId) ||
            intent.Metadata?.TryGetValue("org_id", out var tenant) != true || !Guid.TryParse(tenant, out var orgId))
            return;

        var orders = db.GuestHostExtraOrders.IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(x => x.OrgId == orgId && x.Id == orderId);
        var key = await orders.Select(x => (Guid?)x.ExtraId).SingleOrDefaultAsync(ct);
        if (key is null) return;
        await using var tx = await LockAsync(key.Value, ct);
        var order = await orders.SingleAsync(ct);
        await ApplyObservationAsync(order, intent, account, ct);
        if (tx is not null) await tx.CommitAsync(ct);
    }

    public async Task<bool> ConfirmAuthorizationAsync(PortalContext portal, Guid orderId, CancellationToken ct)
    {
        var key = await Orders(portal).Where(x => x.Id == orderId).Select(x => (Guid?)x.ExtraId).SingleOrDefaultAsync(ct);
        if (key is null) return false;
        await using var tx = await LockAsync(key.Value, ct);
        var order = await Orders(portal).SingleAsync(x => x.Id == orderId, ct);
        if (order.Status == GuestHostExtraStatus.AwaitingAuthorization &&
            order.StripeIntentId is { } id && order.StripeAccountId is { } account)
        {
            var intent = await stripe.GetPaymentIntentAsync(id, account, ct);
            await ApplyObservationAsync(order, intent, account, ct);
        }
        if (tx is not null) await tx.CommitAsync(ct);
        return true;
    }

    private async Task ApplyObservationAsync(GuestHostExtraOrder order, PaymentIntent intent, string account, CancellationToken ct)
    {
        if (order.StripeIntentId != intent.Id || order.StripeAccountId != account ||
            intent.Currency != StayCurrency.ForStripe(order.Currency))
            return;

        var status = (order.Status, intent.Status) switch
        {
            (GuestHostExtraStatus.AwaitingAuthorization, "requires_capture") when IsCapturable(order, intent) => GuestHostExtraStatus.Requested,
            (GuestHostExtraStatus.Requested or GuestHostExtraStatus.CapturePending or GuestHostExtraStatus.ReadyToPay, "succeeded")
                when intent.AmountReceived == Minor(order.Amount) => GuestHostExtraStatus.Paid,
            (GuestHostExtraStatus.AwaitingAuthorization or GuestHostExtraStatus.Requested or GuestHostExtraStatus.CapturePending
                or GuestHostExtraStatus.ReadyToPay, "canceled") => GuestHostExtraStatus.Cancelled,
            _ => order.Status
        };
        if (status == order.Status) return;
        if (status == GuestHostExtraStatus.Paid)
        {
            await GuestExtraFiscalPayment.SavePaidAsync(db, order, ct);
            return;
        }
        order.Status = status;
        await db.SaveChangesAsync(ct);
    }

    private static bool IsCapturable(GuestHostExtraOrder order, PaymentIntent intent) =>
        intent.Status == "requires_capture" && intent.CaptureMethod == "manual" &&
        intent.AmountCapturable == Minor(order.Amount) && intent.AmountReceived == 0 &&
        intent.Currency == StayCurrency.ForStripe(order.Currency);

    private async Task<bool> IsAvailableAsync(GuestHostExtra extra, Booking booking, int quantity, Guid? existingOrder,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var zone = RomeCalendar.ZoneOf(booking.Property.Timezone);
        var arrival = RomeCalendar.ToUtc(DateOnly.FromDateTime(booking.CheckInDate),
            booking.Property.CheckInFrom ?? TimeOnly.MinValue, zone);
        var departure = RomeCalendar.ToUtc(DateOnly.FromDateTime(booking.CheckOutDate),
            booking.Property.CheckOutBy ?? TimeOnly.MaxValue, zone);
        if (now >= departure || (!extra.DuringStay && now >= arrival.AddHours(-extra.CutoffHours)))
            return false;

        if (extra.Calendar != GuestHostExtraCalendar.None)
        {
            var early = extra.Calendar == GuestHostExtraCalendar.EarlyCheckIn;
            var day = early ? booking.CheckInDate.AddDays(-1) : booking.CheckOutDate;
            var next = day.AddDays(1);
            if ((early && now >= arrival) ||
                await db.Bookings.Where(PropertyOccupancy.BookingTakesNightIn(booking.PropertyId, day, next))
                    .Where(CheckoutHolds.OccupiesDates(null))
                    .AnyAsync(x => x.OrgId == booking.OrgId && x.Id != booking.Id, ct) ||
                await db.CalendarBlocks.Where(PropertyOccupancy.BlockTakesNightIn(booking.PropertyId, day, next))
                    .AnyAsync(x => x.OrgId == booking.OrgId, ct))
                return false;
        }
        if (extra.Inventory is not { } inventory)
            return true;

        // Inventory belongs to the host catalog. Count reservations only across overlapping stays.
        var occupied = await (from order in db.GuestHostExtraOrders
                              join other in db.Bookings on order.BookingId equals other.Id
                              where order.OrgId == booking.OrgId && other.OrgId == booking.OrgId && order.ExtraId == extra.Id &&
                                    order.Id != existingOrder && other.Status != BookingStatus.Cancelled && order.Status != GuestHostExtraStatus.Cancelled &&
                                    order.Status != GuestHostExtraStatus.Declined && other.CheckInDate < booking.CheckOutDate &&
                                    other.CheckOutDate > booking.CheckInDate
                              select order.Quantity).SumAsync(ct);
        return quantity <= inventory - occupied;
    }

    private IQueryable<GuestHostExtraOrder> Orders(PortalContext portal) =>
        db.GuestHostExtraOrders.Where(x => x.OrgId == portal.OrgId && x.BookingId == portal.BookingId);

    private Task<Booking?> BookingAsync(PortalContext portal, CancellationToken ct) =>
        db.Bookings.AsNoTracking().Include(x => x.Org).Include(x => x.Property)
            .SingleOrDefaultAsync(x => x.OrgId == portal.OrgId && x.Id == portal.BookingId && x.PropertyId == portal.PropertyId, ct);

    private async Task<bool> AllowsSalesAsync(PortalContext portal, CancellationToken ct)
    {
        foreach (var policy in policies)
        {
            try
            {
                if (!await policy.CanShowPaidServicesAsync(portal, ct)) return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Guest portal policy {Policy} failed for link {LinkId}", policy.GetType().Name, portal.LinkId);
                return false;
            }
        }
        return true;
    }

    private static bool CanSell(Booking? booking) => booking is { Org.CanTakeDirectPayments: true } &&
        GuestPortalRules.IsStayOpenToReservedData(booking.Status, booking.NoShowAt);
    private static bool IsActive(GuestHostExtraStatus status) =>
        status is not (GuestHostExtraStatus.Declined or GuestHostExtraStatus.Cancelled);
    private static decimal Price(GuestHostExtra extra, Booking booking, int quantity) => extra.Price *
        (extra.Pricing switch
        {
            GuestHostExtraPricing.PerNight => Math.Max(1, (booking.CheckOutDate - booking.CheckInDate).Days),
            GuestHostExtraPricing.PerGuest => Math.Max(1, booking.NumberOfGuests),
            GuestHostExtraPricing.PerUnit => quantity,
            _ => 1,
        });
    private static long Minor(decimal amount) => decimal.ToInt64(amount * 100);
    private static DomainConflictException Unavailable() => new("guest_host_extra_unavailable", "GuestHostExtraUnavailable");
    private Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> LockAsync(Guid extraId, CancellationToken ct) =>
        PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, ct,
            (PostgresAdvisoryLocks.Scope.GuestHostExtra, extraId.ToString("N")));
}
