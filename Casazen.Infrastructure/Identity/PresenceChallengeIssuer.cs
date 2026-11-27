using System.Security.Cryptography;

namespace Casazen.Infrastructure.Identity;

/// <summary>
/// Draws the presence challenge (E72/F01/US01/T01, NF-192, ADR-013-R17, decision D6): a short, unpredictable list of moves, each drawn with
/// <see cref="RandomNumberGenerator"/> (never <c>System.Random</c>) from <c>presence.challenge.actions</c> of the policy, with no move following itself when the policy
/// says so. The number of moves, the moves, the lifetime and the rule of the repeat are all the policy's (<see cref="IdentityDecisionPolicy"/>): with the default policy
/// 4 x 3 x 3 = 36 sequences, about 5.2 bits, and the policy provider refuses a file of less than two bits, so a policy it accepted can always draw. Stateless: nothing
/// is kept here, the session keeps the challenge (<see cref="StoredPresenceChallenge"/>), and what the guest receives is the proof of it.
/// </summary>
public sealed class PresenceChallengeIssuer
{
    /// <summary>
    /// A new challenge for <paramref name="attempt"/>, issued at <paramref name="now"/> and expiring after <c>lifetimeSeconds</c> of the policy. Each call gives another
    /// challenge with another id.
    /// </summary>
    /// <exception cref="ArgumentException">The policy cannot draw a challenge (no move, no step, or a move that must not repeat and has no other to give way to): never a policy the provider accepted.</exception>
    public StoredPresenceChallenge Issue(IdentityDecisionPolicy policy, int attempt, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);

        var rules = policy.Presence.Challenge;
        var actions = rules.Actions;
        if (rules.Steps < 1 || actions.Count < 1 || (rules.NoImmediateRepeat && rules.Steps > 1 && actions.Count < 2))
            throw new ArgumentException("The policy cannot draw a presence challenge.", nameof(policy));

        var steps = new List<string>(rules.Steps);
        var previous = -1;
        for (var step = 0; step < rules.Steps; step++)
        {
            int index;
            if (rules.NoImmediateRepeat && previous >= 0)
            {
                // One of the other moves: draw among the n - 1 and step over the one that came before.
                index = RandomNumberGenerator.GetInt32(actions.Count - 1);
                if (index >= previous)
                    index++;
            }
            else
            {
                index = RandomNumberGenerator.GetInt32(actions.Count);
            }

            steps.Add(actions[index]);
            previous = index;
        }

        return new StoredPresenceChallenge(Guid.NewGuid(), steps, now, now.AddSeconds(rules.LifetimeSeconds), attempt);
    }
}
