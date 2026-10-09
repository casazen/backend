namespace Casazen.Web.DTOs;

/// <summary>
/// The body of an anonymous call that finds something with an e-mail address, and whose address is counted by a limit per address
/// (<c>PerEmailRateLimiter</c>, next to the per-IP policy of the endpoint): the guests' "Le mie prenotazioni" (BK-11) and the
/// customer's own area of a booking from a supplier's showcase (SP-11). The filter looks for the first argument of the action that
/// implements it.
/// </summary>
public interface IPerEmailRateLimitedRequest
{
    /// <summary>The address the booking was made with. A call without one is not counted (the model validation refuses it first).</summary>
    string? Email { get; }

    /// <summary>
    /// What the address is counted within: the slug of the supplier, so the same address at two suppliers has two budgets; <c>null</c>
    /// for the whole site (the guests).
    /// </summary>
    string? RateLimitScope { get; }
}
