namespace Casazen.Core.Services;

/// <summary>
/// The license of one accommodation, as the card of the accommodation gives it (<c>license</c>, <c>ApiLicenseForm</c> of the OpenAPI;
/// E09/F02/US02/T01, NF-56, D14, D21, S25). The license belongs to the accommodation, not to the org: one org with three properties has three of
/// them. It is read-only for the partner (a write answers 400 «read only mode»), so CasaZen only reads it and never changes it.
/// </summary>
/// <remarks>
/// <para><b>What is not said is not off.</b> A module the card does not mention is absent from <see cref="Modules"/>: CasaZen cannot tell whether it
/// is on, and a reader must never take the absence for «off» (that would flag a working function as lost). <see cref="Tier"/>,
/// <see cref="RoomsLimit"/> and <see cref="ExpireDay"/> are <c>null</c> in the same way.</para>
/// <para><b>Rooms.</b> Without a license per room Octorate ignores the room in the calendar (a push answers 200 with the warning «License for Room ...
/// has expired!» and writes nothing): a property created by the partner is born in plan <c>FREE</c> with <see cref="RoomsLimit"/> 0, S25.</para>
/// </remarks>
/// <param name="Tier">The plan of the channel manager (<c>channelManager</c> of the card: <c>FREE</c>, <c>BASIC</c>, <c>PRO</c>, <c>ENTERPRISE</c>, <c>PREMIUM</c>); <c>null</c> when the card does not say.</param>
/// <param name="Modules">
/// The module flags the card states, by module code (the codes of <see cref="MarketplaceLicensedFunctions.Codes"/>): <c>true</c> is on, <c>false</c> is
/// off. A module the card does not state is not in the map.
/// </param>
/// <param name="RoomsLimit">The number of rooms the license covers (<c>roomsLimit</c>); <c>null</c> when the card does not say.</param>
/// <param name="ExpireDay">The day the license expires (<c>expireDay</c>); <c>null</c> when the card does not give a date.</param>
public sealed record OctorateLicense(
    string? Tier,
    IReadOnlyDictionary<string, bool> Modules,
    int? RoomsLimit,
    DateOnly? ExpireDay);
