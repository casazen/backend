using Casazen.Core.Identity;
using Casazen.Infrastructure.Identity;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

public class PresenceChallengeIssuerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);

    private readonly PresenceChallengeIssuer _issuer = new();

    [Fact]
    public void PresenceChallengeIssuer_ThousandIssues_AreDiverseAndNeverRepeatImmediately()
    {
        var policy = IdentityTestPolicy.Shipped();

        var challenges = Enumerable.Range(0, 1000).Select(_ => _issuer.Issue(policy, attempt: 1, Now)).ToList();

        Assert.All(challenges, challenge =>
        {
            Assert.Equal(3, challenge.Steps.Count);
            Assert.All(challenge.Steps, step => Assert.Contains(step, PresenceActions.All));
            for (var index = 1; index < challenge.Steps.Count; index++)
                Assert.NotEqual(challenge.Steps[index - 1], challenge.Steps[index]);
        });

        var sequences = challenges.Select(challenge => string.Join('>', challenge.Steps)).ToList();
        Assert.InRange(sequences.Distinct().Count(), 30, 36);
        Assert.Equal(1000, challenges.Select(challenge => challenge.Id).Distinct().Count());

        Assert.InRange(IdentityPolicyProvider.ChallengeEntropyBits(4, 3, noImmediateRepeat: true), 5.1, 5.2);
        var measured = -sequences.GroupBy(sequence => sequence).Select(group => group.Count() / 1000.0).Sum(share => share * Math.Log2(share));
        Assert.True(measured > 4, $"The sequences drawn carry {measured:0.00} bits.");
    }

    [Fact]
    public void Issue_EveryMoveOfThePolicyComesUp_AsTheFirstAndAsTheLast()
    {
        var policy = IdentityTestPolicy.Shipped();

        var challenges = Enumerable.Range(0, 400).Select(_ => _issuer.Issue(policy, attempt: 1, Now)).ToList();

        Assert.Equal(PresenceActions.All.Order(), challenges.Select(challenge => challenge.Steps[0]).Distinct().Order());
        Assert.Equal(PresenceActions.All.Order(), challenges.Select(challenge => challenge.Steps[^1]).Distinct().Order());
    }

    [Fact]
    public void Issue_TheChallengeCarriesTheAttemptAndTheTimesOfThePolicy()
    {
        var policy = IdentityTestPolicy.Shipped();

        var challenge = _issuer.Issue(policy, attempt: 3, Now);

        Assert.Equal(3, challenge.Attempt);
        Assert.Equal(Now, challenge.IssuedAt);
        Assert.Equal(Now.AddSeconds(policy.Presence.Challenge.LifetimeSeconds), challenge.ExpiresAt);
        Assert.Equal(Now.AddSeconds(90), challenge.ExpiresAt);
        Assert.NotEqual(Guid.Empty, challenge.Id);
    }

    [Fact]
    public void Issue_TheStepsTheMovesAndTheLifetimeAreThePolicys_NotTheCodes()
    {
        var policy = IdentityTestPolicy.Shipped();
        var rules = policy.Presence.Challenge with { Steps = 5, Actions = [PresenceActions.LookUp, PresenceActions.LookDown, PresenceActions.TurnLeft], LifetimeSeconds = 45 };
        var changed = policy with { Presence = policy.Presence with { Challenge = rules } };

        var challenges = Enumerable.Range(0, 200).Select(_ => _issuer.Issue(changed, attempt: 1, Now)).ToList();

        Assert.All(challenges, challenge =>
        {
            Assert.Equal(5, challenge.Steps.Count);
            Assert.All(challenge.Steps, step => Assert.Contains(step, rules.Actions));
            Assert.Equal(Now.AddSeconds(45), challenge.ExpiresAt);
        });
        Assert.Equal(3, challenges.SelectMany(challenge => challenge.Steps).Distinct().Count());
    }

    [Fact]
    public void Issue_WithTheRepeatAllowed_AMoveMayFollowItself()
    {
        var policy = IdentityTestPolicy.Shipped();
        var rules = policy.Presence.Challenge with { NoImmediateRepeat = false };
        var changed = policy with { Presence = policy.Presence with { Challenge = rules } };

        var challenges = Enumerable.Range(0, 1000).Select(_ => _issuer.Issue(changed, attempt: 1, Now)).ToList();

        Assert.Contains(challenges, challenge => challenge.Steps[0] == challenge.Steps[1] || challenge.Steps[1] == challenge.Steps[2]);
    }

    [Fact]
    public void Issue_WithOneMoveAndNoRepeat_ASingleStepIsStillDrawn()
    {
        var policy = IdentityTestPolicy.Shipped();
        var rules = policy.Presence.Challenge with { Steps = 1, Actions = [PresenceActions.TurnRight] };
        var changed = policy with { Presence = policy.Presence with { Challenge = rules } };

        var challenge = _issuer.Issue(changed, attempt: 1, Now);

        Assert.Equal([PresenceActions.TurnRight], challenge.Steps);
    }

    [Theory]
    [InlineData(0, 4, false)]
    [InlineData(3, 0, false)]
    [InlineData(2, 1, true)]
    public void Issue_APolicyThatCannotDrawAChallenge_IsRefused(int steps, int actions, bool noImmediateRepeat)
    {
        var policy = IdentityTestPolicy.Shipped();
        var rules = policy.Presence.Challenge with { Steps = steps, Actions = [.. PresenceActions.All.Take(actions)], NoImmediateRepeat = noImmediateRepeat };
        var changed = policy with { Presence = policy.Presence with { Challenge = rules } };

        var refusal = Assert.Throws<ArgumentException>(() => _issuer.Issue(changed, attempt: 1, Now));

        Assert.DoesNotContain(PresenceActions.TurnLeft, refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Issue_AnAttemptBelowOne_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _issuer.Issue(IdentityTestPolicy.Shipped(), attempt: 0, Now));
        Assert.Throws<ArgumentNullException>(() => _issuer.Issue(null!, attempt: 1, Now));
    }

    [Fact]
    public void Issuer_DrawsWithRandomNumberGenerator_NeverWithSystemRandom()
    {
        var source = File.ReadAllText(Path.Combine(IdentityPolicyTestFiles.RepositoryRoot(), "Casazen.Infrastructure", "Identity", "PresenceChallengeIssuer.cs"));

        Assert.Contains("RandomNumberGenerator.GetInt32", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new Random", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Random.Shared", source, StringComparison.Ordinal);
    }
}
