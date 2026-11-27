using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The communication of the short-let contracts handled by the agency without withholding by the agency (E18/F01/US02/T02, NF-117, ⚖ T2). The contracts are the stays that the payouts of
/// the year decided (the facts of their evidence, one definition of what a stay is, <see cref="PayoutFactRules.EvaluateStay"/>): <b>a qualifying short let that the agency did not withhold on</b>
/// (the portal already withheld, or the agency neither collected nor handled the money), never one the agency withheld on (an applicable decision with the stay in its base: that withholding
/// is in the Certificazione Unica and the 770). The decision of a payout that was reversed decides nothing: its stays are open again until the next payout decides them, so they are in the
/// communication of the year that decides them. A stay that is no short-let regime (an owner who is a business, a stay of more than the longest short let, a property outside Italy) is not
/// in it either. The lessor is the owner the withholding would be reported on (<see cref="ReportingOwners"/>), the consideration is the stay's gross without the tourist tax, in euro
/// as the ledger posted it (the owner's share of it, or the sum of the co-owners on the signatory). Every query names the org; nothing is written.
/// </summary>
internal static class ContractsCommunicationBuilder
{
    public static async Task<ContractsCommunication> BuildAsync(AppDbContext db, Guid orgId, int year, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var decisions = await db.TaxObligationEvidence.AsNoTracking()
            .Where(e => e.OrgId == orgId && e.PeriodYear == year && e.ReversesEvidenceId == null)
            .Where(OwnerPayoutService.IsItalianRule)
            .OrderBy(e => e.CreatedAt)
            .ThenBy(e => e.Id)
            .ToListAsync(cancellationToken);
        if (decisions.Count == 0)
            return new ContractsCommunication(year, []);

        var decisionIds = decisions.Select(e => e.Id).ToList();
        var reversed = (await db.TaxObligationEvidence.AsNoTracking()
                .Where(e => e.OrgId == orgId && e.ReversesEvidenceId != null && decisionIds.Contains(e.ReversesEvidenceId.Value))
                .Select(e => e.ReversesEvidenceId!.Value)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var stays = new List<DecidedStay>();
        foreach (var decision in decisions.Where(d => !reversed.Contains(d.Id)))
        {
            // The evidence of before the facts were read from the ledger has typed facts and names no stay: there is no contract to read from it.
            if (PayoutFactRules.FromJson(decision.FactsJson) is not { } facts)
                continue;

            foreach (var stay in facts.Stays.Where(s => s.Qualifying && !(decision.Decision == TaxDecision.Applicable && s.InBase)))
                stays.Add(new DecidedStay(decision.PropertyOwnerId, stay, ReasonOf(decision, stay)));
        }

        if (stays.Count == 0)
            return new ContractsCommunication(year, []);

        var propertyIds = stays.Where(s => s.Stay.PropertyId is not null).Select(s => s.Stay.PropertyId!.Value).Distinct().ToList();
        var owners = await ReportingOwners.ReadAsync(db, orgId, propertyIds, cancellationToken);
        var placed = stays
            .Select(s => (Decided: s, Lessor: owners.ReportingOwnerOf(s.OwnerId, s.Stay.PropertyId, s.Stay.CheckOut).OwnerId))
            .ToList();
        var people = await TaxReportingPeople.ReadAsync(db, orgId, [.. placed.Select(p => p.Lessor).Distinct()], propertyIds, cancellationToken);

        // One contract for each lessor and stay: the shares of the co-owners the signatory takes on are one consideration.
        var rows = placed
            .GroupBy(p => (p.Lessor, p.Decided.Stay.BookingId))
            .Select(group =>
            {
                var first = group.First().Decided.Stay;
                return new ContractCommunicationRow(
                    group.Key.Lessor,
                    people.RecipientOf(group.Key.Lessor, first.CheckOut),
                    first.PropertyId is { } propertyId ? people.PropertyOf(propertyId) : null,
                    first.BookingId,
                    first.Channel,
                    first.CheckIn,
                    first.CheckOut,
                    first.Nights,
                    first.GrossCurrency,
                    group.Sum(p => p.Decided.Stay.Gross),
                    group.First().Decided.Reason);
            })
            .OrderBy(r => r.Lessor.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.LessorId)
            .ThenBy(r => r.Property?.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.CheckIn)
            .ThenBy(r => r.BookingId)
            .ToList();
        return new ContractsCommunication(year, rows);
    }

    /// <summary>Why the agency did not withhold on the stay: what its facts say, or, when its decision has no withholding at all, the reason of the decision.</summary>
    private static string ReasonOf(TaxObligationEvidence decision, PayoutStayFact stay) =>
        stay.InBase ? decision.ReasonCode : stay.Reason;

    private sealed record DecidedStay(Guid OwnerId, PayoutStayFact Stay, string Reason);
}
