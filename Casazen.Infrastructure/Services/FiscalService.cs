using System.Globalization;
using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

public class FiscalService(AppDbContext db) : IFiscalRegimeService, IFiscalReportingService
{
    public async Task<FiscalRegimeSnapshot> GetRegimeAsync(Guid orgId, int taxYear, CancellationToken cancellationToken = default)
    {
        ValidateTaxYear(taxYear);
        var org = await db.Orgs.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orgId, cancellationToken)
            ?? throw new KeyNotFoundException("Org not found");

        var counted = await GetCountedPropertiesAsync(orgId, taxYear, cancellationToken);
        var assignments = await db.PropertyFiscalYears.AsNoTracking()
            .Where(y => y.OrgId == orgId && y.TaxYear == taxYear)
            .ToDictionaryAsync(y => y.PropertyId, cancellationToken);

        var rows = counted.Select(p =>
        {
            assignments.TryGetValue(p.Id, out var row);
            return new FiscalPropertyRow(
                p.Id,
                p.Name,
                RecommendForProperty(counted.Count, row?.IsPrimaryForCedolare == true, org.HasPartitaIva),
                row?.Regime,
                row?.IsPrimaryForCedolare == true);
        }).ToList();

        return new FiscalRegimeSnapshot(
            taxYear,
            counted.Count,
            counted.Count >= 3,
            org.HasPartitaIva,
            FiscalCopy.Disclaimer,
            rows);
    }

    public async Task<FiscalPropertyRow> AssignRegimeAsync(
        Guid orgId,
        Guid propertyId,
        int taxYear,
        StrFiscalRegime regime,
        bool? isPrimaryForCedolare,
        CancellationToken cancellationToken = default)
    {
        ValidateTaxYear(taxYear);
        var org = await db.Orgs.FirstOrDefaultAsync(o => o.Id == orgId, cancellationToken)
            ?? throw new KeyNotFoundException("Org not found");

        var property = await db.Properties.FirstOrDefaultAsync(p => p.Id == propertyId && p.OrgId == orgId, cancellationToken)
            ?? throw new KeyNotFoundException("Property not found");

        var counted = await GetCountedPropertiesAsync(orgId, taxYear, cancellationToken);
        if (counted.All(p => p.Id != propertyId))
            throw new FiscalValidationException("Property is not an active STR property for this tax year.");

        var count = counted.Count;
        if (regime is StrFiscalRegime.CedolareSecca21 or StrFiscalRegime.CedolareSecca26 && count >= 3)
            throw new FiscalConflictException("Cedolare cannot be assigned when three or more STR properties are active in the tax year.");

        if (regime == StrFiscalRegime.CedolareSecca26 && count == 1)
            throw new FiscalValidationException("Cedolare 26% applies only to the second STR property.");

        if (regime == StrFiscalRegime.CedolareSecca26 && count == 2)
        {
            var primaryPropertyId = counted.Single(p => p.Id != propertyId).Id;
            var primaryAssignment = await db.PropertyFiscalYears.AsNoTracking()
                .FirstOrDefaultAsync(y => y.PropertyId == primaryPropertyId && y.TaxYear == taxYear, cancellationToken);
            if (primaryAssignment?.Regime != StrFiscalRegime.CedolareSecca21 ||
                primaryAssignment.IsPrimaryForCedolare != true)
            {
                throw new FiscalValidationException("Assign another STR property as primary Cedolare 21% before assigning Cedolare 26%.");
            }
        }

        if (regime is StrFiscalRegime.RegimeOrdinario or StrFiscalRegime.RegimeForfettario && !org.HasPartitaIva)
            throw new FiscalValidationException("Partita IVA must be recorded before assigning an impresa regime.");

        var makePrimary = isPrimaryForCedolare == true
            || (regime == StrFiscalRegime.CedolareSecca21 && count <= 2);

        if (makePrimary && count == 2)
        {
            var others = await db.PropertyFiscalYears
                .Where(y => y.OrgId == orgId && y.TaxYear == taxYear && y.PropertyId != propertyId)
                .ToListAsync(cancellationToken);
            foreach (var other in others)
            {
                other.IsPrimaryForCedolare = false;
                other.Regime = StrFiscalRegime.CedolareSecca26;
                other.UpdatedAt = DateTime.UtcNow;
            }
        }

        var existing = await db.PropertyFiscalYears
            .FirstOrDefaultAsync(y => y.PropertyId == propertyId && y.TaxYear == taxYear, cancellationToken);
        if (existing is null)
        {
            existing = new PropertyFiscalYear
            {
                OrgId = orgId,
                PropertyId = propertyId,
                TaxYear = taxYear,
            };
            db.PropertyFiscalYears.Add(existing);
        }

        existing.Regime = regime;
        existing.IsPrimaryForCedolare = makePrimary && regime == StrFiscalRegime.CedolareSecca21;
        existing.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return new FiscalPropertyRow(
            property.Id,
            property.Name,
            RecommendForProperty(count, existing.IsPrimaryForCedolare, org.HasPartitaIva),
            existing.Regime,
            existing.IsPrimaryForCedolare);
    }

    public async Task<FiscalTaxProfile> GetTaxProfileAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        var org = await db.Orgs.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orgId, cancellationToken)
            ?? throw new KeyNotFoundException("Org not found");
        return MapProfile(org);
    }

    public async Task<FiscalTaxProfile> UpdateTaxProfileAsync(
        Guid orgId,
        bool hasPartitaIva,
        string? partitaIvaNumber,
        string? fiscalCode,
        CancellationToken cancellationToken = default)
    {
        var org = await db.Orgs.FirstOrDefaultAsync(o => o.Id == orgId, cancellationToken)
            ?? throw new KeyNotFoundException("Org not found");

        if (hasPartitaIva)
        {
            var digits = new string((partitaIvaNumber ?? string.Empty).Where(char.IsDigit).ToArray());
            if (digits.Length is < 11 or > 11)
                throw new FiscalValidationException("Invalid tax identifier.");
            org.PartitaIvaNumber = digits;
        }
        else
        {
            org.PartitaIvaNumber = null;
        }

        if (!string.IsNullOrWhiteSpace(fiscalCode))
        {
            var cf = fiscalCode.Trim().ToUpperInvariant();
            if (cf.Length > 16)
                throw new FiscalValidationException("Invalid tax identifier.");
            org.FiscalCode = cf;
        }

        org.HasPartitaIva = hasPartitaIva;
        org.FiscalDataRetentionUntil ??= new DateTime(DateTime.UtcNow.Year + 10, 12, 31, 0, 0, 0, DateTimeKind.Utc);
        org.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return MapProfile(org);
    }

    public async Task<FiscalSimulateResult> SimulateAsync(
        Guid orgId,
        int taxYear,
        int? hypotheticalStrCount,
        CancellationToken cancellationToken = default)
    {
        ValidateTaxYear(taxYear);
        var count = hypotheticalStrCount
            ?? (await GetCountedPropertiesAsync(orgId, taxYear, cancellationToken)).Count;
        if (count < 0)
            throw new FiscalValidationException("Invalid property count.");

        var label = count switch
        {
            0 => "None",
            1 => nameof(StrFiscalRegime.CedolareSecca21),
            2 => nameof(StrFiscalRegime.CedolareSecca26),
            _ => "RequiresPartitaIva",
        };
        return new FiscalSimulateResult(label, count >= 3, FiscalCopy.Disclaimer);
    }

    public Task ApplyWithholdingOnCreateAsync(Payment payment, Booking booking, bool? applyOtaWithholding, decimal? manualWithholdingTax)
    {
        var isOta = FiscalCopy.IsOtaBookingSource(booking.Source);
        var auto = isOta && applyOtaWithholding != false;
        if (auto)
        {
            payment.OtaWithholdingTax = FiscalCopy.CalculateOtaWithholding(payment.Amount);
            payment.WithholdingTaxApplied = payment.OtaWithholdingTax > 0;
            payment.NetAmountAfterWithholding = payment.Amount - payment.OtaWithholdingTax;
            payment.WithholdingSource = WithholdingSource.AutoOta;
        }
        else if (manualWithholdingTax is decimal manual && manual >= 0)
        {
            payment.OtaWithholdingTax = decimal.Round(manual, 2, MidpointRounding.AwayFromZero);
            payment.WithholdingTaxApplied = payment.OtaWithholdingTax > 0;
            payment.NetAmountAfterWithholding = payment.Amount - payment.OtaWithholdingTax;
            payment.WithholdingSource = WithholdingSource.Manual;
        }
        else
        {
            payment.OtaWithholdingTax = 0;
            payment.WithholdingTaxApplied = false;
            payment.NetAmountAfterWithholding = payment.Amount;
            payment.WithholdingSource = WithholdingSource.None;
        }

        return Task.CompletedTask;
    }

    public async Task<AnnualIncomeReport> GetAnnualReportAsync(Guid orgId, int taxYear, CancellationToken cancellationToken = default)
    {
        ValidateTaxYear(taxYear);
        var (yearStart, yearEnd) = YearBounds(taxYear);
        var counted = await GetCountedPropertiesAsync(orgId, taxYear, cancellationToken);
        var assignments = await db.PropertyFiscalYears.AsNoTracking()
            .Where(y => y.OrgId == orgId && y.TaxYear == taxYear)
            .ToDictionaryAsync(y => y.PropertyId, cancellationToken);

        var lines = new List<AnnualIncomeLine>();
        foreach (var property in counted)
        {
            var payments = await SettledInTaxYear(db.Payments.AsNoTracking(), yearStart, yearEnd)
                .Where(p => p.OrgId == orgId && p.Booking.PropertyId == property.Id)
                .ToListAsync(cancellationToken);
            assignments.TryGetValue(property.Id, out var row);
            var gross = payments.Sum(ReportableGross);
            var withholding = payments.Sum(ReportableWithholding);
            lines.Add(new AnnualIncomeLine(property.Id, property.Name, row?.Regime, gross, withholding, gross - withholding));
        }

        return new AnnualIncomeReport(
            taxYear,
            FiscalCopy.PackLabel,
            FiscalCopy.Disclaimer,
            lines,
            new AnnualIncomeTotals(
                lines.Sum(l => l.GrossIncome),
                lines.Sum(l => l.Withholding),
                lines.Sum(l => l.Net)));
    }

    public async Task<WithholdingReport> GetWithholdingReportAsync(Guid orgId, int taxYear, CancellationToken cancellationToken = default)
    {
        ValidateTaxYear(taxYear);
        var (yearStart, yearEnd) = YearBounds(taxYear);
        var payments = await SettledInTaxYear(db.Payments.AsNoTracking().Include(p => p.Booking), yearStart, yearEnd)
            .Where(p => p.OrgId == orgId && p.WithholdingTaxApplied)
            .ToListAsync(cancellationToken);

        var lines = payments.Select(p => new WithholdingLine(
            p.Id,
            p.Booking.PropertyId,
            p.Booking.Source.ToString(),
            p.ProcessedAt ?? p.CreatedAt,
            ReportableGross(p),
            ReportableWithholding(p),
            ReportableNet(p))).ToList();

        var byOta = lines
            .GroupBy(l => l.Source)
            .Select(g => new WithholdingOtaBucket(g.Key, g.Sum(x => x.Gross), g.Sum(x => x.Withholding), g.Sum(x => x.Net), g.Count()))
            .OrderBy(b => b.Source)
            .ToList();

        return new WithholdingReport(taxYear, FiscalCopy.PackLabel, byOta, lines);
    }

    public byte[] ToCsv(AnnualIncomeReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine(report.PackLabel);
        sb.AppendLine(report.Disclaimer);
        sb.AppendLine("propertyId,name,regime,gross,withholding,net");
        foreach (var line in report.Properties)
        {
            sb.AppendLine(string.Join(',',
                line.PropertyId,
                Csv(line.Name),
                line.Regime?.ToString() ?? "",
                F(line.GrossIncome),
                F(line.Withholding),
                F(line.Net)));
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public byte[] ToCsv(WithholdingReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine(report.PackLabel);
        sb.AppendLine("source,gross,withholding,net,payoutCount");
        foreach (var bucket in report.ByOta)
            sb.AppendLine(string.Join(',', bucket.Source, F(bucket.Gross), F(bucket.Withholding), F(bucket.Net), bucket.PayoutCount));
        sb.AppendLine("paymentId,propertyId,source,paidAt,gross,withholding,net");
        foreach (var line in report.Lines)
            sb.AppendLine(string.Join(',', line.PaymentId, line.PropertyId, line.Source, line.PaidAt.ToString("O"), F(line.Gross), F(line.Withholding), F(line.Net)));
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public byte[] ToPdf(string title, string body) => FiscalPdfWriter.Write(title, body);

    private async Task<List<Property>> GetCountedPropertiesAsync(Guid orgId, int taxYear, CancellationToken cancellationToken)
    {
        var (yearStart, yearEnd) = YearBounds(taxYear);
        var properties = await db.Properties.AsNoTracking()
            .Where(p => p.OrgId == orgId && p.IsActive)
            .ToListAsync(cancellationToken);

        var counted = new List<Property>();
        foreach (var property in properties)
        {
            var hasStrBooking = await db.Bookings.AsNoTracking().AnyAsync(
                b => b.PropertyId == property.Id && b.CheckInDate >= yearStart && b.CheckInDate < yearEnd,
                cancellationToken);
            var hasLease = await db.LeaseContracts.AsNoTracking().AnyAsync(
                l => l.PropertyId == property.Id && l.StartDate < yearEnd && l.EndDate >= yearStart,
                cancellationToken);
            if (hasLease && !hasStrBooking)
                continue;
            counted.Add(property);
        }

        return counted;
    }

    private static StrFiscalRegime? RecommendForProperty(int count, bool isPrimary, bool hasPartitaIva) =>
        count switch
        {
            1 => StrFiscalRegime.CedolareSecca21,
            2 => isPrimary ? StrFiscalRegime.CedolareSecca21 : StrFiscalRegime.CedolareSecca26,
            _ when hasPartitaIva => StrFiscalRegime.RegimeOrdinario,
            _ => null,
        };

    private static FiscalTaxProfile MapProfile(Org org) =>
        new(org.HasPartitaIva, org.PartitaIvaNumber, org.FiscalCode, org.FiscalDataRetentionUntil);

    private static void ValidateTaxYear(int taxYear)
    {
        if (taxYear is < 2026 or > 2100)
            throw new FiscalValidationException("Invalid tax year.");
    }

    private static (DateTime Start, DateTime End) YearBounds(int taxYear) =>
        (new DateTime(taxYear, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(taxYear + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    private static IQueryable<Payment> SettledInTaxYear(IQueryable<Payment> payments, DateTime yearStart, DateTime yearEnd) =>
        payments.Where(p =>
            (p.Status == PaymentStatus.Completed || p.Status == PaymentStatus.PartiallyRefunded)
            && (p.ProcessedAt ?? p.CreatedAt) >= yearStart
            && (p.ProcessedAt ?? p.CreatedAt) < yearEnd);

    private static decimal ReportableGross(Payment payment) =>
        Math.Max(0m, payment.Amount - payment.RefundedAmount);

    private static decimal ReportableWithholding(Payment payment)
    {
        var gross = ReportableGross(payment);
        if (gross <= 0 || payment.Amount <= 0 || payment.OtaWithholdingTax <= 0)
            return 0m;

        return decimal.Round(payment.OtaWithholdingTax * (gross / payment.Amount), 2, MidpointRounding.AwayFromZero);
    }

    private static decimal ReportableNet(Payment payment) =>
        ReportableGross(payment) - ReportableWithholding(payment);

    private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
    private static string F(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
