namespace Casazen.Core.Services;

/// <summary>A hold of the public showcase as the slot planner needs it: the hours it takes and when it lapses. Nothing else.</summary>
public readonly record struct ShowcaseHoldInterval(DateTime StartUtc, DateTime EndUtc, DateTime ExpiresAtUtc);

/// <summary>
/// The holds of a supplier that take its time (SP-10): the bookings from the public showcase that wait for the customer to check
/// the e-mail. The agenda adds them to the planning input (<c>SupplierOccupancy.Hold</c>), so no slot is offered, or accepted,
/// while a hold has it. Reads the table of the holds with the supplier org as an explicit predicate and returns nothing of the
/// customer: no payload, no address, no code.
/// </summary>
public interface IShowcaseHoldReader
{
    /// <summary>
    /// The holds of <paramref name="supplierOrgId"/> that overlap <c>[fromUtc, toUtc)</c> and are alive at
    /// <paramref name="nowUtc"/>: not expired and not consumed (a consumed hold is the request it became).
    /// </summary>
    Task<IReadOnlyList<ShowcaseHoldInterval>> ListLiveAsync(
        Guid supplierOrgId,
        DateTime fromUtc,
        DateTime toUtc,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);
}
