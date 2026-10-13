using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The admin tools of the service payments (SP-15b): the list and the detail of the payments, the commission of a supplier, and the
/// monthly commission export. See <see cref="ISupplierPaymentAdminService"/>.
/// </summary>
/// <remarks>
/// <para>A payment has two parties and an anonymous payer, so <c>ServiceRequestPayments</c> is not tenant-filtered; these are the
/// platform admin's reads across every supplier on purpose (the endpoints are <c>AdminOnly</c>), read-only projections that return
/// ids, names, amounts and Stripe's codes and never an email, a phone or an address. The commission change is the only write, and
/// it is saved together with its audit entry.</para>
/// </remarks>
public sealed class SupplierPaymentAdminService(
    AppDbContext db,
    IOptions<SupplierPaymentsOptions> options,
    ILogger<SupplierPaymentAdminService> logger,
    TimeProvider? timeProvider = null) : ISupplierPaymentAdminService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    private static readonly ServicePaymentStatus[] PaidStatuses =
    [
        ServicePaymentStatus.Paid,
        ServicePaymentStatus.PartiallyRefunded,
        ServicePaymentStatus.Refunded,
    ];

    // ─── The list and the detail ────────────────────────────────────────────────────────────────────────────────────

    public async Task<(IReadOnlyList<AdminServicePaymentItem> Items, int Total)> ListAsync(
        AdminServicePaymentQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = Math.Max(query.Page, 1);
        var pageSize = Math.Clamp(query.PageSize, 1, ServicePaymentLimits.AdminMaxPageSize);

        var payments = db.ServiceRequestPayments.AsNoTracking().AsQueryable();
        if (query.Status is { } status)
            payments = payments.Where(p => p.Status == status);
        if (query.SupplierOrgId is { } supplier)
            payments = payments.Where(p => p.SupplierOrgId == supplier);
        if (query.Late is true)
            payments = payments.Where(p => p.LateAt != null);
        else if (query.Late is false)
            payments = payments.Where(p => p.LateAt == null);
        if (query.CreatedFrom is { } from)
            payments = payments.Where(p => p.CreatedAt >= from);
        if (query.CreatedTo is { } to)
            payments = payments.Where(p => p.CreatedAt < to);

        var total = await payments.CountAsync(cancellationToken);
        var rows = await payments
            .OrderByDescending(p => p.CreatedAt)
            .ThenBy(p => p.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var names = await ReadNamesAsync(rows, cancellationToken);
        return (rows.Select(row => ToItem(row, names)).ToList(), total);
    }

    public async Task<AdminServicePaymentDetail> GetAsync(Guid paymentId, CancellationToken cancellationToken = default)
    {
        var payment = await db.ServiceRequestPayments.AsNoTracking().Where(p => p.Id == paymentId).FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException($"Service payment {paymentId} not found")
            {
                Code = ServicePaymentErrors.NotFound,
                MessageKey = ServicePaymentErrors.NotFoundMessageKey,
            };

        var refunds = await db.ServiceRequestPaymentRefunds
            .AsNoTracking()
            .Where(r => r.ServiceRequestPaymentId == paymentId)
            .OrderBy(r => r.Sequence)
            .ToListAsync(cancellationToken);

        var names = await ReadNamesAsync([payment], cancellationToken);
        return new AdminServicePaymentDetail(ToItem(payment, names), refunds);
    }

    private sealed record Names(IReadOnlyDictionary<Guid, string> Orgs, IReadOnlyDictionary<Guid, string?> VatNumbers);

    /// <summary>The names of the supplier and payer orgs of the payments, and the suppliers' P.IVA: read once for the whole page.</summary>
    private async Task<Names> ReadNamesAsync(IReadOnlyCollection<ServiceRequestPayment> payments, CancellationToken cancellationToken)
    {
        var orgIds = payments.SelectMany(p => new[] { (Guid?)p.SupplierOrgId, p.PayerOrgId }).OfType<Guid>().Distinct().ToList();

        // Org is the tenant itself (no tenant filter, TN-2 allow-list); read by the ids of this page.
        var orgs = await db.Orgs
            .AsNoTracking()
            .Where(o => orgIds.Contains(o.Id))
            .Select(o => new { o.Id, o.Name, o.DisplayName })
            .ToListAsync(cancellationToken);

        // SupplierProfile is keyed by the supplier org and not tenant-filtered; read by the supplier ids of this page.
        var supplierIds = payments.Select(p => p.SupplierOrgId).Distinct().ToList();
        var profiles = await db.SupplierProfiles
            .AsNoTracking()
            .Where(sp => supplierIds.Contains(sp.OrgId))
            .Select(sp => new { sp.OrgId, sp.LegalName, sp.VatNumber })
            .ToListAsync(cancellationToken);

        var names = orgs.ToDictionary(o => o.Id, o => string.IsNullOrWhiteSpace(o.DisplayName) ? o.Name : o.DisplayName);
        foreach (var profile in profiles.Where(profile => !string.IsNullOrWhiteSpace(profile.LegalName)))
            names[profile.OrgId] = profile.LegalName;

        return new Names(names, profiles.ToDictionary(profile => profile.OrgId, profile => profile.VatNumber));
    }

    private static AdminServicePaymentItem ToItem(ServiceRequestPayment p, Names names) => new(
        p.Id,
        p.ServiceRequestId,
        p.SupplierOrgId,
        names.Orgs.GetValueOrDefault(p.SupplierOrgId) ?? string.Empty,
        p.PayerOrgId,
        p.PayerOrgId is { } payer ? names.Orgs.GetValueOrDefault(payer) : null,
        p.Status,
        p.PaidVia,
        p.AmountCents,
        p.Currency,
        p.CommissionPercent,
        p.ApplicationFeeCents,
        p.NetCents,
        p.RefundedCents,
        SupplierCommission.FeeRefundedCents(p.ApplicationFeeCents, p.AmountCents, p.RefundedCents),
        p.RequestedAt,
        p.LateAt,
        p.PaidAt,
        p.FailureCode,
        p.StripePaymentIntentId,
        p.ConnectedAccountId,
        p.CreatedAt);

    // ─── The commission of a supplier ───────────────────────────────────────────────────────────────────────────────

    public async Task<SupplierCommissionSetting> GetCommissionAsync(Guid supplierOrgId, CancellationToken cancellationToken = default)
    {
        var profile = await ReadProfileAsync(supplierOrgId, cancellationToken);
        return ToSetting(profile, Now());
    }

    public async Task<SupplierCommissionSetting> SetCommissionAsync(
        Guid supplierOrgId,
        decimal? percent,
        DateTime? until,
        string reason,
        string actorUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var note = reason.Trim();
        ArgumentOutOfRangeException.ThrowIfGreaterThan(note.Length, ServicePaymentLimits.OfflineNoteMaxLength, nameof(reason));

        if (percent is { } value
            && (value < 0m || value > SupplierPaymentsOptions.MaxCommissionPercent || decimal.Round(value, 2) != value))
        {
            throw new DomainRuleException(
                ServicePaymentErrors.CommissionInvalid, ServicePaymentErrors.CommissionInvalidMessageKey, SupplierPaymentsOptions.MaxCommissionPercent);
        }

        var now = Now();
        DateTime? end = until is { } at ? DateTime.SpecifyKind(at.Kind == DateTimeKind.Local ? at.ToUniversalTime() : at, DateTimeKind.Utc) : null;

        // A period needs the percentage it applies to, and has to end in the future: an end that has passed would change nothing.
        if (end is { } endsAt && (percent is null || endsAt <= now))
            throw new DomainRuleException(ServicePaymentErrors.CommissionUntilInvalid, ServicePaymentErrors.CommissionUntilInvalidMessageKey);

        // Tracked: this is the one write of the admin tools. SupplierProfile is keyed by the supplier org and not tenant-filtered.
        var profile = await db.SupplierProfiles.Where(sp => sp.OrgId == supplierOrgId).FirstOrDefaultAsync(cancellationToken)
            ?? throw SupplierNotFound(supplierOrgId);

        var before = (profile.CommissionPercentOverride, profile.CommissionOverrideUntil);
        profile.CommissionPercentOverride = percent;
        profile.CommissionOverrideUntil = percent is null ? null : end;
        profile.UpdatedAt = now;

        db.SupplierAdminAuditEntries.Add(new SupplierAdminAuditEntry
        {
            Action = SupplierAdminAuditAction.CommissionChanged,
            SupplierOrgId = supplierOrgId,
            ActorUserId = actorUserId,
            OccurredAt = now,
            Reason = Truncate($"commission {Describe(before.CommissionPercentOverride, before.CommissionOverrideUntil)} -> {Describe(percent, end)}: {note}", 500),
        });
        await db.SaveChangesAsync(cancellationToken);

        logger.LogWarning(
            "Commission of supplier {SupplierOrgId} changed by admin {ActorUserId}: {Before} -> {After}",
            supplierOrgId, actorUserId, Describe(before.CommissionPercentOverride, before.CommissionOverrideUntil), Describe(percent, end));
        return ToSetting(profile, now);
    }

    private async Task<SupplierProfile> ReadProfileAsync(Guid supplierOrgId, CancellationToken cancellationToken) =>
        await db.SupplierProfiles.AsNoTracking().Where(sp => sp.OrgId == supplierOrgId).FirstOrDefaultAsync(cancellationToken)
        ?? throw SupplierNotFound(supplierOrgId);

    private SupplierCommissionSetting ToSetting(SupplierProfile profile, DateTime now)
    {
        var platform = options.Value.RequireCommissionPercent();
        var active = profile.CommissionPercentOverride is not null
                     && (profile.CommissionOverrideUntil is null || profile.CommissionOverrideUntil > now);
        return new SupplierCommissionSetting(
            profile.OrgId,
            platform,
            profile.CommissionPercentOverride,
            profile.CommissionOverrideUntil,
            active,
            SupplierCommission.EffectivePercent(platform, profile.CommissionPercentOverride, profile.CommissionOverrideUntil, now));
    }

    // Invariant: the audit text and the log read the same whatever the culture of the server.
    private static string Describe(decimal? percent, DateTime? until) =>
        percent is null
            ? "platform default"
            : FormattableString.Invariant($"{percent.Value:0.##}%")
              + (until is { } end ? FormattableString.Invariant($" until {end:yyyy-MM-dd'T'HH:mm:ss'Z'}") : string.Empty);

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];

    private static NotFoundException SupplierNotFound(Guid orgId) => new($"Supplier {orgId} not found")
    {
        Code = SupplierAdminErrorCodes.SupplierNotFound,
        MessageKey = SupplierAdminErrorCodes.SupplierNotFoundMessageKey,
    };

    // ─── The monthly export ─────────────────────────────────────────────────────────────────────────────────────────

    public async Task<CommissionExport> ExportCommissionsAsync(int year, int month, CancellationToken cancellationToken = default)
    {
        var today = RomeCalendar.DateInRome(Now());
        if (year is < 2000 or > 9999 || month is < 1 or > 12 || new DateOnly(year, month, 1) > today)
            throw new DomainRuleException(ServicePaymentErrors.ExportMonthInvalid, ServicePaymentErrors.ExportMonthInvalidMessageKey);

        // The month of the invoice is the calendar month in Rome.
        var first = new DateOnly(year, month, 1);
        var fromUtc = RomeCalendar.StartOfDayUtc(first);
        var toUtc = RomeCalendar.StartOfDayUtc(first.AddMonths(1));

        // Payments collected through Stripe in the month (an offline payment carries no commission and is not in the file).
        var paid = await db.ServiceRequestPayments
            .AsNoTracking()
            .Where(p => p.PaidVia == ServicePaymentChannel.Stripe && p.PaidAt >= fromUtc && p.PaidAt < toUtc && PaidStatuses.Contains(p.Status))
            .ToListAsync(cancellationToken);

        // Refunds that succeeded in the month, of payments collected through Stripe: the commission that went back.
        var refunds = await db.ServiceRequestPaymentRefunds
            .AsNoTracking()
            .Where(r => r.Status == ServicePaymentRefundStatus.Succeeded && r.CompletedAt >= fromUtc && r.CompletedAt < toUtc
                        && r.ServiceRequestPayment.PaidVia == ServicePaymentChannel.Stripe)
            .Select(r => new { Refund = r, Payment = r.ServiceRequestPayment })
            .ToListAsync(cancellationToken);

        var payments = paid.Concat(refunds.Select(r => r.Payment)).DistinctBy(p => p.Id).ToList();
        var names = await ReadNamesAsync(payments, cancellationToken);

        var rows = new List<CommissionExportRow>(paid.Count + refunds.Count);
        foreach (var p in paid)
        {
            rows.Add(new CommissionExportRow(
                CommissionExportRowType.Payment,
                RomeCalendar.DateInRome(p.PaidAt!.Value),
                p.Id,
                p.ServiceRequestId,
                p.SupplierOrgId,
                names.Orgs.GetValueOrDefault(p.SupplierOrgId) ?? string.Empty,
                names.VatNumbers.GetValueOrDefault(p.SupplierOrgId),
                p.PayerOrgId,
                p.PayerOrgId is { } payer ? names.Orgs.GetValueOrDefault(payer) : null,
                p.Currency,
                p.AmountCents,
                p.CommissionPercent,
                p.ApplicationFeeCents,
                p.NetCents));
        }

        foreach (var entry in refunds)
        {
            var p = entry.Payment;
            long gross = -entry.Refund.AmountCents;
            long commission = -(entry.Refund.ApplicationFeeRefundedCents ?? 0);
            rows.Add(new CommissionExportRow(
                CommissionExportRowType.Refund,
                RomeCalendar.DateInRome(entry.Refund.CompletedAt!.Value),
                p.Id,
                p.ServiceRequestId,
                p.SupplierOrgId,
                names.Orgs.GetValueOrDefault(p.SupplierOrgId) ?? string.Empty,
                names.VatNumbers.GetValueOrDefault(p.SupplierOrgId),
                p.PayerOrgId,
                p.PayerOrgId is { } payer ? names.Orgs.GetValueOrDefault(payer) : null,
                p.Currency,
                gross,
                p.CommissionPercent,
                commission,
                gross - commission));
        }

        var ordered = rows.OrderBy(r => r.Date).ThenBy(r => r.Type, StringComparer.Ordinal).ThenBy(r => r.PaymentId).ToList();
        return new CommissionExport(year, month, options.Value.CommissionVatPercent, ordered);
    }
}
