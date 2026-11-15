using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// The F24 data of the owners' withholding (ADR-018 amended, E06/F05/US02/T03). The withholding itself is decided only when a payout to the beneficiary
/// is confirmed, on the approved rule in force that day (<see cref="IOwnerPayoutService"/>): no statement operation creates withholding.
/// </summary>
public interface IWithholdingService
{
    /// <summary>
    /// The F24 record of a month from the applicable withholding evidence of the payouts paid in it (and their reversals): the total, the tax code and
    /// the rule versions of that evidence, due on the 16th of the following month (ADR-018-R8). Generated again while it is only generated; a generated
    /// F24 is never a filed one.
    /// </summary>
    /// <exception cref="Exceptions.DomainRuleException">422 <c>tax_no_obligations</c>, <c>f24_tax_code_mixed</c>.</exception>
    /// <exception cref="Exceptions.DomainConflictException">409 <c>tax_file_already_filed</c> once reviewed or further.</exception>
    Task<F24Record> GenerateF24Async(
        Guid orgId,
        int month,
        int year,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The F24 data of a month to export (E18/F01/US01/T02, NF-117), read only: from the applicable withholding evidence of the payouts paid in the month and their
    /// reversals, of an Italian rule only (the evidence the F24 is built from), one line per tax code with its amount in euro and its due date (the 16th of the next
    /// month), and the breakdown per owner and payout, whose amounts add up to the lines. It neither generates nor changes the F24 record; it is never the form
    /// filed nor a telematic file: the agency or its accountant pays and files it.
    /// </summary>
    /// <exception cref="Exceptions.DomainRuleException">422 <c>tax_no_obligations</c> (no evidence in the month), <c>owner_period_invalid</c>.</exception>
    Task<F24MonthData> GetF24DataAsync(
        Guid orgId,
        int year,
        int month,
        CancellationToken cancellationToken = default);
}

/// <summary>The F24 data of a month (E18/F01/US01/T02): the lines of the section Erario and the decisions of the payouts they add up.</summary>
public sealed record F24MonthData(int Year, int Month, IReadOnlyList<F24TaxLine> Lines, IReadOnlyList<F24PayoutLine> Breakdown);

/// <summary>
/// One line of the section Erario of the F24: the tax code (the approved rule's, <c>1919</c> while it says so), the reference month and year, the amount in euro (the
/// decisions of the month less their reversals), the due date and how many decisions and reversals make it.
/// </summary>
public sealed record F24TaxLine(string TaxCode, int ReferenceMonth, int ReferenceYear, decimal Amount, DateOnly DueDate, int EvidenceCount);

/// <summary>
/// One decision of the month, or the reversal of one (<paramref name="Reversal"/>, with the opposite amounts): the owner, the payout (its reference and day, its
/// currency, its gross, the withholding it kept and its net, in that currency), the tax code and the version of the rule, the base, the rate and the amount in euro.
/// </summary>
public sealed record F24PayoutLine(
    Guid OwnerId,
    string OwnerName,
    Guid PayoutId,
    string? PayoutReference,
    DateOnly PaidOn,
    bool Reversal,
    string TaxCode,
    string RuleVersion,
    decimal GrossBase,
    decimal? RatePercent,
    decimal Amount,
    string PayoutCurrency,
    decimal PayoutGross,
    decimal PayoutWithheld,
    decimal PayoutNet);

/// <summary>The dates of the F24 of the withholding.</summary>
public static class F24Dates
{
    /// <summary>The due date of the F24 of a month: the 16th of the month after it (ADR-018-R8), 16 January of the next year for December.</summary>
    public static DateOnly DueDateOf(int year, int month) => new DateOnly(year, month, 1).AddMonths(1).AddDays(15);
}
