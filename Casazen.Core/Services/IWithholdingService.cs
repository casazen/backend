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
}
