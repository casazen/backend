using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>The filters of the admin list of the service payments (<c>GET api/admin/supplier-payments</c>).</summary>
/// <param name="Status">Only the payments in this state (<c>NeedsReview</c> is the work queue of an admin).</param>
/// <param name="SupplierOrgId">Only the payments of this supplier.</param>
/// <param name="Late">True: only the payments flagged late; false: only the ones that are not.</param>
/// <param name="CreatedFrom">Only the payments created at or after this instant (UTC).</param>
/// <param name="CreatedTo">Only the payments created before this instant (UTC).</param>
/// <param name="Page">1-based page.</param>
/// <param name="PageSize">1 to <c>ServicePaymentLimits.AdminMaxPageSize</c>.</param>
public sealed record AdminServicePaymentQuery(
    ServicePaymentStatus? Status,
    Guid? SupplierOrgId,
    bool? Late,
    DateTime? CreatedFrom,
    DateTime? CreatedTo,
    int Page,
    int PageSize);

/// <summary>
/// One service payment as the admin sees it: gross, commission, net, what was refunded and the commission that went back with it
/// (in proportion, <c>SupplierCommission.FeeRefundedCents</c>). Ids of the supplier and the payer org and their names; no email,
/// no phone, no address.
/// </summary>
public sealed record AdminServicePaymentItem(
    Guid Id,
    Guid ServiceRequestId,
    Guid SupplierOrgId,
    string SupplierName,
    Guid? PayerOrgId,
    string? PayerName,
    ServicePaymentStatus Status,
    ServicePaymentChannel? PaidVia,
    int AmountCents,
    string Currency,
    decimal CommissionPercent,
    int ApplicationFeeCents,
    int NetCents,
    int RefundedCents,
    int CommissionRefundedCents,
    DateTime? RequestedAt,
    DateTime? LateAt,
    DateTime? PaidAt,
    string? FailureCode,
    string? StripePaymentIntentId,
    string? ConnectedAccountId,
    DateTime CreatedAt);

/// <summary>A payment with its refunds, newest last.</summary>
public sealed record AdminServicePaymentDetail(AdminServicePaymentItem Payment, IReadOnlyList<ServiceRequestPaymentRefund> Refunds);

/// <summary>
/// The commission applied to a supplier (<c>GET/PUT api/admin/suppliers/{orgId}/commission</c>).
/// </summary>
/// <param name="PlatformPercent">The platform's percentage (<c>SupplierPayments:CommissionPercent</c>).</param>
/// <param name="OverridePercent">The supplier's own percentage; null when it has none.</param>
/// <param name="OverrideUntil">When the override stops; null = it has no end.</param>
/// <param name="OverrideActive">The override is in force now (it exists and has not ended).</param>
/// <param name="EffectivePercent">What a payment created now is charged.</param>
public sealed record SupplierCommissionSetting(
    Guid SupplierOrgId,
    decimal PlatformPercent,
    decimal? OverridePercent,
    DateTime? OverrideUntil,
    bool OverrideActive,
    decimal EffectivePercent);

/// <summary>Kinds of line of the commission export.</summary>
public static class CommissionExportRowType
{
    /// <summary>A payment collected through Stripe in the month: the commission CasaZen kept.</summary>
    public const string Payment = "payment";

    /// <summary>A refund that succeeded in the month: the commission that went back (negative amounts).</summary>
    public const string Refund = "refund";
}

/// <summary>One line of the monthly commission export.</summary>
/// <param name="Type"><see cref="CommissionExportRowType"/>.</param>
/// <param name="Date">The day, in Rome, the payment was paid or the refund succeeded.</param>
/// <param name="SupplierVatNumber">The supplier's P.IVA as it is on its profile: the fiscal datum the invoice and a future DAC7 report need.</param>
/// <param name="GrossCents">What the payer paid (negative for a refund: what was given back).</param>
/// <param name="CommissionCents">CasaZen's commission (negative for a refund: the part that went back).</param>
/// <param name="NetCents">Gross minus commission: what the supplier gets before Stripe's own fees.</param>
public sealed record CommissionExportRow(
    string Type,
    DateOnly Date,
    Guid PaymentId,
    Guid ServiceRequestId,
    Guid SupplierOrgId,
    string SupplierName,
    string? SupplierVatNumber,
    Guid? PayerOrgId,
    string? PayerName,
    string Currency,
    long GrossCents,
    decimal CommissionPercent,
    long CommissionCents,
    long NetCents);

/// <summary>The commission of one month, for the manual invoice (<c>GET api/admin/supplier-payments/export</c>).</summary>
/// <param name="VatPercent">The VAT rate in the configuration; <b>empty</b> until the tax consultant decides (D4). Shown, never applied.</param>
public sealed record CommissionExport(int Year, int Month, decimal? VatPercent, IReadOnlyList<CommissionExportRow> Rows);

/// <summary>
/// The admin tools of the service payments (SP-15b): the list and the detail of the payments, the commission of a supplier
/// (its own percentage, or a period with a lower one), and the monthly commission export for the manual invoice. Platform admins
/// only (<c>AdminOnly</c>); they read across every supplier on purpose, so every query is a read-only projection.
/// </summary>
public interface ISupplierPaymentAdminService
{
    /// <summary>The payments, newest first, paginated in SQL.</summary>
    Task<(IReadOnlyList<AdminServicePaymentItem> Items, int Total)> ListAsync(
        AdminServicePaymentQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>One payment with its refunds. 404 <c>service_payment_not_found</c>.</summary>
    Task<AdminServicePaymentDetail> GetAsync(Guid paymentId, CancellationToken cancellationToken = default);

    /// <summary>The commission applied to a supplier. 404 <c>supplier_not_found</c>.</summary>
    Task<SupplierCommissionSetting> GetCommissionAsync(Guid supplierOrgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the supplier's own commission (<paramref name="percent"/>, 0 to the maximum of the configuration, two decimals at most;
    /// a free period is 0), optionally until <paramref name="until"/>, or removes it (<paramref name="percent"/> null). It applies
    /// to the payments created afterwards: a payment keeps the percentage it was created with. The change and its audit entry
    /// (who, when, old and new value, the reason) are saved together. 404 <c>supplier_not_found</c>; 422
    /// <c>supplier_commission_invalid</c>, <c>supplier_commission_until_invalid</c>.
    /// </summary>
    Task<SupplierCommissionSetting> SetCommissionAsync(
        Guid supplierOrgId,
        decimal? percent,
        DateTime? until,
        string reason,
        string actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The commission of a month (Europe/Rome): one line for each payment collected through Stripe in it, and one for each refund
    /// that succeeded in it (negative). 422 <c>service_payment_export_month_invalid</c> for a month in the future.
    /// </summary>
    Task<CommissionExport> ExportCommissionsAsync(int year, int month, CancellationToken cancellationToken = default);
}
