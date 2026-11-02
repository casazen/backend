using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IHostSupplierCatalogService"/>
/// <remarks>
/// <para><b>One rule for the offer and the check.</b> The slots are the plan of <see cref="ISupplierAgendaService.PlanAsync(Guid, DateOnly, DateOnly, SupplierSlotQuery, CancellationToken)"/>
/// for <see cref="SupplierServiceForRequest.ToSlotQuery"/>: the query <c>ServiceRequestService</c> checks the time of a request with
/// (<c>ResolveCreateSchedule</c>, then <c>EnsureSlotFreeAsync</c>). The days are the window of the public page and of the supplier's own
/// preview (<see cref="PublicSlotWindow"/>), cut at the supplier's horizon. Nothing is cached: the host reads a few days of one
/// supplier, once per choice.</para>
/// <para><b>Tenancy and privacy.</b> The property is read with the org of the host as a predicate (the soft-delete filter stays); the
/// supplier's profile and services are not tenant-filtered (hosts of every org read active suppliers) and only the published
/// services come out, through <see cref="ISupplierServiceCatalogService"/>, which is the only code that touches that table. The slots
/// carry two instants and nothing of the supplier's agenda: no reason, no job, no person.</para>
/// </remarks>
public sealed class HostSupplierCatalogService(
    AppDbContext db,
    ISupplierServiceCatalogService catalog,
    ISupplierAgendaService agenda,
    ISupplierComuneMatcher comuneMatcher,
    TimeProvider? timeProvider = null) : IHostSupplierCatalogService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyList<HostSupplierService>> ListServicesAsync(
        Guid hostOrgId,
        Guid propertyId,
        Guid supplierOrgId,
        string? category,
        CancellationToken cancellationToken = default)
    {
        // A value that is not a category code is a 422 before any lookup, as in the supplier picker.
        var code = string.IsNullOrWhiteSpace(category) ? null : ServiceCategories.Require(category);

        await EnsureSupplierForPropertyAsync(hostOrgId, propertyId, supplierOrgId, cancellationToken);

        var published = await catalog.ListPickerServicesAsync([supplierOrgId], cancellationToken);
        return published.TryGetValue(supplierOrgId, out var services)
            ? services
                .Where(s => code is null || string.Equals(s.Category, code, StringComparison.Ordinal))
                .Select(s => new HostSupplierService(
                    s.Id,
                    s.Name,
                    s.Category,
                    s.DurationMinutes,
                    s.PriceFromCents,
                    s.PriceUnit,
                    s.RequiresQuote,
                    SupplierServiceForRequest.EstimateOf(s.RequiresQuote, s.PriceUnit, s.PriceFromCents)))
                .ToList()
            : [];
    }

    public async Task<HostSupplierSlots> GetSlotsAsync(
        Guid hostOrgId,
        Guid propertyId,
        Guid supplierOrgId,
        Guid serviceId,
        DateOnly? date,
        CancellationToken cancellationToken = default)
    {
        await EnsureSupplierForPropertyAsync(hostOrgId, propertyId, supplierOrgId, cancellationToken);

        // Another supplier's service, and a deleted one, are answered like a missing one; a draft or a paused one cannot be requested.
        var service = await catalog.FindForRequestAsync(supplierOrgId, serviceId, cancellationToken)
            ?? throw SupplierServiceCatalogErrors.ServiceNotFound(serviceId);
        if (!service.IsRequestable)
        {
            throw new DomainRuleException(
                ServiceRequestErrorCodes.ServiceUnavailable, ServiceRequestErrorCodes.ServiceUnavailableMessageKey);
        }

        var today = _clock.TodayInRomeAsDateOnly();
        var rules = await agenda.GetRulesAsync(supplierOrgId, cancellationToken);
        var bookableUntil = today.AddDays(rules.HorizonDays);

        // A service without a duration has no slot to compute (the host gives the time and the length), and a first day past the
        // horizon is an empty window: no day, not an error.
        if (service.ToSlotQuery() is not { } query
            || PublicSlotWindow.RangeOf(today, bookableUntil, date, HostSupplierSlotLimits.Days) is not { } range)
            return new HostSupplierSlots(service.Id, service.DurationMinutes, null, null, []);

        var plans = await agenda.PlanAsync(supplierOrgId, range.First, range.Last, query, cancellationToken);
        return new HostSupplierSlots(
            service.Id,
            service.DurationMinutes,
            range.First,
            range.Last,
            plans.SelectMany(plan => plan.Slots).ToList());
    }

    /// <summary>
    /// The rules of the creation of a request that come before the service: the property is the host's, the supplier exists, is active
    /// and covers the comune of the property (by ISTAT code when the property has a chosen comune, by its written city otherwise).
    /// </summary>
    private async Task EnsureSupplierForPropertyAsync(
        Guid hostOrgId,
        Guid propertyId,
        Guid supplierOrgId,
        CancellationToken cancellationToken)
    {
        // IgnoreQueryFilters (tenant only): scoped by the explicit OrgId predicate, as the creation of a request does; another org's
        // property answers like a missing one and the soft-delete filter stays.
        var property = await db.Properties
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == propertyId && p.OrgId == hostOrgId, cancellationToken)
            ?? throw new NotFoundException($"Property {propertyId} not found for the services of a supplier")
            {
                Code = ServiceRequestErrorCodes.PropertyNotFound,
                MessageKey = ServiceRequestErrorCodes.PropertyNotFoundMessageKey,
            };

        // SupplierProfile is keyed by the supplier org and not tenant-filtered; scoped by the supplier org id.
        var supplier = await db.SupplierProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(sp => sp.OrgId == supplierOrgId, cancellationToken)
            ?? throw new NotFoundException($"Supplier {supplierOrgId} not found")
            {
                Code = ServiceRequestErrorCodes.SupplierNotFound,
                MessageKey = ServiceRequestErrorCodes.SupplierNotFoundMessageKey,
            };

        if (supplier.Status != SupplierStatus.Active)
        {
            throw new DomainRuleException(
                ServiceRequestErrorCodes.SupplierInactive, ServiceRequestErrorCodes.SupplierInactiveMessageKey);
        }

        if (!await comuneMatcher.CoversAsync(supplier, ComuneTarget.ForProperty(property), cancellationToken))
        {
            throw new DomainRuleException(
                ServiceRequestErrorCodes.SupplierOutsideComune, ServiceRequestErrorCodes.SupplierOutsideComuneMessageKey);
        }
    }
}
