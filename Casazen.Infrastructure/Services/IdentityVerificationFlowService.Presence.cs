using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Features;
using Casazen.Core.Identity;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The presence challenge of the self-hosted flow (E72/F01/US01/T01, NF-192, ADR-013-R17, decision D6): the part of <see cref="IdentityVerificationFlowService"/> that
/// implements <see cref="IIdentityPresenceFlow"/>. It follows the pattern of the pose challenge of the legacy flow (<c>IssueSelfieChallengeAsync</c>): the challenge is stored
/// in <c>DocumentCheckJson</c> under the same row lock, and the guest receives a proof made with Data Protection that is bound to the session, the token of its link, the
/// attempt, the time of the consent and the challenge. What differs: the challenge is several moves drawn by <see cref="Identity.PresenceChallengeIssuer"/> from the policy
/// (never one bit), and every call replaces the stored one, so a proof that was received earlier stops working.
/// </summary>
public sealed partial class IdentityVerificationFlowService : IIdentityPresenceFlow
{
    /// <summary>Data Protection purpose of the proof of a presence challenge. Never change it: a proof issued before would not be accepted any more.</summary>
    internal const string PresenceTokenPurpose = "Casazen.CheckIn.Presence.v1";

    /// <summary>The longest proof that is looked at: a proof of this purpose is far shorter, and the rest is not a proof.</summary>
    private const int MaxPresenceTokenLength = 2048;

    public Task<PresenceChallengeIssue?> IssuePresenceChallengeAsync(GuestCheckInSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);

        // What does not depend on the session is read before its row is locked: the flags, the policy and the models. When one of them is missing nothing is stored
        // and no lock is taken; the page offers the assisted path.
        var snapshot = identityPolicy?.Snapshot;
        if (!SelfHostedFlowIsOn() || presenceIssuer is null || snapshot is null || !snapshot.IsReady || biometricModels is not { IsAvailable: true })
            return Task.FromResult<PresenceChallengeIssue?>(null);

        var policy = snapshot.Policy;
        var limits = identityOptions?.Value.Presence ?? new IdentityPresenceOptions();
        return WithLockedPoseSessionAsync<PresenceChallengeIssue?>(session, null, async () =>
        {
            // Opened and not closed, and the guest did not choose a person (the assisted path is never handed an automatic challenge); then what the legacy step
            // also asks of a selfie: the consent is the current one, the document step is complete, the verification is not with a person (a refusal, a minor, an
            // organization that does not offer it, the attempts used) and has not passed.
            if (session.IdentityOpenedAt is null || session.IdentityClosedAt is not null || session.IdentityChoice == IdentityChoices.Assisted)
                return null;

            var state = await GetStateAsync(session, ct);
            if (state.Step is not (IdentityVerificationSteps.Selfie or IdentityVerificationSteps.Presence) || ReadCheck(session) is not { } check)
                return null;

            // A new challenge replaces the stored one: the proof of the old one is made with the id of the old one and no longer unprotects.
            var now = clock.GetUtcNow();
            var attempt = PresenceAttempt(session);
            var challenge = presenceIssuer.Issue(policy, attempt, now);
            session.DocumentCheckJson = JsonSerializer.Serialize(check with { Presence = challenge }, Json);
            await db.SaveChangesAsync(ct);

            var proof = PresenceProtector(session, attempt, challenge.Id).Protect(challenge.Id.ToString("N"));
            logger.LogInformation("Presence challenge of session {SessionId} issued for attempt {Attempt}", session.Id, attempt);
            return new PresenceChallengeIssue(
                proof,
                challenge.Steps,
                policy.Presence.Challenge.StepMs,
                policy.Presence.Challenge.LeadInMs,
                challenge.ExpiresAt,
                policy.Presence.Sequence.TargetFps,
                Math.Min(policy.Presence.Sequence.MaxFrames, limits.MaxFrames),
                limits.MaxFrameSide);
        }, ct);
    }

    public bool TryValidatePresenceToken(GuestCheckInSession session, string? challengeToken, out PresenceTokenStatus status)
    {
        ArgumentNullException.ThrowIfNull(session);
        status = PresenceTokenStatus.Required;
        if (challengeToken is not { Length: > 0 and <= MaxPresenceTokenLength } || session.BiometricConsentAt is null)
            return false;

        // The challenge stored for this session, and only for the attempt the session is at: a proof of an earlier attempt is not one any more.
        if (ReadCheck(session)?.Presence is not { } challenge || challenge.Attempt != PresenceAttempt(session))
            return false;

        try
        {
            if (!string.Equals(PresenceProtector(session, challenge.Attempt, challenge.Id).Unprotect(challengeToken), challenge.Id.ToString("N"), StringComparison.Ordinal))
                return false;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Of another session, attempt, consent or challenge, or not a proof at all.
            return false;
        }

        if (challenge.ExpiresAt <= clock.GetUtcNow())
        {
            status = PresenceTokenStatus.Expired;
            return false;
        }

        status = PresenceTokenStatus.Valid;
        return true;
    }

    /// <summary>The two flags of the self-hosted flow, both on: the alone-flag of the plan works only together with the one of the identity step.</summary>
    private bool SelfHostedFlowIsOn() =>
        featureFlags is not null
        && featureFlags.IsEnabled(FeatureFlags.CheckInIdentityVerification)
        && featureFlags.IsEnabled(FeatureFlags.CheckInIdentitySelfHosted);

    /// <summary>The attempt a presence challenge is for: the attempts made, the guided retries used, and this one.</summary>
    private static int PresenceAttempt(GuestCheckInSession session) => session.BiometricAttempts + session.IdentityGuidedRetries + 1;

    private IDataProtector PresenceProtector(GuestCheckInSession session, int attempt, Guid challengeId) =>
        dataProtection.CreateProtector(PresenceTokenPurpose, session.Id.ToString("N"), session.TokenHash, attempt.ToString(CultureInfo.InvariantCulture),
            session.BiometricConsentAt!.Value.Ticks.ToString(CultureInfo.InvariantCulture), challengeId.ToString("N"));
}
