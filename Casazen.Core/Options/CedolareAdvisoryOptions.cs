using Microsoft.Extensions.Options;

namespace Casazen.Core.Options;

public sealed class CedolareAdvisoryOptions
{
    public const string SectionName = "CedolareAdvisory";

    public CedolareRateOptions Cedolare { get; set; } = new();

    public RegistroTaxOptions Registro { get; set; } = new();

    public StampDutyOptions Bollo { get; set; } = new();

    public IrpefComparisonOptions Irpef { get; set; } = new();
}

public sealed class CedolareRateOptions
{
    public decimal StandardRate { get; set; }

    public decimal ConcordatoAtaRate { get; set; }

    public string Source { get; set; } = string.Empty;
}

public sealed class RegistroTaxOptions
{
    public decimal Rate { get; set; }

    public decimal FirstYearMinimumEur { get; set; }

    public decimal ConcordatoAtaBaseShare { get; set; }

    public string Source { get; set; } = string.Empty;
}

/// <summary>
/// Stamp duty of the ordinary regime (fiscale.md L10): <see cref="EurPerUnit"/> every <see cref="PagesPerUnit"/> written
/// pages, and in any case every <see cref="LinesPerUnit"/> lines, for each copy to register.
/// </summary>
/// <remarks>
/// LT-01 (PO 2026-10-08): <see cref="EurPerUnit"/> (16,00 €) is used ONLY in the tax advisory report shown to the
/// landlord. CasaZen does NOT collect, charge or settle bollo or imposta di registro via Stripe or any other payment
/// channel. Any computed amount is visible only in the advisory/reporting UI.
/// </remarks>
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
    public int TaxYear { get; set; }

    /// <summary>Flat reduction of the rent for the landlord's income (5%, art. 37 c. 4-bis TUIR).</summary>
    public decimal RentFlatReduction { get; set; }

    public decimal ComputationIncomeLimitEur { get; set; }

    public List<IrpefBracketOptions> Brackets { get; set; } = [];

    public string Source { get; set; } = string.Empty;

    public bool Configured => Brackets.Count > 0;
}

public sealed class IrpefBracketOptions
{
    public decimal? UpToEur { get; set; }

    public decimal Rate { get; set; }
}

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
