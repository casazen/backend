using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// Small helpers of the SP-04e tests: requests and payments written straight to the database, in the state the flows reach slowly,
/// over the scenario of SP-04 (Thursday 8 October 2026, 12:00 in Rome, a host and a supplier).
/// </summary>
internal static class SupplierReadModelsTestSupport
{
    /// <summary>The scenario clock as a UTC instant.</summary>
    public static DateTime Now => ServiceRequestScenario.Instant.UtcDateTime;

    /// <summary>A UTC instant written like <c>2026-10-05T09:00:00Z</c>.</summary>
    public static DateTime Utc(string value) =>
        DateTime.Parse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal);

    /// <summary>
    /// A request of the supplier written straight to the database with the dates the test names (each <c>null</c> where the request has
    /// none). The creation date is the one the periods and the response time are counted on.
    /// </summary>
    public static Task<ServiceRequest> SeedRequestAsync(
        this ServiceRequestScenario s,
        ServiceRequestStatus status,
        string created,
        string? taken = null,
        string? completed = null,
        string? updated = null,
        string? paid = null,
        Guid? supplierOrgId = null,
        Action<ServiceRequest>? change = null) =>
        s.SeedAsync(
            status,
            request =>
            {
                request.TakenAt = taken is null ? null : Utc(taken);
                request.CompletedAt = completed is null ? null : Utc(completed);
                request.PaidAt = paid is null ? null : Utc(paid);
                request.UpdatedAt = Utc(updated ?? completed ?? taken ?? created);
                change?.Invoke(request);
            },
            supplierOrgId,
            Utc(created));

    /// <summary>
    /// A payment record of a request, as the payments service would have written it: 10 % commission on <paramref name="amountCents"/>,
    /// sent at <paramref name="requestedAt"/> (the scenario clock by default), paid by Stripe when <paramref name="status"/> says it was.
    /// </summary>
    public static async Task<ServiceRequestPayment> AddPaymentAsync(
        this ServiceRequestScenario s,
        Guid requestId,
        ServicePaymentStatus status = ServicePaymentStatus.Requested,
        int amountCents = ServiceRequestScenario.ServicePriceCents,
        decimal commissionPercent = 10m,
        DateTime? requestedAt = null,
        DateTime? lastSentAt = null,
        int sentCount = 1,
        DateTime? lateAt = null,
        DateTime? paidAt = null,
        ServicePaymentChannel? paidVia = null,
        Guid? supplierOrgId = null)
    {
        var collected = status is ServicePaymentStatus.Paid or ServicePaymentStatus.PartiallyRefunded or ServicePaymentStatus.Refunded;
        var fee = SupplierCommission.FeeCents(amountCents, commissionPercent);
        var payment = new ServiceRequestPayment
        {
            ServiceRequestId = requestId,
            SupplierOrgId = supplierOrgId ?? s.SupplierOrgId,
            PayerKind = ServicePayerKind.Host,
            PayerOrgId = s.HostOrgId,
            AmountCents = amountCents,
            Currency = "eur",
            CommissionPercent = commissionPercent,
            ApplicationFeeCents = fee,
            NetCents = SupplierCommission.NetCents(amountCents, fee),
            Status = status,
            RequestedAt = requestedAt ?? Now,
            LastSentAt = lastSentAt ?? requestedAt ?? Now,
            SentCount = sentCount,
            LateAt = lateAt,
            PaidAt = paidAt ?? (collected ? Now : null),
            PaidVia = paidVia ?? (collected ? ServicePaymentChannel.Stripe : null),
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        s.Db.ServiceRequestPayments.Add(payment);
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        return payment;
    }

    /// <summary>A request completed with a final amount, as a supplier that is paid by hand leaves it.</summary>
    public static Task<ServiceRequest> SeedCompletedAsync(
        this ServiceRequestScenario s,
        int? finalAmountCents = ServiceRequestScenario.ServicePriceCents,
        ServiceRequestPaymentMode mode = ServiceRequestPaymentMode.Manual,
        Guid? supplierOrgId = null,
        Action<ServiceRequest>? change = null) =>
        s.SeedRequestAsync(
            ServiceRequestStatus.Completato,
            created: "2026-10-05T08:00:00Z",
            taken: "2026-10-05T08:10:00Z",
            completed: "2026-10-06T10:00:00Z",
            supplierOrgId: supplierOrgId,
            change: request =>
            {
                request.FinalAmountCents = finalAmountCents;
                request.PaymentMode = mode;
                change?.Invoke(request);
            });

    /// <summary>The payment records of the supplier, as saved.</summary>
    public static Task<List<ServiceRequestPayment>> AllPaymentsAsync(this ServiceRequestScenario s) =>
        s.Db.ServiceRequestPayments.AsNoTracking().OrderBy(p => p.CreatedAt).ToListAsync();
}
