using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Leases;
using Casazen.Core.Regulatory;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services.LeaseDrafts;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// What the path of one lease needs, as read in the statement of a lease list (LR-01b): scalars of the lease and the three aggregates of
/// its rent. Dates are stored values (midnight UTC of the Europe/Rome day), instants where the column is one.
/// </summary>
internal sealed record LeaseFlowRow(
    Guid Id,
    Guid PropertyId,
    LeaseStatus Status,
    LeaseOrigin Origin,
    LeaseContractType ContractType,
    DateTime StartDate,
    DateTime CurrentTermEndDate,
    DateTime? NoticeOn,
    DateTime? TerminatedOn,
    DateTime? StipulaDate,
    DateTime? PropertyDeliveryDate,
    DateTime? QuesturaCommunicationDate,
    bool HasExtraEuTenant,
    DateTime? SentForSignatureAt,
    DateTime? ReviewConfirmedAt,
    int OverdueRentCount,
    decimal OverdueRentAmount,
    DateOnly? OldestOverdueRentDueDate,
    DateOnly? NextRentDueDate);

/// <summary>
/// Gathers what <see cref="LeaseFlow"/> needs and asks it (LR-01b): the one place that turns the leases of a list into their stage and next
/// step, used by the lease list and by the overview, so the two never disagree. The rows come from the statement of the list; here are
/// read the two things it does not carry, and only for the leases that need them, in a number of statements that does not depend on how
/// many leases there are: whether the registered leases have an active rent plan (one statement), and the drafts (the leases with their
/// parties, their properties and the energy certificates: three statements). A list with no registered lease and no draft sends none.
/// </summary>
/// <remarks>
/// The drafts are evaluated the way the guided draft does (<see cref="LeaseDraftProgress"/>, the same values and the same facts of the
/// property), without the range of the canone concordato: that is a calculation per draft, not a read, and the list does not make one per
/// row. For a draft whose only open problem is a rent outside the range the list says "review" and the review itself says what is wrong.
/// </remarks>
internal static class LeaseFlowReader
{
    public static async Task<IReadOnlyDictionary<Guid, LeaseFlowResult>> DeriveAsync(
        AppDbContext db, IReadOnlyCollection<LeaseFlowRow> rows, DateTime todayInRome, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(rows);

        var results = new Dictionary<Guid, LeaseFlowResult>(rows.Count);
        if (rows.Count == 0)
            return results;

        var today = DateOnly.FromDateTime(todayInRome);
        var withPlan = await ActivePlansAsync(db, rows, cancellationToken);
        var drafts = await DraftFlowsAsync(db, rows, today, cancellationToken);

        foreach (var row in rows)
        {
            results[row.Id] = LeaseFlow.Derive(
                ToInput(row, today, todayInRome, withPlan.Contains(row.Id), drafts.GetValueOrDefault(row.Id)));
        }

        return results;
    }

    private static LeaseFlowInput ToInput(LeaseFlowRow row, DateOnly today, DateTime todayInRome, bool hasActivePlan, LeaseDraftFlow? draft) => new()
    {
        Today = today,
        Status = row.Status,
        Origin = row.Origin,
        ContractType = row.ContractType,
        StartDate = RomeCalendar.DateInRome(row.StartDate),
        CurrentTermEndDate = RomeCalendar.DateInRome(row.CurrentTermEndDate),
        NoticeOn = Day(row.NoticeOn),
        TerminatedOn = Day(row.TerminatedOn),
        RegistrationDeadline = Day(RliRegistrationDeadline.Resolve(row.Status, row.StipulaDate, row.StartDate, todayInRome)),
        PropertyDeliveryDate = Day(row.PropertyDeliveryDate),
        QuesturaCommunicationDate = Day(row.QuesturaCommunicationDate),
        HasExtraEuTenant = row.HasExtraEuTenant,
        SentForSignatureOn = Day(row.SentForSignatureAt),
        ReviewConfirmed = row.ReviewConfirmedAt is not null,
        Draft = draft,
        HasActiveRentPlan = hasActivePlan,
        OverdueRentCount = row.OverdueRentCount,
        OverdueRentAmount = row.OverdueRentAmount,
        OldestOverdueRentDueDate = row.OldestOverdueRentDueDate,
        NextRentDueDate = row.NextRentDueDate,
    };

    private static DateOnly? Day(DateTime? value) => value is { } instant ? RomeCalendar.DateInRome(instant) : null;

    /// <summary>The registered leases that have a rent plan that is active (the others cannot ask for one: they are not running yet or are over).</summary>
    private static async Task<HashSet<Guid>> ActivePlansAsync(
        AppDbContext db, IReadOnlyCollection<LeaseFlowRow> rows, CancellationToken cancellationToken)
    {
        var registered = rows.Where(r => r.Status == LeaseStatus.Registered).Select(r => r.Id).ToList();
        if (registered.Count == 0)
            return [];

        var planned = await db.RentSchedules
            .AsNoTracking()
            .Where(s => s.IsActive && registered.Contains(s.LeaseContractId))
            .Select(s => s.LeaseContractId)
            .ToListAsync(cancellationToken);
        return [.. planned];
    }

    /// <summary>The steps of every draft of the list, from the values it keeps and the facts of its property.</summary>
    private static async Task<Dictionary<Guid, LeaseDraftFlow>> DraftFlowsAsync(
        AppDbContext db, IReadOnlyCollection<LeaseFlowRow> rows, DateOnly today, CancellationToken cancellationToken)
    {
        var flows = new Dictionary<Guid, LeaseDraftFlow>();
        var draftIds = rows.Where(r => r.Status == LeaseStatus.Draft).Select(r => r.Id).ToList();
        if (draftIds.Count == 0)
            return flows;

        var drafts = await db.LeaseContracts
            .AsNoTracking()
            .Include(l => l.Parties)
            .Where(l => draftIds.Contains(l.Id))
            .ToListAsync(cancellationToken);
        var propertyIds = drafts.Select(l => l.PropertyId).Distinct().ToList();

        var properties = (await db.Properties
                .AsNoTracking()
                .Where(p => propertyIds.Contains(p.Id))
                .Select(p => new { p.Id, p.RentalMode, p.CadastralSheet, p.CadastralParcel, p.CadastralCategory, p.CadastralIncome })
                .ToListAsync(cancellationToken))
            .ToDictionary(p => p.Id);

        // Like the guided draft, the existence of the certificate is enough here: the one that was uploaded was recognised as official.
        var withApe = (await db.PropertyDocuments
                .AsNoTracking()
                .Where(d => d.DocumentType == DocumentType.Ape && propertyIds.Contains(d.PropertyId))
                .Select(d => d.PropertyId)
                .Distinct()
                .ToListAsync(cancellationToken))
            .ToHashSet();

        foreach (var lease in drafts)
        {
            var facts = properties.TryGetValue(lease.PropertyId, out var property)
                ? new LeaseDraftFacts
                {
                    PropertyExists = true,
                    HasApe = withApe.Contains(lease.PropertyId),
                    HasCadastralData = new Property
                    {
                        CadastralSheet = property.CadastralSheet,
                        CadastralParcel = property.CadastralParcel,
                        CadastralCategory = property.CadastralCategory,
                        CadastralIncome = property.CadastralIncome,
                    }.HasCadastralData,
                    PropertyIsShortMode = property.RentalMode == RentalMode.Short,
                }
                : new LeaseDraftFacts { PropertyExists = false };

            flows[lease.Id] = LeaseDraftProgress.Compute(new LeaseDraftProgressInput
            {
                Origin = lease.Origin,
                Status = lease.Status,
                WizardMaxStep = lease.WizardMaxStep,
                ReviewConfirmedAt = lease.ReviewConfirmedAt,
                SentForSignatureAt = lease.SentForSignatureAt,
                Values = LeaseDraftProjection.ReadValues(lease),
                Facts = facts,
                Today = today,
            });
        }

        return flows;
    }
}
