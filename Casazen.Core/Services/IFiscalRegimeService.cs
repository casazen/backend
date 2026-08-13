using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public static class FiscalCopy
{
    public const string Disclaimer =
        "Raccomandazione informativa, non consulenza fiscale. CasaZen non presenta dichiarazioni e non sostituisce un commercialista. Riferimenti: L. 199/2025, D.L. 50/2017.";

    public const string PackLabel =
        "Pacchetto dati per il commercialista — non è una dichiarazione, F24 o Certificazione Unica ufficiale";

    public static bool IsOtaBookingSource(BookingSource source) =>
        source is BookingSource.Airbnb
            or BookingSource.BookingCom
            or BookingSource.Expedia
            or BookingSource.Vrbo
            or BookingSource.TripAdvisor
            or BookingSource.Agoda;

    public static decimal CalculateOtaWithholding(decimal gross) =>
        decimal.Round(gross * 0.21m, 2, MidpointRounding.AwayFromZero);
}

public record FiscalPropertyRow(
    Guid PropertyId,
    string Name,
    StrFiscalRegime? RecommendedRegime,
    StrFiscalRegime? AssignedRegime,
    bool IsPrimaryForCedolare);

public record FiscalRegimeSnapshot(
    int TaxYear,
    int StrPropertyCount,
    bool RequiresPartitaIva,
    bool HasPartitaIva,
    string Disclaimer,
    IReadOnlyList<FiscalPropertyRow> Properties);

public record FiscalTaxProfile(
    bool HasPartitaIva,
    string? PartitaIvaNumber,
    string? FiscalCode,
    DateTime? FiscalDataRetentionUntil);

public record AnnualIncomeLine(
    Guid PropertyId,
    string Name,
    StrFiscalRegime? Regime,
    decimal GrossIncome,
    decimal Withholding,
    decimal Net);

public record AnnualIncomeTotals(decimal GrossIncome, decimal Withholding, decimal Net);

public record AnnualIncomeReport(
    int TaxYear,
    string PackLabel,
    string Disclaimer,
    IReadOnlyList<AnnualIncomeLine> Properties,
    AnnualIncomeTotals Totals);

public record WithholdingOtaBucket(string Source, decimal Gross, decimal Withholding, decimal Net, int PayoutCount);

public record WithholdingLine(
    Guid PaymentId,
    Guid PropertyId,
    string Source,
    DateTime PaidAt,
    decimal Gross,
    decimal Withholding,
    decimal Net);

public record WithholdingReport(
    int TaxYear,
    string PackLabel,
    IReadOnlyList<WithholdingOtaBucket> ByOta,
    IReadOnlyList<WithholdingLine> Lines);

public record FiscalSimulateResult(string RecommendedForCount, bool RequiresPartitaIva, string Disclaimer);

public interface IFiscalRegimeService
{
    Task<FiscalRegimeSnapshot> GetRegimeAsync(Guid orgId, int taxYear, CancellationToken cancellationToken = default);
    Task<FiscalPropertyRow> AssignRegimeAsync(
        Guid orgId,
        Guid propertyId,
        int taxYear,
        StrFiscalRegime regime,
        bool? isPrimaryForCedolare,
        CancellationToken cancellationToken = default);
    Task<FiscalTaxProfile> GetTaxProfileAsync(Guid orgId, CancellationToken cancellationToken = default);
    Task<FiscalTaxProfile> UpdateTaxProfileAsync(
        Guid orgId,
        bool hasPartitaIva,
        string? partitaIvaNumber,
        string? fiscalCode,
        CancellationToken cancellationToken = default);
    Task<FiscalSimulateResult> SimulateAsync(Guid orgId, int taxYear, int? hypotheticalStrCount, CancellationToken cancellationToken = default);
    Task ApplyWithholdingOnCreateAsync(Payment payment, Booking booking, bool? applyOtaWithholding, decimal? manualWithholdingTax);
}

public interface IFiscalReportingService
{
    Task<AnnualIncomeReport> GetAnnualReportAsync(Guid orgId, int taxYear, CancellationToken cancellationToken = default);
    Task<WithholdingReport> GetWithholdingReportAsync(Guid orgId, int taxYear, CancellationToken cancellationToken = default);
    byte[] ToCsv(AnnualIncomeReport report);
    byte[] ToCsv(WithholdingReport report);
    byte[] ToPdf(string title, string body);
}

public sealed class FiscalValidationException(string message) : Exception(message);
public sealed class FiscalConflictException(string message) : Exception(message);
