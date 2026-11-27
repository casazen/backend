using Casazen.Core.Entities.Enums;
using Casazen.Core.Regulatory;
using Casazen.Core.Utilities;

namespace Casazen.Core.Leases;

public enum LeaseReviewSeverity
{
    Block,

    Warn,

    Info,

    Ok,
}

public sealed record LeaseReviewCheck(string Code, LeaseReviewSeverity Severity, int Step, IReadOnlyDictionary<string, object?>? Params = null);

public sealed record LeaseReviewFacts
{
    /// <summary>
    /// The other lease of the same property that covers some of the days of this one (<see cref="LeasePeriodRules"/>, LR-OVERLAP):
    /// two leases cannot cover the same days, so it blocks. It replaces the warning of decision LR-D14 (one may let by rooms), which the
    /// product owner reversed on 2026-10-25.
    /// </summary>
    public LeasePeriodConflict? PeriodConflict { get; init; }

    public bool TemplateNotApproved { get; init; }

    public bool TemplateDataMissing { get; init; }
}

/// <summary>
/// The review of a draft, before the landlord confirms it (LR-12): the blocking problems, the things to check, the things to
/// know and what is fine, each with the step to fix it in. Pure: the same inputs as <see cref="LeaseDraftProgress"/> plus the two
/// facts of <see cref="LeaseReviewFacts"/>. The port of <c>checksOf</c> of the demo (<c>lc-info.js</c>) without the checks the
/// interview took out (IBAN, telephone, clauses, attachments, the invitation of the tenant).
/// </summary>
/// <remarks>
/// The blocks are the needs of the flow in steps 1 to 5 (same code, same step), so a screen that lists "Resta da fare" and the
/// review never disagree; for an imported contract the missing APE and cadastral data are warnings (decision D2) and a start in the
/// past is not even that (LR-13). The parameters carry no personal data: counts, amounts and dates, never a name or a fiscal code.
/// </remarks>
public static class LeaseReview
{
    public static IReadOnlyList<LeaseReviewCheck> Compute(LeaseDraftProgressInput input, LeaseReviewFacts facts)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(facts);

        var imported = input.Origin == LeaseOrigin.Imported;
        var values = input.Values;
        var reading = LeaseDraftRules.Read(values, input.Origin);
        var flow = LeaseDraftProgress.Compute(input);
        var checks = new List<LeaseReviewCheck>();

        void Add(string code, LeaseReviewSeverity severity, int step, params (string Key, object? Value)[] parameters) =>
            checks.Add(new LeaseReviewCheck(
                code,
                severity,
                step,
                parameters.Length == 0 ? null : parameters.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)));

        var blocked = new HashSet<string>(StringComparer.Ordinal);
        for (var step = 1; step <= LeaseDraftDefaults.LastDataStep; step++)
        {
            foreach (var need in flow[step].Needs)
            {
                blocked.Add(need.Code);
                var severity = imported && need.Code is LeaseNeedCodes.ApeMissing or LeaseNeedCodes.CadastralMissing
                    ? LeaseReviewSeverity.Warn
                    : LeaseReviewSeverity.Block;
                checks.Add(new LeaseReviewCheck(need.Code, severity, step, need.Params));
            }
        }

        if (imported && values.PropertyId is not null && input.Facts.PropertyExists)
        {
            if (!input.Facts.HasApe)
                Add(LeaseNeedCodes.ApeMissing, LeaseReviewSeverity.Warn, 1);
            if (!input.Facts.HasCadastralData)
                Add(LeaseNeedCodes.CadastralMissing, LeaseReviewSeverity.Warn, 1);
        }

        if (values.PropertyId is not null && input.Facts.PropertyExists)
        {
            Add(LeaseNeedCodes.PropertyOk, LeaseReviewSeverity.Ok, 1);
            if (!blocked.Contains(LeaseNeedCodes.ApeMissing) && !blocked.Contains(LeaseNeedCodes.ApeIdentificationMissing) && input.Facts.HasApe)
                Add(LeaseNeedCodes.ApeOk, LeaseReviewSeverity.Ok, 1);
            if (!blocked.Contains(LeaseNeedCodes.CadastralMissing) && input.Facts.HasCadastralData)
                Add(LeaseNeedCodes.CadastralOk, LeaseReviewSeverity.Ok, 1);
            if (input.Facts.PropertyIsShortMode)
                Add(LeaseNeedCodes.PropertyShortMode, LeaseReviewSeverity.Warn, 1);
        }

        if (values.Landlords.Count > 0 && values.Landlords.All(LeaseDraftRules.IsPartyComplete))
            Add(LeaseNeedCodes.LandlordsOk, LeaseReviewSeverity.Ok, 2, ("count", values.Landlords.Count));
        if (values.Tenants.Count > 0 && values.Tenants.All(LeaseDraftRules.IsPartyComplete))
            Add(LeaseNeedCodes.TenantsOk, LeaseReviewSeverity.Ok, 2, ("count", values.Tenants.Count));

        // 3 · type, regime, rent, deposit
        // The rent of a transitory contract in a municipality of more than 10,000 inhabitants has to be inside the bands of the territorial
        // agreement, or the contract falls back to a 4+4 (D.M. 16/01/2017 art. 2 c. 2 and c. 6). CasaZen knows neither the inhabitants of the
        // municipality nor the agreement and has no calculator for the bands (TR-FU1): it tells the landlord to check it, it does not block.
        if (!imported && reading is { Type: LeaseContractType.Transitorio, TypeAvailable: true })
            Add(LeaseNeedCodes.TransitoryRentBandToCheck, LeaseReviewSeverity.Info, 3, ("inhabitants", LeaseContractTerms.TransitorioBandFromInhabitants));

        if (reading.Rent is { } rent)
        {
            Add(LeaseNeedCodes.RentOk, LeaseReviewSeverity.Ok, 3, ("amount", rent));
            if (reading.Type == LeaseContractType.Concordato && reading.TypeAvailable && input.Concordato is { UnavailableReason: null } band
                && values.Concordato is not null && LeaseDraftRules.IsConcordatoValid(values.Concordato))
            {
                if (!band.RentOutOfBand)
                    Add(LeaseNeedCodes.RentInBand, LeaseReviewSeverity.Ok, 3, ("minMonthly", band.MinMonthly), ("maxMonthly", band.MaxMonthly));
                else if (band.Indicative)
                    Add(LeaseNeedCodes.RentOutsideIndicativeBand, LeaseReviewSeverity.Warn, 3, ("minMonthly", band.MinMonthly), ("maxMonthly", band.MaxMonthly));
            }

            if (reading.Deposit is { } deposit && !blocked.Contains(LeaseNeedCodes.DepositTooHigh))
                Add(LeaseNeedCodes.DepositOk, LeaseReviewSeverity.Ok, 3, ("amount", deposit), ("rents", decimal.Round(deposit / rent, 1)));
        }

        if (reading.Start is { } start && reading.End is { } end)
        {
            if (reading.Months is { } months && !blocked.Contains(LeaseNeedCodes.EndInvalid) && !blocked.Contains(LeaseNeedCodes.TermOutOfRange))
            {
                Add(
                    LeaseNeedCodes.StartOk,
                    LeaseReviewSeverity.Ok,
                    4,
                    ("start", start.ToString("yyyy-MM-dd")),
                    ("end", end.ToString("yyyy-MM-dd")),
                    ("months", months));
            }
            else
            {
                Add(LeaseNeedCodes.StartOk, LeaseReviewSeverity.Ok, 4, ("start", start.ToString("yyyy-MM-dd")));
            }

            if (start < input.Today && !imported)
                Add(LeaseNeedCodes.StartInPast, LeaseReviewSeverity.Warn, 4, ("start", start.ToString("yyyy-MM-dd")));
            if (facts.PeriodConflict is { } conflict)
            {
                Add(
                    LeaseNeedCodes.LeaseOverlap,
                    LeaseReviewSeverity.Block,
                    4,
                    ("conflictingLeaseId", conflict.ConflictingLeaseId),
                    ("conflictingFrom", conflict.ConflictingFrom.ToString("yyyy-MM-dd")),
                    ("conflictingTo", conflict.ConflictingTo.ToString("yyyy-MM-dd")));
            }
        }

        if (!imported)
        {
            var unverified = values.Landlords.Concat(values.Tenants)
                .Count(p => LeaseDraftRules.IsPartyComplete(p) && LeaseDraftRules.Clean(p.DocumentsVerifiedOn) is null);
            if (unverified > 0)
                Add(LeaseNeedCodes.DocumentsNotVerified, LeaseReviewSeverity.Info, 5, ("count", unverified));
        }

        if (values.Tenants.Any(t => LeaseDraftRules.IsPartyComplete(t)
                && Clean(t.Citizenship) is { } citizenship && !EuMemberStates.IsEuCitizenship(citizenship)))
        {
            Add(LeaseNeedCodes.TenantExtraEu, LeaseReviewSeverity.Info, LeaseDraftDefaults.LastStep);
        }

        if (!imported)
        {
            if (facts.TemplateNotApproved)
                Add(LeaseNeedCodes.TemplateNotApproved, LeaseReviewSeverity.Info, 6);
            else if (facts.TemplateDataMissing)
                Add(LeaseNeedCodes.TemplateDataMissing, LeaseReviewSeverity.Warn, 6);
        }

        return checks
            .OrderBy(c => (int)c.Severity)
            .ThenBy(c => c.Step)
            .ToList();
    }

    public static bool HasBlock(IEnumerable<LeaseReviewCheck> checks) => checks.Any(c => c.Severity == LeaseReviewSeverity.Block);

    private static string? Clean(string? value) => LeaseDraftRules.Clean(value);
}
