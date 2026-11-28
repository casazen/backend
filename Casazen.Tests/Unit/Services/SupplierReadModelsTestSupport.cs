using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;

namespace Casazen.Tests.Unit.Services;

internal static class SupplierReadModelsTestSupport
{
    public static DateTime Now => ServiceRequestScenario.Instant.UtcDateTime;

    public static DateTime Utc(string value) =>
        DateTime.Parse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal);

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
}
