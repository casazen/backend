using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// STR fiscal area (issue #3, CO-18). Rules and sources: <c>.claude/context/regulations/fiscale.md</c> § CO-18, values in
/// <see cref="ShortStayFiscalOptions"/>. The short-rental threshold and the one 21% cedolare unit are per taxpayer (titolare
/// fiscale: <see cref="Property.TaxpayerFiscalCode"/>, else the org tax profile) and per tax year, counting only the
/// apartments with short-term stays in that year. Over the threshold the activity is presumed a business: cedolare and
/// IRPEF-ordinaria are refused, the host is warned, and no OTA withholding is computed by default.
/// </summary>
public class FiscalService(
    AppDbContext db,
    IPdfDocumentRenderer pdfRenderer,
    IOptions<ShortStayFiscalOptions> fiscalOptions,
    TimeProvider? timeProvider = null)
    : IFiscalRegimeService, IFiscalReportingService
{
    /// <summary>Taxpayer key of the org tax profile when it has no codice fiscale.</summary>
    private const string OrgProfileKey = "";

    private static readonly Regex TaxpayerFiscalCodePattern = new("^[A-Z0-9]{16}$", RegexOptions.Compiled);

    private readonly ShortStayFiscalOptions _rules = fiscalOptions.Value;
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<FiscalRegimeSnapshot> GetRegimeAsync(Guid orgId, int taxYear, CancellationToken cancellationToken = default)
    {
        ValidateTaxYear(taxYear);
        var org = await db.Orgs.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orgId, cancellationToken)
            ?? throw new KeyNotFoundException("Org not found");

        var view = await LoadYearAsync(orgId, org.FiscalCode, taxYear, trackAssignments: false, cancellationToken);
        var rows = view.Candidates.Select(view.BuildRow).ToList();

        return new FiscalRegimeSnapshot(
            taxYear,
            view.Candidates.Count(p => p.ShortStay),
            view.Taxpayers.Any(t => t.ThresholdExceeded),
            org.HasPartitaIva,
            FiscalCopy.Disclaimer,
            rows,
            _rules.MaxApartmentsPerTaxpayer,
            _rules.ThresholdSource,
            view.Taxpayers.Select(t => t.ToSummary()).ToList());
    }

    public async Task<FiscalPropertyRow> AssignRegimeAsync(
        Guid orgId,
        Guid propertyId,
        int taxYear,
        StrFiscalRegime regime,
        bool? isPrimaryForCedolare,
        CancellationToken cancellationToken = default)
    {
        // isPrimaryForCedolare is kept for API compatibility: the designated 21% unit is the one with CedolareSecca21.
        ValidateTaxYear(taxYear);
        if (!Enum.IsDefined(regime))
            throw new FiscalValidationException("Unknown fiscal regime.");

        var org = await db.Orgs.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orgId, cancellationToken)
            ?? throw new KeyNotFoundException("Org not found");
        if (!await db.Properties.AnyAsync(p => p.Id == propertyId && p.OrgId == orgId, cancellationToken))
            throw new KeyNotFoundException("Property not found");

        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, (PostgresAdvisoryLocks.Scope.OrgFiscalRegime, orgId.ToString("N")));

        var view = await LoadYearAsync(orgId, org.FiscalCode, taxYear, trackAssignments: true, cancellationToken);
        var property = view.Candidates.FirstOrDefault(p => p.Id == propertyId)
            ?? throw new FiscalValidationException("Property is not an active STR property for this tax year.");
        var taxpayer = view.TaxpayerOf(property);

        if (FiscalCopy.IsShortRentalRegime(regime) && view.WouldExceedThreshold(property))
        {
            // fiscale.md C3: beyond the threshold the short-rental regime (and IRPEF without partita IVA) is not available.
            throw new DomainConflictException(
                "fiscal_short_stay_threshold_exceeded",
                "FiscalShortStayThresholdExceeded",
                _rules.MaxApartmentsPerTaxpayer,
                taxYear);
        }

        // The org tax profile says whether it has a partita IVA; for another taxpayer CasaZen has no such data.
        if (FiscalCopy.IsImpresaRegime(regime) && taxpayer.IsOrgTaxProfile && !org.HasPartitaIva)
            throw new FiscalValidationException("Partita IVA must be recorded before assigning an impresa regime.");

        if (regime == StrFiscalRegime.CedolareSecca21)
        {
            // One 21% unit per taxpayer and tax year (fiscale.md C2): the previous one goes back to the general rate.
            foreach (var other in view.ReducedRateAssignmentsOf(taxpayer).Where(y => y.PropertyId != propertyId))
            {
                other.IsPrimaryForCedolare = false;
                if (other.Regime == StrFiscalRegime.CedolareSecca21)
                    other.Regime = StrFiscalRegime.CedolareSecca26;
                other.UpdatedAt = DateTime.UtcNow;
            }
        }

        if (!view.Assignments.TryGetValue(propertyId, out var existing))
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
        existing.IsPrimaryForCedolare = regime == StrFiscalRegime.CedolareSecca21;
        existing.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        var updated = await LoadYearAsync(orgId, org.FiscalCode, taxYear, trackAssignments: false, cancellationToken);
        return updated.BuildRow(updated.Candidates.Single(p => p.Id == propertyId));
    }

    public async Task<FiscalPropertyTaxpayer> SetPropertyTaxpayerAsync(
        Guid orgId,
        Guid propertyId,
        string? fiscalCode,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeFiscalCode(fiscalCode);
        if (normalized is not null && !TaxpayerFiscalCodePattern.IsMatch(normalized))
            throw new DomainRuleException("invalid_taxpayer_fiscal_code", "FiscalTaxpayerCodeInvalid");

        var org = await db.Orgs.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orgId, cancellationToken)
            ?? throw new KeyNotFoundException("Org not found");

        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, (PostgresAdvisoryLocks.Scope.OrgFiscalRegime, orgId.ToString("N")));

        var property = await db.Properties.FirstOrDefaultAsync(p => p.Id == propertyId && p.OrgId == orgId, cancellationToken)
            ?? throw new KeyNotFoundException("Property not found");

        if (property.TaxpayerFiscalCode != normalized)
        {
            await EnsureReducedRateUnitFreeAsync(orgId, org.FiscalCode, propertyId, normalized, cancellationToken);
            property.TaxpayerFiscalCode = normalized;
            property.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        return new FiscalPropertyTaxpayer(propertyId, normalized is null ? null : PersonalDataMasking.MaskFiscalCode(normalized));
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
        org.FiscalDataRetentionUntil ??= new DateTime(_clock.TodayInRome().Year + 10, 12, 31, 0, 0, 0, DateTimeKind.Utc);
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
        int count;
        if (hypotheticalStrCount is int hypothetical)
        {
            count = hypothetical;
        }
        else
        {
            // Per taxpayer (fiscale.md C4): the simulation shows the taxpayer of the org with the most apartments.
            var orgFiscalCode = await db.Orgs.AsNoTracking()
                .Where(o => o.Id == orgId)
                .Select(o => o.FiscalCode)
                .FirstOrDefaultAsync(cancellationToken);
            var view = await LoadYearAsync(orgId, orgFiscalCode, taxYear, trackAssignments: false, cancellationToken);
            count = view.Taxpayers.Select(t => t.ShortStayApartmentCount).DefaultIfEmpty(0).Max();
        }

        if (count < 0)
            throw new FiscalValidationException("Invalid property count.");

        var exceeded = ThresholdApplies(taxYear) && count > _rules.MaxApartmentsPerTaxpayer;
        var label = count switch
        {
            0 => "None",
            _ when exceeded => "RequiresPartitaIva",
            1 => nameof(StrFiscalRegime.CedolareSecca21),
            _ => nameof(StrFiscalRegime.CedolareSecca26),
        };
        return new FiscalSimulateResult(label, exceeded, FiscalCopy.Disclaimer);
    }

    public async Task ApplyWithholdingOnCreateAsync(Payment payment, Booking booking, bool? applyOtaWithholding, decimal? manualWithholdingTax)
    {
        var isOta = FiscalCopy.IsOtaBookingSource(booking.Source);
        var auto = isOta && applyOtaWithholding switch
        {
            true => true,
            false => false,
            null => await IsShortRentalContractAsync(booking),
        };
        if (auto)
        {
            payment.OtaWithholdingTax = FiscalCopy.CalculateOtaWithholding(payment.Amount, _rules.OtaWithholdingRate);
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
    }

    public async Task<AnnualIncomeReport> GetAnnualReportAsync(Guid orgId, int taxYear, CancellationToken cancellationToken = default)
    {
        ValidateTaxYear(taxYear);
        var (yearStart, yearEnd) = YearBounds(taxYear);
        var view = await LoadYearAsync(orgId, orgFiscalCode: null, taxYear, trackAssignments: false, cancellationToken);
        var reportProperties = view.Candidates.ToDictionary(p => p.Id, p => p.Name);
        var settledPayments = await SettledInTaxYear(
                db.Payments.AsNoTracking()
                    .Include(p => p.Booking)
                    .ThenInclude(b => b.Property),
                yearStart,
                yearEnd)
            .Where(p => p.OrgId == orgId)
            .ToListAsync(cancellationToken);

        foreach (var property in settledPayments.Select(p => p.Booking.Property))
            reportProperties.TryAdd(property.Id, property.Name);

        var lines = new List<AnnualIncomeLine>();
        foreach (var (propertyId, name) in reportProperties.OrderBy(p => p.Value).ThenBy(p => p.Key))
        {
            var payments = settledPayments
                .Where(p => p.Booking.PropertyId == propertyId)
                .ToList();
            view.Assignments.TryGetValue(propertyId, out var row);
            var gross = payments.Sum(ReportableGross);
            var withholding = payments.Sum(ReportableWithholding);
            lines.Add(new AnnualIncomeLine(propertyId, name, row?.Regime, gross, withholding, gross - withholding));
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

    public byte[] ToPdf(string title, string body) => pdfRenderer.Render(PdfDocumentContent.FromPlainText(title, body));

    /// <summary>
    /// Whether an OTA stay is a short-term rental contract, the only one subject to the intermediary's withholding
    /// (fiscale.md C1, C7, C10): at most the configured nights, and the property's taxpayer not in business (impresa) for
    /// the stay's tax year, neither by the assigned regime nor by the threshold presumption.
    /// </summary>
    private async Task<bool> IsShortRentalContractAsync(Booking booking, CancellationToken cancellationToken = default)
    {
        if (!IsShortStay(booking.CheckInDate, booking.CheckOutDate))
            return false;

        var taxYear = booking.CheckInDate.Year;
        var assignment = await db.PropertyFiscalYears.AsNoTracking()
            .FirstOrDefaultAsync(y => y.PropertyId == booking.PropertyId && y.TaxYear == taxYear, cancellationToken);
        if (assignment is not null && FiscalCopy.IsImpresaRegime(assignment.Regime))
            return false;

        if (!ThresholdApplies(taxYear))
            return true;

        var orgFiscalCode = await db.Orgs.AsNoTracking()
            .Where(o => o.Id == booking.OrgId)
            .Select(o => o.FiscalCode)
            .FirstOrDefaultAsync(cancellationToken);
        var view = await LoadYearAsync(booking.OrgId, orgFiscalCode, taxYear, trackAssignments: false, cancellationToken);
        return !view.TryGetProperty(booking.PropertyId, out var property) || !view.TaxpayerOf(property).ThresholdExceeded;
    }

    /// <summary>
    /// A 21% unit designated for a tax year stays one per taxpayer when the property changes taxpayer: refuses the change if
    /// the new taxpayer already has another 21% unit in one of those years.
    /// </summary>
    private async Task EnsureReducedRateUnitFreeAsync(
        Guid orgId,
        string? orgFiscalCode,
        Guid propertyId,
        string? newTaxpayerFiscalCode,
        CancellationToken cancellationToken)
    {
        var reducedYears = await db.PropertyFiscalYears.AsNoTracking()
            .Where(y => y.PropertyId == propertyId
                && (y.IsPrimaryForCedolare || y.Regime == StrFiscalRegime.CedolareSecca21))
            .Select(y => y.TaxYear)
            .ToListAsync(cancellationToken);
        if (reducedYears.Count == 0)
            return;

        var orgKey = NormalizeFiscalCode(orgFiscalCode) ?? OrgProfileKey;
        var newKey = newTaxpayerFiscalCode ?? orgKey;
        var siblings = await db.Properties.AsNoTracking()
            .Where(p => p.OrgId == orgId && p.Id != propertyId)
            .Select(p => new { p.Id, p.TaxpayerFiscalCode })
            .ToListAsync(cancellationToken);
        var sameTaxpayer = siblings
            .Where(p => (NormalizeFiscalCode(p.TaxpayerFiscalCode) ?? orgKey) == newKey)
            .Select(p => p.Id)
            .ToList();
        var takenYear = await db.PropertyFiscalYears.AsNoTracking()
            .Where(y => y.OrgId == orgId
                && sameTaxpayer.Contains(y.PropertyId)
                && reducedYears.Contains(y.TaxYear)
                && (y.IsPrimaryForCedolare || y.Regime == StrFiscalRegime.CedolareSecca21))
            .OrderBy(y => y.TaxYear)
            .Select(y => (int?)y.TaxYear)
            .FirstOrDefaultAsync(cancellationToken);
        if (takenYear is int year)
            throw new DomainConflictException("fiscal_reduced_rate_unit_taken", "FiscalReducedRateUnitTaken", year);
    }

    /// <summary>
    /// The org's properties for <paramref name="taxYear"/>, grouped by taxpayer. Candidates are the properties shown in the
    /// fiscal area: active ones not only let long-term in the year, plus any property with short-term stays in the year.
    /// Only the latter count toward the threshold (fiscale.md C4: apartments let short-term in the tax year).
    /// </summary>
    private async Task<FiscalYearView> LoadYearAsync(
        Guid orgId,
        string? orgFiscalCode,
        int taxYear,
        bool trackAssignments,
        CancellationToken cancellationToken)
    {
        var (yearStart, yearEnd) = YearBounds(taxYear);
        var stays = await db.Bookings.AsNoTracking()
            .Where(b => b.OrgId == orgId
                && b.CheckInDate >= yearStart
                && b.CheckInDate < yearEnd
                && (b.Status == BookingStatus.Confirmed
                    || b.Status == BookingStatus.CheckedIn
                    || b.Status == BookingStatus.CheckedOut))
            .Select(b => new { b.PropertyId, b.CheckInDate, b.CheckOutDate })
            .ToListAsync(cancellationToken);
        var shortStayPropertyIds = stays
            .Where(b => IsShortStay(b.CheckInDate, b.CheckOutDate))
            .Select(b => b.PropertyId)
            .ToHashSet();

        var leasedPropertyIds = (await db.LeaseContracts.AsNoTracking()
                .Where(l => l.OrgId == orgId && l.StartDate < yearEnd && l.EndDate >= yearStart)
                .Select(l => l.PropertyId)
                .Distinct()
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var properties = await db.Properties.AsNoTracking()
            .Where(p => p.OrgId == orgId)
            .Select(p => new { p.Id, p.Name, p.IsActive, p.TaxpayerFiscalCode })
            .ToListAsync(cancellationToken);

        var assignmentsQuery = db.PropertyFiscalYears.Where(y => y.OrgId == orgId && y.TaxYear == taxYear);
        if (!trackAssignments)
            assignmentsQuery = assignmentsQuery.AsNoTracking();
        var assignments = await assignmentsQuery.ToDictionaryAsync(y => y.PropertyId, cancellationToken);

        var orgKey = NormalizeFiscalCode(orgFiscalCode) ?? OrgProfileKey;
        var all = properties
            .Select(p =>
            {
                var shortStay = shortStayPropertyIds.Contains(p.Id);
                var candidate = shortStay || (p.IsActive && !leasedPropertyIds.Contains(p.Id));
                var taxpayerCode = NormalizeFiscalCode(p.TaxpayerFiscalCode);
                return new YearProperty(p.Id, p.Name, taxpayerCode ?? orgKey, taxpayerCode, candidate, shortStay);
            })
            .ToDictionary(p => p.Id);

        return new FiscalYearView(orgKey, all, assignments, _rules, ThresholdApplies(taxYear));
    }

    private bool IsShortStay(DateTime checkIn, DateTime checkOut) =>
        (checkOut.Date - checkIn.Date).TotalDays <= _rules.MaxStayNights;

    private bool ThresholdApplies(int taxYear) => taxYear >= _rules.ThresholdFromTaxYear;

    private static string? NormalizeFiscalCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return string.Concat(value.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();
    }

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

    private sealed record YearProperty(
        Guid Id,
        string Name,
        string TaxpayerKey,
        string? TaxpayerFiscalCode,
        bool IsCandidate,
        bool ShortStay);

    private sealed record TaxpayerYear(
        int Index,
        string Key,
        string? FiscalCode,
        bool IsOrgTaxProfile,
        int ShortStayApartmentCount,
        bool ThresholdExceeded,
        Guid? ReducedRatePropertyId)
    {
        public FiscalTaxpayerSummary ToSummary() => new(
            Index,
            FiscalCode is null ? null : PersonalDataMasking.MaskFiscalCode(FiscalCode),
            IsOrgTaxProfile,
            ShortStayApartmentCount,
            ThresholdExceeded,
            ReducedRatePropertyId);
    }

    /// <summary>One tax year of an org: its properties, their assigned regimes and their taxpayers.</summary>
    private sealed class FiscalYearView
    {
        private readonly IReadOnlyDictionary<Guid, YearProperty> _properties;
        private readonly Dictionary<string, TaxpayerYear> _taxpayers;
        private readonly ShortStayFiscalOptions _rules;
        private readonly bool _thresholdApplies;

        public FiscalYearView(
            string orgKey,
            IReadOnlyDictionary<Guid, YearProperty> properties,
            Dictionary<Guid, PropertyFiscalYear> assignments,
            ShortStayFiscalOptions rules,
            bool thresholdApplies)
        {
            _properties = properties;
            Assignments = assignments;
            _rules = rules;
            _thresholdApplies = thresholdApplies;
            Candidates = properties.Values.Where(p => p.IsCandidate).OrderBy(p => p.Name).ThenBy(p => p.Id).ToList();

            // The org tax profile first, then the other taxpayers in a stable order.
            _taxpayers = Candidates
                .GroupBy(p => p.TaxpayerKey)
                .OrderBy(g => g.Key == orgKey ? 0 : 1)
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select((g, index) =>
                {
                    var count = g.Count(p => p.ShortStay);
                    var reduced = assignments.Values
                        .Where(y => IsReducedRate(y) && properties.TryGetValue(y.PropertyId, out var p) && p.TaxpayerKey == g.Key)
                        .Select(y => (Guid?)y.PropertyId)
                        .FirstOrDefault();
                    var fiscalCode = g.Key == OrgProfileKey ? null : g.Key;
                    return new TaxpayerYear(
                        index,
                        g.Key,
                        fiscalCode,
                        g.Key == orgKey,
                        count,
                        thresholdApplies && count > rules.MaxApartmentsPerTaxpayer,
                        reduced);
                })
                .ToDictionary(t => t.Key, StringComparer.Ordinal);
        }

        public IReadOnlyList<YearProperty> Candidates { get; }

        public Dictionary<Guid, PropertyFiscalYear> Assignments { get; }

        public IEnumerable<TaxpayerYear> Taxpayers => _taxpayers.Values.OrderBy(t => t.Index);

        public bool TryGetProperty(Guid propertyId, out YearProperty property)
        {
            if (_properties.TryGetValue(propertyId, out var found) && found.IsCandidate)
            {
                property = found;
                return true;
            }

            property = null!;
            return false;
        }

        public TaxpayerYear TaxpayerOf(YearProperty property) => _taxpayers[property.TaxpayerKey];

        /// <summary>
        /// Whether the taxpayer would be over the threshold with this apartment let short-term too. Besides the apartments
        /// with short-term stays, it counts those the host already declared short-term for the year (a cedolare or
        /// IRPEF-ordinaria regime assigned): otherwise a third apartment could get cedolare before its first booking.
        /// </summary>
        public bool WouldExceedThreshold(YearProperty property)
        {
            if (!_thresholdApplies)
                return false;

            var taxpayerKey = property.TaxpayerKey;
            var declared = Candidates.Count(p =>
                p.TaxpayerKey == taxpayerKey
                && (p.Id == property.Id
                    || p.ShortStay
                    || (Assignments.TryGetValue(p.Id, out var assignment) && FiscalCopy.IsShortRentalRegime(assignment.Regime))));
            return declared > _rules.MaxApartmentsPerTaxpayer;
        }

        /// <summary>The taxpayer's rows at the 21% rate in this year (normally at most one).</summary>
        public IEnumerable<PropertyFiscalYear> ReducedRateAssignmentsOf(TaxpayerYear taxpayer) =>
            Assignments.Values.Where(y =>
                IsReducedRate(y)
                && _properties.TryGetValue(y.PropertyId, out var p)
                && p.TaxpayerKey == taxpayer.Key);

        public FiscalPropertyRow BuildRow(YearProperty property)
        {
            var taxpayer = TaxpayerOf(property);
            Assignments.TryGetValue(property.Id, out var assignment);
            var assigned = assignment?.Regime;
            var cedolareRate = assigned switch
            {
                StrFiscalRegime.CedolareSecca21 => _rules.CedolareReducedRate,
                StrFiscalRegime.CedolareSecca26 => _rules.CedolareRate,
                _ => (decimal?)null,
            };
            var taxNote = taxpayer.ThresholdExceeded
                ? FiscalTaxNotes.ThresholdExceeded
                : assigned == StrFiscalRegime.IrpefOrdinaria ? FiscalTaxNotes.IrpefOrdinariaNotComputed : null;

            return new FiscalPropertyRow(
                property.Id,
                property.Name,
                Recommend(property, taxpayer),
                assigned,
                assignment?.IsPrimaryForCedolare == true,
                property.ShortStay,
                taxpayer.Index,
                cedolareRate,
                taxNote);
        }

        /// <summary>
        /// Informative only (the regime is the taxpayer's choice): within the threshold, 21% for the designated unit (or the
        /// only apartment), 26% for the others; nothing beyond it, where the accountant decides (fiscale.md C2, C3).
        /// </summary>
        private StrFiscalRegime? Recommend(YearProperty property, TaxpayerYear taxpayer)
        {
            if (WouldExceedThreshold(property))
                return null;
            if (taxpayer.ReducedRatePropertyId is Guid reduced)
                return reduced == property.Id ? StrFiscalRegime.CedolareSecca21 : StrFiscalRegime.CedolareSecca26;

            // No unit designated yet: 21% suggested when this is the taxpayer's only apartment let short-term.
            var count = taxpayer.ShortStayApartmentCount + (property.ShortStay ? 0 : 1);
            return count == 1 ? StrFiscalRegime.CedolareSecca21 : StrFiscalRegime.CedolareSecca26;
        }

        private static bool IsReducedRate(PropertyFiscalYear row) =>
            row.IsPrimaryForCedolare || row.Regime == StrFiscalRegime.CedolareSecca21;
    }
}
