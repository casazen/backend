using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IOwnerReconciliationService"/>
/// <remarks>
/// <para><b>Channels.</b> From the live lines of the owner ledger of the month that come from a booking (<c>BookingRevenue</c> and <c>ChannelFee</c>; the booking is the source of a
/// booking line and of a flat fee, and the reference of a payment line): the stays posted, their gross and their commission by where it comes from (the rule version of the line:
/// <c>payload</c>, <c>channel-rule:{id}</c>, <c>channel-default</c>), in the currency of the stays. For a portal, a stay it collected (not <c>OtaCompanyCollect = false</c>) is expected
/// to be paid out its gross less its commission; what the payment ledger records for it is the payments collected in CasaZen (recorded by hand, net of refunds) or, when there are
/// none, the net the channel manager reported (<see cref="Booking.OtaNetAmount"/>), never derived. A stay whose payout is not the one expected is a difference by its booking.</para>
/// <para><b>Stays not posted.</b> An active stay of the month with no collected payment (the booking path of the ledger: a portal, the agency's own site, a booking entered by hand)
/// and a gross to post that has no live revenue line in the ledger, in any month: told by its booking. A stay paid in CasaZen is posted by its payments, on their day, and is never
/// told here.</para>
/// <para><b>Statements.</b> The published version of an owner (the newest published of the month, even superseded by a version not published yet, as the owner portal reads it) holds
/// the lines of the owner posted up to its cut-off: the lines posted after it, or all of them when no version is published, are the difference between the ledger and the
/// statements, listed by their stay (the lines of a booking of an owner together) or by their entry; what is left when they are taken out (a total that is not the one of the
/// lines of the version) is told by the version. So the listed differences always add up to the difference of the totals.</para>
/// <para>Every read names the org. Nothing is written. The logs carry ids and counts, never an amount.</para>
/// </remarks>
public sealed class OwnerReconciliationService(AppDbContext db, ILogger<OwnerReconciliationService> logger) : IOwnerReconciliationService
{
    /// <summary>The states of a payment that collected money (a refund moves it to <c>PartiallyRefunded</c> or <c>Refunded</c>): as the owner ledger reads them.</summary>
    private static readonly PaymentStatus[] CollectedStatuses = [PaymentStatus.Completed, PaymentStatus.PartiallyRefunded, PaymentStatus.Refunded];

    public async Task<OwnerReconciliation> GetAsync(Guid orgId, int year, int month, CancellationToken ct = default)
    {
        if (year is < 2000 or > 2100 || month is < 1 or > 12)
            throw new DomainRuleException(OwnerAccountingErrors.PeriodInvalid, "OwnerPeriodInvalid");

        var start = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddMonths(1);

        var lines = await LinesAsync(orgId, year, month, ct);
        var reversed = await ReversedAsync(orgId, lines, ct);
        var live = lines.Where(l => l.ReversesEntryId is null && !reversed.Contains(l.Id)).ToList();

        var bookingIds = lines.Select(BookingOf).Where(id => id is not null).Select(id => id!.Value).Distinct().ToList();
        var bookings = await BookingsAsync(orgId, bookingIds, ct);
        var paid = await PaymentsAsync(orgId, bookingIds, ct);

        var differences = new List<ReconciliationDifference>();
        var channels = ChannelRows(live, bookings, paid, differences);
        differences.AddRange(await StaysNotPostedAsync(orgId, start, end, ct));
        var statements = await StatementRowsAsync(orgId, year, month, lines, bookings, differences, ct);

        var ordered = differences
            .OrderBy(d => d.Kind)
            .ThenBy(d => d.Channel is null ? -1 : (int)d.Channel.Value)
            .ThenBy(d => d.Currency, StringComparer.Ordinal)
            .ThenBy(d => d.Reason, StringComparer.Ordinal)
            .ThenBy(d => d.Id)
            .ThenBy(d => d.OwnerId)
            .ToList();
        logger.LogInformation(
            "Reconciliation {Year}-{Month} of org {OrgId}: {Channels} channel rows, {Statements} statement rows, {Differences} differences",
            year, month, orgId, channels.Count, statements.Count, ordered.Count);
        return new OwnerReconciliation(year, month, channels, statements, ordered);
    }

    // ---------------------------------------------------------------- the channels

    /// <summary>
    /// One row for each channel and currency with a stay posted in the month; the payout differences of the portals' stays are added to <paramref name="differences"/>.
    /// </summary>
    private static List<ChannelReconciliationRow> ChannelRows(
        IReadOnlyList<Line> live,
        IReadOnlyDictionary<Guid, BookingInfo> bookings,
        IReadOnlyDictionary<Guid, decimal> paid,
        List<ReconciliationDifference> differences)
    {
        var stays = live
            .Where(l => l.Type is OwnerLedgerEntryType.BookingRevenue or OwnerLedgerEntryType.ChannelFee)
            .Select(l => (Line: l, Booking: BookingOf(l)))
            .Where(x => x.Booking is { } id && bookings.ContainsKey(id))
            .GroupBy(x => (Booking: x.Booking!.Value, Currency: x.Line.OriginalCurrency))
            .Select(g => StayFigures.Of(bookings[g.Key.Booking], g.Key.Currency, g.Select(x => x.Line)))
            .ToList();

        var rows = new List<ChannelReconciliationRow>();
        var groups = stays
            .GroupBy(s => (s.Booking.Source, s.Currency))
            .OrderBy(g => (int)g.Key.Source)
            .ThenBy(g => g.Key.Currency, StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var channel = group.Key.Source;
            var currency = group.Key.Currency;
            var isPortal = FiscalCopy.IsOtaBookingSource(channel);
            var commissions = new ChannelCommissionSplit(group.Sum(s => s.FeeFromPortal), group.Sum(s => s.FeeFromRule), group.Sum(s => s.FeeFromDefault));

            decimal? expectedTotal = null;
            decimal? recordedTotal = null;
            decimal? differenceTotal = null;
            int? withoutPayout = null;
            int? hostCollected = null;
            if (isPortal)
            {
                var collected = group.Where(s => OwnerReconciliationRules.CollectedByPortal(s.Booking.OtaCompanyCollect)).ToList();
                hostCollected = group.Count() - collected.Count;
                var expected = 0m;
                var recorded = 0m;
                var missing = 0;
                foreach (var stay in collected.OrderBy(s => s.Booking.Id))
                {
                    var payout = paid.TryGetValue(stay.Booking.Id, out var collectedInCasaZen) ? collectedInCasaZen : stay.Booking.OtaNet;
                    expected += stay.Net;
                    if (payout is null)
                    {
                        // Nothing to expect from a stay that nets nothing (a stay for free): nothing is missing.
                        if (stay.Net > 0m)
                        {
                            missing++;
                            differences.Add(new ReconciliationDifference(
                                ReconciliationDifferenceKind.Stay, stay.Booking.Id, OwnerReconciliationReasons.PayoutNotRecorded, currency, stay.Net, null, -stay.Net, channel));
                        }

                        continue;
                    }

                    recorded += payout.Value;
                    if (payout.Value != stay.Net)
                    {
                        differences.Add(new ReconciliationDifference(
                            ReconciliationDifferenceKind.Stay, stay.Booking.Id, OwnerReconciliationReasons.PayoutDiffers, currency, stay.Net, payout.Value, payout.Value - stay.Net, channel));
                    }
                }

                expectedTotal = expected;
                recordedTotal = recorded;
                differenceTotal = recorded - expected;
                withoutPayout = missing;
            }

            rows.Add(new ChannelReconciliationRow(
                channel,
                currency,
                isPortal,
                group.Count(s => s.HasRevenue),
                group.Sum(s => s.Gross),
                commissions,
                expectedTotal,
                recordedTotal,
                differenceTotal,
                withoutPayout,
                hostCollected));
        }

        return rows;
    }

    /// <summary>
    /// The active stays that check out in the month, with no collected payment and a gross to post (the booking path of the ledger, as <c>PlanAsync</c> reads them), that have no live
    /// revenue line from their booking in any month.
    /// </summary>
    private async Task<List<ReconciliationDifference>> StaysNotPostedAsync(Guid orgId, DateTime start, DateTime end, CancellationToken ct)
    {
        var stays = (await db.Bookings.AsNoTracking()
                .Where(b => b.OrgId == orgId && b.CheckOutDate >= start && b.CheckOutDate < end
                    && AlloggiatiStatusRules.ActiveBookingStatuses.Contains(b.Status) && b.NoShowAt == null
                    && !b.Payments.Any(p => CollectedStatuses.Contains(p.Status) && p.ProcessedAt != null))
                .Select(b => new { b.Id, b.Source, b.Property.Currency, b.BasePrice, b.TotalPrice, b.TouristTax })
                .ToListAsync(ct))
            .Select(b => new
            {
                b.Id,
                b.Source,
                Currency = StayCurrency.Normalize(b.Currency),
                Gross = OwnerLedgerRules.GrossOfStay(FiscalCopy.IsOtaBookingSource(b.Source), b.BasePrice, b.TotalPrice, b.TouristTax),
            })
            .Where(b => b.Gross > 0m)
            .ToList();
        if (stays.Count == 0)
            return [];

        var ids = stays.Select(s => s.Id).ToList();
        var posted = await db.OwnerLedgerEntries.AsNoTracking()
            .Where(e => e.OrgId == orgId && e.EntryType == OwnerLedgerEntryType.BookingRevenue && e.SourceType == OwnerLedgerSourceType.Booking
                && e.ReversesEntryId == null && e.SourceId != null && ids.Contains(e.SourceId.Value))
            .Select(e => new { e.Id, BookingId = e.SourceId!.Value })
            .ToListAsync(ct);
        var reversed = await ReversedAsync(orgId, posted.Select(e => e.Id).ToList(), ct);
        var postedBookings = posted.Where(e => !reversed.Contains(e.Id)).Select(e => e.BookingId).ToHashSet();

        return stays
            .Where(s => !postedBookings.Contains(s.Id))
            .Select(s => new ReconciliationDifference(
                ReconciliationDifferenceKind.Stay, s.Id, OwnerReconciliationReasons.StayNotPosted, s.Currency, s.Gross, 0m, -s.Gross, s.Source))
            .ToList();
    }

    // ---------------------------------------------------------------- the statements

    /// <summary>
    /// The published statements against the ledger, one row for each statement currency; the ledger lines the published statements do not hold, and the totals that are not the one
    /// of their lines, are added to <paramref name="differences"/>.
    /// </summary>
    private async Task<List<StatementReconciliationRow>> StatementRowsAsync(
        Guid orgId,
        int year,
        int month,
        IReadOnlyList<Line> lines,
        IReadOnlyDictionary<Guid, BookingInfo> bookings,
        List<ReconciliationDifference> differences,
        CancellationToken ct)
    {
        // The newest published version of each owner. Only the columns the totals need: the published file of a version is never read here.
        var published = (await db.OwnerStatementVersions.AsNoTracking()
                .Where(v => v.OrgId == orgId && v.PeriodYear == year && v.PeriodMonth == month && v.PublishedAt != null)
                .Select(v => new PublishedVersion(v.Id, v.PropertyOwnerId, v.VersionNumber, v.Currency, v.CutoffAt, v.ClosingBalance - v.OpeningBalance))
                .ToListAsync(ct))
            .GroupBy(v => v.OwnerId)
            .ToDictionary(g => g.Key, g => g.MaxBy(v => v.Number)!);

        var ledgerTotals = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var ledgerOwners = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);
        var publishedTotals = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var publishedCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            ledgerTotals[line.StatementCurrency] = ledgerTotals.GetValueOrDefault(line.StatementCurrency) + line.Amount;
            if (!ledgerOwners.TryGetValue(line.StatementCurrency, out var owners))
                ledgerOwners[line.StatementCurrency] = owners = [];
            owners.Add(line.OwnerId);
        }

        foreach (var version in published.Values)
        {
            publishedTotals[version.Currency] = publishedTotals.GetValueOrDefault(version.Currency) + version.Total;
            publishedCounts[version.Currency] = publishedCounts.GetValueOrDefault(version.Currency) + 1;
        }

        // The lines the published statement of their owner does not hold: posted after its cut-off, or no statement published at all.
        var notInStatement = lines
            .Where(l => !published.TryGetValue(l.OwnerId, out var version) || l.PostedAt > version.CutoffAt)
            .ToList();
        var listed = new Dictionary<(Guid Owner, string Currency), decimal>();
        foreach (var group in notInStatement.GroupBy(l => (l.OwnerId, l.StatementCurrency, Booking: BookingOf(l))))
        {
            var reason = published.ContainsKey(group.Key.OwnerId) ? OwnerReconciliationReasons.PostedAfterPublication : OwnerReconciliationReasons.StatementNotPublished;
            var currency = group.Key.StatementCurrency;
            var key = (group.Key.OwnerId, currency);
            listed[key] = listed.GetValueOrDefault(key) + group.Sum(l => l.Amount);

            // The lines of a booking of an owner are one difference (the stay), any other line is one by itself (the entry).
            if (group.Key.Booking is { } booking)
            {
                var channel = bookings.TryGetValue(booking, out var info) ? info.Source : (BookingSource?)null;
                var amount = group.Sum(l => l.Amount);
                differences.Add(new ReconciliationDifference(
                    ReconciliationDifferenceKind.Stay, booking, reason, currency, amount, 0m, -amount, channel, group.Key.OwnerId));
            }
            else
            {
                differences.AddRange(group.Select(l => new ReconciliationDifference(
                    ReconciliationDifferenceKind.Entry, l.Id, reason, currency, l.Amount, 0m, -l.Amount, null, l.OwnerId)));
            }
        }

        // What is left once those lines are taken out: the total of a published statement that is not the total of its lines, told by the version (so the listed differences
        // always add up to the difference of the totals, also for an owner with lines in more than one currency).
        foreach (var version in published.Values.OrderBy(v => v.OwnerId))
        {
            foreach (var currency in lines.Where(l => l.OwnerId == version.OwnerId).Select(l => l.StatementCurrency).Append(version.Currency).Distinct(StringComparer.Ordinal))
            {
                // The lines the statement was cut from (the ledger less what was posted after its cut-off) against the total it says.
                var held = lines.Where(l => l.OwnerId == version.OwnerId && l.StatementCurrency == currency).Sum(l => l.Amount) - listed.GetValueOrDefault((version.OwnerId, currency));
                var statement = currency == version.Currency ? version.Total : 0m;
                if (statement != held)
                {
                    differences.Add(new ReconciliationDifference(
                        ReconciliationDifferenceKind.Statement, version.Id, OwnerReconciliationReasons.StatementTotalDiffers, currency, held, statement, statement - held, null, version.OwnerId));
                }
            }
        }

        return ledgerTotals.Keys.Concat(publishedTotals.Keys)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(currency =>
            {
                var ledger = ledgerTotals.GetValueOrDefault(currency);
                var statement = publishedTotals.GetValueOrDefault(currency);
                return new StatementReconciliationRow(
                    currency,
                    ledgerOwners.TryGetValue(currency, out var owners) ? owners.Count : 0,
                    publishedCounts.GetValueOrDefault(currency),
                    ledger,
                    statement,
                    statement - ledger);
            })
            .ToList();
    }

    // ---------------------------------------------------------------- the reads

    /// <summary>Every line of the month, reversals included.</summary>
    private async Task<List<Line>> LinesAsync(Guid orgId, int year, int month, CancellationToken ct) =>
        await db.OwnerLedgerEntries.AsNoTracking()
            .Where(e => e.OrgId == orgId && e.PeriodYear == year && e.PeriodMonth == month)
            .Select(e => new Line(
                e.Id, e.PropertyOwnerId, e.EntryType, e.SourceType, e.SourceId, e.SourceReference, e.OriginalCurrency, e.OriginalAmount, e.StatementCurrency, e.Amount, e.RuleVersion,
                e.ReversesEntryId, e.PostedAt))
            .ToListAsync(ct);

    /// <summary>The lines among <paramref name="lines"/> that a reversal (in any month) reverses.</summary>
    private Task<HashSet<Guid>> ReversedAsync(Guid orgId, IReadOnlyList<Line> lines, CancellationToken ct) =>
        ReversedAsync(orgId, lines.Select(l => l.Id).ToList(), ct);

    private async Task<HashSet<Guid>> ReversedAsync(Guid orgId, List<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return [];

        var reversed = await db.OwnerLedgerEntries.AsNoTracking()
            .Where(e => e.OrgId == orgId && e.ReversesEntryId != null && ids.Contains(e.ReversesEntryId.Value))
            .Select(e => e.ReversesEntryId!.Value)
            .ToListAsync(ct);
        return reversed.ToHashSet();
    }

    /// <summary>The bookings the lines come from, also of a property deleted since (its history stays).</summary>
    private async Task<Dictionary<Guid, BookingInfo>> BookingsAsync(Guid orgId, List<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return [];

        var rows = await db.Bookings.AsNoTracking()
            .IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter])
            .Where(b => b.OrgId == orgId && ids.Contains(b.Id))
            .Select(b => new BookingInfo(b.Id, b.Source, b.OtaNetAmount, b.OtaCompanyCollect))
            .ToListAsync(ct);
        return rows.ToDictionary(b => b.Id);
    }

    /// <summary>What the payments collected in CasaZen for each booking come to, net of their refunds (the payment ledger's paid lines recorded here, by hand or by card).</summary>
    private async Task<Dictionary<Guid, decimal>> PaymentsAsync(Guid orgId, List<Guid> bookingIds, CancellationToken ct)
    {
        if (bookingIds.Count == 0)
            return [];

        var rows = await db.Payments.AsNoTracking()
            .IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter])
            .Where(p => p.OrgId == orgId && bookingIds.Contains(p.BookingId) && CollectedStatuses.Contains(p.Status))
            .Select(p => new { p.BookingId, Net = p.Amount - p.RefundedAmount })
            .ToListAsync(ct);
        return rows.GroupBy(p => p.BookingId).ToDictionary(g => g.Key, g => g.Sum(p => p.Net));
    }

    /// <summary>
    /// The booking a line comes from: the source of a booking line (the commission of a channel, the revenue and the flat fees of a stay posted from its booking) and the reference of
    /// a payment line (its booking); <c>null</c> for a charge, a correction, a wire, a payout or an owner invoice.
    /// </summary>
    private static Guid? BookingOf(Line line) => line.Source switch
    {
        OwnerLedgerSourceType.Booking or OwnerLedgerSourceType.MandateFlatFee => line.SourceId,
        OwnerLedgerSourceType.Payment => Guid.TryParseExact(line.SourceReference, "N", out var booking) ? booking : (Guid?)null,
        _ => null,
    };

    private sealed record Line(
        Guid Id,
        Guid OwnerId,
        OwnerLedgerEntryType Type,
        OwnerLedgerSourceType Source,
        Guid? SourceId,
        string? SourceReference,
        string OriginalCurrency,
        decimal OriginalAmount,
        string StatementCurrency,
        decimal Amount,
        string? RuleVersion,
        Guid? ReversesEntryId,
        DateTime PostedAt);

    private sealed record BookingInfo(Guid Id, BookingSource Source, decimal? OtaNet, bool? OtaCompanyCollect);

    private sealed record PublishedVersion(Guid Id, Guid OwnerId, int Number, string Currency, DateTime CutoffAt, decimal Total);

    /// <summary>What the live lines of one stay say in one currency: its gross, and its commission by where it comes from (positive amounts, the sum of the owners' shares).</summary>
    private sealed record StayFigures(BookingInfo Booking, string Currency, bool HasRevenue, decimal Gross, decimal FeeFromPortal, decimal FeeFromRule, decimal FeeFromDefault)
    {
        /// <summary>What the portal is expected to pay out for the stay: its gross less its commission.</summary>
        public decimal Net => Gross - FeeFromPortal - FeeFromRule - FeeFromDefault;

        public static StayFigures Of(BookingInfo booking, string currency, IEnumerable<Line> lines)
        {
            var all = lines.ToList();
            var revenue = all.Where(l => l.Type == OwnerLedgerEntryType.BookingRevenue).ToList();
            decimal FeeOf(ChannelCommissionSource source) =>
                -all.Where(l => l.Type == OwnerLedgerEntryType.ChannelFee && OwnerReconciliationRules.SourceOf(l.RuleVersion) == source).Sum(l => l.OriginalAmount);

            return new StayFigures(
                booking, currency, revenue.Count > 0, revenue.Sum(l => l.OriginalAmount),
                FeeOf(ChannelCommissionSource.Portal), FeeOf(ChannelCommissionSource.Rule), FeeOf(ChannelCommissionSource.Default));
        }
    }
}
