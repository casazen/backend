using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Chooses the backup suppliers of a new request (SP-BKP): the active suppliers of the same category that cover the same comune, best
/// score first (the ranking of <see cref="ISupplierMatchService"/>), without the primary and the host's own org, without the ones that
/// cannot take work (a new version of the Terms to accept) and without the ones whose time is not free. At most
/// <c>Suppliers:ServiceRequests:MaxBackupSuppliers</c> of them; fewer, or none, when the comune has fewer.
/// </summary>
/// <remarks>
/// <para><b>Free at that time.</b> A backup does not hold the slot until it has the job, so the check is the one a take makes, made earlier:
/// no job the supplier already took overlaps the time (always), and, when the host picked the time among the primary's published slots
/// (<paramref name="slotPlanned"/>), the time is one of the backup's own slots too (hours, time off, closed days, notice, daily maximum,
/// buffer, what it already has). It is checked again, under the supplier's calendar lock, when the job is assigned to it.</para>
/// <para><b>Cost.</b> Each candidate costs a few reads, so at most <see cref="MaxCandidatesExamined"/> are looked at; the ranking comes first,
/// so those are the best ones.</para>
/// </remarks>
public sealed class ServiceRequestBackupSelector(
    ISupplierMatchService matcher,
    ISupplierAgendaService agenda,
    ILegalDocumentService legalDocuments,
    AppDbContext db,
    ILogger<ServiceRequestBackupSelector> logger)
{
    /// <summary>The most candidates examined for one request, whatever the number of backups wanted.</summary>
    public const int MaxCandidatesExamined = 12;

    public async Task<IReadOnlyList<SupplierProfile>> SelectAsync(
        Property property,
        string category,
        ServiceRequestUrgency urgency,
        Guid hostOrgId,
        Guid primarySupplierOrgId,
        DateTime startUtc,
        DateTime endUtc,
        bool slotPlanned,
        int max,
        CancellationToken cancellationToken)
    {
        var ranked = await matcher.RankActiveAsync(ComuneTarget.ForProperty(property), category, urgency, cancellationToken);
        if (ranked.Count == 0)
            return [];

        var tosVersion = legalDocuments.GetTos().Version;
        var chosen = new List<SupplierProfile>(max);
        var examined = 0;
        foreach (var candidate in ranked)
        {
            var profile = candidate.Profile;
            if (profile.OrgId == primarySupplierOrgId
                || profile.OrgId == hostOrgId
                || SupplierActivationRules.TosState(profile, tosVersion).BlocksActions)
            {
                continue;
            }

            if (++examined > MaxCandidatesExamined)
                break;

            if (!await IsFreeAsync(profile.OrgId, startUtc, endUtc, slotPlanned, cancellationToken))
                continue;

            chosen.Add(profile);
            if (chosen.Count == max)
                break;
        }

        logger.LogInformation(
            "Backup suppliers of a {Category} request of org {OrgId}: {Chosen} chosen among {Ranked} that cover the comune",
            category, hostOrgId, chosen.Count, ranked.Count);
        return chosen;
    }

    /// <summary>
    /// True when the supplier could take the job at that time: no job it took overlaps it and, when the time must be one of the
    /// published slots, it is one of the supplier's.
    /// </summary>
    public async Task<bool> IsFreeAsync(
        Guid supplierOrgId,
        DateTime startUtc,
        DateTime endUtc,
        bool slotPlanned,
        CancellationToken cancellationToken)
    {
        if (await OverlapsATakenJobAsync(db, supplierOrgId, startUtc, endUtc, exceptRequestId: null, cancellationToken))
            return false;

        if (!slotPlanned)
            return true;

        var minutes = (int)(endUtc - startUtc).TotalMinutes;
        var day = RomeCalendar.DateInRome(startUtc);
        var plans = await agenda.PlanAsync(supplierOrgId, day, day, new SupplierSlotQuery(minutes), exceptRequestId: null, cancellationToken);
        return plans.SelectMany(plan => plan.Slots).Any(slot => slot.StartUtc == startUtc);
    }

    /// <summary>
    /// True when a job the supplier already took (<c>PresoInCarico</c> or <c>InCorso</c>) overlaps <c>[startUtc, endUtc)</c>: the rule of
    /// 409 <c>supplier_commitment_conflict</c>, without raising it. ServiceRequest has two parties and no tenant filter (TN-2 allow-list):
    /// the supplier org is an explicit predicate.
    /// </summary>
    internal static async Task<bool> OverlapsATakenJobAsync(
        AppDbContext db,
        Guid supplierOrgId,
        DateTime startUtc,
        DateTime endUtc,
        Guid? exceptRequestId,
        CancellationToken cancellationToken)
    {
        var others = await db.ServiceRequests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.SupplierOrgId == supplierOrgId
                && r.Id != exceptRequestId
                && (r.Status == ServiceRequestStatus.PresoInCarico || r.Status == ServiceRequestStatus.InCorso)
                && r.ScheduledStartUtc != null
                && r.ScheduledEndUtc != null)
            .Select(r => new { Start = r.ScheduledStartUtc!.Value, End = r.ScheduledEndUtc!.Value })
            .ToListAsync(cancellationToken);

        var mine = new SupplierInterval(startUtc, endUtc);
        return others.Any(other => other.End > other.Start && mine.Overlaps(new SupplierInterval(other.Start, other.End)));
    }
}
