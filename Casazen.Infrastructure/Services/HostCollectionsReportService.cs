using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IHostCollectionsReportService"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>The Home's money.</b> The payments are the ones <see cref="PaymentCashRules"/> counts as collected, read the way the Home reads its "incassato"
/// (<c>HostDashboardService.GetCollectedAsync</c>): the payments of the scope, optionally of one property, and the total is the sum of what each brings
/// (<see cref="PaymentCashRules.Collected"/>) rounded once, so the report and the Home give one number for the same days.</item>
/// <item><b>Reach in SQL.</b> Every query of the org narrows with <c>InScope</c> (the property of the booking of the payment); a property outside the scope is
/// one 404, like the other reports.</item>
/// <item><b>Fixed number of queries.</b> The collected payments, the credits of the card provider, the credits of the channels: three for the collections; the
/// stays of CasaZen's money and the stays of the channel manager's: two for the balances. Never one for each payment or booking.</item>
/// <item><b>Nothing invented.</b> The credits are what the capture of the provider figures (<c>Payment.Provider*</c>) and the import of the reservations
/// (<c>Booking.Ota*</c>) stored: what is not stored is "not available", never zero. Nothing is captured, read from Octorate or sent here.</item>
/// <item><b>No guest.</b> The balances are read without the guest: no query joins it, so no name or contact can reach a row.</item>
/// </list>
/// </remarks>
public sealed class HostCollectionsReportService(
    AppDbContext db,
    IConfiguration configuration,
    TimeProvider? timeProvider = null) : IHostCollectionsReportService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<HostCollectionsReport> GetCollectionsAsync(
        HostScope scope,
        HostReportQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);

        var period = HostReportRules.PeriodOf(query.From, query.To);
        await EnsurePropertyInScopeAsync(scope, query.PropertyId, cancellationToken);

        // What came in on those days, by cash: the Home's rule, with only the columns the cuts and the credits need. The payments are read one at a time and added up as
        // they come, never all held: a period with many payments needs the memory of one with a few.
        var sums = new HostCollectionsSums(period);
        var cardPayments = 0;
        var cardPaymentsWithoutFigures = 0;
        await foreach (var row in PaymentsInScope(scope, query.PropertyId)
                           .Where(PaymentCashRules.CollectedBetween(period.From, period.To))
                           .Select(p => new CollectedRow(
                               p.ProcessedAt ?? p.CreatedAt,
                               p.Amount,
                               p.RefundedAmount,
                               p.Method,
                               p.Booking.Source,
                               p.StripePaymentIntentId != null,
                               p.ProviderNetAmount != null && p.ProviderAvailableOn != null))
                           .AsAsyncEnumerable()
                           .WithCancellation(cancellationToken))
        {
            sums.Add(new HostCollectedPayment(row.CollectedAt, row.Amount, row.RefundedAmount, row.Method, row.Source));
            if (!row.IsCard)
                continue;

            cardPayments++;
            if (!row.HasFigures)
                cardPaymentsWithoutFigures++;
        }

        var figures = sums.Figures();
        var credits = new HostCredits(
            await GetStripeCreditsAsync(scope, query, cardPayments, cardPaymentsWithoutFigures, cancellationToken),
            await GetChannelCreditsAsync(scope, query, period, cancellationToken));
        return new HostCollectionsReport(period, figures.Total, figures.ByDate, figures.ByMethod, figures.ByChannel, credits);
    }

    public async Task<HostBalancesToCollect> GetBalancesToCollectAsync(
        HostScope scope,
        HostBalancesQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);

        await EnsurePropertyInScopeAsync(scope, query.PropertyId, cancellationToken);
        var now = _clock.GetUtcNow();
        var today = DateOnly.FromDateTime(RomeCalendar.TodayAt(now));

        // The stays that hold their dates: not cancelled and not a checkout hold that expired (the one definition of the booking site), and not ended before the horizon of
        // the report (24 months, like every report): the list does not grow with the whole history of the host.
        var cutoff = CheckoutHolds.CutoffAt(now.UtcDateTime, CheckoutHolds.GetTtlMinutes(configuration));
        var since = HostCollectionsRules.BalancesSince(today);
        var firstCheckOut = RomeCalendar.StartOfDayUtc(since);
        var holding = BookingsInScope(scope, query.PropertyId)
            .Where(CheckoutHolds.OccupiesDates(cutoff))
            .Where(b => b.CheckOutDate >= firstCheckOut);

        // CasaZen's money: the total of the stay against what its payments collected (the Home's rule), added up in SQL. A portal collects the money of its
        // stays itself, so a stay of a channel is never here.
        var ofCasaZen = await holding
            .Where(HostCollectionsRules.HoldsCasaZenMoney(db.OctorateBookingSyncs))
            .Where(b => b.TotalPrice > b.Payments.AsQueryable().Where(PaymentCashRules.IsCollected()).Sum(PaymentCashRules.CollectedAmount()))
            .Select(b => new BalanceRow(
                b.Id,
                b.BookingCode,
                b.PropertyId,
                b.Property.Name,
                b.Property.Currency,
                b.Source,
                b.CheckInDate,
                b.CheckOutDate,
                b.TotalPrice,
                b.Payments.AsQueryable().Where(PaymentCashRules.IsCollected()).Sum(PaymentCashRules.CollectedAmount()),
                null,
                null))
            .ToListAsync(cancellationToken);

        // The channel manager's money: the balance of the reservation, stored as read. Nothing when the channel collects it (the host has nothing to ask the
        // guest for) and nothing when no balance was reported: a balance is never worked out from the price (ADR-033-R2).
        var ofChannel = await holding
            .Where(HostCollectionsRules.HoldsChannelManagerMoney(db.OctorateBookingSyncs))
            .Where(b => b.OtaBalanceAmount > 0m && b.OtaCompanyCollect != true)
            .Select(b => new BalanceRow(
                b.Id,
                b.BookingCode,
                b.PropertyId,
                b.Property.Name,
                b.Property.Currency,
                b.Source,
                b.CheckInDate,
                b.CheckOutDate,
                b.TotalPrice,
                0m,
                b.OtaPaidAmount,
                b.OtaBalanceAmount))
            .ToListAsync(cancellationToken);

        var rows = ofCasaZen
            .Select(r => CasaZenRowOf(r, today))
            .Concat(ofChannel.Select(r => ChannelRowOf(r, today)))
            .OrderBy(r => r.CheckIn)
            .ThenBy(r => r.BookingCode, StringComparer.Ordinal)
            .ThenBy(r => r.BookingId)
            .ToList();
        return new HostBalancesToCollect(today, rows, HostCollectionsRules.TotalsOf(rows), since);
    }

    // What the card provider credited on the days of the report, from the figures the capture stored on the payments (nothing is asked of Stripe here).
    // The payments collected in the period that have no figures yet are counted, not shown as zero.
    private async Task<HostStripeCredits> GetStripeCreditsAsync(
        HostScope scope,
        HostReportQuery query,
        int cardPayments,
        int cardPaymentsWithoutFigures,
        CancellationToken cancellationToken)
    {
        // The day of a credit is the date the provider reported (UTC), as the payment ledger shows it.
        var first = DateTime.SpecifyKind(query.From.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var end = DateTime.SpecifyKind(query.To.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var settled = await PaymentsInScope(scope, query.PropertyId)
            .Where(p => p.StripePaymentIntentId != null && PaymentProviderFeeService.SettledStatuses.Contains(p.Status))
            .Where(p => p.ProviderNetAmount != null && p.ProviderAvailableOn >= first && p.ProviderAvailableOn < end)
            .Select(p => new SettlementRow(p.ProviderAvailableOn!.Value, p.ProviderNetAmount!.Value, p.ProviderBalanceCurrency ?? p.Booking.Property.Currency))
            .ToListAsync(cancellationToken);

        return HostCollectionsRules.StripeCreditsOf(
            settled.Select(r => new HostStripeSettlement(r.AvailableOn, r.Net, r.Currency)),
            _clock.TodayInRomeAsDateOnly(),
            cardPayments,
            cardPaymentsWithoutFigures);
    }

    // What the channels collected for the stays that arrive in the period, as the channel manager reports it: the net of each stay the channel collected
    // itself (companyCollect). Only the import writes those figures; most stay empty until Octorate documents a net amount.
    private async Task<HostChannelCredits> GetChannelCreditsAsync(
        HostScope scope,
        HostReportQuery query,
        HostDashboardPeriod period,
        CancellationToken cancellationToken)
    {
        var stays = await BookingsInScope(scope, query.PropertyId)
            .Where(StayKpiRules.IsConfirmedStay())
            .Where(StayKpiRules.ArrivesBetween(period.From, period.To))
            .Where(b => !string.IsNullOrWhiteSpace(b.OctorateReservationId) && b.OtaCompanyCollect == true)
            .Select(b => new HostChannelCreditStay(b.Source, b.Property.Currency, b.OtaNetAmount))
            .ToListAsync(cancellationToken);
        return HostCollectionsRules.ChannelCreditsOf(stays);
    }

    private static HostBalanceRow CasaZenRowOf(BalanceRow row, DateOnly today)
    {
        var checkOut = RomeCalendar.DateInRome(row.CheckOut);
        return new HostBalanceRow(
            row.Id,
            row.BookingCode,
            row.PropertyId,
            row.PropertyName,
            row.Source,
            RomeCalendar.DateInRome(row.CheckIn),
            checkOut,
            StayCurrency.Normalize(row.Currency),
            row.TotalPrice,
            row.Collected,
            HostCollectionsRules.ToCollect(row.TotalPrice, row.Collected),
            HostCollectionsRules.IsOverdue(checkOut, today),
            HostBalanceBasis.Payments);
    }

    private static HostBalanceRow ChannelRowOf(BalanceRow row, DateOnly today)
    {
        var checkOut = RomeCalendar.DateInRome(row.CheckOut);
        return new HostBalanceRow(
            row.Id,
            row.BookingCode,
            row.PropertyId,
            row.PropertyName,
            row.Source,
            RomeCalendar.DateInRome(row.CheckIn),
            checkOut,
            StayCurrency.Normalize(row.Currency),
            row.TotalPrice > 0m ? row.TotalPrice : null,
            row.OtaPaid,
            row.OtaBalance!.Value,
            HostCollectionsRules.IsOverdue(checkOut, today),
            HostBalanceBasis.ChannelManager);
    }

    // The payments of the scope, optionally of one property: the very query of the Home's collected figure.
    private IQueryable<Payment> PaymentsInScope(HostScope scope, Guid? propertyId)
    {
        var payments = db.Payments.AsNoTracking().Where(p => p.OrgId == scope.OrgId).InScope(scope);
        return propertyId is { } id ? payments.Where(p => p.Booking.PropertyId == id) : payments;
    }

    // Bookings of the scope: the org and, unless org-wide, the properties the caller reaches; of one property when asked.
    private IQueryable<Booking> BookingsInScope(HostScope scope, Guid? propertyId)
    {
        var bookings = db.Bookings.AsNoTracking().Where(b => b.OrgId == scope.OrgId).InScope(scope);
        return propertyId is { } id ? bookings.Where(b => b.PropertyId == id) : bookings;
    }

    // One 404 for a property that is missing, another org's or not reached by the caller: nothing tells them apart.
    private async Task EnsurePropertyInScopeAsync(HostScope scope, Guid? propertyId, CancellationToken cancellationToken)
    {
        if (propertyId is not { } id)
            return;

        var reachable = await db.Properties
            .AsNoTracking()
            .Where(p => p.OrgId == scope.OrgId && p.Id == id)
            .InScope(scope)
            .AnyAsync(cancellationToken);
        if (!reachable)
            throw new NotFoundException($"Property {id} not found") { MessageKey = "PropertyNotFound" };
    }

    private sealed record CollectedRow(
        DateTime CollectedAt,
        decimal Amount,
        decimal RefundedAmount,
        PaymentMethod Method,
        BookingSource Source,
        bool IsCard,
        bool HasFigures);

    private sealed record SettlementRow(DateTime AvailableOn, decimal Net, string? Currency);

    private sealed record BalanceRow(
        Guid Id,
        string BookingCode,
        Guid PropertyId,
        string PropertyName,
        string? Currency,
        BookingSource Source,
        DateTime CheckIn,
        DateTime CheckOut,
        decimal TotalPrice,
        decimal Collected,
        decimal? OtaPaid,
        decimal? OtaBalance);
}
