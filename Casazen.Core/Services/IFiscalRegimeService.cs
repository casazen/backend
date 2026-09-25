using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public static class FiscalCopy
{
    public const string Disclaimer =
        "Raccomandazione informativa, non consulenza fiscale. CasaZen non presenta dichiarazioni e non sostituisce un commercialista. Riferimenti: L. 199/2025, D.L. 50/2017.";

    public const string PackLabel =
        "Pacchetto dati per il commercialista — non è una dichiarazione, F24 o Certificazione Unica ufficiale";

    /// <summary>Tourist tax report (CO-19): a basis for the comune's statement and payments, not the statement.</summary>
    public const string TouristTaxDisclaimer =
        "Riepilogo per il versamento e la dichiarazione al comune (per esempio il Modello 21): non è la dichiarazione. Scadenze, modalità di versamento ed esenzioni dipendono dal regolamento di ciascun comune.";

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

    /// <summary>Report only: no regime assigned to the property for the tax year, so no tax is estimated (CO-19).</summary>
    public const string RegimeNotAssigned = "regime_not_assigned";

    /// <summary>
    /// Report only: business (impresa) regime, ordinario or forfettario. The income depends on costs and on the forfettario
    /// coefficient, which CasaZen does not have (fiscale.md "Punti che richiedono il commercialista" 5): not estimated (CO-19).
    /// </summary>
    public const string ImpresaNotComputed = "impresa_not_computed";
}

/// <param name="ShortStayInTaxYear">
/// The apartment had at least one short-term stay (confirmed, at most <c>ShortStayFiscal:MaxStayNights</c> nights) starting
/// in the tax year, so it counts toward its taxpayer's threshold.
/// </param>
/// <param name="TaxpayerIndex">Index of the property's taxpayer in <see cref="FiscalRegimeSnapshot.Taxpayers"/>.</param>
/// <param name="CedolareRate">Rate of the assigned cedolare regime (fraction), from configuration; null otherwise.</param>
/// <param name="TaxNote">A <see cref="FiscalTaxNotes"/> code, or null.</param>
/// <param name="AvailableRegimes">
/// Regimes the host can assign now (CO-19): cedolare 21/26 and IRPEF ordinaria while the taxpayer stays within the threshold
/// with this apartment; the impresa regimes when the partita IVA is recorded (always for a taxpayer other than the org tax
/// profile, of whom CasaZen has no such data). The same rules <see cref="IFiscalRegimeService.AssignRegimeAsync"/> enforces.
/// </param>
public record FiscalPropertyRow(
    Guid PropertyId,
    string Name,
    StrFiscalRegime? RecommendedRegime,
    StrFiscalRegime? AssignedRegime,
    bool IsPrimaryForCedolare,
    bool ShortStayInTaxYear,
    int TaxpayerIndex,
    decimal? CedolareRate,
    string? TaxNote,
    IReadOnlyList<StrFiscalRegime> AvailableRegimes);

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

/// <summary>Partial update of the org tax profile: null = unchanged.</summary>
/// <param name="HasPartitaIva">false also clears the stored partita IVA number.</param>
/// <param name="PartitaIvaNumber">11 digits (spaces ignored); only with a partita IVA, set now or already saved.</param>
/// <param name="FiscalCode">Codice fiscale (16 characters, or 11 digits for an entity); an empty string clears it.</param>
public record FiscalTaxProfileUpdate(bool? HasPartitaIva, string? PartitaIvaNumber, string? FiscalCode);

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

    /// <summary>
    /// Changes only the fields of <paramref name="update"/> that are set (CO-19): a null field keeps the saved value, so a
    /// form sending only what the host changed never overwrites the rest with defaults.
    /// </summary>
    Task<FiscalTaxProfile> UpdateTaxProfileAsync(
        Guid orgId,
        FiscalTaxProfileUpdate update,
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

/// <summary>
/// Reports of the fiscal area for the accountant (CO-19, A5-23): summary per property and taxpayer, withholding detail,
/// tourist tax per comune and month. Every list is limited to <see cref="Casazen.Core.Authorization.HostScope"/> (the org,
/// and only the caller's own properties for a caller without org-wide access). PDF (via <c>IPdfDocumentRenderer</c>) and
/// CSV in Italian.
/// </summary>
public interface IFiscalReportingService
{
    Task<AnnualIncomeReport> GetAnnualReportAsync(
        Casazen.Core.Authorization.HostScope scope,
        int taxYear,
        FiscalReportPeriod? period = null,
        CancellationToken cancellationToken = default);

    Task<WithholdingReport> GetWithholdingReportAsync(
        Casazen.Core.Authorization.HostScope scope,
        int taxYear,
        FiscalReportPeriod? period = null,
        CancellationToken cancellationToken = default);

    Task<TouristTaxReport> GetTouristTaxReportAsync(
        Casazen.Core.Authorization.HostScope scope,
        FiscalReportPeriod period,
        CancellationToken cancellationToken = default);

    byte[] ToCsv(AnnualIncomeReport report);
    byte[] ToCsv(WithholdingReport report);
    byte[] ToCsv(TouristTaxReport report);
    byte[] ToPdf(AnnualIncomeReport report);
    byte[] ToPdf(WithholdingReport report);
    byte[] ToPdf(TouristTaxReport report);
}

/// <summary>
/// Invalid input of the fiscal area, answered 400 with the stable <see cref="Code"/> and the localized
/// <see cref="MessageKey"/> of <c>SharedResources</c> (IT/EN).
/// </summary>
public sealed class FiscalValidationException(string code, string messageKey, params object[] messageArgs) : Exception(code)
{
    public string Code { get; } = code;

    public string MessageKey { get; } = messageKey;

    public object[] MessageArgs { get; } = messageArgs;
}
