using System.Globalization;
using System.Text.RegularExpressions;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Microsoft.Extensions.Options;

namespace Casazen.Core.Options;

/// <summary>
/// The catalog of the tax rules of the fiscal documents (NF-82, NF-83, NF-113, D2, E11/F01/US01/T02), bound from <c>FiscalDocuments:TaxRules</c>: a list
/// of rule sets, each for one country (<c>IT</c> or <c>CH</c>), one regime of the issuer and a range of dates, with a VAT rate (and in Italy the nature
/// code of a rate of 0) for every <see cref="FiscalLineType"/>. A document takes the rule in force at the date of its stay (the check-out) or of its
/// period, and stores its <see cref="FiscalTaxRuleSetOptions.Version"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Validated at startup</b> (<see cref="FiscalTaxRulesOptionsValidator"/>, <c>ValidateOnStart</c>): overlapping ranges of one country and regime,
/// a line type without a rate, an Italian rate of 0 without a nature code, and the other errors listed there stop the application with a clear message,
/// instead of a document issued with a rate nobody approved. An empty catalog is valid: nothing is issued (<c>document_rule_not_configured</c>).</item>
/// <item><b>A published version never changes.</b> A new rate is a new rule set from its first day, the old one closed the day before
/// (<c>docs/runbooks/fiscal-documents.md</c> § Tax rules).</item>
/// <item>The values marked ⚖ in <c>appsettings.json</c> are prudent defaults to be confirmed by an accountant.</item>
/// </list>
/// </remarks>
public sealed class FiscalTaxRulesOptions
{
    /// <summary>The section the catalog is read from: its rule sets are <c>FiscalDocuments:TaxRules</c>.</summary>
    public const string SectionName = "FiscalDocuments";

    /// <summary>The rule sets.</summary>
    public List<FiscalTaxRuleSetOptions> TaxRules { get; set; } = [];
}

/// <summary>One rule set of the catalog: a country, a regime and a range of dates.</summary>
public sealed class FiscalTaxRuleSetOptions
{
    /// <summary>The longest <see cref="Version"/>: it is stored on every document (<c>FiscalDocument.RuleVersion</c>).</summary>
    public const int VersionMaxLength = 64;

    /// <summary>The country of the rule: <c>IT</c> or <c>CH</c>.</summary>
    public string Jurisdiction { get; set; } = string.Empty;

    /// <summary>The regime of the issuer the rule is for; one of the regimes of <see cref="Jurisdiction"/>.</summary>
    public DocumentRegime Regime { get; set; }

    /// <summary>The first day the rule is in force.</summary>
    public DateOnly ValidFrom { get; set; }

    /// <summary>The last day the rule is in force; <c>null</c> = open.</summary>
    public DateOnly? ValidTo { get; set; }

    /// <summary>The name of this version, stored on the documents made with it (<c>ch-vat-2024.1</c>). Unique in the catalog.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Where the rates were read from (the page of the tax administration or of the law).</summary>
    public string? SourceUrl { get; set; }

    /// <summary>The document the rule gives: <see cref="FiscalDocumentKind.Receipt"/> or <see cref="FiscalDocumentKind.Invoice"/>.</summary>
    public FiscalDocumentKind DocumentKind { get; set; } = FiscalDocumentKind.None;

    /// <summary>The regime code of FatturaPA (<c>RF01</c>, <c>RF19</c>); the profile of the issuer can override it. Italy only.</summary>
    public string? FatturaPaRegimeCode { get; set; }

    /// <summary>The type of document of an invoice in FatturaPA. Italy only.</summary>
    public string InvoiceTypeCode { get; set; } = "TD01";

    /// <summary>The type of document of a credit note in FatturaPA. Italy only.</summary>
    public string CreditNoteTypeCode { get; set; } = "TD04";

    /// <summary>
    /// The VAT of each line type: the rate in percent and, in Italy for a rate of 0, the nature code. The rate of <see cref="FiscalLineType.Cleaning"/> is
    /// the one of the cleaning as a service: the cleaning that follows the stay takes the rate of the stay.
    /// </summary>
    public Dictionary<FiscalLineType, FiscalTaxRateOptions> Rates { get; set; } = [];

    /// <summary>The stamp duty of the documents without VAT. Italy only; <c>null</c> = none.</summary>
    public FiscalStampDutyOptions? StampDuty { get; set; }

    /// <summary>The nature code of the stay of an issuer whose stay is exempt (<c>StayVatTreatment.ExemptArt10</c>, <c>N4</c>). Italy only.</summary>
    public string? StayExemptNature { get; set; }
}

/// <summary>The VAT of one line type.</summary>
public sealed class FiscalTaxRateOptions
{
    /// <summary>The rate in percent, 0 to 100; <c>null</c> is a missing rate (the startup fails).</summary>
    public decimal? Percent { get; set; }

    /// <summary>The nature code of an Italian rate of 0 (<c>N1</c>, <c>N2.2</c>, <c>N4</c>...); never on a Swiss rule.</summary>
    public string? Nature { get; set; }
}

/// <summary>The stamp duty of a rule set: due when the amounts without VAT exceed <see cref="Threshold"/>.</summary>
public sealed class FiscalStampDutyOptions
{
    public decimal Threshold { get; set; }

    public decimal Amount { get; set; }

    /// <summary>Whether the stamp duty is added to the total (charged to the recipient); off, the issuer bears it.</summary>
    public bool ChargedToRecipient { get; set; }
}

/// <summary>
/// The checks of the catalog at startup (<see cref="FiscalTaxRulesOptions"/>): every problem is named with the position and the version of its rule set.
/// </summary>
public sealed partial class FiscalTaxRulesOptionsValidator : IValidateOptions<FiscalTaxRulesOptions>
{
    public ValidateOptionsResult Validate(string? name, FiscalTaxRulesOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var problems = Problems(options);
        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }

    /// <summary>What is wrong with the catalog; empty when it can be used.</summary>
    public static IReadOnlyList<string> Problems(FiscalTaxRulesOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var problems = new List<string>();
        var sets = options.TaxRules ?? [];
        var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < sets.Count; i++)
        {
            var set = sets[i];
            var where = string.Create(
                CultureInfo.InvariantCulture,
                $"FiscalDocuments:TaxRules:{i} ({set.Version}, {set.Jurisdiction} {set.Regime})");
            var jurisdiction = set.Jurisdiction?.Trim().ToUpperInvariant() ?? string.Empty;
            var swiss = jurisdiction == FiscalDocumentRules.Switzerland;
            if (jurisdiction is not (FiscalDocumentRules.Italy or FiscalDocumentRules.Switzerland))
                problems.Add($"{where}: the jurisdiction must be IT or CH.");
            else if (!Enum.IsDefined(set.Regime) || FiscalDocumentRules.CountryOfRegime(set.Regime) != jurisdiction)
                problems.Add($"{where}: the regime is not a regime of {jurisdiction}.");

            if (string.IsNullOrWhiteSpace(set.Version) || set.Version.Trim().Length > FiscalTaxRuleSetOptions.VersionMaxLength)
                problems.Add($"{where}: the version is required, at most {FiscalTaxRuleSetOptions.VersionMaxLength} characters.");
            else if (!versions.Add(set.Version.Trim()))
                problems.Add($"{where}: the version is used by another rule set (a published version is never edited: add a new one).");

            if (set.ValidFrom == default)
                problems.Add($"{where}: ValidFrom is required.");
            if (set.ValidTo is { } to && to < set.ValidFrom)
                problems.Add($"{where}: ValidTo is before ValidFrom.");

            if (set.DocumentKind is not (FiscalDocumentKind.Receipt or FiscalDocumentKind.Invoice))
                problems.Add($"{where}: the document kind must be Receipt or Invoice.");

            foreach (var type in Enum.GetValues<FiscalLineType>())
            {
                if (set.Rates is null || !set.Rates.TryGetValue(type, out var rate) || rate?.Percent is not { } percent)
                {
                    problems.Add($"{where}: the line type {type} has no rate.");
                    continue;
                }

                if (percent is < 0m or > 100m)
                    problems.Add($"{where}: the rate of {type} must be between 0 and 100.");
                if (set.DocumentKind == FiscalDocumentKind.Receipt && percent != 0m)
                    problems.Add($"{where}: a receipt carries no VAT, the rate of {type} must be 0.");

                var nature = rate.Nature?.Trim();
                if (swiss && !string.IsNullOrEmpty(nature))
                    problems.Add($"{where}: a Swiss rule has no nature code ({type}).");
                else if (!swiss && percent == 0m && string.IsNullOrEmpty(nature))
                    problems.Add($"{where}: the Italian rate of 0 of {type} has no nature code.");
                else if (!swiss && percent > 0m && !string.IsNullOrEmpty(nature))
                    problems.Add($"{where}: a nature code goes only with a rate of 0 ({type}).");
                else if (!string.IsNullOrEmpty(nature) && !NatureCode().IsMatch(nature))
                    problems.Add($"{where}: the nature code of {type} is not one of FatturaPA (N1 to N7).");
            }

            if (swiss && set.StampDuty is not null)
                problems.Add($"{where}: a Swiss rule has no stamp duty.");
            if (set.StampDuty is { } stamp && (stamp.Threshold < 0m || stamp.Amount <= 0m))
                problems.Add($"{where}: the stamp duty needs a threshold of 0 or more and a positive amount.");

            if (!string.IsNullOrWhiteSpace(set.StayExemptNature) && (swiss || !NatureCode().IsMatch(set.StayExemptNature.Trim())))
                problems.Add($"{where}: the exempt nature of the stay is an Italian nature code (N1 to N7).");

            if (!swiss && set.DocumentKind == FiscalDocumentKind.Invoice
                && (string.IsNullOrWhiteSpace(set.InvoiceTypeCode) || string.IsNullOrWhiteSpace(set.CreditNoteTypeCode)))
            {
                problems.Add($"{where}: an Italian invoice needs the document types of FatturaPA (TD01, TD04).");
            }
        }

        // Two versions of one country and regime never overlap: a date has one rule.
        foreach (var group in sets
                     .Select((set, index) => (Set: set, Index: index))
                     .GroupBy(x => (Jurisdiction: x.Set.Jurisdiction?.Trim().ToUpperInvariant(), x.Set.Regime)))
        {
            var ordered = group.OrderBy(x => x.Set.ValidFrom).ToList();
            for (var a = 0; a < ordered.Count; a++)
            {
                for (var b = a + 1; b < ordered.Count; b++)
                {
                    var first = ordered[a].Set;
                    var second = ordered[b].Set;
                    if (first.ValidFrom <= (second.ValidTo ?? DateOnly.MaxValue) && second.ValidFrom <= (first.ValidTo ?? DateOnly.MaxValue))
                    {
                        problems.Add(string.Create(
                            CultureInfo.InvariantCulture,
                            $"FiscalDocuments:TaxRules:{ordered[a].Index} ({first.Version}) and :{ordered[b].Index} ({second.Version}): the ranges of {group.Key.Jurisdiction} {group.Key.Regime} overlap."));
                    }
                }
            }
        }

        return problems;
    }

    /// <summary>A nature code of FatturaPA: N1 to N7, with the sub-codes (N2.2, N3.5, N6.9).</summary>
    [GeneratedRegex("^N[1-7](\\.[0-9]{1,2})?$", RegexOptions.CultureInvariant)]
    private static partial Regex NatureCode();
}
