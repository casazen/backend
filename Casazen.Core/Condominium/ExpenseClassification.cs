using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Condominium;

/// <summary>
/// Default recoverability: ordinary is recoverable, extraordinary and unknown need review.
/// Nothing in review, and nothing with confidence under 0.85, becomes a tenant charge.
/// </summary>
public static class ExpenseClassification
{
    public const decimal ChargeConfidence = 0.85m;

    public sealed record Outcome(
        TenantRecoverability Recoverability,
        decimal? Percentage,
        decimal RecoverableAmount,
        ExpenseReviewStatus ReviewStatus,
        bool ChargesTenantAutomatically);

    public static Outcome Apply(
        CondominiumExpenseNature nature,
        decimal amount,
        TenantRecoverability? requested,
        decimal? percentage,
        ExpenseClassificationSource source,
        decimal? confidence)
    {
        var recoverability = requested ?? Default(nature);
        if (nature != CondominiumExpenseNature.Ordinary && source != ExpenseClassificationSource.Manual && requested is null)
            recoverability = TenantRecoverability.NeedsReview;

        if (recoverability == TenantRecoverability.NeedsReview)
        {
            return new Outcome(recoverability, null, 0m, ExpenseReviewStatus.Pending, false);
        }

        var share = recoverability switch
        {
            TenantRecoverability.OwnerOnly => 0m,
            TenantRecoverability.PartiallyRecoverable => percentage ?? throw new InvalidOperationException("percentage"),
            _ => percentage ?? 100m,
        };
        var recoverable = CondominiumMoney.Round(amount * share / 100m);
        var lowConfidence = confidence is < ChargeConfidence;
        var review = lowConfidence ? ExpenseReviewStatus.Pending : ExpenseReviewStatus.Accepted;
        var charges = review == ExpenseReviewStatus.Accepted
            && recoverability is TenantRecoverability.Recoverable or TenantRecoverability.PartiallyRecoverable
            && recoverable != 0m
            && confidence is null or >= ChargeConfidence;
        return new Outcome(recoverability, share, recoverable, review, charges);
    }

    public static TenantRecoverability Default(CondominiumExpenseNature nature) =>
        nature == CondominiumExpenseNature.Ordinary
            ? TenantRecoverability.Recoverable
            : TenantRecoverability.NeedsReview;

    /// <summary>
    /// A stored line becomes a tenant charge only when a person has accepted it, it is recoverable, and it is not an
    /// uncertain extraordinary. <see cref="TenantRecoverability.NeedsReview"/> and confidence under 0.85 never charge.
    /// </summary>
    public static bool ChargesStored(
        CondominiumExpenseNature nature,
        TenantRecoverability recoverability,
        ExpenseReviewStatus review,
        decimal recoverableAmount,
        decimal? confidence)
    {
        if (recoverability is TenantRecoverability.NeedsReview or TenantRecoverability.OwnerOnly)
            return false;
        if (review != ExpenseReviewStatus.Accepted)
            return false;
        if (nature == CondominiumExpenseNature.Extraordinary && review != ExpenseReviewStatus.Accepted)
            return false;
        if (confidence is < ChargeConfidence)
            return false;
        if (recoverability is not (TenantRecoverability.Recoverable or TenantRecoverability.PartiallyRecoverable))
            return false;
        return recoverableAmount != 0m;
    }
}
