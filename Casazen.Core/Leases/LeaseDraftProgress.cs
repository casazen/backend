using Casazen.Core.Entities.Enums;
using Casazen.Core.Regulatory;

namespace Casazen.Core.Leases;

public enum LeaseStepState
{
    Done,

    Todo,

    Waiting,

    Locked,

    NotApplicable,
}

public sealed record LeaseNeed(string Code, IReadOnlyDictionary<string, object?>? Params = null)
{
    public static LeaseNeed Of(string code, params (string Key, object? Value)[] parameters) =>
        new(code, parameters.Length == 0 ? null : parameters.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));
}

public sealed record LeaseFlowStep(int Step, LeaseStepState State, IReadOnlyList<LeaseNeed> Needs);

public sealed record LeaseDraftFlow(IReadOnlyList<LeaseFlowStep> Steps)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool DataComplete { get; init; }

    /// <summary>
    /// The other lease of the property that covers some of the days of this one, once the property and both dates are there (LR-OVERLAP,
    /// <see cref="LeasePeriodRules"/>); <c>null</c> otherwise. Not a need of a step: it blocks nothing here, it is the soft warning of the
    /// wizard. The hard answer is the 409 <c>lease_period_overlaps</c> of the steps that make the lease a contract. Set by the service, which
    /// asks the database; the pure computation of the steps never does.
    /// </summary>
    public LeasePeriodConflict? PeriodConflict { get; init; }

    public LeaseFlowStep this[int step] => Steps[step - 1];
}

public sealed record LeaseDraftFacts
{
    public bool PropertyExists { get; init; } = true;

    public bool HasApe { get; init; }

    public bool HasApeIdentification { get; init; }

    public bool HasCadastralData { get; init; }

    /// <summary>The property is let for short stays (decision LR-D15: a warning in the review, not a block).</summary>
    public bool PropertyIsShortMode { get; init; }
}

public sealed record LeaseDraftConcordatoOutcome
{
    public string? UnavailableReason { get; init; }

    public bool RentOutOfBand { get; init; }

    public bool Indicative { get; init; }

    public decimal? MinMonthly { get; init; }

    public decimal? MaxMonthly { get; init; }
}

public sealed record LeaseDraftProgressInput
{
    public LeaseOrigin Origin { get; init; } = LeaseOrigin.Created;

    public LeaseStatus Status { get; init; } = LeaseStatus.Draft;

    public int WizardMaxStep { get; init; }

    public DateTime? ReviewConfirmedAt { get; init; }

    public DateTime? SentForSignatureAt { get; init; }

    public required LeaseDraftValues Values { get; init; }

    public LeaseDraftFacts Facts { get; init; } = new();

    public LeaseDraftConcordatoOutcome? Concordato { get; init; }

    public DateOnly Today { get; init; }
}

/// <summary>
/// The wizard of a lease, step by step (LR-12): for each of the eight steps its <see cref="LeaseStepState"/> and what is missing,
/// computed on the server from the values typed and the facts about the property, so the same rules serve the screens, the
/// list and the reminders and the interface keeps none of them. The port of <c>stepsOf</c> of the demo (<c>lc-flow.js</c>) without
/// what the interview took out (IBAN, telephone, invitation of the tenant, clauses and attachments).
/// </summary>
/// <remarks>
/// A data step (1 to 5) is <see cref="LeaseStepState.Done"/> when nothing is missing and the landlord has been there
/// (<c>WizardMaxStep ≥ n</c>); steps 6 to 8 follow the facts and are <see cref="LeaseStepState.Locked"/> until the ones before are
/// complete. For an imported contract steps 5 and 6 do not apply and step 7 is "contratto firmato".
/// </remarks>
public static class LeaseDraftProgress
{
    private static readonly HashSet<string> PropertyFacts =
        [LeaseNeedCodes.ApeMissing, LeaseNeedCodes.ApeIdentificationMissing, LeaseNeedCodes.CadastralMissing];

    public static LeaseDraftFlow Compute(LeaseDraftProgressInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var imported = input.Origin == LeaseOrigin.Imported;
        var values = input.Values;
        var reading = LeaseDraftRules.Read(values, input.Origin);

        var needs = new List<LeaseNeed>[LeaseDraftDefaults.LastStep];
        for (var i = 0; i < needs.Length; i++)
            needs[i] = [];

        Property(input, imported, needs[0]);
        Parties(values, needs[1]);
        Economics(input, imported, reading, needs[2]);
        Start(values, imported, reading, needs[3]);
        if (!imported)
            Attachments(values, input.Today, needs[4]);

        var reached = input.Status == LeaseStatus.Draft ? input.WizardMaxStep : LeaseDraftDefaults.LastStep;
        var steps = new LeaseFlowStep[LeaseDraftDefaults.LastStep];
        for (var n = 1; n <= LeaseDraftDefaults.LastDataStep; n++)
        {
            steps[n - 1] = imported && n == 5
                ? new LeaseFlowStep(n, LeaseStepState.NotApplicable, [])
                : new LeaseFlowStep(n, needs[n - 1].Count == 0 && reached >= n ? LeaseStepState.Done : LeaseStepState.Todo, needs[n - 1]);
        }

        var dataStepsComplete = Enumerable.Range(0, imported ? 4 : LeaseDraftDefaults.LastDataStep).All(i => needs[i].Count == 0);
        var dataComplete = Enumerable.Range(0, LeaseDraftDefaults.LastDataStep).All(i => needs[i].All(n => PropertyFacts.Contains(n.Code)));

        var signed = IsSigned(input.Status);
        var reviewDone = !imported && (input.ReviewConfirmedAt is not null || input.Status != LeaseStatus.Draft);

        steps[5] = imported
            ? new LeaseFlowStep(6, LeaseStepState.NotApplicable, [])
            : !dataStepsComplete
                ? new LeaseFlowStep(6, LeaseStepState.Locked, [])
                : reviewDone
                    ? new LeaseFlowStep(6, LeaseStepState.Done, [])
                    : new LeaseFlowStep(6, LeaseStepState.Todo, [LeaseNeed.Of(LeaseNeedCodes.ReviewNotConfirmed)]);

        var signatureOpen = imported ? dataStepsComplete : steps[5].State == LeaseStepState.Done;
        steps[6] = !signatureOpen
            ? new LeaseFlowStep(7, LeaseStepState.Locked, [])
            : signed
                ? new LeaseFlowStep(7, LeaseStepState.Done, [])
                : new LeaseFlowStep(
                    7,
                    LeaseStepState.Todo,
                    [LeaseNeed.Of(!imported && input.SentForSignatureAt is null ? LeaseNeedCodes.NotSent : LeaseNeedCodes.SignedCopyMissing)]);

        steps[7] = steps[6].State != LeaseStepState.Done
            ? new LeaseFlowStep(8, LeaseStepState.Locked, [])
            : input.Status == LeaseStatus.Registered
                ? new LeaseFlowStep(8, LeaseStepState.Done, [])
                : new LeaseFlowStep(8, LeaseStepState.Todo, [LeaseNeed.Of(LeaseNeedCodes.RegistrationMissing)]);

        return new LeaseDraftFlow(steps) { DataComplete = dataComplete };
    }

    public static bool IsSigned(LeaseStatus status) =>
        status is LeaseStatus.Signed or LeaseStatus.RegistrationPending or LeaseStatus.SentToProvider or LeaseStatus.Registered;

    private static void Property(LeaseDraftProgressInput input, bool imported, List<LeaseNeed> needs)
    {
        if (input.Values.PropertyId is null || !input.Facts.PropertyExists)
        {
            needs.Add(LeaseNeed.Of(LeaseNeedCodes.PropertyMissing));
            return;
        }

        if (imported)
            return;

        // The model of a contract made in CasaZen prints the code and the energy class of the APE ({{ape_estremi}}), and the reading of the
        // certificate does not leave them on the document: without them the final contract would answer 422 contract_data_missing after the
        // review was confirmed. An APE that is there but has neither is a need of its own, so that the interface points at where they are
        // read or typed (LR-APE).
        if (!input.Facts.HasApe)
            needs.Add(LeaseNeed.Of(LeaseNeedCodes.ApeMissing));
        else if (!input.Facts.HasApeIdentification)
            needs.Add(LeaseNeed.Of(LeaseNeedCodes.ApeIdentificationMissing));
        if (!input.Facts.HasCadastralData)
            needs.Add(LeaseNeed.Of(LeaseNeedCodes.CadastralMissing));
    }

    private static void Parties(LeaseDraftValues values, List<LeaseNeed> needs)
    {
        Role(values.Landlords, PartyRole.Landlord, LeaseNeedCodes.LandlordMissing, LeaseNeedCodes.LandlordIncomplete, needs);
        Role(values.Tenants, PartyRole.Tenant, LeaseNeedCodes.TenantMissing, LeaseNeedCodes.TenantIncomplete, needs);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = new List<object>();
        foreach (var (role, party, index) in Entries(values))
        {
            var code = ItalianFiscalCode.Normalize(party.FiscalCode);
            if (ItalianFiscalCode.IsValid(code) && !seen.Add(code))
                duplicates.Add(new Dictionary<string, object?> { ["role"] = role.ToString(), ["index"] = index });
        }

        if (duplicates.Count > 0)
            needs.Add(LeaseNeed.Of(LeaseNeedCodes.PartyFiscalCodeDuplicate, ("entries", duplicates)));
    }

    private static void Role(
        IReadOnlyList<LeaseDraftParty> entries, PartyRole role, string missingCode, string incompleteCode, List<LeaseNeed> needs)
    {
        if (entries.Count == 0)
        {
            needs.Add(LeaseNeed.Of(missingCode));
            return;
        }

        for (var index = 0; index < entries.Count; index++)
        {
            var problems = LeaseDraftRules.PartyProblems(entries[index]);
            if (problems.Count > 0)
                needs.Add(LeaseNeed.Of(incompleteCode, ("index", index), ("fields", problems)));
        }
    }

    internal static IEnumerable<(PartyRole Role, LeaseDraftParty Party, int Index)> Entries(LeaseDraftValues values)
    {
        for (var i = 0; i < values.Landlords.Count; i++)
            yield return (PartyRole.Landlord, values.Landlords[i], i);
        for (var i = 0; i < values.Tenants.Count; i++)
            yield return (PartyRole.Tenant, values.Tenants[i], i);
    }

    private static void Economics(LeaseDraftProgressInput input, bool imported, LeaseDraftReading reading, List<LeaseNeed> needs)
    {
        var values = input.Values;

        if (LeaseDraftRules.Clean(values.ContractType) is null)
        {
            needs.Add(LeaseNeed.Of(LeaseNeedCodes.ContractTypeMissing));
        }
        else if (!reading.TypeAvailable)
        {
            needs.Add(reading.Type is { } type
                ? LeaseNeed.Of(LeaseNeedCodes.LeaseTypeNotAvailable, ("type", type.ToString()))
                : LeaseNeed.Of(LeaseNeedCodes.LeaseTypeNotAvailable));
        }

        // A transitory contract made in CasaZen states the need that justifies its short term, who has it and what it is (D.M. 16/01/2017
        // art. 2 c. 4; art. 2 of the Allegato B, which the contract prints). One already signed prints nothing of its own (TR).
        if (reading is { Type: LeaseContractType.Transitorio, TypeAvailable: true } && !imported)
        {
            if (LeaseDraftRules.ParseNeedParty(values.TransitoryParty) is null)
                needs.Add(LeaseNeed.Of(LeaseNeedCodes.TransitoryPartyMissing));
            if (LeaseDraftRules.Clean(values.TransitoryNeed) is null)
                needs.Add(LeaseNeed.Of(LeaseNeedCodes.TransitoryNeedMissing));
        }

        if (reading.TaxRegime is null)
            needs.Add(LeaseNeed.Of(LeaseNeedCodes.TaxRegimeMissing));
        else if (reading.TaxRegime == LeaseTaxRegime.CedolareSecca && values.Tenants.Any(LeaseDraftRules.IsCompany))
            needs.Add(LeaseNeed.Of(LeaseNeedCodes.CedolareNotAllowedCompany));

        if (values.MonthlyRent is null)
            needs.Add(LeaseNeed.Of(LeaseNeedCodes.RentMissing));
        else if (reading.Rent is null)
            needs.Add(LeaseNeed.Of(LeaseNeedCodes.RentInvalid));

        if (reading.Type == LeaseContractType.Concordato && reading.TypeAvailable && !imported)
            Concordato(input, reading, needs);

        if (values.SecurityDeposit is null && !imported)
            needs.Add(LeaseNeed.Of(LeaseNeedCodes.DepositMissing));

        if (values.SecurityDeposit is not null)
        {
            if (reading.Deposit is null)
            {
                needs.Add(LeaseNeed.Of(LeaseNeedCodes.DepositInvalid));
            }
            else if (reading.Rent is { } rent && reading.Deposit > rent * LongRentRules.DepositMaxRents)
            {
                needs.Add(LeaseNeed.Of(
                    LeaseNeedCodes.DepositTooHigh,
                    ("maxAmount", rent * LongRentRules.DepositMaxRents),
                    ("maxRents", LongRentRules.DepositMaxRents)));
            }
        }
    }

    private static void Concordato(LeaseDraftProgressInput input, LeaseDraftReading reading, List<LeaseNeed> needs)
    {
        if (input.Values.Concordato is not { } characteristics)
        {
            needs.Add(LeaseNeed.Of(LeaseNeedCodes.ConcordatoDataMissing));
            return;
        }

        if (!LeaseDraftRules.IsConcordatoValid(characteristics))
        {
            needs.Add(LeaseNeed.Of(LeaseNeedCodes.ConcordatoDataInvalid));
            return;
        }

        // The range is computed by the server (the existing calculator); a comune without agreement data is blocked with its
        // reason (decision D28), a rent outside a verified range is a block, outside an indicative one a warning of the review.
        if (input.Concordato is not { } outcome)
            return;

        if (outcome.UnavailableReason is { } reason)
        {
            needs.Add(LeaseNeed.Of(LeaseNeedCodes.ConcordatoUnavailable, ("reason", reason)));
        }
        else if (reading.Rent is not null && outcome.RentOutOfBand && !outcome.Indicative)
        {
            needs.Add(LeaseNeed.Of(
                LeaseNeedCodes.RentOutOfBand, ("minMonthly", outcome.MinMonthly), ("maxMonthly", outcome.MaxMonthly)));
        }
    }

    private static void Start(LeaseDraftValues values, bool imported, LeaseDraftReading reading, List<LeaseNeed> needs)
    {
        if (LeaseDraftRules.Clean(values.StartDate) is null)
        {
            needs.Add(LeaseNeed.Of(LeaseNeedCodes.StartMissing));
        }
        else if (reading.Start is not { } start)
        {
            needs.Add(LeaseNeed.Of(LeaseNeedCodes.StartInvalid));
        }
        else if (imported || reading is { Type: LeaseContractType.Transitorio, TypeAvailable: true })
        {
            var before = needs.Count;
            Term(values, reading, start, needs);

            // A transitory contract of more than 30 days proves its need with documentation (art. 2 c. 4): asked here, with the term that
            // decides it, once the term is a good one. A contract signed elsewhere prints nothing of it.
            if (!imported && needs.Count == before && reading.End is { } end)
                Documentation(values, start, end, needs);
        }
    }

    private static void Documentation(LeaseDraftValues values, DateOnly start, DateOnly end, List<LeaseNeed> needs)
    {
        if (!LeaseContractTerms.TransitoryNeedsDocumentation(LeaseDraftRules.ToStoredDate(start), LeaseDraftRules.ToStoredDate(end)))
            return;

        if (LeaseDraftRules.Clean(values.TransitoryDocumentation) is not { } documentation)
            needs.Add(LeaseNeed.Of(LeaseNeedCodes.TransitoryDocumentationMissing));
        else if (documentation.Length > LeaseDraftRules.TransitoryDocumentationMaxLength)
            needs.Add(LeaseNeed.Of(LeaseNeedCodes.TransitoryDocumentationTooLong, ("maxLength", LeaseDraftRules.TransitoryDocumentationMaxLength)));
    }

    private static void Term(LeaseDraftValues values, LeaseDraftReading reading, DateOnly start, List<LeaseNeed> needs)
    {
        if (LeaseDraftRules.Clean(values.EndDate) is null)
        {
            if (reading.Type == LeaseContractType.Transitorio)
                needs.Add(LeaseNeed.Of(LeaseNeedCodes.EndMissing));
            return;
        }

        if (LeaseDraftRules.ParseDate(values.EndDate) is not { } end)
        {
            needs.Add(LeaseNeed.Of(LeaseNeedCodes.EndInvalid));
            return;
        }

        var type = reading.TypeAvailable ? reading.Type : null;
        if (LeaseContractTerms.CheckTerm(type, LeaseDraftRules.ToStoredDate(start), LeaseDraftRules.ToStoredDate(end)) is not { } problem)
            return;

        var parameters = new List<(string Key, object? Value)> { ("reason", problem.Code) };
        if (type is { } named)
            parameters.Add(("type", named.ToString()));
        if (problem.MinMonths is { } min)
            parameters.Add(("minMonths", min));
        if (problem.MaxMonths is { } max)
            parameters.Add(("maxMonths", max));
        needs.Add(LeaseNeed.Of(LeaseNeedCodes.TermOutOfRange, parameters.ToArray()));
    }

    private static void Attachments(LeaseDraftValues values, DateOnly today, List<LeaseNeed> needs)
    {
        foreach (var (role, party, index) in Entries(values))
        {
            if (LeaseDraftRules.HasInvalidDocumentsDate(party, today))
                needs.Add(LeaseNeed.Of(LeaseNeedCodes.PartyDocumentsDateInvalid, ("role", role.ToString()), ("index", index)));
        }
    }
}
