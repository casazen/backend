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
    public const string SectionName = "FiscalDocuments";

    public List<FiscalTaxRuleSetOptions> TaxRules { get; set; } = [];
}

public sealed class FiscalTaxRuleSetOptions
{
    public const int VersionMaxLength = 64;

    public string Jurisdiction { get; set; } = string.Empty;

    public DocumentRegime Regime { get; set; }

    public DateOnly ValidFrom { get; set; }

    public DateOnly? ValidTo { get; set; }

    /// <summary>The name of this version, stored on the documents made with it (<c>ch-vat-2024.1</c>). Unique in the catalog.</summary>
    public string Version { get; set; } = string.Empty;

    public string? SourceUrl { get; set; }

    public FiscalDocumentKind DocumentKind { get; set; } = FiscalDocumentKind.None;

    /// <summary>The regime code of FatturaPA (<c>RF01</c>, <c>RF19</c>); the profile of the issuer can override it. Italy only.</summary>
    public string? FatturaPaRegimeCode { get; set; }

    public string InvoiceTypeCode { get; set; } = "TD01";

    public string CreditNoteTypeCode { get; set; } = "TD04";

    /// <summary>
    /// The VAT of each line type: the rate in percent and, in Italy for a rate of 0, the nature code. The rate of <see cref="FiscalLineType.Cleaning"/> is
    /// the one of the cleaning as a service: the cleaning that follows the stay takes the rate of the stay.
    /// </summary>
    public Dictionary<FiscalLineType, FiscalTaxRateOptions> Rates { get; set; } = [];

    /// <summary>The stamp duty of the documents without VAT. Italy only; <c>null</c> = none.</summary>
    public FiscalStampDutyOptions? StampDuty { get; set; }

    public string? StayExemptNature { get; set; }
}

public sealed class FiscalTaxRateOptions
{
    public decimal? Percent { get; set; }

    public string? Nature { get; set; }
}

/// <summary>The stamp duty of a rule set: due when the amounts without VAT exceed <see cref="Threshold"/>.</summary>
public sealed class FiscalStampDutyOptions
{
    public decimal Threshold { get; set; }

    public decimal Amount { get; set; }

    public bool ChargedToRecipient { get; set; }
}

public sealed partial class FiscalTaxRulesOptionsValidator : IValidateOptions<FiscalTaxRulesOptions>
{
    public ValidateOptionsResult Validate(string? name, FiscalTaxRulesOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var problems = Problems(options);
        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }

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

    [GeneratedRegex("^N[1-7](\\.[0-9]{1,2})?$", RegexOptions.CultureInvariant)]
    private static partial Regex NatureCode();
}
