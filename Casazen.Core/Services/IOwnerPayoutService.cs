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

    /// <summary>
    /// What a payout of the version paid on <paramref name="paidOn"/> would be, read only (the dialog shows it before the confirmation): the facts of the ledger,
    /// the balance still unpaid and the decision of the approved rule in force that day, or the code that blocks the confirmation. 404
    /// <c>owner_statement_version_not_found</c>; 422 <c>owner_payout_invalid</c> (<c>paidOn</c>), <c>owner_statement_not_approved</c>.
    /// </summary>
    Task<OwnerPayoutPreview> PreviewPayoutAsync(Guid orgId, Guid statementVersionId, DateOnly paidOn, CancellationToken ct = default);

    /// <summary>Reverses a confirmed payout with its reason: the evidence and the ledger lines get their reversal rows. 409 <c>owner_payout_reversed</c>.</summary>
    Task<OwnerPayoutResult> ReversePayoutAsync(Guid orgId, Guid payoutId, string? reason, string? userId, CancellationToken ct = default);

    /// <summary>A payout with its evidence; 404 <c>owner_payout_not_found</c>.</summary>
    Task<OwnerPayoutResult> GetPayoutAsync(Guid orgId, Guid payoutId, CancellationToken ct = default);

    /// <summary>The payouts of the org, or of one owner, the newest first.</summary>
    Task<IReadOnlyList<OwnerPayout>> ListPayoutsAsync(Guid orgId, Guid? ownerId, CancellationToken ct = default);

    /// <summary>
    /// The annual Certificazione Unica data of the year from the applicable evidence, per owner; regenerated while it is only generated. 422
    /// <c>tax_no_obligations</c>; 409 <c>tax_file_already_filed</c>.
    /// </summary>
    Task<TaxReportingDataset> GenerateAnnualDatasetAsync(Guid orgId, int year, string? userId, CancellationToken ct = default);

    /// <summary>The annual dataset of the year, or <c>null</c>.</summary>
    Task<TaxReportingDataset?> GetAnnualDatasetAsync(Guid orgId, int year, CancellationToken ct = default);

    /// <summary>
    /// Moves an F24 along its filing states (<see cref="TaxFiling"/>). 409 <c>tax_filing_transition_invalid</c>; 422 <c>tax_filing_reference_required</c>;
    /// 404 <c>f24_not_found</c>.
    /// </summary>
    Task<F24Record> SetF24FilingStateAsync(Guid orgId, Guid f24Id, TaxFilingCommand command, string? userId, CancellationToken ct = default);

    /// <summary>Moves an annual dataset along its filing states, as <see cref="SetF24FilingStateAsync"/>. 404 <c>tax_dataset_not_found</c>.</summary>
    Task<TaxReportingDataset> SetDatasetFilingStateAsync(Guid orgId, Guid datasetId, TaxFilingCommand command, string? userId, CancellationToken ct = default);

    /// <summary>The obligations of each month of the year against their F24 and the year against the annual dataset.</summary>
    Task<TaxReconciliation> ReconcileAsync(Guid orgId, int year, CancellationToken ct = default);
}

/// <summary>A payout with its withholding evidence; <see cref="Replayed"/> when the call was a retry of a payout already confirmed.</summary>
public sealed record OwnerPayoutResult(OwnerPayout Payout, IReadOnlyList<TaxObligationEvidence> Evidence, bool Replayed);

/// <summary>
/// A payout to confirm: the version it pays, the day, the reference and the caller's key. No fact of the withholding is here (E18/F01/US01/T01): the gross, the
/// collection and the qualifying short lets are read from the ledger (<see cref="PayoutFacts"/>).
/// </summary>
public sealed record OwnerPayoutCommand
{
    /// <summary>The caller's key: a retry with the same key is the same payout.</summary>
    public string? IdempotencyKey { get; init; }

    public Guid OwnerId { get; init; }

    public Guid StatementVersionId { get; init; }

    /// <summary>
    /// What the agency pays now, before the withholding: <c>null</c> (the dialog) for the balance of the version still unpaid; an amount (a part of it, the
    /// working capital the agency keeps) never more than that balance (422 <c>owner_payout_exceeds_balance</c>).
    /// </summary>
    public decimal? GrossAmount { get; init; }

    /// <summary>The currency of the version, when the caller names it (another one is 422 <c>owner_payout_invalid</c>).</summary>
    public string? Currency { get; init; }

    public DateOnly PaidOn { get; init; }

    public string? Reference { get; init; }
}

/// <summary>
/// What a payout of a version would be (<see cref="IOwnerPayoutService.PreviewPayoutAsync"/>): the facts of the ledger, the balance still unpaid in the currency
/// of the version, and the decision of the approved rule with the withholding in the currency of the payout; or, when the confirmation would be refused, the
/// code (and the field) that refuses it and no decision.
/// </summary>
public sealed record OwnerPayoutPreview(
    PayoutFacts Facts,
    decimal Balance,
    string Currency,
    WithholdingDecision? Decision,
    decimal? WithholdingAmount,
    string? BlockingCode,
    string? BlockingField);

/// <summary>A move of a tax file along its filing states, with the reference the move needs.</summary>
public sealed record TaxFilingCommand
{
    public TaxFilingState State { get; init; }

    /// <summary>For <see cref="TaxFilingState.Submitted"/>: the controlled manual handoff (who sent it and how).</summary>
    public string? HandoffReference { get; init; }

    /// <summary>For <see cref="TaxFilingState.Accepted"/>: the receipt of the authority.</summary>
    public string? ReceiptReference { get; init; }

    /// <summary>For <see cref="TaxFilingState.Rejected"/>: why.</summary>
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

/// <summary>The obligations of a month against its F24 (<c>null</c> while there is none).</summary>
public sealed record TaxPeriodReconciliation(int Month, decimal Obligations, decimal? F24, TaxFilingState? FilingState);

/// <summary>The stable codes of the payouts and of the tax files.</summary>
public static class OwnerPayoutErrors
{
    public const string TaxApplicabilityUnresolved = "tax_applicability_unresolved";
    public const string PayoutInvalid = "owner_payout_invalid";
    public const string PayoutKeyReused = "owner_payout_key_reused";
    public const string PayoutNotFound = "owner_payout_not_found";
    public const string PayoutReversed = "owner_payout_reversed";

    /// <summary>422 (with the <c>balance</c> still unpaid and the <c>currency</c>): a payout never pays more than the version still owes (E18/F01/US01/T01).</summary>
    public const string PayoutExceedsBalance = "owner_payout_exceeds_balance";

    public const string NoObligations = "tax_no_obligations";
    public const string F24TaxCodeMixed = "f24_tax_code_mixed";
    public const string FileAlreadyFiled = "tax_file_already_filed";
    public const string FilingTransitionInvalid = "tax_filing_transition_invalid";
    public const string FilingReferenceRequired = "tax_filing_reference_required";
    public const string DatasetNotFound = "tax_dataset_not_found";
}

/// <summary>The moves of a tax file: generated, reviewed, submitted (by a controlled manual handoff), accepted or rejected.</summary>
public static class TaxFiling
{
    public static bool CanMove(TaxFilingState from, TaxFilingState to) => (from, to) is
        (TaxFilingState.Generated, TaxFilingState.Reviewed)
        or (TaxFilingState.Reviewed, TaxFilingState.Submitted)
        or (TaxFilingState.Submitted, TaxFilingState.Accepted)
        or (TaxFilingState.Submitted, TaxFilingState.Rejected);
}

/// <summary>
/// The withholding decision of a payout from the parameters of the approved rule and the facts of the payout (E06/F05/US02/T03): nothing is guessed;
/// a fact the rule needs that is unknown is 422 <c>tax_applicability_unresolved</c> with the <c>field</c>.
/// </summary>
public static class OwnerWithholdingRules
{
    public const string Applicable = "applicable";
    public const string RuleWithoutWithholding = "rule_without_withholding";
    public const string NoCollectionOrIntervention = "no_collection_or_intervention";
    public const string NotQualifyingShortLet = "not_qualifying_short_let";

    /// <summary>Every stay of the payout was withheld by its portal (⚖ R5): the agency withholds nothing on it again (E18/F01/US01/T01, AC-NF117-1).</summary>
    public const string AlreadyWithheldByPortal = "already_withheld_by_portal";

    /// <summary>The payout pays no stay an earlier payout did not decide on (the balance is charges or corrections, or a second payout of the same version).</summary>
    public const string NoStaysToDecide = "no_stays_to_decide";

    /// <summary>The decision on the facts: the rule without withholding, then the collection, the contract, the base, the rate, the rounding and the code.</summary>
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

/// <summary>A withholding decision: applicable with its base, rate, rounding, amount and code, or not applicable with its reason and no amount.</summary>
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
    /// <summary>Where the facts come from: always <see cref="PayoutFactRules.FromLedger"/> (the evidence of before E18/F01/US01 has the facts typed then).</summary>
    public string Source { get; init; } = PayoutFactRules.FromLedger;

    /// <summary>The currency of the version and of the payout.</summary>
    public string Currency { get; init; } = string.Empty;

    /// <summary>What the payout settles, before the withholding, in <see cref="Currency"/> (the balance still unpaid, or the amount asked; 0 in a preview).</summary>
    public decimal GrossAmount { get; init; }

    /// <summary>The balance of the version still unpaid when the facts were read, in <see cref="Currency"/>.</summary>
    public decimal Balance { get; init; }

    /// <summary>The currency of the withholding and of its base: the euro the F24 is paid in.</summary>
    public string WithholdingCurrency { get; init; } = StayCurrency.Euro;

    /// <summary>At least one stay of the payout was collected or handled by the agency; <c>null</c> without stays.</summary>
    public bool? AgencyCollected { get; init; }

    /// <summary>At least one stay of the payout is a qualifying short let; <c>null</c> without stays.</summary>
    public bool? QualifyingShortLet { get; init; }

    /// <summary>The components of the base (<see cref="PayoutFactRules.StayGross"/>: the gross of the stays in the base), in <see cref="WithholdingCurrency"/>.</summary>
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

    /// <summary>Each stay with its facts and why it is or is not in the base.</summary>
    public IReadOnlyList<PayoutStayFact> Stays { get; init; } = [];

    /// <summary>The portals taken to withhold for an Italian private host themselves (⚖ R5): the rule's, else the configured default.</summary>
    public IReadOnlyList<string> PortalsWithholding { get; init; } = [];

    /// <summary>
    /// The agency's rate of the payout day from <see cref="WithholdingCurrency"/> to <see cref="Currency"/> when they differ (⚖ R6: the withholding decided in euro is
    /// kept from the payout at it); <c>null</c> for a payout in euro, or when the agency has no rate of the day (a withholding then cannot be kept: 422).
    /// </summary>
    public PayoutFx? Fx { get; init; }

    /// <summary>The parameters of the approved rule the decision was taken on (none in a preview that is blocked).</summary>
    public OwnerAccountingRuleParameters? Parameters { get; init; }
}

/// <summary>The rate of a payout day (an <see cref="OwnerFxRate"/> of the agency): one unit of <paramref name="From"/> is <paramref name="Rate"/> units of <paramref name="To"/>.</summary>
public sealed record PayoutFx(string From, string To, decimal Rate, string? Source, DateOnly? Day);

/// <summary>
/// One stay of a payout with the facts its withholding is decided on (E18/F01/US01/T01): the owner's gross of it without the tourist tax (the ledger lines), the
/// tourist tax left out, how the ledger posted it, whether it is a qualifying short let, whether the agency collected or handled it, whether its portal already
/// withheld, and whether it is in the base. The codes are those of <see cref="PayoutStayReasons"/>.
/// </summary>
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

/// <summary>What the rules of <see cref="PayoutFactRules.EvaluateStay"/> read of one stay, as the ledger, the booking and the checks of the owner give it.</summary>
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

/// <summary>The settings the stays are judged with: the longest short let, the portals that withhold themselves (⚖ R5) and what the rule requires.</summary>
public sealed record PayoutFactSettings(
    int MaxStayNights,
    IReadOnlySet<BookingSource> PortalsWithholding,
    bool RequiresAgencyCollection,
    bool RequiresQualifyingShortLet);

/// <summary>The codes of the facts of a stay of a payout (cross-repo contract): never rename one.</summary>
public static class PayoutStayReasons
{
    // How the ledger posted the revenue of the stay.
    public const string PathPayment = "payment";
    public const string PathBooking = "booking";

    // Why a stay is or is not a qualifying short let (with the codes of ShortLetTreatmentReasons).
    public const string StayNotActive = "stay_not_active";
    public const string PropertyNotItalian = "property_not_italian";
    public const string StayLengthOutOfRange = "stay_length_out_of_range";
    public const string OwnerNotPrivate = "owner_not_private";

    // Who collected the money of the stay.
    public const string CasaZenPayment = "casazen_payment";
    public const string RecordedPayment = "recorded_payment";
    public const string PortalPaysAgency = "portal_pays_agency";
    public const string PortalPaysOwner = "portal_pays_owner";
    public const string CollectedAtProperty = "collected_at_property";
    public const string PaymentNotRecorded = "payment_not_recorded";

    // Why the portal is taken to have withheld already.
    public const string WithheldOnPayment = "payment";
    public const string WithheldByPortalRule = "portal_rule";

    // Where the stay ends up.
    public const string InBase = "in_base";
}

/// <summary>
/// The rules of the facts of a payout (E18/F01/US01/T01, NF-117, ⚖ R5), pure: what one stay is (<see cref="EvaluateStay"/>) and the decision of the rule on the
/// stays of a payout (<see cref="Decide"/>, through <see cref="OwnerWithholdingRules.Decide"/>). The service, the preview and the tests apply the same definition.
/// </summary>
public static class PayoutFactRules
{
    /// <summary>The <see cref="PayoutFacts.Source"/> of the facts read from the ledger.</summary>
    public const string FromLedger = "ledger";

    /// <summary>The component of the base: the gross of the stays in the base without the tourist tax (room, cleaning and extras, as the ledger posts a stay).</summary>
    public const string StayGross = "stay_gross";

    private static readonly JsonSerializerOptions FactsSerializer = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The facts of the ledger a decision was taken on (<see cref="TaxObligationEvidence.FactsJson"/>); <c>null</c> for the facts typed before E18/F01/US01 (no
    /// <c>source</c>) or a JSON that cannot be read. The API and the payouts read them the same way.
    /// </summary>
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

    /// <summary>
    /// The decision of <paramref name="rule"/> on the stays of a payout, whose base amounts (<see cref="PayoutStayFact.BaseAmount"/>, in the currency of the
    /// withholding) are set for the stays in the base. A rule without withholding decides first; then no stay to decide, every stay withheld by its portal; then
    /// the stays in the base make the component <see cref="StayGross"/> of <see cref="OwnerWithholdingRules.Decide"/>, or, with none, the collection and the
    /// contract of the stays not withheld say why nothing is withheld.
    /// </summary>
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

        // Nothing in the base: the rule says why, from the stays that were not withheld (no collection first, then no qualifying contract among the collected).
        var collected = open.Where(s => s.CollectedByAgency).ToList();
        return OwnerWithholdingRules.Decide(
            rule,
            agencyCollected: collected.Count > 0,
            qualifyingShortLet: (rule.RequiresAgencyCollection ? collected : open).Any(s => s.Qualifying),
            new Dictionary<string, decimal>(StringComparer.Ordinal) { [StayGross] = 0m });
    }

    /// <summary>The portals among <paramref name="names"/> (the names of <see cref="BookingSource"/>, any case); a name that is no portal is left out.</summary>
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

        // A portal that does not collect (the guest pays at the property) pays nobody; one that collects pays the holder of the account (unknown counts as the
        // portal collecting, as the reconciliation of the month reads it).
        if (stay.PortalCollects == false)
            return (false, PayoutStayReasons.CollectedAtProperty);
        return stay.AccountHolder == PortalAccountHolder.Agency
            ? (true, PayoutStayReasons.PortalPaysAgency)
            : (false, PayoutStayReasons.PortalPaysOwner);
    }
}
