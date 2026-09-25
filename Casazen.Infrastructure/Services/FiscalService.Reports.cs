using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Core.Utilities;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Reports of the fiscal area for the accountant (CO-19, A5-23): summary per property and taxpayer with the tax estimated
/// only where fiscale.md documents the computation (cedolare secca), OTA withholding detail, tourist tax per comune and
/// month from the amounts recorded by the tourist tax engine (BK-03). Documents in <see cref="FiscalReportDocuments"/>.
/// </summary>
public partial class FiscalService
{
    public async Task<AnnualIncomeReport> GetAnnualReportAsync(
        HostScope scope,
        int taxYear,
        FiscalReportPeriod? period = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ValidateTaxYear(taxYear);
        var range = ValidateYearPeriod(taxYear, period);
        var org = await LoadReportOrgAsync(scope.OrgId, cancellationToken);
        var view = await LoadYearAsync(scope.OrgId, org.FiscalCode, taxYear, trackAssignments: false, cancellationToken);

        var payments = await SettledIn(ScopedPayments(scope), range).ToListAsync(cancellationToken);

        // Every property shown in the fiscal area for the year, plus any other one with income in the period.
        var reportProperties = view.Candidates
            .Where(p => scope.OwnerId is null || p.OwnerId == scope.OwnerId)
            .ToDictionary(p => p.Id, p => p.Name);
        foreach (var property in payments.Select(p => p.Booking.Property))
            reportProperties.TryAdd(property.Id, property.Name);

        var lines = new List<AnnualIncomeLine>();
        foreach (var (propertyId, name) in reportProperties.OrderBy(p => p.Value, StringComparer.CurrentCulture).ThenBy(p => p.Key))
        {
            var propertyPayments = payments.Where(p => p.Booking.PropertyId == propertyId).ToList();
            view.Assignments.TryGetValue(propertyId, out var assignment);
            var taxpayer = view.TaxpayerOfAny(propertyId);
            lines.Add(BuildIncomeLine(propertyId, name, assignment?.Regime, taxpayer, propertyPayments));
        }

        var taxpayers = view.Taxpayers
            .Select(t => (Taxpayer: t, Lines: lines.Where(l => l.TaxpayerIndex == t.Index).ToList()))
            .Where(x => x.Lines.Count > 0)
            .Select(x => new AnnualTaxpayerTotals(
                x.Taxpayer.Index,
                x.Taxpayer.ToSummary().FiscalCodeMasked,
                x.Taxpayer.IsOrgTaxProfile,
                x.Taxpayer.ThresholdExceeded,
                x.Lines.Count,
                x.Lines.Sum(l => l.RentalIncome),
                x.Lines.Sum(l => l.Withholding),
                x.Lines.Sum(l => l.EstimatedTax ?? 0m)))
            .ToList();

        var totals = new AnnualIncomeTotals(
            lines.Sum(l => l.GrossIncome),
            lines.Sum(l => l.Withholding),
            lines.Sum(l => l.Net),
            lines.Sum(l => l.TouristTax),
            lines.Sum(l => l.RentalIncome),
            lines.Sum(l => l.TaxableIncome ?? 0m),
            lines.Sum(l => l.EstimatedTax ?? 0m),
            lines.Count(l => l.EstimatedTax is null && l.GrossIncome > 0));

        return new AnnualIncomeReport(
            taxYear,
            FiscalCopy.PackLabel,
            FiscalCopy.Disclaimer,
            lines,
            totals,
            range,
            org.Name,
            _clock.TodayInRomeAsDateOnly(),
            taxpayers,
            ReportRules());
    }

    public async Task<WithholdingReport> GetWithholdingReportAsync(
        HostScope scope,
        int taxYear,
        FiscalReportPeriod? period = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ValidateTaxYear(taxYear);
        var range = ValidateYearPeriod(taxYear, period);
        var org = await LoadReportOrgAsync(scope.OrgId, cancellationToken);

        var payments = await SettledIn(ScopedPayments(scope), range)
            .Where(p => p.WithholdingTaxApplied)
            .ToListAsync(cancellationToken);

        var lines = payments
            .Select(p =>
            {
                var paidAt = p.ProcessedAt ?? p.CreatedAt;
                return new WithholdingLine(
                    p.Id,
                    p.Booking.PropertyId,
                    p.Booking.Source.ToString(),
                    paidAt,
                    ReportableGross(p),
                    ReportableWithholding(p),
                    ReportableNet(p),
                    p.Booking.Property.Name,
                    BookingCodes.Format(p.Booking.BookingCode),
                    RomeCalendar.DateInRome(paidAt),
                    p.WithholdingSource);
            })
            .OrderBy(l => l.PaidAt)
            .ThenBy(l => l.PaymentId)
            .ToList();

        var byOta = lines
            .GroupBy(l => l.Source)
            .Select(g => new WithholdingOtaBucket(g.Key, g.Sum(x => x.Gross), g.Sum(x => x.Withholding), g.Sum(x => x.Net), g.Count()))
            .OrderBy(b => b.Source, StringComparer.Ordinal)
            .ToList();

        return new WithholdingReport(
            taxYear,
            FiscalCopy.PackLabel,
            byOta,
            lines,
            range,
            FiscalCopy.Disclaimer,
            org.Name,
            _clock.TodayInRomeAsDateOnly(),
            new WithholdingTotals(lines.Sum(l => l.Gross), lines.Sum(l => l.Withholding), lines.Sum(l => l.Net), lines.Count),
            ReportRules());
    }

    public async Task<TouristTaxReport> GetTouristTaxReportAsync(
        HostScope scope,
        FiscalReportPeriod period,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(period);
        if (!period.IsValid)
            throw new FiscalValidationException("fiscal_report_period_invalid", "FiscalReportPeriodInvalid");
        var org = await LoadReportOrgAsync(scope.OrgId, cancellationToken);

        // Stay dates are stored as midnight UTC of the calendar date (FD-06).
        var from = period.From.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var toExclusive = period.To.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var stays = await db.Bookings.AsNoTracking()
            .Where(b => b.OrgId == scope.OrgId
                && (scope.OwnerId == null || b.Property.OwnerId == scope.OwnerId)
                && b.CheckInDate >= from
                && b.CheckInDate < toExclusive
                && (b.Status == BookingStatus.Confirmed
                    || b.Status == BookingStatus.CheckedIn
                    || b.Status == BookingStatus.CheckedOut))
            .Select(b => new
            {
                b.Id,
                b.BookingCode,
                b.PropertyId,
                PropertyName = b.Property.Name,
                b.Property.City,
                b.CheckInDate,
                b.CheckOutDate,
                b.NumberOfGuests,
                b.Source,
                b.TouristTaxAmount,
            })
            .ToListAsync(cancellationToken);

        // One comune per normalized name (BK-03 lookup rule), shown with its most frequent spelling.
        var comuneNames = stays
            .GroupBy(s => TouristTaxComune.NormalizeName(s.City))
            .ToDictionary(
                g => g.Key,
                g => g.Select(s => s.City.Trim())
                    .GroupBy(name => name, StringComparer.Ordinal)
                    .OrderByDescending(n => n.Count())
                    .ThenBy(n => n.Key, StringComparer.Ordinal)
                    .Select(n => n.Key)
                    .FirstOrDefault(n => n.Length > 0) ?? "—");

        var lines = stays
            .Select(s => new TouristTaxStayLine(
                s.Id,
                BookingCodes.Format(s.BookingCode),
                s.PropertyId,
                s.PropertyName,
                comuneNames[TouristTaxComune.NormalizeName(s.City)],
                DateOnly.FromDateTime(s.CheckInDate),
                DateOnly.FromDateTime(s.CheckOutDate),
                Math.Max(0, (s.CheckOutDate.Date - s.CheckInDate.Date).Days),
                s.NumberOfGuests,
                s.Source,
                s.TouristTaxAmount))
            .OrderBy(l => l.Comune, StringComparer.CurrentCulture)
            .ThenBy(l => l.CheckIn)
            .ThenBy(l => l.BookingCode, StringComparer.Ordinal)
            .ToList();

        var rows = lines
            .GroupBy(l => (l.Comune, l.CheckIn.Year, l.CheckIn.Month))
            .Select(g => new TouristTaxPeriodRow(
                g.Key.Comune,
                g.Key.Year,
                g.Key.Month,
                g.Count(),
                g.Sum(l => l.Nights),
                g.Sum(l => l.Guests),
                g.Sum(l => l.Amount),
                g.Count(l => l.Amount <= 0)))
            .OrderBy(r => r.Comune, StringComparer.CurrentCulture)
            .ThenBy(r => r.Year)
            .ThenBy(r => r.Month)
            .ToList();

        var byComune = rows
            .GroupBy(r => r.Comune)
            .Select(g => new TouristTaxComuneTotals(
                g.Key,
                g.Sum(r => r.Stays),
                g.Sum(r => r.Nights),
                g.Sum(r => r.Guests),
                g.Sum(r => r.Amount),
                g.Sum(r => r.StaysWithoutAmount)))
            .ToList();

        return new TouristTaxReport(
            period,
            FiscalCopy.TouristTaxDisclaimer,
            org.Name,
            _clock.TodayInRomeAsDateOnly(),
            rows,
            byComune,
            lines,
            new TouristTaxTotals(
                lines.Count,
                lines.Sum(l => l.Nights),
                lines.Sum(l => l.Guests),
                lines.Sum(l => l.Amount),
                lines.Count(l => l.Amount <= 0)));
    }

    public byte[] ToCsv(AnnualIncomeReport report) => FiscalReportDocuments.Csv(report);

    public byte[] ToCsv(WithholdingReport report) => FiscalReportDocuments.Csv(report);

    public byte[] ToCsv(TouristTaxReport report) => FiscalReportDocuments.Csv(report);

    public byte[] ToPdf(AnnualIncomeReport report) => pdfRenderer.Render(FiscalReportDocuments.Pdf(report));

    public byte[] ToPdf(WithholdingReport report) => pdfRenderer.Render(FiscalReportDocuments.Pdf(report));

    public byte[] ToPdf(TouristTaxReport report) => pdfRenderer.Render(FiscalReportDocuments.Pdf(report));

    /// <summary>
    /// One property of the summary. The tax is estimated only for the cedolare secca (fiscale.md C2): gross rent (tourist
    /// tax excluded, no deduction) times the configured rate. Not for IRPEF ordinaria (C11: depends on other income and
    /// on the cadastral income), the impresa regimes, no regime, or a taxpayer over the threshold (C3).
    /// </summary>
    private AnnualIncomeLine BuildIncomeLine(
        Guid propertyId,
        string name,
        StrFiscalRegime? regime,
        TaxpayerYear? taxpayer,
        IReadOnlyCollection<Payment> payments)
    {
        var gross = payments.Sum(ReportableGross);
        var withholding = payments.Sum(ReportableWithholding);
        var touristTax = payments.Sum(TouristTaxShare);
        var rental = gross - touristTax;

        decimal? rate = null;
        string? note = null;
        if (taxpayer?.ThresholdExceeded == true)
        {
            note = FiscalTaxNotes.ThresholdExceeded;
        }
        else
        {
            switch (regime)
            {
                case StrFiscalRegime.CedolareSecca21:
                    rate = _rules.CedolareReducedRate;
                    break;
                case StrFiscalRegime.CedolareSecca26:
                    rate = _rules.CedolareRate;
                    break;
                case StrFiscalRegime.IrpefOrdinaria:
                    note = FiscalTaxNotes.IrpefOrdinariaNotComputed;
                    break;
                case StrFiscalRegime.RegimeOrdinario or StrFiscalRegime.RegimeForfettario:
                    note = FiscalTaxNotes.ImpresaNotComputed;
                    break;
                default:
                    note = FiscalTaxNotes.RegimeNotAssigned;
                    break;
            }
        }

        decimal? taxable = rate is null ? null : rental;
        decimal? estimated = rate is decimal r ? decimal.Round(rental * r, 2, MidpointRounding.AwayFromZero) : null;

        return new AnnualIncomeLine(
            propertyId,
            name,
            regime,
            gross,
            withholding,
            gross - withholding,
            touristTax,
            rental,
            Commissions: null,
            taxable,
            rate,
            estimated,
            note,
            taxpayer?.Index);
    }

    private FiscalReportRules ReportRules() => new(
        _rules.CedolareRate,
        _rules.CedolareReducedRate,
        _rules.CedolareSource,
        _rules.OtaWithholdingRate,
        _rules.OtaWithholdingSource,
        _rules.MaxApartmentsPerTaxpayer,
        _rules.ThresholdSource);

    private static FiscalReportPeriod ValidateYearPeriod(int taxYear, FiscalReportPeriod? period)
    {
        var range = period ?? FiscalReportPeriod.WholeYear(taxYear);
        if (!range.IsValid || !range.IsWithinYear(taxYear))
            throw new FiscalValidationException("fiscal_report_period_invalid", "FiscalReportPeriodInvalid");
        return range;
    }

    private async Task<(string Name, string? FiscalCode)> LoadReportOrgAsync(Guid orgId, CancellationToken cancellationToken)
    {
        var org = await db.Orgs.AsNoTracking()
            .Where(o => o.Id == orgId)
            .Select(o => new { o.Name, o.DisplayName, o.FiscalCode })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException("Org not found");
        return (string.IsNullOrWhiteSpace(org.DisplayName) ? org.Name : org.DisplayName, org.FiscalCode);
    }

    /// <summary>Payments of the scope: the org, and only the caller's own properties without org-wide access (TN-3).</summary>
    private IQueryable<Payment> ScopedPayments(HostScope scope) =>
        db.Payments.AsNoTracking()
            .Include(p => p.Booking)
            .ThenInclude(b => b.Property)
            .Where(p => p.OrgId == scope.OrgId && (scope.OwnerId == null || p.Booking.Property.OwnerId == scope.OwnerId));

    /// <summary>
    /// Payments completed (or partially refunded) in <paramref name="period"/>: payment date (processing, else creation) in
    /// Europe/Rome, the calendar of the host.
    /// </summary>
    private static IQueryable<Payment> SettledIn(IQueryable<Payment> payments, FiscalReportPeriod period)
    {
        var start = RomeCalendar.StartOfDayUtc(period.From.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var end = RomeCalendar.StartOfDayUtc(period.To.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        return payments.Where(p =>
            (p.Status == PaymentStatus.Completed || p.Status == PaymentStatus.PartiallyRefunded)
            && (p.ProcessedAt ?? p.CreatedAt) >= start
            && (p.ProcessedAt ?? p.CreatedAt) < end);
    }

    private static decimal ReportableGross(Payment payment) =>
        Math.Max(0m, payment.Amount - payment.RefundedAmount);

    private static decimal ReportableWithholding(Payment payment) => Share(payment, payment.OtaWithholdingTax);

    private static decimal ReportableNet(Payment payment) =>
        ReportableGross(payment) - ReportableWithholding(payment);

    /// <summary>
    /// Tourist tax included in the payment: the amount recorded on the stay (BK-03) in proportion to the part of the stay's
    /// total this payment covers, net of refunds. Never more than the payment itself or the stay's tax.
    /// </summary>
    private static decimal TouristTaxShare(Payment payment)
    {
        var booking = payment.Booking;
        if (booking.TouristTaxAmount <= 0 || booking.TotalPrice <= 0)
            return 0m;

        var gross = ReportableGross(payment);
        var share = decimal.Round(booking.TouristTaxAmount * (gross / booking.TotalPrice), 2, MidpointRounding.AwayFromZero);
        return Math.Min(share, Math.Min(booking.TouristTaxAmount, gross));
    }

    /// <summary><paramref name="amount"/> of the payment reduced in proportion to the refunds, rounded to the cent.</summary>
    private static decimal Share(Payment payment, decimal amount)
    {
        var gross = ReportableGross(payment);
        if (gross <= 0 || payment.Amount <= 0 || amount <= 0)
            return 0m;

        return decimal.Round(amount * (gross / payment.Amount), 2, MidpointRounding.AwayFromZero);
    }
}
