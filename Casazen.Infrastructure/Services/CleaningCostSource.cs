using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="ICleaningCostSource" />
/// <remarks>
/// <list type="bullet">
/// <item><b>Supplier cleanings</b>: the service requests of the org of category cleaning that are <c>Completato</c> or <c>Pagato</c>, the ones the host sees (a backup that waits and
/// the primary it replaced are not jobs). A request that was cancelled, refused or is still open never comes back. The date is the one of the completion (<c>CompletedAt</c>; the
/// payment, then the last change, for a row that has none), on the Europe/Rome calendar.</item>
/// <item><b>The amount</b> is the one the model has, never one made up: the final amount the supplier declared, else the price it committed to (<c>QuotedAmountCents</c>, which is also the
/// quote the customer accepted), else a quote still waiting, else the estimated amount of the request; none at all is 0 with the VAT <see cref="CleaningAmountVat.Unknown"/>. Every amount
/// of a service request is «gross as declared» (decision D4: no VAT split), so a supplier's amount is <see cref="CleaningAmountVat.IncludesSupplierVat"/>; a staff cost has no VAT of a
/// supplier (<see cref="CleaningAmountVat.NotApplicable"/>). <c>SupplierInvoiceReference</c> is always <c>null</c>: the model links no invoice or receipt of the supplier to a request or to
/// its payment (a payment holds the identifiers of the payment provider, which are not an invoice).</item>
/// <item><b>Staff cleanings</b>: the staff activities of type Cleaning that are completed, their internal rate in cents (the rate has two decimals), with the flag of the recharge.</item>
/// <item><b>Currency</b>: the one of the property (requests and rates carry none), also for a property deleted since.</item>
/// <item><b>Whose</b>: the tenant filter and the explicit <c>orgId</c> on the property, the request and the activity; a property of another org is never in the answer, and an id list never
/// widens it. A showcase request (the supplier's own, with no property) is never a host's cleaning. Read only: nothing is tracked or written.</item>
/// </list>
/// </remarks>
public sealed class CleaningCostSource(AppDbContext db) : ICleaningCostSource
{
    public async Task<IReadOnlyList<CompletedCleaning>> GetCompletedAsync(
        Guid orgId,
        IReadOnlyCollection<Guid>? propertyIds,
        DateOnly from,
        DateOnly to,
        CancellationToken ct)
    {
        // An empty list of properties is none, never «every property»; a period that ends before it starts has no day.
        if (propertyIds is { Count: 0 } || to < from)
            return [];

        // The days are Europe/Rome calendar days: [the start of the first, the start of the day after the last).
        var fromUtc = RomeCalendar.StartOfDayUtc(from);
        var untilUtc = RomeCalendar.StartOfDayUtc(to == DateOnly.MaxValue ? to : to.AddDays(1));

        // The properties of the org, deleted ones too (the cost of a cleaning of a house sold since is still the owner's): tenant filter on, org named.
        var properties = db.Properties
            .IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter])
            .AsNoTracking()
            .Where(p => p.OrgId == orgId);
        if (propertyIds is not null)
        {
            var wanted = propertyIds.Distinct().ToList();
            properties = properties.Where(p => wanted.Contains(p.Id));
        }

        var currencies = (await properties.Select(p => new { p.Id, p.Currency }).ToListAsync(ct))
            .ToDictionary(p => p.Id, p => StayCurrency.Normalize(p.Currency));
        if (currencies.Count == 0)
            return [];

        var ids = currencies.Keys.ToList();
        var found = new List<(DateTime At, CompletedCleaning Cleaning)>();

        // The service requests of the org: not tenant-filtered (two parties), so the org is the predicate; the property is one of the org's by the list above.
        var requests = await db.ServiceRequests
            .AsNoTracking()
            .OnlyVisibleToHost()
            .Where(r => r.OrgId == orgId
                        && r.RentalContext != ServiceRequestRentalContext.Showcase
                        && r.PropertyId != null
                        && ids.Contains(r.PropertyId.Value)
                        && r.Category == ServiceCategories.Cleaning
                        && (r.Status == ServiceRequestStatus.Completato || r.Status == ServiceRequestStatus.Pagato)
                        && (r.CompletedAt ?? r.PaidAt ?? r.UpdatedAt) >= fromUtc
                        && (r.CompletedAt ?? r.PaidAt ?? r.UpdatedAt) < untilUtc)
            .Select(r => new
            {
                r.Id,
                PropertyId = r.PropertyId!.Value,
                r.BookingId,
                At = r.CompletedAt ?? r.PaidAt ?? r.UpdatedAt,
                r.FinalAmountCents,
                r.QuotedAmountCents,
                r.QuoteTotalCents,
                r.EstimatedAmountCents,
            })
            .ToListAsync(ct);

        foreach (var request in requests)
        {
            var amount = request.FinalAmountCents ?? request.QuotedAmountCents ?? request.QuoteTotalCents ?? request.EstimatedAmountCents;
            found.Add((
                request.At,
                new CompletedCleaning(
                    request.PropertyId,
                    request.BookingId,
                    RomeCalendar.DateInRome(request.At),
                    amount ?? 0,
                    currencies[request.PropertyId],
                    amount is null ? CleaningAmountVat.Unknown : CleaningAmountVat.IncludesSupplierVat,
                    CleaningKind.Supplier,
                    RechargeToOwner: true,
                    CleaningSourceKind.ServiceRequest,
                    request.Id,
                    SupplierInvoiceReference: null)));
        }

        // The staff work of the org (tenant-filtered, the org named): the cleanings that were completed, not the To do rows that share the table.
        var staffWork = await db.InternalActivities
            .AsNoTracking()
            .Where(a => a.OrgId == orgId
                        && a.Action == null
                        && a.PropertyId != null
                        && ids.Contains(a.PropertyId.Value)
                        && a.ActivityType == InternalActivity.CleaningActivityType
                        && a.Status == InternalActivity.StatusCompleted
                        && (a.CompletedAt ?? a.ScheduledAt) >= fromUtc
                        && (a.CompletedAt ?? a.ScheduledAt) < untilUtc)
            .Select(a => new
            {
                a.Id,
                PropertyId = a.PropertyId!.Value,
                a.BookingId,
                At = a.CompletedAt ?? a.ScheduledAt,
                a.InternalRate,
                a.RechargeToOwner,
            })
            .ToListAsync(ct);

        foreach (var activity in staffWork)
        {
            found.Add((
                activity.At,
                new CompletedCleaning(
                    activity.PropertyId,
                    activity.BookingId,
                    RomeCalendar.DateInRome(activity.At),
                    activity.InternalRate is { } rate ? (long)decimal.Round(rate * 100m, 0, MidpointRounding.AwayFromZero) : 0,
                    currencies[activity.PropertyId],
                    CleaningAmountVat.NotApplicable,
                    CleaningKind.Staff,
                    activity.RechargeToOwner,
                    CleaningSourceKind.InternalActivity,
                    activity.Id,
                    SupplierInvoiceReference: null)));
        }

        return [.. found.OrderBy(f => f.At).ThenBy(f => f.Cleaning.SourceId).Select(f => f.Cleaning)];
    }
}
