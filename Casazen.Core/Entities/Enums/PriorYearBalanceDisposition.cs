namespace Casazen.Core.Entities.Enums;

/// <summary>
/// How the owner settles last year's balance (CondoLedger «Come si salda il conguaglio?»).
/// Stored as an integer: never renumber. The choice is explicit; ordinary and extraordinary installments are not rewritten.
/// </summary>
public enum PriorYearBalanceDisposition
{
    /// <summary>Together with the next installment (CondoLedger <c>prossima</c> / <c>scalato</c>).</summary>
    NextInstallment = 0,

    /// <summary>Its own installment, not mixed into the year's rates (CondoLedger <c>aparte</c> for a debit).</summary>
    SeparateInstallment = 1,

    /// <summary>The administrator refunds a credit. No installment is changed.</summary>
    Refund = 2,

    /// <summary>Carried on the next year as a balance only. No installment is changed.</summary>
    CarryForward = 3,
}
