using Casazen.Core.Authorization;
using Casazen.Core.DTOs.Leases;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Leases;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="ILongRentPropertyService"/>
/// <remarks>
/// <para>Four statements, whatever the number of properties: the properties (the columns the list needs, active and in long-term mode,
/// narrowed to the scope in SQL like every other list of the host), their APE documents, their scheduled changes of mode, and, only for who
/// may read the leases, the lease list of the area (the same read the Contratti page makes, so the stage of a lease is the one it shows). The
/// rest is in memory.</para>
/// <para>Which lease holds a property is the rule of the page: a lease in force (stage <c>Active</c> or <c>Expiring</c>, so registered and
/// not over by the end of the period it is in, LR-14), the one that has started last; a lease registered ahead (a renewal that starts next
/// month) does not hold the property yet while another one runs. With none, the lease on its way that was touched last. No name, no
/// amount and no other datum of a tenant is read or returned.</para>
/// </remarks>
public sealed class LongRentPropertyService(
    AppDbContext db,
    ILeaseContractRepository leaseRepository,
    TimeProvider? timeProvider = null) : ILongRentPropertyService
{
    private static readonly LeaseStage[] InForce = [LeaseStage.Active, LeaseStage.Expiring];

    private static readonly LeaseStage[] OnItsWay =
        [LeaseStage.Draft, LeaseStage.DataPending, LeaseStage.InReview, LeaseStage.SentForSignature, LeaseStage.Signed];

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyList<LongRentPropertyRow>> GetPropertiesAsync(
        HostScope scope, LongRentPropertyQuery query, bool includeLeases, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);

        // Nothing is known of the state of a property whose leases the caller may not read: a view of the let or the free ones holds nothing.
        if (!includeLeases && query.View != LongRentPropertyView.All)
            return [];

        var todayDate = _clock.TodayInRome();
        var today = DateOnly.FromDateTime(todayDate);

        var properties = await db.Properties
            .AsNoTracking()
            .Where(p => p.OrgId == scope.OrgId && p.IsActive && p.RentalMode == RentalMode.Long)
            .InScope(scope)
            .OrderBy(p => p.Name)
            .ThenBy(p => p.Id)
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.Address,
                p.City,
                p.Unit,
                p.CadastralSheet,
                p.CadastralParcel,
                p.CadastralCategory,
                p.CadastralIncome,
            })
            .ToListAsync(cancellationToken);
        if (properties.Count == 0)
            return [];

        var ids = properties.Select(p => p.Id).ToList();
        var apes = (await db.PropertyDocuments
                .AsNoTracking()
                .Where(d => d.DocumentType == DocumentType.Ape && ids.Contains(d.PropertyId))
                .Select(d => new { d.PropertyId, d.ApeCode, d.ApeEnergyClass, d.UploadedAt })
                .ToListAsync(cancellationToken))
            .ToLookup(d => d.PropertyId);
        var changes = (await db.PropertyModeChanges
                .AsNoTracking()
                .Where(c => c.Status == PropertyModeChangeStatus.Scheduled && ids.Contains(c.PropertyId))
                .Select(c => new { c.PropertyId, c.ToMode, c.EffectiveDate })
                .ToListAsync(cancellationToken))
            .GroupBy(c => c.PropertyId)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.EffectiveDate).First());
        var leases = includeLeases
            ? (await leaseRepository.GetSummariesAsync(scope, new LeaseListQuery(), todayDate)).ToLookup(l => l.PropertyId)
            : null;

        var term = query.NormalizedSearch;
        var rows = new List<LongRentPropertyRow>(properties.Count);
        foreach (var property in properties)
        {
            if (term is not null
                && !Contains(property.Name, term) && !Contains(property.Address, term) && !Contains(property.City, term) && !Contains(property.Unit, term))
            {
                continue;
            }

            var (ape, apeClass) = ApeOf(apes[property.Id].Select(d => (d.ApeCode, d.ApeEnergyClass, d.UploadedAt)));
            var (current, preparing) = leases is null ? (null, null) : LeasesOf(leases[property.Id], today);
            LongRentPropertyStatus? status = leases is null
                ? null
                : current is not null ? LongRentPropertyStatus.Rented
                : preparing is not null ? LongRentPropertyStatus.InPreparation
                : LongRentPropertyStatus.Free;

            if (query.View == LongRentPropertyView.Rented && status != LongRentPropertyStatus.Rented)
                continue;
            if (query.View == LongRentPropertyView.Free && status == LongRentPropertyStatus.Rented)
                continue;

            rows.Add(new LongRentPropertyRow(
                property.Id,
                property.Name,
                property.Address,
                property.City,
                property.Unit,
                ape,
                ape == LongRentApeStatus.Identified,
                apeClass,
                Property.IsCadastralDataComplete(property.CadastralSheet, property.CadastralParcel, property.CadastralCategory, property.CadastralIncome),
                status,
                current is null ? null : new LongRentPropertyLease(current.Id, current.Stage, current.CurrentTermEndDate, current.NoticeOn, FreeFrom(current)),
                preparing is null ? null : new LongRentPreparingLease(preparing.Id, preparing.Stage),
                changes.TryGetValue(property.Id, out var change) ? new LongRentScheduledModeChange(change.ToMode, change.EffectiveDate) : null));
        }

        return rows;
    }

    private static bool Contains(string? value, string term) => value is not null && value.Contains(term, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The certificate as the contract needs it: identified when one document has both its code and its energy class (the rule the lease
    /// draft and <see cref="ApeComplianceService"/> both accept without reading the file again), the class being the one of the latest such
    /// document; to be confirmed when there are documents and none is identified; missing without any.
    /// </summary>
    private static (LongRentApeStatus Status, string? EnergyClass) ApeOf(IEnumerable<(string? Code, string? EnergyClass, DateTime UploadedAt)> documents)
    {
        var all = documents.ToList();
        var identified = all
            .Where(d => PropertyDocument.IsApeIdentified(d.Code, d.EnergyClass))
            .OrderByDescending(d => d.UploadedAt)
            .FirstOrDefault();
        if (identified.EnergyClass is not null)
            return (LongRentApeStatus.Identified, identified.EnergyClass.Trim().ToUpperInvariant());
        return (all.Count > 0 ? LongRentApeStatus.ToConfirm : LongRentApeStatus.Missing, null);
    }

    /// <summary>The lease that holds the property and, only when none does, the lease on its way that was touched last.</summary>
    private static (LeaseSummaryDto? Current, LeaseSummaryDto? Preparing) LeasesOf(IEnumerable<LeaseSummaryDto> own, DateOnly today)
    {
        var all = own.ToList();
        var inForce = all
            .Where(l => l.Stage is { } stage && InForce.Contains(stage))
            .OrderByDescending(l => l.StartDate)
            .ThenByDescending(l => l.CreatedAt)
            .ToList();
        var current = inForce.FirstOrDefault(l => DateOnly.FromDateTime(l.StartDate) <= today) ?? inForce.LastOrDefault();
        if (current is not null)
            return (current, null);

        var preparing = all
            .Where(l => l.Stage is { } stage && OnItsWay.Contains(stage))
            .OrderByDescending(l => l.UpdatedAt)
            .ThenBy(l => l.Id)
            .FirstOrDefault();
        return (null, preparing);
    }

    /// <summary>
    /// The day the property is free again, when the lease in force does not go on: a notice or an early termination is recorded, or its type
    /// does not renew. A lease nobody gave notice of renews by itself, so the property stays let and there is no such day.
    /// </summary>
    private static DateOnly? FreeFrom(LeaseSummaryDto lease)
    {
        var ends = lease.NoticeOn is not null || lease.TerminatedOn is not null || !LeaseRenewal.Renews(lease.ContractType);
        return ends ? DateOnly.FromDateTime(lease.CurrentTermEndDate).AddDays(1) : null;
    }
}
