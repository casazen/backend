namespace Casazen.Infrastructure.Identity;

/// <summary>
/// The presence challenge of one attempt as the session keeps it (member <c>presence</c> of <c>GuestCheckInSession.DocumentCheckJson</c>, E70/F01/US01/T01,
/// NF-192, ADR-013-R17): its id, the moves asked in their order (codes of <c>PresenceActions</c>), when it was issued and when it expires, and the attempt it
/// belongs to. Codes and times only: never a frame, a landmark or a score. A record of the session written before the self-hosted flow has none and reads as
/// before.
/// </summary>
/// <param name="Id">The id of the challenge, bound to the session and the attempt by the proof the guest receives.</param>
/// <param name="Steps">The moves asked, in their order.</param>
/// <param name="IssuedAt">When the challenge was issued: a sequence longer than the time since then is not plausible.</param>
/// <param name="ExpiresAt">When the challenge expires.</param>
/// <param name="Attempt">The attempt it belongs to (the attempts of the session plus one, when it was issued).</param>
public sealed record StoredPresenceChallenge(Guid Id, IReadOnlyList<string> Steps, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt, int Attempt);
