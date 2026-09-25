using Microsoft.Extensions.Options;

namespace Casazen.Core.Options;

/// <summary>
/// Parameters of the long-term lease tax advisory (LT-08, A7-09), section <c>CedolareAdvisory</c>. Every rate, minimum and
/// threshold is configuration with the source it was verified on (<c>.claude/context/regulations/fiscale.md</c>, rules
/// L5-L12 and C11-C12, research RS-5): none is written in code, and the startup fails when one is missing or out of range
/// (<see cref="CedolareAdvisoryOptionsValidator"/>). Runbook: <c>docs/runbooks/rli.md</c> § "Tax advisory (LT-08)".
/// </summary>
public sealed class CedolareAdvisoryOptions
{
    public const string SectionName = "CedolareAdvisory";

    public CedolareRateOptions Cedolare { get; set; } = new();

    public RegistroTaxOptions Registro { get; set; } = new();

    public StampDutyOptions Bollo { get; set; } = new();

    /// <summary>Optional: without brackets the IRPEF comparison is not computed (left to the accountant).</summary>
    public IrpefComparisonOptions Irpef { get; set; } = new();
}

/// <summary>Cedolare secca rates (fiscale.md L11-L12).</summary>
public sealed class CedolareRateOptions
{
    /// <summary>Rate on residential leases (21%).</summary>
    public decimal StandardRate { get; set; }

    /// <summary>Rate of a canone concordato lease in a verified ATA comune (10%).</summary>
    public decimal ConcordatoAtaRate { get; set; }

    public string Source { get; set; } = string.Empty;
}

/// <summary>Registration tax of the ordinary regime (fiscale.md L5, L6, L8).</summary>
public sealed class RegistroTaxOptions
{
    /// <summary>Rate on the annual rent of an urban residential unit (2%).</summary>
    public decimal Rate { get; set; }

    /// <summary>Minimum of the first annuity (67 €); the later annuities have none.</summary>
    public decimal FirstYearMinimumEur { get; set; }

    /// <summary>Share of the annual rent taxed for a canone concordato lease in a verified ATA comune (70%).</summary>
    public decimal ConcordatoAtaBaseShare { get; set; }

    public string Source { get; set; } = string.Empty;
}

/// <summary>
/// Stamp duty of the ordinary regime (fiscale.md L10): <see cref="EurPerUnit"/> every <see cref="PagesPerUnit"/> written
/// pages, and in any case every <see cref="LinesPerUnit"/> lines, for each copy to register.
/// </summary>
public sealed class StampDutyOptions
{
    public decimal EurPerUnit { get; set; }

    public int PagesPerUnit { get; set; }

    public int LinesPerUnit { get; set; }

    public string Source { get; set; } = string.Empty;
}

/// <summary>
/// IRPEF brackets for the comparison with the cedolare (fiscale.md C11-C12). Only the national gross tax is computed:
/// regional and municipal surcharges and deductions are not documented and are left out.
/// </summary>
public sealed class IrpefComparisonOptions
{
    /// <summary>Tax year of the brackets: from the next year on the comparison is not computed until they are updated.</summary>
    public int TaxYear { get; set; }

    /// <summary>Flat reduction of the rent for the landlord's income (5%, art. 37 c. 4-bis TUIR).</summary>
    public decimal RentFlatReduction { get; set; }

    /// <summary>
    /// Total income above which the brackets alone are not the tax (the 33% is neutralized above 200.000 €): the
    /// comparison is not computed.
    /// </summary>
    public decimal ComputationIncomeLimitEur { get; set; }

    /// <summary>Ascending brackets; the last one has no upper limit.</summary>
    public List<IrpefBracketOptions> Brackets { get; set; } = [];

    public string Source { get; set; } = string.Empty;

    public bool Configured => Brackets.Count > 0;
}

public sealed class IrpefBracketOptions
{
    /// <summary>Upper limit of the bracket in euros; null for the last, open bracket.</summary>
    public decimal? UpToEur { get; set; }

    public decimal Rate { get; set; }
}

/// <summary>
/// Rejects a missing or impossible advisory parameter at startup, so a typo in the configuration never shows a wrong
/// tax figure to a landlord.
/// </summary>
public sealed class CedolareAdvisoryOptionsValidator : IValidateOptions<CedolareAdvisoryOptions>
{
    private const string Prefix = CedolareAdvisoryOptions.SectionName + "__";

    public ValidateOptionsResult Validate(string? name, CedolareAdvisoryOptions options)
    {
        var failures = new List<string>();

        var cedolare = options.Cedolare;
        RequireRate(failures, "Cedolare__StandardRate", cedolare.StandardRate);
        RequireRate(failures, "Cedolare__ConcordatoAtaRate", cedolare.ConcordatoAtaRate);
        if (cedolare.ConcordatoAtaRate > cedolare.StandardRate)
            failures.Add($"{Prefix}Cedolare__ConcordatoAtaRate is higher than {Prefix}Cedolare__StandardRate.");
        RequireSource(failures, "Cedolare__Source", cedolare.Source);

        var registro = options.Registro;
        RequireRate(failures, "Registro__Rate", registro.Rate);
        if (registro.FirstYearMinimumEur <= 0)
            failures.Add($"{Prefix}Registro__FirstYearMinimumEur is missing or not positive.");
        if (registro.ConcordatoAtaBaseShare is <= 0 or > 1)
            failures.Add($"{Prefix}Registro__ConcordatoAtaBaseShare is missing or not in (0, 1].");
        RequireSource(failures, "Registro__Source", registro.Source);

        var bollo = options.Bollo;
        if (bollo.EurPerUnit <= 0)
            failures.Add($"{Prefix}Bollo__EurPerUnit is missing or not positive.");
        if (bollo.PagesPerUnit <= 0)
            failures.Add($"{Prefix}Bollo__PagesPerUnit is missing or not positive.");
        if (bollo.LinesPerUnit <= 0)
            failures.Add($"{Prefix}Bollo__LinesPerUnit is missing or not positive.");
        RequireSource(failures, "Bollo__Source", bollo.Source);

        if (options.Irpef.Configured)
            ValidateIrpef(failures, options.Irpef);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateIrpef(List<string> failures, IrpefComparisonOptions irpef)
    {
        if (irpef.TaxYear < 2000)
            failures.Add($"{Prefix}Irpef__TaxYear is missing.");
        if (irpef.RentFlatReduction is < 0 or >= 1)
            failures.Add($"{Prefix}Irpef__RentFlatReduction is not in [0, 1).");
        if (irpef.ComputationIncomeLimitEur <= 0)
            failures.Add($"{Prefix}Irpef__ComputationIncomeLimitEur is missing or not positive.");
        RequireSource(failures, "Irpef__Source", irpef.Source);

        decimal previous = 0;
        for (var i = 0; i < irpef.Brackets.Count; i++)
        {
            var bracket = irpef.Brackets[i];
            RequireRate(failures, $"Irpef__Brackets__{i}__Rate", bracket.Rate);

            var last = i == irpef.Brackets.Count - 1;
            if (last && bracket.UpToEur is not null)
                failures.Add($"{Prefix}Irpef__Brackets__{i}__UpToEur must be empty: the last bracket has no upper limit.");
            if (!last && (bracket.UpToEur is not { } upTo || upTo <= previous))
                failures.Add($"{Prefix}Irpef__Brackets__{i}__UpToEur is missing or not above the previous bracket.");

            previous = bracket.UpToEur ?? previous;
        }
    }

    private static void RequireRate(List<string> failures, string key, decimal rate)
    {
        if (rate is <= 0 or >= 1)
            failures.Add($"{Prefix}{key} is missing or not in (0, 1).");
    }

    private static void RequireSource(List<string> failures, string key, string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            failures.Add($"{Prefix}{key} is missing: every parameter cites its source.");
    }
}
