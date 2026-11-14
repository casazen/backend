using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Events;

/// <summary>
/// How a flow that makes a payment of a booking paid records <c>PaymentSucceeded</c> (E08/F01/US01/T04, ADR-022-R1): through
/// <see cref="IDomainEventRecorder"/>, in the unit of work of the status change, so a rollback leaves no event. Once per payment: the dedup key
/// is <c>payment-succeeded:{paymentId}</c>, and a key already taken (in this unit of work or in the outbox) records nothing. The callers record
/// on the change of the status to <see cref="PaymentStatus.Completed"/> only. Without a recorder (the tests that build the services by hand)
/// nothing is recorded.
/// </summary>
public static class PaymentEvents
{
    /// <summary>The dedup key of the success of <paramref name="paymentId"/>.</summary>
    public static string SucceededKeyOf(Guid paymentId) => $"payment-succeeded:{paymentId}";

    /// <summary>Records the success of <paramref name="payment"/> once, checking this unit of work and the outbox of its org.</summary>
    public static async Task RecordSucceededAsync(
        IDomainEventRecorder? recorder,
        AppDbContext db,
        Payment payment,
        string? currency,
        CancellationToken cancellationToken = default)
    {
        if (recorder is null)
            return;

        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(payment);
        var key = SucceededKeyOf(payment.Id);

        // The org is named, so the tenant filter of the caller (a webhook, a job) does not decide what is taken.
        var taken = await db.OutboxEvents
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .AnyAsync(e => e.OrgId == payment.OrgId && e.DedupKey == key, cancellationToken);
        if (!taken)
            RecordSucceeded(recorder, db, payment, currency);
    }

    /// <summary>
    /// Records the success of <paramref name="payment"/> once in this unit of work, for a synchronous step of a flow that records it on the change
    /// of the status only (a payment is completed once).
    /// </summary>
    public static void RecordSucceeded(IDomainEventRecorder? recorder, AppDbContext db, Payment payment, string? currency)
    {
        if (recorder is null)
            return;

        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(payment);
        var key = SucceededKeyOf(payment.Id);
        if (db.ChangeTracker.Entries<OutboxEvent>().Any(e =>
                e.State == EntityState.Added && e.Entity.OrgId == payment.OrgId && e.Entity.DedupKey == key))
        {
            return;
        }

        recorder.Record(DomainEventType.PaymentSucceeded, payment.Id, PaymentEventPayload.Of(payment, currency), key, payment.OrgId);
    }

    /// <summary>The dedup key of the refund that brings what was refunded of <paramref name="paymentId"/> to <paramref name="refundedAmount"/> in all.</summary>
    public static string RefundedKeyOf(Guid paymentId, decimal refundedAmount) =>
        string.Create(CultureInfo.InvariantCulture, $"payment-refunded:{paymentId:N}:{refundedAmount:0.00}");

    /// <summary>
    /// Records that more was refunded of <paramref name="payment"/> (E12/F01/US01/T03): <c>PaymentRefunded</c>, once for each amount refunded in all (<paramref name="refundedAmount"/>),
    /// checking this unit of work and the outbox of its org, so a refund seen again (a webhook delivered twice, a recompute) records nothing. In the unit of work of the change of the
    /// payment, so a rollback leaves no event. Without a recorder (the tests that build the service by hand) nothing is recorded.
    /// </summary>
    public static async Task RecordRefundedAsync(
        IDomainEventRecorder? recorder,
        AppDbContext db,
        Payment payment,
        decimal refundedAmount,
        CancellationToken cancellationToken = default)
    {
        if (recorder is null)
            return;

        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(payment);
        var key = RefundedKeyOf(payment.Id, refundedAmount);
        if (db.ChangeTracker.Entries<OutboxEvent>().Any(e =>
                e.State == EntityState.Added && e.Entity.OrgId == payment.OrgId && e.Entity.DedupKey == key))
        {
            return;
        }

        var taken = await db.OutboxEvents
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .AnyAsync(e => e.OrgId == payment.OrgId && e.DedupKey == key, cancellationToken);
        if (!taken)
            recorder.Record(DomainEventType.PaymentRefunded, payment.Id, new PaymentRefundedPayload(payment.Id, payment.BookingId, refundedAmount), key, payment.OrgId);
    }

    /// <summary>The currency of the stays of the property of <paramref name="booking"/>, read across orgs (a webhook has no tenant).</summary>
    public static async Task<string> CurrencyOfAsync(AppDbContext db, Booking booking, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(booking);

        var currency = await db.Properties
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter, AppDbContext.SoftDeleteQueryFilter])
            .AsNoTracking()
            .Where(p => p.Id == booking.PropertyId)
            .Select(p => p.Currency)
            .FirstOrDefaultAsync(cancellationToken);
        return StayCurrency.Normalize(currency);
    }
}
