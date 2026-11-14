using Casazen.Core.Entities.Enums;
using FiscalDocumentKind = Casazen.Core.Options.FiscalDocumentKind;

namespace Casazen.Core.Services;

/// <summary>
/// The tax rule of a document (NF-82, NF-83, NF-113, D2, E11/F01/US01/T02): the rule set of the catalog <c>FiscalDocuments:TaxRules</c> in force for the
/// country and the regime of the issuer at a date, with the choices of the issuer applied (the exempt stay, the cleaning with the stay).
/// </summary>
/// <remarks>
/// The date is the check-out of the stay for a document of a guest, the last day of the period for an invoice to an owner: a stay that ended before a
/// new version keeps the old rate.
/// </remarks>
public interface IFiscalTaxRuleResolver
{
    /// <summary>
    /// The rule in force, or 422 <c>document_rule_not_configured</c> when no rule set applies (no regime, a country or a date the catalog does not cover):
    /// nothing is issued with a rate nobody approved.
    /// </summary>
    FiscalTaxRule Resolve(string? countryCode, DocumentRegime? regime, StayVatTreatment stayVatTreatment, CleaningFeeTreatment cleaningFeeTreatment, DateOnly date);

    /// <summary>The rule in force, or <c>null</c> when none applies.</summary>
    FiscalTaxRule? TryResolve(string? countryCode, DocumentRegime? regime, StayVatTreatment stayVatTreatment, CleaningFeeTreatment cleaningFeeTreatment, DateOnly date);
}

/// <summary>A tax rule resolved for an issuer and a date: the version of the catalog and the VAT of every line type, the treatments applied.</summary>
/// <param name="Jurisdiction"><c>IT</c> or <c>CH</c>.</param>
/// <param name="Regime">The regime of the issuer.</param>
/// <param name="Version">The version stored on the document.</param>
/// <param name="ValidFrom">The first day of the version.</param>
/// <param name="ValidTo">The last day of the version; <c>null</c> = open.</param>
/// <param name="SourceUrl">Where the rates were read from.</param>
/// <param name="DocumentKind">A receipt or an invoice.</param>
/// <param name="FatturaPaRegimeCode">The regime code of FatturaPA of the rule (Italy).</param>
/// <param name="InvoiceTypeCode">The document type of an invoice in FatturaPA (Italy).</param>
/// <param name="CreditNoteTypeCode">The document type of a credit note in FatturaPA (Italy).</param>
/// <param name="Rates">The VAT of every line type, the treatments of the issuer applied.</param>
/// <param name="StampDuty">The stamp duty (Italy); <c>null</c> = none.</param>
public sealed record FiscalTaxRule(
    string Jurisdiction,
    DocumentRegime Regime,
    string Version,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    string? SourceUrl,
    FiscalDocumentKind DocumentKind,
    string? FatturaPaRegimeCode,
    string InvoiceTypeCode,
    string CreditNoteTypeCode,
    IReadOnlyDictionary<FiscalLineType, FiscalLineRate> Rates,
    FiscalStampDutyRule? StampDuty)
{
    /// <summary>A Swiss rule: no XML, no SDI, no nature code, no stamp duty.</summary>
    public bool IsSwiss => Jurisdiction == FiscalDocumentRules.Switzerland;

    /// <summary>The VAT of <paramref name="type"/> (the catalog is validated to have one for every line type).</summary>
    public FiscalLineRate RateOf(FiscalLineType type) =>
        Rates.TryGetValue(type, out var rate) ? rate : throw new InvalidOperationException($"The rule {Version} has no rate for {type}.");
}

/// <summary>The VAT of a line type: the rate in percent and, for an Italian rate of 0, the nature code.</summary>
public sealed record FiscalLineRate(decimal Percent, string? NatureCode);
