using Casazen.Core.Entities.Enums;
using Casazen.Core.Regulatory;

namespace Casazen.Core.Leases;

/// <summary>The state of a step of the wizard, decided by the server (LR-12).</summary>
public enum LeaseStepState
{
    /// <summary>Nothing is missing and the landlord has been through the step (a step from 6 to 8 follows the facts: review confirmed, signed, registered).</summary>
    Done,

    /// <summary>There is something to do in the step, or the landlord has not been through it yet.</summary>
    Todo,

    /// <summary>
    /// The step waits for somebody else (the tenant filling in their own data). It exists in the type for the day the tenant is
    /// invited (phase 2, decision D1); the first release never produces it.
    /// </summary>
    Waiting,

    /// <summary>The step cannot be done yet: a step before it is not complete.</summary>
    Locked,

    /// <summary>The step does not apply to this contract (an imported one has no model and no review).</summary>
    NotApplicable,
}

/// <summary>One thing missing in a step, or one thing to know about it: a stable <see cref="Code"/> (<see cref="LeaseNeedCodes"/>) and its parameters.</summary>
public sealed record LeaseNeed(string Code, IReadOnlyDictionary<string, object?>? Params = null)
{
    /// <summary>A need with the given parameters (an empty set is not sent).</summary>
    public static LeaseNeed Of(string code, params (string Key, object? Value)[] parameters) =>
        new(code, parameters.Length == 0 ? null : parameters.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));
}

/// <summary>A step of the wizard (1 to 8) with its state and what is missing.</summary>
public sealed record LeaseFlowStep(int Step, LeaseStepState State, IReadOnlyList<LeaseNeed> Needs);

/// <summary>
/// The eight steps of the wizard as the server sees them (<c>flow</c> of the draft endpoints): Immobile, Parti, Condizioni
/// economiche, Durata e date, Modello e allegati, Revisione, Firma, Dopo la firma.
/// </summary>
public sealed record LeaseDraftFlow(IReadOnlyList<LeaseFlowStep> Steps)
{
    /// <summary>
    /// Every value of the data steps is there and valid: only the facts about the property (the APE, the cadastral data) can still
    /// be missing. Then the columns and the parties of the lease hold the values and <c>DraftData</c> is not kept.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool DataComplete { get; init; }

    public LeaseFlowStep this[int step] => Steps[step - 1];
}

/// <summary>The facts about the property that the steps depend on (not values of the draft).</summary>
public sealed record LeaseDraftFacts
{
    /// <summary>The property exists and the caller reaches it.</summary>
    public bool PropertyExists { get; init; } = true;

    /// <summary>The property has an energy certificate (APE) that is valid.</summary>
    public bool HasApe { get; init; }

    /// <summary>Sheet, parcel, category and income of the property are known (the subaltern is optional).</summary>
    public bool HasCadastralData { get; init; }

    /// <summary>The property is let for short stays (decision LR-D15: a warning in the review, not a block).</summary>
    public bool PropertyIsShortMode { get; init; }
}

/// <summary>What the server found out about the rent band of a canone concordato for the characteristics typed.</summary>
public sealed record LeaseDraftConcordatoOutcome
{
    /// <summary>Why there is no range (<see cref="CanoneConcordatoReasonCodes"/>): no data for the comune, a zone that is missing, a surface out of the bands…; <c>null</c> when there is one.</summary>
    public string? UnavailableReason { get; init; }

    /// <summary>The rent typed is outside the range.</summary>
    public bool RentOutOfBand { get; init; }

    /// <summary>The range comes from agreement data that are not confirmed: it is a guide, it never blocks (A7-23).</summary>
    public bool Indicative { get; init; }

    public decimal? MinMonthly { get; init; }

    public decimal? MaxMonthly { get; init; }
}

/// <summary>Everything the flow of a lease depends on. No entity, no database: the service gathers it.</summary>
public sealed record LeaseDraftProgressInput
{
    public LeaseOrigin Origin { get; init; } = LeaseOrigin.Created;

    public LeaseStatus Status { get; init; } = LeaseStatus.Draft;

    public int WizardMaxStep { get; init; }

    public DateTime? ReviewConfirmedAt { get; init; }

    public DateTime? SentForSignatureAt { get; init; }

    public required LeaseDraftValues Values { get; init; }

    public LeaseDraftFacts Facts { get; init; } = new();

    /// <summary>The outcome for the characteristics typed; <c>null</c> when the type is not a concordato or the characteristics are not valid.</summary>
    public LeaseDraftConcordatoOutcome? Concordato { get; init; }

    /// <summary>Today in Rome (the verification of a document cannot be after it).</summary>
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
    /// <summary>The needs that are about the property and not about the values: they do not keep <c>DraftData</c> alive.</summary>
    private static readonly HashSet<string> PropertyFacts = [LeaseNeedCodes.ApeMissing, LeaseNeedCodes.CadastralMissing];

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

        // A draft that left the wizard (signed, registered) was visited all the way; before that, the furthest step reached.
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

        // 6 · Revisione
        steps[5] = imported
            ? new LeaseFlowStep(6, LeaseStepState.NotApplicable, [])
            : !dataStepsComplete
                ? new LeaseFlowStep(6, LeaseStepState.Locked, [])
                : reviewDone
                    ? new LeaseFlowStep(6, LeaseStepState.Done, [])
                    : new LeaseFlowStep(6, LeaseStepState.Todo, [LeaseNeed.Of(LeaseNeedCodes.ReviewNotConfirmed)]);

        // 7 · Firma (an imported contract: the signed contract)
        var signatureOpen = imported ? dataStepsComplete : steps[5].State == LeaseStepState.Done;
        steps[6] = !signatureOpen
            ? new LeaseFlowStep(7, LeaseStepState.Locked, [])
            : signed
                ? new LeaseFlowStep(7, LeaseStepState.Done, [])
                : new LeaseFlowStep(
                    7,
                    LeaseStepState.Todo,
                    [LeaseNeed.Of(!imported && input.SentForSignatureAt is null ? LeaseNeedCodes.NotSent : LeaseNeedCodes.SignedCopyMissing)]);

        // 8 · Dopo la firma
        steps[7] = steps[6].State != LeaseStepState.Done
            ? new LeaseFlowStep(8, LeaseStepState.Locked, [])
            : input.Status == LeaseStatus.Registered
                ? new LeaseFlowStep(8, LeaseStepState.Done, [])
                : new LeaseFlowStep(8, LeaseStepState.Todo, [LeaseNeed.Of(LeaseNeedCodes.RegistrationMissing)]);

        return new LeaseDraftFlow(steps) { DataComplete = dataComplete };
    }

    /// <summary>The contract is signed by every party (a status past the signature, except a rejected one).</summary>
    public static bool IsSigned(LeaseStatus status) =>
        status is LeaseStatus.Signed or LeaseStatus.RegistrationPending or LeaseStatus.SentToProvider or LeaseStatus.Registered;

    // ---- 1 · the property ----------------------------------------------------------------------------------------

    private static void Property(LeaseDraftProgressInput input, bool imported, List<LeaseNeed> needs)
    {
        if (input.Values.PropertyId is null || !input.Facts.PropertyExists)
        {
            needs.Add(LeaseNeed.Of(LeaseNeedCodes.PropertyMissing));
            return;
        }

        // An imported contract is already signed: the missing APE and cadastral data are warnings of the review (decision D2).
        if (imported)
            return;

        if (!input.Facts.HasApe)
            needs.Add(LeaseNeed.Of(LeaseNeedCodes.ApeMissing));
        if (!input.Facts.HasCadastralData)
            needs.Add(LeaseNeed.Of(LeaseNeedCodes.CadastralMissing));
    }

    // ---- 2 · the parties -----------------------------------------------------------------------------------------

    private static void Parties(LeaseDraftValues values, List<LeaseNeed> needs)
    {
        Role(values.Landlords, PartyRole.Landlord, LeaseNeedCodes.LandlordMissing, LeaseNeedCodes.LandlordIncomplete, needs);
        Role(values.Tenants, PartyRole.Tenant, LeaseNeedCodes.TenantMissing, LeaseNeedCodes.TenantIncomplete, needs);

        // No fiscal code twice in the same contract (LT-14): the later entries are the ones to change.
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

    /// <summary>Every entry with its role and its position among the entries of that role.</summary>
    internal static IEnumerable<(PartyRole Role, LeaseDraftParty Party, int Index)> Entries(LeaseDraftValues values)
    {
        for (var i = 0; i < values.Landlords.Count; i++)
            yield return (PartyRole.Landlord, values.Landlords[i], i);
        for (var i = 0; i < values.Tenants.Count; i++)
            yield return (PartyRole.Tenant, values.Tenants[i], i);
    }

    // ---- 3 · type, regime, rent, deposit -------------------------------------------------------------------------

    private static void Economics(LeaseDraftProgressInput input, bool imported, LeaseDraftReading reading, List<LeaseNeed> needs)
    {
        var values = input.Values;

        if (LeaseDraftRules.Clean(values.ContractType) is null)
        {
            needs.Add(LeaseNeed.Of(LeaseNeedCodes.ContractTypeMissing));
        }
        else if (!reading.TypeAvailable)
        {
            // The type exists (the transitory lease) or names nothing: kept as typed, never applied to the contract (decision D27).
            needs.Add(reading.Type is { } type
                ? LeaseNeed.Of(LeaseNeedCodes.LeaseTypeNotAvailable, ("type", type.ToString()))
                : LeaseNeed.Of(LeaseNeedCodes.LeaseTypeNotAvailable));
        }

        if (reading.TaxRegime is null)
            needs.Add(LeaseNeed.Of(LeaseNeedCodes.TaxRegimeMissing));
        else if (reading.TaxRegime == LeaseTaxRegime.CedolareSecca && values.Tenants.Any(LeaseDraftRules.IsCompany))
            needs.Add(LeaseNeed.Of(LeaseNeedCodes.CedolareNotAllowedCompany));

        if (values.MonthlyRent is null)
            needs.Add(LeaseNeed.Of(LeaseNeedCodes.RentMissing));
        else if (reading.Rent is null)
            needs.Add(LeaseNeed.Of(LeaseNeedCodes.RentInvalid));

        // The range of a canone concordato is checked for a contract that is going to be signed. An imported one is already signed
        // (decision LR-D13): the unit's characteristics, the agreement data of the comune and the range do not come into it.
        if (reading.Type == LeaseContractType.Concordato && reading.TypeAvailable && !imported)
            Concordato(input, reading, needs);

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

    // ---- 4 · the start date --------------------------------------------------------------------------------------

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
        else if (imported)
        {
            Term(values, reading, start, needs);
        }
    }

    /// <summary>
    /// The term of an imported contract is the one it states (LR-13): the last day typed, inside the limits of its type
    /// (<see cref="LeaseContractTerms.CheckTerm"/>). Without one it follows the type, except for the transitory type, which has no term
    /// of its own. A start in the past is no problem: the contract is already in force.
    /// </summary>
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

    // ---- 5 · model and attachments -------------------------------------------------------------------------------

    private static void Attachments(LeaseDraftValues values, DateOnly today, List<LeaseNeed> needs)
    {
        // Nothing is required at step 5: the template is chosen by the type and the regime, the APE is a fact of the property, and
        // the documents of the parties are not kept (decision D3): only the day they were checked, which is optional but, if
        // typed, a real day not after today.
        foreach (var (role, party, index) in Entries(values))
        {
            if (LeaseDraftRules.HasInvalidDocumentsDate(party, today))
                needs.Add(LeaseNeed.Of(LeaseNeedCodes.PartyDocumentsDateInvalid, ("role", role.ToString()), ("index", index)));
        }
    }
}
