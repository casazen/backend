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

    /// <summary>OTA withholding on <paramref name="gross"/> at <paramref name="rate"/> (<c>ShortStayFiscal:OtaWithholdingRate</c>).</summary>
    public static decimal CalculateOtaWithholding(decimal gross, decimal rate) =>
        decimal.Round(gross * rate, 2, MidpointRounding.AwayFromZero);

    /// <summary>Business (impresa) regimes: not short-term rentals, so no cedolare and no OTA withholding (fiscale.md C10).</summary>
    public static bool IsImpresaRegime(StrFiscalRegime regime) =>
        regime is StrFiscalRegime.RegimeOrdinario or StrFiscalRegime.RegimeForfettario;

    /// <summary>Regimes reserved to taxpayers within the short-rental threshold (fiscale.md C2, C3, C11).</summary>
    public static bool IsShortRentalRegime(StrFiscalRegime regime) =>
        regime is StrFiscalRegime.CedolareSecca21 or StrFiscalRegime.CedolareSecca26 or StrFiscalRegime.IrpefOrdinaria;
}

/// <summary>Stable codes of <see cref="FiscalPropertyRow.TaxNote"/>, translated by the frontend.</summary>
public static class FiscalTaxNotes
{
    /// <summary>Ordinary IRPEF: CasaZen does not compute it; the taxpayer or the accountant does (fiscale.md C11).</summary>
    public const string IrpefOrdinariaNotComputed = "irpef_ordinaria_not_computed";

    /// <summary>The taxpayer is over the threshold: business activity presumed, check with the accountant (fiscale.md C3).</summary>
    public const string ThresholdExceeded = "short_stay_threshold_exceeded";
}

/// <param name="ShortStayInTaxYear">
/// The apartment had at least one short-term stay (confirmed, at most <c>ShortStayFiscal:MaxStayNights</c> nights) starting
/// in the tax year, so it counts toward its taxpayer's threshold.
/// </param>
/// <param name="TaxpayerIndex">Index of the property's taxpayer in <see cref="FiscalRegimeSnapshot.Taxpayers"/>.</param>
/// <param name="CedolareRate">Rate of the assigned cedolare regime (fraction), from configuration; null otherwise.</param>
/// <param name="TaxNote">A <see cref="FiscalTaxNotes"/> code, or null.</param>
public record FiscalPropertyRow(
    Guid PropertyId,
    string Name,
    StrFiscalRegime? RecommendedRegime,
    StrFiscalRegime? AssignedRegime,
    bool IsPrimaryForCedolare,
    bool ShortStayInTaxYear,
    int TaxpayerIndex,
    decimal? CedolareRate,
    string? TaxNote);

/// <summary>One taxpayer (titolare fiscale) of the org's properties and its short-rental threshold for the tax year.</summary>
/// <param name="FiscalCodeMasked">Masked codice fiscale; null for the org tax profile without a codice fiscale.</param>
/// <param name="IsOrgTaxProfile">The org's own tax profile (properties with no taxpayer recorded, or the same codice fiscale).</param>
/// <param name="ShortStayApartmentCount">Apartments of this taxpayer with short-term stays in the tax year.</param>
/// <param name="ThresholdExceeded">
/// More than <see cref="FiscalRegimeSnapshot.MaxShortStayApartmentsPerTaxpayer"/>: business activity presumed, no cedolare
/// and no IRPEF-ordinaria regime; the host should check with an accountant.
/// </param>
/// <param name="ReducedRatePropertyId">The one unit designated at the 21% cedolare, if any.</param>
public record FiscalTaxpayerSummary(
    int Index,
    string? FiscalCodeMasked,
    bool IsOrgTaxProfile,
    int ShortStayApartmentCount,
    bool ThresholdExceeded,
    Guid? ReducedRatePropertyId);

/// <param name="StrPropertyCount">Apartments of the org with short-term stays in the tax year (all taxpayers).</param>
/// <param name="RequiresPartitaIva">
/// At least one taxpayer is over the threshold (business activity presumed, partita IVA as a consequence). The threshold is
/// per taxpayer (<see cref="Taxpayers"/>), never per org.
/// </param>
public record FiscalRegimeSnapshot(
    int TaxYear,
    int StrPropertyCount,
    bool RequiresPartitaIva,
    bool HasPartitaIva,
    string Disclaimer,
    IReadOnlyList<FiscalPropertyRow> Properties,
    int MaxShortStayApartmentsPerTaxpayer,
    string ThresholdSource,
    IReadOnlyList<FiscalTaxpayerSummary> Taxpayers);

/// <summary>Taxpayer recorded on a property (masked codice fiscale; null when the org tax profile applies).</summary>
public record FiscalPropertyTaxpayer(Guid PropertyId, string? FiscalCodeMasked);

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
    /// <summary>
    /// Records who lets the property (titolare fiscale) by codice fiscale, or clears it (<paramref name="fiscalCode"/> null or
    /// empty: the org tax profile applies). The threshold and the 21% unit are counted per taxpayer (CO-18).
    /// </summary>
    Task<FiscalPropertyTaxpayer> SetPropertyTaxpayerAsync(
        Guid orgId,
        Guid propertyId,
        string? fiscalCode,
        CancellationToken cancellationToken = default);
    Task<FiscalSimulateResult> SimulateAsync(Guid orgId, int taxYear, int? hypotheticalStrCount, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the withholding of an OTA payment. By default (<paramref name="applyOtaWithholding"/> null) the configured rate
    /// applies only to short-term rentals: not when the property's regime for the stay's tax year is impresa or its taxpayer
    /// is over the threshold (fiscale.md C10). <c>true</c> records the withholding anyway (an OTA that applied it), <c>false</c>
    /// never computes it; <paramref name="manualWithholdingTax"/> records the amount actually withheld.
    /// </summary>
    Task ApplyWithholdingOnCreateAsync(Payment payment, Booking booking, bool? applyOtaWithholding, decimal? manualWithholdingTax);
}

public interface IFiscalReportingService
{
    Task<AnnualIncomeReport> GetAnnualReportAsync(Guid orgId, int taxYear, CancellationToken cancellationToken = default);
    Task<WithholdingReport> GetWithholdingReportAsync(Guid orgId, int taxYear, CancellationToken cancellationToken = default);
    byte[] ToCsv(AnnualIncomeReport report);
    byte[] ToCsv(WithholdingReport report);
    /// <summary>A4 PDF of <paramref name="title"/> and a plain text body (blank line = new paragraph), via <c>IPdfDocumentRenderer</c>.</summary>
    byte[] ToPdf(string title, string body);
}

public sealed class FiscalValidationException(string message) : Exception(message);
