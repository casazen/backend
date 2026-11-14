using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// <see cref="IFiscalTaxRuleResolver"/> over the catalog <c>FiscalDocuments:TaxRules</c> (validated at startup, <see cref="FiscalTaxRulesOptionsValidator"/>):
/// the rule set of the country and the regime whose range holds the date, then the choices of the issuer: an exempt stay takes the exempt nature of the
/// rule (<c>N4</c>, Italy; a rule without one keeps its rate), and the cleaning that follows the stay takes the VAT of the stay. A Swiss rule never carries
/// a nature code.
/// </summary>
public sealed class FiscalTaxRuleResolver(IOptions<FiscalTaxRulesOptions> options) : IFiscalTaxRuleResolver
{
    public FiscalTaxRule Resolve(
        string? countryCode,
        DocumentRegime? regime,
        StayVatTreatment stayVatTreatment,
        CleaningFeeTreatment cleaningFeeTreatment,
        DateOnly date) =>
        TryResolve(countryCode, regime, stayVatTreatment, cleaningFeeTreatment, date)
        ?? throw new DomainRuleException(DocumentIssuerErrorCodes.RuleNotConfigured, DocumentIssuerErrorCodes.MessageKeyOf(DocumentIssuerErrorCodes.RuleNotConfigured));

    public FiscalTaxRule? TryResolve(
        string? countryCode,
        DocumentRegime? regime,
        StayVatTreatment stayVatTreatment,
        CleaningFeeTreatment cleaningFeeTreatment,
        DateOnly date)
    {
        if (regime is not { } known || !Enum.IsDefined(known))
            return null;

        var jurisdiction = string.IsNullOrWhiteSpace(countryCode) ? FiscalDocumentRules.Italy : countryCode.Trim().ToUpperInvariant();
        var set = options.Value.TaxRules.FirstOrDefault(s =>
            string.Equals(s.Jurisdiction?.Trim(), jurisdiction, StringComparison.OrdinalIgnoreCase)
            && s.Regime == known
            && s.ValidFrom <= date
            && (s.ValidTo is null || date <= s.ValidTo));
        if (set is null)
            return null;

        var swiss = jurisdiction == FiscalDocumentRules.Switzerland;
        var rates = Enum.GetValues<FiscalLineType>().ToDictionary(type => type, type => RateOf(set, type, swiss));

        // The choices of the issuer: an exempt stay (art. 10, where the rule has the nature of it), and the cleaning that follows the stay.
        if (stayVatTreatment == StayVatTreatment.ExemptArt10 && !swiss && !string.IsNullOrWhiteSpace(set.StayExemptNature))
            rates[FiscalLineType.Accommodation] = new FiscalLineRate(0m, set.StayExemptNature.Trim());
        if (cleaningFeeTreatment == CleaningFeeTreatment.FollowsStay)
            rates[FiscalLineType.Cleaning] = rates[FiscalLineType.Accommodation];

        return new FiscalTaxRule(
            jurisdiction,
            known,
            set.Version.Trim(),
            set.ValidFrom,
            set.ValidTo,
            set.SourceUrl,
            set.DocumentKind,
            swiss ? null : Empty(set.FatturaPaRegimeCode),
            set.InvoiceTypeCode,
            set.CreditNoteTypeCode,
            rates,
            swiss || set.StampDuty is not { } stamp ? null : new FiscalStampDutyRule(stamp.Threshold, stamp.Amount, stamp.ChargedToRecipient));
    }

    private static FiscalLineRate RateOf(FiscalTaxRuleSetOptions set, FiscalLineType type, bool swiss)
    {
        if (!set.Rates.TryGetValue(type, out var rate) || rate?.Percent is not { } percent)
            throw new InvalidOperationException($"The tax rule {set.Version} has no rate for {type}.");

        return new FiscalLineRate(percent, swiss || percent != 0m ? null : Empty(rate.Nature));
    }

    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
