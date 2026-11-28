using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Validation;

namespace Casazen.Core.Services;

/// <summary>
/// The payouts to the owners and the evidence of their withholding (E06/F05/US02/T03, NF-25, NF-110, ADR-018 amended): the withholding is decided only
/// when a payout to the beneficiary is confirmed, on the approved rule in force that day and the facts of the payout, never by a statement; a retry of
/// the same payout finds it; a reversal keeps the evidence. The annual dataset and the filing states of the F24 and of the dataset: generating a file is
/// never filing it.
/// </summary>
/// <remarks>
/// <b>The facts come from the ledger</b> (E18/F01/US01/T01, NF-117): the gross the payout settles, which stays the agency collected or handled and which of them
/// are qualifying short lets are read from the owner ledger of the version being paid (<see cref="PayoutFacts"/>), never typed by the caller. The request names
/// the version, the day and the reference; the facts are the evidence of the decision.
/// </remarks>
public interface IOwnerPayoutService
{
    /// <summary>
    /// Confirms a payout of an approved or published statement version once per <see cref="OwnerPayoutCommand.IdempotencyKey"/> (a retry answers the same
    /// payout and evidence, <see cref="OwnerPayoutResult.Replayed"/>), with its decision taken on the facts of the ledger: applicable (base, rate, rounding,
    /// amount, tax code of the rule) or not applicable with its reason. The gross is the balance of the version still unpaid, or the amount the command names,
    /// never more (422 <c>owner_payout_exceeds_balance</c>). 422 <c>tax_applicability_unresolved</c> (with the <c>field</c>) when a fact the rule needs is
    /// unknown, the <c>owner_fiscal_*</c> codes (a rule set that is not approved decides nothing), <c>owner_payout_invalid</c>, <c>owner_statement_not_approved</c>;
    /// 409 <c>owner_payout_key_reused</c>, <c>owner_period_closed</c>.
    /// </summary>
    Task<OwnerPayoutResult> ConfirmPayoutAsync(Guid orgId, OwnerPayoutCommand command, string? userId, CancellationToken ct = default);

    Task<OwnerPayoutPreview> PreviewPayoutAsync(Guid orgId, Guid statementVersionId, DateOnly paidOn, CancellationToken ct = default);

    Task<OwnerPayoutResult> ReversePayoutAsync(Guid orgId, Guid payoutId, string? reason, string? userId, CancellationToken ct = default);

    Task<OwnerPayoutResult> GetPayoutAsync(Guid orgId, Guid payoutId, CancellationToken ct = default);

    Task<IReadOnlyList<OwnerPayout>> ListPayoutsAsync(Guid orgId, Guid? ownerId, CancellationToken ct = default);

    Task<TaxReportingDataset> GenerateAnnualDatasetAsync(Guid orgId, int year, string? userId, CancellationToken ct = default);

    Task<TaxReportingDataset?> GetAnnualDatasetAsync(Guid orgId, int year, CancellationToken ct = default);

    /// <summary>
    /// Moves an F24 along its filing states (<see cref="TaxFiling"/>). 409 <c>tax_filing_transition_invalid</c>; 422 <c>tax_filing_reference_required</c>;
    /// 404 <c>f24_not_found</c>.
    /// </summary>
    Task<F24Record> SetF24FilingStateAsync(Guid orgId, Guid f24Id, TaxFilingCommand command, string? userId, CancellationToken ct = default);

    Task<TaxReportingDataset> SetDatasetFilingStateAsync(Guid orgId, Guid datasetId, TaxFilingCommand command, string? userId, CancellationToken ct = default);

    /// <summary>The obligations of each month of the year against their F24 and the year against the annual dataset.</summary>
    Task<TaxReconciliation> ReconcileAsync(Guid orgId, int year, CancellationToken ct = default);
}

/// <summary>A payout with its withholding evidence; <see cref="Replayed"/> when the call was a retry of a payout already confirmed.</summary>
public sealed record OwnerPayoutResult(OwnerPayout Payout, IReadOnlyList<TaxObligationEvidence> Evidence, bool Replayed);

public sealed record OwnerPayoutCommand
{
    /// <summary>The caller's key: a retry with the same key is the same payout.</summary>
    public string? IdempotencyKey { get; init; }

    public Guid OwnerId { get; init; }

    public Guid StatementVersionId { get; init; }

    public decimal? GrossAmount { get; init; }

    public string? Currency { get; init; }

    public DateOnly PaidOn { get; init; }

    public string? Reference { get; init; }
}

public sealed record OwnerPayoutPreview(
    PayoutFacts Facts,
    decimal Balance,
    string Currency,
    WithholdingDecision? Decision,
    decimal? WithholdingAmount,
    string? BlockingCode,
    string? BlockingField);

public sealed record TaxFilingCommand
{
    public TaxFilingState State { get; init; }

    public string? HandoffReference { get; init; }

    public string? ReceiptReference { get; init; }

    public string? Reason { get; init; }
}

/// <summary>The obligations of the year against the F24 of each month and the annual dataset.</summary>
public sealed record TaxReconciliation(
    int Year,
    decimal ObligationsTotal,
    decimal F24Total,
    decimal? AnnualTotal,
    IReadOnlyList<TaxPeriodReconciliation> Months)
{
    public bool Reconciled => Months.All(m => m.F24 == m.Obligations) && (AnnualTotal is null || AnnualTotal == ObligationsTotal);
}

public sealed record TaxPeriodReconciliation(int Month, decimal Obligations, decimal? F24, TaxFilingState? FilingState);

public static class OwnerPayoutErrors
{
    public const string TaxApplicabilityUnresolved = "tax_applicability_unresolved";
    public const string PayoutInvalid = "owner_payout_invalid";
    public const string PayoutKeyReused = "owner_payout_key_reused";
    public const string PayoutNotFound = "owner_payout_not_found";
    public const string PayoutReversed = "owner_payout_reversed";

    public const string PayoutExceedsBalance = "owner_payout_exceeds_balance";

    public const string NoObligations = "tax_no_obligations";
    public const string F24TaxCodeMixed = "f24_tax_code_mixed";
    public const string FileAlreadyFiled = "tax_file_already_filed";
    public const string FilingTransitionInvalid = "tax_filing_transition_invalid";
    public const string FilingReferenceRequired = "tax_filing_reference_required";
    public const string DatasetNotFound = "tax_dataset_not_found";
}

public static class TaxFiling
{
    public static bool CanMove(TaxFilingState from, TaxFilingState to) => (from, to) is
        (TaxFilingState.Generated, TaxFilingState.Reviewed)
        or (TaxFilingState.Reviewed, TaxFilingState.Submitted)
        or (TaxFilingState.Submitted, TaxFilingState.Accepted)
        or (TaxFilingState.Submitted, TaxFilingState.Rejected);
}

public static class OwnerWithholdingRules
{
    public const string Applicable = "applicable";
    public const string RuleWithoutWithholding = "rule_without_withholding";
    public const string NoCollectionOrIntervention = "no_collection_or_intervention";
    public const string NotQualifyingShortLet = "not_qualifying_short_let";

    public const string AlreadyWithheldByPortal = "already_withheld_by_portal";

    public const string NoStaysToDecide = "no_stays_to_decide";

    public static WithholdingDecision Decide(
        OwnerAccountingRuleParameters rule,
        bool? agencyCollected,
        bool? qualifyingShortLet,
        IReadOnlyDictionary<string, decimal>? components)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (!rule.WithholdingApplies)
            return WithholdingDecision.NotApplicable(RuleWithoutWithholding);

        if (rule.RequiresAgencyCollection)
        {
            if (agencyCollected is null)
                throw Unresolved("agencyCollected");
            if (agencyCollected == false)
                return WithholdingDecision.NotApplicable(NoCollectionOrIntervention);
        }

        if (rule.RequiresQualifyingShortLet)
        {
            if (qualifyingShortLet is null)
                throw Unresolved("qualifyingShortLet");
            if (qualifyingShortLet == false)
                return WithholdingDecision.NotApplicable(NotQualifyingShortLet);
        }

        if (rule.WithholdingRatePercent is not { } rate || rate <= 0m)
            throw Unresolved("withholdingRatePercent");
        if (string.IsNullOrWhiteSpace(rule.WithholdingTaxCode))
            throw Unresolved("withholdingTaxCode");
        var mode = rule.Rounding switch
        {
            "half_up" => MidpointRounding.AwayFromZero,
            "half_even" => MidpointRounding.ToEven,
            _ => throw Unresolved("rounding"),
        };
        if (rule.GrossBaseComponents.Count == 0)
            throw Unresolved("grossBaseComponents");

        var grossBase = 0m;
        foreach (var component in rule.GrossBaseComponents)
        {
            if (components is null || !components.TryGetValue(component, out var amount))
                throw Unresolved("components." + component);
            grossBase += amount;
        }

        var withheld = Math.Round(grossBase * rate / 100m, 2, mode);
        return new WithholdingDecision(TaxDecision.Applicable, Applicable, grossBase, rate, rule.Rounding, withheld, rule.WithholdingTaxCode);
    }

    internal static DomainRuleException Unresolved(string field) =>
        new(OwnerPayoutErrors.TaxApplicabilityUnresolved, "TaxApplicabilityUnresolved")
        {
            Extensions = new Dictionary<string, object?> { ["field"] = field },
        };
}

public sealed record WithholdingDecision(
    TaxDecision Decision,
    string Reason,
    decimal? GrossBase,
    decimal? RatePercent,
    string? Rounding,
    decimal? Amount,
    string? TaxCode)
{
    public static WithholdingDecision NotApplicable(string reason) => new(TaxDecision.NotApplicable, reason, null, null, null, null, null);
}

/// <summary>
/// The facts of a payout, read from the owner ledger of the version it pays (E18/F01/US01/T01, NF-117): stored as the JSON of its evidence
/// (<see cref="TaxObligationEvidence.FactsJson"/>) and shown read only. Nothing here is typed by a person. The amounts of a stay are the owner's share of it
/// as the ledger posted it, without the tourist tax; the base and the components are in <see cref="WithholdingCurrency"/> (the euro of the F24).
/// </summary>
public sealed record PayoutFacts
{
    public string Source { get; init; } = PayoutFactRules.FromLedger;

    public string Currency { get; init; } = string.Empty;

    public decimal GrossAmount { get; init; }

    public decimal Balance { get; init; }

    /// <summary>The currency of the withholding and of its base: the euro the F24 is paid in.</summary>
    public string WithholdingCurrency { get; init; } = StayCurrency.Euro;

    public bool? AgencyCollected { get; init; }

    public bool? QualifyingShortLet { get; init; }

    public IReadOnlyDictionary<string, decimal> Components { get; init; } = new Dictionary<string, decimal>();

    /// <summary>
    /// The stays the payout decided on, in the base or not: their revenue lines are <see cref="DecidedEntryIds"/>. A later payout never decides those lines again, only
    /// what was posted since (the rest of a stay paid in two months included), unless this payout is reversed.
    /// </summary>
    public IReadOnlyList<Guid> StayIds { get; init; } = [];

    /// <summary>
    /// The revenue lines of the ledger (<see cref="OwnerLedgerEntry"/> ids) the payout decided: those of the stays of <see cref="Stays"/>. While the payout is confirmed no
    /// other payout decides them again (never withheld twice); once it is reversed they are open again and the next payout decides them (never lost), whatever other
    /// payout of the owner was confirmed in between. <c>null</c> only in the facts of before this list (they decided the lines of their version, its month and the
    /// months before, posted up to its cut-off).
    /// </summary>
    public IReadOnlyList<Guid>? DecidedEntryIds { get; init; }

    public IReadOnlyList<PayoutStayFact> Stays { get; init; } = [];

    public IReadOnlyList<string> PortalsWithholding { get; init; } = [];

    public PayoutFx? Fx { get; init; }

    public OwnerAccountingRuleParameters? Parameters { get; init; }
}

public sealed record PayoutFx(string From, string To, decimal Rate, string? Source, DateOnly? Day);

public sealed record PayoutStayFact(
    Guid BookingId,
    Guid? PropertyId,
    string Channel,
    DateOnly CheckIn,
    DateOnly CheckOut,
    int Nights,
    decimal SharePercent,
    string GrossCurrency,
    decimal Gross,
    decimal TouristTax,
    string RevenuePath,
    bool Qualifying,
    string QualifyingReason,
    bool CompanyTenantDecisionPending,
    bool CollectedByAgency,
    string Collection,
    bool WithheldByPortal,
    string? PortalWithholding,
    bool InBase,
    decimal? BaseAmount,
    string Reason);

public sealed record PayoutStayInput(
    Guid BookingId,
    Guid? PropertyId,
    BookingSource Channel,
    bool Active,
    bool ItalianProperty,
    DateOnly CheckIn,
    DateOnly CheckOut,
    decimal SharePercent,
    string GrossCurrency,
    decimal Gross,
    decimal TouristTax,
    bool PostedFromPayments,
    bool PaidInCasaZen,
    bool? PortalCollects,
    PortalAccountHolder AccountHolder,
    bool WithheldOnPayment,
    bool OwnerPrivate,
    StayShortLetTreatment? Treatment);

public sealed record PayoutFactSettings(
    int MaxStayNights,
    IReadOnlySet<BookingSource> PortalsWithholding,
    bool RequiresAgencyCollection,
    bool RequiresQualifyingShortLet);

public static class PayoutStayReasons
{
    public const string PathPayment = "payment";
    public const string PathBooking = "booking";

    public const string StayNotActive = "stay_not_active";
    public const string PropertyNotItalian = "property_not_italian";
    public const string StayLengthOutOfRange = "stay_length_out_of_range";
    public const string OwnerNotPrivate = "owner_not_private";

    public const string CasaZenPayment = "casazen_payment";
    public const string RecordedPayment = "recorded_payment";
    public const string PortalPaysAgency = "portal_pays_agency";
    public const string PortalPaysOwner = "portal_pays_owner";
    public const string CollectedAtProperty = "collected_at_property";
    public const string PaymentNotRecorded = "payment_not_recorded";

    public const string WithheldOnPayment = "payment";
    public const string WithheldByPortalRule = "portal_rule";

    public const string InBase = "in_base";
}

public static class PayoutFactRules
{
    public const string FromLedger = "ledger";

    public const string StayGross = "stay_gross";

    private static readonly JsonSerializerOptions FactsSerializer = new(JsonSerializerDefaults.Web);

    public static PayoutFacts? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("source", out var source)
                || source.ValueKind != JsonValueKind.String
                || source.GetString() != FromLedger)
            {
                return null;
            }

            return document.RootElement.Deserialize<PayoutFacts>(FactsSerializer);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The facts of one stay. <b>Qualifying short let</b>: an active stay (the one definition, <c>AlloggiatiStatusRules</c>), in an Italian property, of 1 to
    /// <see cref="PayoutFactSettings.MaxStayNights"/> nights, of an owner who is a private natural person, not presumed a business and with no decision against the
    /// stay of a company tenant (E18/F01/US03). <b>Collected or handled by the agency</b>: a stay the ledger posted from its payments (a payment in CasaZen, or one the
    /// agency recorded), or a stay of a portal that collects and pays the agency, which holds the account (V2). A portal stay of the owner's account, one paid at
    /// the property and a stay whose payment nobody recorded are not. <b>Already withheld by the portal</b>: a payment of the stay carries the withholding of the
    /// portal, or the portal is one that withholds for an Italian private host (⚖ R5) and the stay qualifies. In the base: what the rule requires, and not withheld.
    /// </summary>
    public static PayoutStayFact EvaluateStay(PayoutStayInput stay, PayoutFactSettings settings)
    {
        ArgumentNullException.ThrowIfNull(stay);
        ArgumentNullException.ThrowIfNull(settings);

        var nights = stay.CheckOut.DayNumber - stay.CheckIn.DayNumber;
        var (qualifying, qualifyingReason) = QualifyingOf(stay, nights, settings.MaxStayNights);
        var (collected, collection) = CollectionOf(stay);
        var portal = FiscalCopy.IsOtaBookingSource(stay.Channel);
        string? withholding = stay.WithheldOnPayment
            ? PayoutStayReasons.WithheldOnPayment
            : portal && qualifying && settings.PortalsWithholding.Contains(stay.Channel) ? PayoutStayReasons.WithheldByPortalRule : null;

        var reason = withholding is not null ? OwnerWithholdingRules.AlreadyWithheldByPortal
            : settings.RequiresQualifyingShortLet && !qualifying ? OwnerWithholdingRules.NotQualifyingShortLet
            : settings.RequiresAgencyCollection && !collected ? OwnerWithholdingRules.NoCollectionOrIntervention
            : PayoutStayReasons.InBase;

        return new PayoutStayFact(
            stay.BookingId,
            stay.PropertyId,
            stay.Channel.ToString(),
            stay.CheckIn,
            stay.CheckOut,
            nights,
            stay.SharePercent,
            stay.GrossCurrency,
            stay.Gross,
            stay.TouristTax,
            stay.PostedFromPayments ? PayoutStayReasons.PathPayment : PayoutStayReasons.PathBooking,
            qualifying,
            qualifyingReason,
            stay.Treatment?.DecisionPending == true,
            collected,
            collection,
            withholding is not null,
            withholding,
            reason == PayoutStayReasons.InBase,
            null,
            reason);
    }

    public static WithholdingDecision Decide(OwnerAccountingRuleParameters rule, IReadOnlyList<PayoutStayFact> stays)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(stays);

        if (!rule.WithholdingApplies)
            return OwnerWithholdingRules.Decide(rule, null, null, null);
        if (stays.Count == 0)
            return WithholdingDecision.NotApplicable(OwnerWithholdingRules.NoStaysToDecide);

        var open = stays.Where(s => !s.WithheldByPortal).ToList();
        if (open.Count == 0)
            return WithholdingDecision.NotApplicable(OwnerWithholdingRules.AlreadyWithheldByPortal);

        var inBase = open.Where(s => s.InBase).ToList();
        if (inBase.Count > 0)
        {
            var components = new Dictionary<string, decimal>(StringComparer.Ordinal) { [StayGross] = inBase.Sum(s => s.BaseAmount ?? 0m) };
            return OwnerWithholdingRules.Decide(rule, agencyCollected: true, qualifyingShortLet: true, components);
        }

        var collected = open.Where(s => s.CollectedByAgency).ToList();
        return OwnerWithholdingRules.Decide(
            rule,
            agencyCollected: collected.Count > 0,
            qualifyingShortLet: (rule.RequiresAgencyCollection ? collected : open).Any(s => s.Qualifying),
            new Dictionary<string, decimal>(StringComparer.Ordinal) { [StayGross] = 0m });
    }

    public static IReadOnlySet<BookingSource> PortalsOf(IEnumerable<string>? names)
    {
        var portals = new HashSet<BookingSource>();
        foreach (var name in names ?? [])
        {
            if (EnumNames.TryParseDefined<BookingSource>(name, out var channel) && FiscalCopy.IsOtaBookingSource(channel))
                portals.Add(channel);
        }

        return portals;
    }

    private static (bool Qualifying, string Reason) QualifyingOf(PayoutStayInput stay, int nights, int maxStayNights)
    {
        if (!stay.Active)
            return (false, PayoutStayReasons.StayNotActive);
        if (!stay.ItalianProperty)
            return (false, PayoutStayReasons.PropertyNotItalian);
        if (nights < 1 || nights > maxStayNights)
            return (false, PayoutStayReasons.StayLengthOutOfRange);
        if (!stay.OwnerPrivate)
            return (false, PayoutStayReasons.OwnerNotPrivate);
        if (stay.Treatment is not { } treatment)
            throw OwnerWithholdingRules.Unresolved("qualifyingShortLet");

        return (treatment.QualifiesAsShortLet, treatment.Reason);
    }

    private static (bool Collected, string Collection) CollectionOf(PayoutStayInput stay)
    {
        if (stay.PostedFromPayments)
            return (true, stay.PaidInCasaZen ? PayoutStayReasons.CasaZenPayment : PayoutStayReasons.RecordedPayment);
        if (!FiscalCopy.IsOtaBookingSource(stay.Channel))
            return (false, PayoutStayReasons.PaymentNotRecorded);

        if (stay.PortalCollects == false)
            return (false, PayoutStayReasons.CollectedAtProperty);
        return stay.AccountHolder == PortalAccountHolder.Agency
            ? (true, PayoutStayReasons.PortalPaysAgency)
            : (false, PayoutStayReasons.PortalPaysOwner);
    }
}
