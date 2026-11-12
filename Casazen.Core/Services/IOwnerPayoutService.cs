using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;

namespace Casazen.Core.Services;

/// <summary>
/// The payouts to the owners and the evidence of their withholding (E06/F05/US02/T03, NF-25, NF-110, ADR-018 amended): the withholding is decided only
/// when a payout to the beneficiary is confirmed, on the approved rule in force that day and the facts of the payout, never by a statement; a retry of
/// the same payout finds it; a reversal keeps the evidence. The annual dataset and the filing states of the F24 and of the dataset: generating a file is
/// never filing it.
/// </summary>
public interface IOwnerPayoutService
{
    /// <summary>
    /// Confirms a payout of an approved or published statement version once per <see cref="OwnerPayoutCommand.IdempotencyKey"/> (a retry answers the same
    /// payout and evidence, <see cref="OwnerPayoutResult.Replayed"/>), with its decision: applicable (base, rate, rounding, amount, tax code of the rule) or
    /// not applicable with its reason. 422 <c>tax_applicability_unresolved</c> (with the <c>field</c>) when a fact the rule needs is unknown, the
    /// <c>owner_fiscal_*</c> codes, <c>owner_payout_invalid</c>, <c>owner_statement_not_approved</c>; 409 <c>owner_payout_key_reused</c>,
    /// <c>owner_period_closed</c>.
    /// </summary>
    Task<OwnerPayoutResult> ConfirmPayoutAsync(Guid orgId, OwnerPayoutCommand command, string? userId, CancellationToken ct = default);

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

/// <summary>A payout to confirm, with the facts the withholding is decided on.</summary>
public sealed record OwnerPayoutCommand
{
    /// <summary>The caller's key: a retry with the same key is the same payout.</summary>
    public string? IdempotencyKey { get; init; }

    public Guid OwnerId { get; init; }

    public Guid StatementVersionId { get; init; }

    /// <summary>What the agency owed and settles, before the withholding.</summary>
    public decimal GrossAmount { get; init; }

    public string? Currency { get; init; }

    public DateOnly PaidOn { get; init; }

    public string? Reference { get; init; }

    /// <summary>Whether the agency collected the rent or intervened in its payment; <c>null</c> = not known.</summary>
    public bool? AgencyCollected { get; init; }

    /// <summary>Whether the stays are under a qualifying short-let contract; <c>null</c> = not known.</summary>
    public bool? QualifyingShortLet { get; init; }

    /// <summary>The gross components of the payout by code (<c>rent</c>, <c>cleaning</c>, <c>linen</c>...): the rule names the ones of the base.</summary>
    public IReadOnlyDictionary<string, decimal>? Components { get; init; }

    /// <summary>The bookings (stays) the payout covers.</summary>
    public IReadOnlyList<Guid>? StayIds { get; init; }
}

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

    private static DomainRuleException Unresolved(string field) =>
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
