using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The annual Certificazione Unica data of the owners, built from the applicable withholding evidence of the payouts paid in the year and their reversals, of an Italian rule only
/// (E18/F01/US02/T01, NF-117, ⚖ I6, T2). One record for each recipient: <b>the owner who was paid</b>, or, for a property of several co-owners, the co-owner who signed the mandate
/// with everything on its name (the default), or each co-owner with its own share when the mandate says so (<see cref="ReportingOwners"/>). The records are made of the pieces of
/// the evidence (<see cref="TaxEvidenceLines"/>), so <b>the records add up to the evidence to the cent</b>, whatever the attribution: the shares of the co-owners add up to what the
/// signatory would have, and the totals of the year are the ones of the F24 of its months. Nothing is written and nothing is sent: the telematic record layout is not produced
/// (⚖ T2), the export carries every field the accountant's software needs.
/// </summary>
internal static class CuDatasetBuilder
{
    public static async Task<CuDataset> BuildAsync(AppDbContext db, Guid orgId, int year, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var evidence = await OwnerPayoutService.F24EvidenceQuery(db, orgId)
            .Where(e => e.PeriodYear == year)
            .OrderBy(e => e.CreatedAt)
            .ThenBy(e => e.Id)
            .ToListAsync(cancellationToken);
        var totalBase = evidence.Sum(e => e.GrossBase ?? 0m);
        var totalWithheld = evidence.Sum(e => e.Amount ?? 0m);
        var hash = OwnerPayoutService.HashOf(evidence);
        if (evidence.Count == 0)
            return new CuDataset(year, [], 0m, 0m, 0, hash);

        var lines = evidence.SelectMany(TaxEvidenceLines.Of).ToList();
        var propertyIds = lines.Where(l => l.PropertyId is not null).Select(l => l.PropertyId!.Value).Distinct().ToList();
        var owners = await ReportingOwners.ReadAsync(db, orgId, propertyIds, cancellationToken);

        // Each piece goes to the owner the withholding is reported on: the one who was paid, or the signatory of its property.
        var placed = lines
            .Select(line =>
            {
                var (owner, ownShare) = owners.ReportingOwnerOf(line.OwnerId, line.PropertyId, line.CheckOut ?? line.PaidOn);
                return (Line: line, Owner: owner, OwnShare: ownShare);
            })
            .ToList();
        var ownerIds = placed.SelectMany(p => new[] { p.Owner, p.Line.OwnerId }).Distinct().ToList();
        var people = await TaxReportingPeople.ReadAsync(db, orgId, ownerIds, propertyIds, cancellationToken);

        var records = new List<CuRecord>();
        foreach (var group in placed.GroupBy(p => p.Owner))
        {
            var pieces = group.Select(p => p.Line).ToList();
            var cuLines = Aggregate(pieces);
            var record = new CuRecord
            {
                OwnerId = group.Key,
                GrossBase = pieces.Sum(l => l.GrossBase),
                Withheld = pieces.Sum(l => l.Withheld),
                Evidence = pieces.Select(l => l.EvidenceId).Distinct().Count(),
                Recipient = people.RecipientOf(group.Key, PaymentDayOf(pieces, year)),
                Attribution = AttributionOf(group.Select(p => (p.Line.OwnerId, p.OwnShare)).ToList()),
                TaxCode = TaxCodeOf(pieces),
                RuleVersions = [.. pieces.Select(l => l.RuleVersion).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                Shares = SharesOf(pieces, people),
                Properties = [.. cuLines.Where(l => l.PropertyId is not null).Select(l => l.PropertyId!.Value).Distinct().Select(people.PropertyOf).Where(p => p is not null).Select(p => p!).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id)],
                Lines = cuLines,
            };

            // A recipient whose withholding was reversed in the same year is withheld nothing: no Certificazione Unica is due to it.
            if (record.GrossBase != 0m || record.Withheld != 0m || cuLines.Count > 0)
                records.Add(record);
        }

        return new CuDataset(
            year,
            [.. records.OrderBy(r => r.Recipient?.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.OwnerId)],
            totalBase,
            totalWithheld,
            evidence.Count,
            hash);
    }

    /// <summary>
    /// The pieces as the lines of a record: those of one stay of one owner paid in one month are one line (a decision and its reversal in the same month cancel out and leave none),
    /// by month, check-in and property.
    /// </summary>
    private static List<CuLine> Aggregate(IReadOnlyList<EvidenceLine> pieces) =>
        pieces
            .GroupBy(l => (l.OwnerId, l.Month, l.BookingId, l.PropertyId, l.CheckIn, l.CheckOut, l.Nights))
            .Select(g => new CuLine
            {
                BookingId = g.Key.BookingId,
                PropertyId = g.Key.PropertyId,
                CheckIn = g.Key.CheckIn,
                CheckOut = g.Key.CheckOut,
                Nights = g.Key.Nights,
                SourceOwnerId = g.Key.OwnerId,
                Month = g.Key.Month,
                GrossBase = g.Sum(l => l.GrossBase),
                Withheld = g.Sum(l => l.Withheld),
            })
            .Where(l => l.GrossBase != 0m || l.Withheld != 0m)
            .OrderBy(l => l.Month)
            .ThenBy(l => l.CheckIn ?? DateOnly.MaxValue)
            .ThenBy(l => l.PropertyId)
            .ThenBy(l => l.BookingId)
            .ThenBy(l => l.SourceOwnerId)
            .ToList();

    /// <summary>Several owners in one record: the signatory with the others on it; none of them: an owner on its own, or a co-owner that kept its own share.</summary>
    private static string AttributionOf(IReadOnlyList<(Guid Source, bool OwnShare)> sources)
    {
        if (sources.Select(s => s.Source).Distinct().Count() > 1)
            return CuAttributions.Signatory;

        return sources.Any(s => s.OwnShare) ? CuAttributions.Share : CuAttributions.Owner;
    }

    /// <summary>What each owner puts into a record that adds up several (the signatory with its co-owners); nothing for a record of one owner.</summary>
    private static List<CuShare> SharesOf(IReadOnlyList<EvidenceLine> pieces, TaxReportingPeople people)
    {
        if (pieces.Select(l => l.OwnerId).Distinct().Count() < 2)
            return [];

        return pieces
            .GroupBy(l => l.OwnerId)
            .Select(g => new CuShare { OwnerId = g.Key, Name = people.NameOf(g.Key), GrossBase = g.Sum(l => l.GrossBase), Withheld = g.Sum(l => l.Withheld) })
            .OrderBy(s => s.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.OwnerId)
            .ToList();
    }

    /// <summary>The tax code of the F24 of the record: the only one of its pieces, or those of the pieces side by side when a record has more than one.</summary>
    private static string? TaxCodeOf(IReadOnlyList<EvidenceLine> pieces)
    {
        var codes = pieces.Select(l => l.TaxCode).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!.Trim()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return codes.Count == 0 ? null : string.Join(", ", codes);
    }

    /// <summary>The day the profile of the recipient is read on: the last payment of the record inside the year (never after its end), so the data is the one in force when it was paid.</summary>
    private static DateOnly PaymentDayOf(IReadOnlyList<EvidenceLine> pieces, int year)
    {
        var last = pieces.Max(l => l.PaidOn);
        var end = new DateOnly(year, 12, 31);
        return last > end ? end : last;
    }
}
