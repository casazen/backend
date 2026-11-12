using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using DocumentKind = Casazen.Core.Entities.Enums.FiscalDocumentKind;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IBookingPaymentLedgerService"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>Payments.</b> A row with a Stripe PaymentIntent is a card line (fee, net and credit date from the capture of E06/F04/US01/T02); a bank transfer
/// and a cash row are manual lines; the status maps to the state (completed is paid, pending and processing are to collect).</item>
/// <item><b>Channels.</b> From the OTA amounts of the imported booking (ADR-033-R2): a paid line «collected by the channel» when the paid amount is stored,
/// a line to collect when a positive balance is, the commission and the net only when stored; nothing when they are all missing.</item>
/// <item><b>Document.</b> The fiscal document of the booking (issued by CasaZen or imported from Octorate), its latest one that is not a credit note.</item>
/// </list>
/// </remarks>
public sealed class BookingPaymentLedgerService(
    AppDbContext db,
    IOctorateApiClient octorate,
    IOctorateOrgScope orgScope,
    IFeatureFlags featureFlags,
    ILogger<BookingPaymentLedgerService> logger) : IBookingPaymentLedgerService
{
    public async Task<PaymentLedgerBooking?> GetForBookingAsync(HostScope scope, Guid bookingId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var booking = await Bookings(scope).Where(b => b.Id == bookingId).Select(BookingRowOf).FirstOrDefaultAsync(ct);
        if (booking is null)
            return null;

        var documents = await DocumentsOfAsync([booking.Id], ct);
        var payments = await PaymentLinesAsync(scope, new PaymentListCriteria(BookingId: booking.Id), ct);
        var lines = payments.Select(p => LineOf(p, documents.GetValueOrDefault(p.BookingId))).ToList();

        var degraded = false;
        var collections = booking.OctorateReservationId is { } reservation && featureFlags.IsEnabled(FeatureFlags.OctorateConnector)
            ? await CollectionsAsync(scope.OrgId, booking.PropertyId, reservation, ct)
            : null;
        if (collections is { Failed: true })
            degraded = true;

        if (collections is { Payments: { Count: > 0 } read })
        {
            // A payment CasaZen recorded on the reservation (its transaction is the one stored on the payment) comes back in the reservation's payments: it is
            // the line of the payment above, never a second one (the amount on Octorate is net of the tourist tax, so it cannot be matched by amount).
            var mirrored = payments.Where(p => p.OctorateTransactionId is not null).Select(p => p.OctorateTransactionId!).ToHashSet(StringComparer.Ordinal);
            lines.AddRange(read
                .Where(p => p.Transaction is null || !mirrored.Contains(p.Transaction))
                .Select(p => CollectionLineOf(booking, p, documents.GetValueOrDefault(booking.Id))));
        }
        else
        {
            lines.AddRange(OtaLinesOf(booking, documents.GetValueOrDefault(booking.Id)));
        }

        return new PaymentLedgerBooking(
            booking.Id,
            booking.BookingCode,
            booking.Source.ToString(),
            StayCurrency.Normalize(booking.Currency),
            lines.OrderByDescending(l => l.Date).ThenBy(l => l.PaymentId).ToList(),
            degraded);
    }

    public async Task<PaymentLedgerPage> ListAsync(HostScope scope, PaymentListCriteria criteria, int page, int pageSize, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(criteria);
        page = Math.Clamp(page, 1, PaymentListPage.MaxPage);
        pageSize = Math.Clamp(pageSize, 1, PaymentListPage.MaxPageSize);

        var payments = await PaymentLinesAsync(scope, criteria, ct);
        var otaBookings = await OtaBookingsAsync(scope, criteria, ct);
        var documents = await DocumentsOfAsync(payments.Select(p => p.BookingId).Concat(otaBookings.Select(b => b.Id)).Distinct().ToList(), ct);

        var lines = payments.Select(p => LineOf(p, documents.GetValueOrDefault(p.BookingId)))
            .Concat(otaBookings.SelectMany(b => OtaLinesOf(b, documents.GetValueOrDefault(b.Id))))
            .OrderByDescending(l => l.Date)
            .ThenBy(l => l.BookingCode, StringComparer.Ordinal)
            .ThenBy(l => l.PaymentId)
            .ToList();

        return new PaymentLedgerPage(lines.Skip((page - 1) * pageSize).Take(pageSize).ToList(), lines.Count, page, pageSize);
    }

    // ---------------------------------------------------------------- reads (scoped in SQL)

    private IQueryable<Booking> Bookings(HostScope scope) =>
        db.Bookings
            .AsNoTracking()
            .IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter])
            .Where(b => b.OrgId == scope.OrgId)
            .InScope(scope);

    private async Task<List<PaymentRow>> PaymentLinesAsync(HostScope scope, PaymentListCriteria criteria, CancellationToken ct)
    {
        var query = db.Payments
            .AsNoTracking()
            .IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter])
            .Where(p => p.OrgId == scope.OrgId)
            .InScope(scope);
        if (criteria.PropertyId is { } propertyId)
            query = query.Where(p => p.Booking.PropertyId == propertyId);
        if (criteria.BookingId is { } bookingId)
            query = query.Where(p => p.BookingId == bookingId);
        if (criteria.From is { } from)
        {
            var start = RomeCalendar.StartOfDayUtc(from);
            query = query.Where(p => (p.ProcessedAt ?? p.CreatedAt) >= start);
        }

        if (criteria.To is { } to && to != DateOnly.MaxValue)
        {
            var end = RomeCalendar.StartOfDayUtc(to.AddDays(1));
            query = query.Where(p => (p.ProcessedAt ?? p.CreatedAt) < end);
        }

        return await query
            .OrderByDescending(p => p.ProcessedAt ?? p.CreatedAt)
            .Take(PaymentLedgerPage.MaxRowsPerSource)
            .Select(p => new PaymentRow(
                p.Id,
                p.BookingId,
                p.Booking.BookingCode,
                p.Booking.Property.Currency,
                p.Amount,
                p.Status,
                p.Method,
                p.StripePaymentIntentId,
                p.StripeIntentOnPlatform,
                p.ProcessedAt ?? p.CreatedAt,
                p.ProviderFeeAmount,
                p.ProviderNetAmount,
                p.ProviderBalanceCurrency,
                p.ProviderAvailableOn,
                p.OctorateTransactionId))
            .ToListAsync(ct);
    }

    /// <summary>The bookings of a channel of the period that carry an OTA amount (by their check-in day): the lines Octorate collected.</summary>
    private async Task<List<BookingRow>> OtaBookingsAsync(HostScope scope, PaymentListCriteria criteria, CancellationToken ct)
    {
        var query = Bookings(scope).Where(b => b.OtaPaidAmount != null || b.OtaBalanceAmount != null);
        if (criteria.PropertyId is { } propertyId)
            query = query.Where(b => b.PropertyId == propertyId);
        if (criteria.BookingId is { } bookingId)
            query = query.Where(b => b.Id == bookingId);
        if (criteria.From is { } from)
            query = query.Where(b => b.CheckInDate >= from.ToDateTime(TimeOnly.MinValue));
        if (criteria.To is { } to && to != DateOnly.MaxValue)
            query = query.Where(b => b.CheckInDate < to.AddDays(1).ToDateTime(TimeOnly.MinValue));

        return await query
            .OrderByDescending(b => b.CheckInDate)
            .Take(PaymentLedgerPage.MaxRowsPerSource)
            .Select(BookingRowOf)
            .ToListAsync(ct);
    }

    private static readonly System.Linq.Expressions.Expression<Func<Booking, BookingRow>> BookingRowOf = b => new BookingRow(
        b.Id,
        b.PropertyId,
        b.BookingCode,
        b.Source,
        b.Property.Currency,
        b.CheckInDate,
        b.OctorateReservationId,
        b.OtaPaidAmount,
        b.OtaBalanceAmount,
        b.OtaCommission,
        b.OtaNetAmount,
        b.OtaCompanyCollect);

    /// <summary>The latest fiscal document of each booking that is not a credit note (CasaZen or Octorate, issued first).</summary>
    private async Task<Dictionary<Guid, PaymentLedgerDocument>> DocumentsOfAsync(IReadOnlyCollection<Guid> bookingIds, CancellationToken ct)
    {
        if (bookingIds.Count == 0)
            return [];

        var rows = await db.FiscalDocuments
            .AsNoTracking()
            .Where(d => d.BookingId != null && bookingIds.Contains(d.BookingId.Value)
                        && d.Kind != DocumentKind.CreditNote && d.Status != FiscalDocumentStatus.Discarded)
            .Select(d => new { BookingId = d.BookingId!.Value, d.Id, d.Number, d.Kind, d.Status, d.PaymentState, d.CreatedAt, d.Source, d.Total })
            .ToListAsync(ct);

        // The state of a document CasaZen issued is the current one (what was collected for the booking, net of the refunds), as the document API gives it: the
        // state saved with the draft is not shown. A document Octorate issued keeps the state Octorate gave it.
        var collected = await FiscalDocumentService.CollectedByBookingAsync(db, rows.Select(r => r.BookingId).Distinct().ToList(), ct);
        return rows
            .GroupBy(d => d.BookingId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(d => d.Status == FiscalDocumentStatus.Issued ? 0 : 1).ThenByDescending(d => d.CreatedAt).Select(d =>
                    new PaymentLedgerDocument(
                        d.Id,
                        d.Number,
                        d.Kind.ToString(),
                        d.Status.ToString(),
                        (d.Source == FiscalDocumentSource.CasaZen ? FiscalDocumentService.PaymentStateOf(collected.GetValueOrDefault(d.BookingId), d.Total) : d.PaymentState)
                        .ToString())).First());
    }

    /// <summary>The collections of the reservation from Octorate, under the org's scope; <c>Failed</c> when the read did not answer.</summary>
    private async Task<Collections?> CollectionsAsync(Guid orgId, Guid propertyId, string reservationId, CancellationToken ct)
    {
        var accommodation = await db.OctorateListingMappings
            .AsNoTracking()
            .Where(m => m.OrgId == orgId && m.PropertyId == propertyId && m.Status == OctorateMappingStatus.Confirmed)
            .Select(m => m.OctorateAccommodationId)
            .FirstOrDefaultAsync(ct);
        if (accommodation is null)
            return null;

        try
        {
            using (orgScope.Use(orgId))
                return new Collections(await octorate.GetReservationPaymentsAsync(accommodation, reservationId, ct), Failed: false);
        }
        catch (OctorateException ex)
        {
            logger.LogWarning("Collections of the Octorate reservation of property {PropertyId} not read ({Error}): stored amounts answered", propertyId, ex.GetType().Name);
            return new Collections([], Failed: true);
        }
    }

    // ---------------------------------------------------------------- lines

    private static PaymentLedgerLine LineOf(PaymentRow p, PaymentLedgerDocument? document) => new(
        p.StripePaymentIntentId is not null
            ? PaymentLedgerKind.Card
            : p.Method switch
            {
                PaymentMethod.BankTransfer => PaymentLedgerKind.BankTransfer,
                PaymentMethod.CashOnArrival => PaymentLedgerKind.Cash,
                _ => PaymentLedgerKind.Other,
            },
        p.BookingId,
        p.BookingCode,
        p.Id,
        RomeCalendar.DateInRome(p.SettledAt),
        p.Amount,
        p.ProviderFee,
        p.ProviderNet,
        p.ProviderCurrency,
        p.ProviderAvailableOn is { } available ? DateOnly.FromDateTime(available) : null,
        StayCurrency.Normalize(p.Currency),
        p.Status switch
        {
            PaymentStatus.Completed => PaymentLedgerState.Paid,
            PaymentStatus.Pending or PaymentStatus.Processing => PaymentLedgerState.ToCollect,
            PaymentStatus.Refunded => PaymentLedgerState.Refunded,
            PaymentStatus.PartiallyRefunded => PaymentLedgerState.PartiallyRefunded,
            PaymentStatus.Failed => PaymentLedgerState.Failed,
            _ => PaymentLedgerState.Canceled,
        },
        p.StripePaymentIntentId is not null && p.StripeIntentOnPlatform ? PaymentLedgerCollector.Platform : PaymentLedgerCollector.Host,
        document);

    /// <summary>The lines of the OTA amounts stored on the booking: never one Octorate did not give.</summary>
    private static IEnumerable<PaymentLedgerLine> OtaLinesOf(BookingRow b, PaymentLedgerDocument? document)
    {
        var day = DateOnly.FromDateTime(b.CheckInDate);
        var currency = StayCurrency.Normalize(b.Currency);
        var collector = b.OtaCompanyCollect == false ? PaymentLedgerCollector.Host : PaymentLedgerCollector.Channel;
        if (b.OtaPaidAmount is { } paid)
        {
            yield return new PaymentLedgerLine(
                PaymentLedgerKind.Ota, b.Id, b.BookingCode, null, day, paid, b.OtaCommission, b.OtaNetAmount, b.OtaCommission is null ? null : currency,
                null, currency, PaymentLedgerState.Paid, collector, document);
        }

        if (b.OtaBalanceAmount is > 0m and { } balance)
        {
            yield return new PaymentLedgerLine(
                PaymentLedgerKind.Ota, b.Id, b.BookingCode, null, day, balance, null, null, null, null, currency, PaymentLedgerState.ToCollect, collector, document);
        }
    }

    private static PaymentLedgerLine CollectionLineOf(BookingRow b, OctoratePayment payment, PaymentLedgerDocument? document)
    {
        var currency = StayCurrency.Normalize(b.Currency);
        var collector = payment.CompanyCollect?.ToUpperInvariant() switch
        {
            "COMPANY" => PaymentLedgerCollector.Channel,
            "HOTEL" => PaymentLedgerCollector.Host,
            _ => PaymentLedgerCollector.Unknown,
        };
        return new PaymentLedgerLine(
            PaymentLedgerKind.Ota, b.Id, b.BookingCode, null, payment.Date ?? DateOnly.FromDateTime(b.CheckInDate), payment.Amount,
            null, null, null, null, currency, PaymentLedgerState.Paid, collector, document);
    }

    private sealed record PaymentRow(
        Guid Id,
        Guid BookingId,
        string BookingCode,
        string? Currency,
        decimal Amount,
        PaymentStatus Status,
        PaymentMethod Method,
        string? StripePaymentIntentId,
        bool StripeIntentOnPlatform,
        DateTime SettledAt,
        decimal? ProviderFee,
        decimal? ProviderNet,
        string? ProviderCurrency,
        DateTime? ProviderAvailableOn,
        string? OctorateTransactionId);

    private sealed record BookingRow(
        Guid Id,
        Guid PropertyId,
        string BookingCode,
        BookingSource Source,
        string? Currency,
        DateTime CheckInDate,
        string? OctorateReservationId,
        decimal? OtaPaidAmount,
        decimal? OtaBalanceAmount,
        decimal? OtaCommission,
        decimal? OtaNetAmount,
        bool? OtaCompanyCollect);

    private sealed record Collections(IReadOnlyList<OctoratePayment> Payments, bool Failed);
}
