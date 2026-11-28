using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

public sealed partial class IdentityVerificationFlowService
{
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(2);
    private static readonly SemaphoreSlim[] LocalPoseLocks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public Task<SelfieChallenge?> IssueSelfieChallengeAsync(GuestCheckInSession session, CancellationToken ct)
    {
        if (SelfHostedOn)
            return Task.FromResult<SelfieChallenge?>(null);

        return WithLockedPoseSessionAsync<SelfieChallenge?>(session, null, async () =>
        {
            var state = await GetStateAsync(session, ct);
            var check = ReadCheck(session);
            if (state.Step != IdentityVerificationSteps.Selfie || check is null || !state.ConsentRecorded)
                return null;

            var now = clock.GetUtcNow();
            var pose = check.Pose;
            if (pose is null || pose.ExpiresAt <= now || pose.Attempt != session.BiometricAttempts + 1)
            {
                pose = new StoredPoseChallenge(Guid.NewGuid(), RandomNumberGenerator.GetInt32(2) == 1,
                    now.Add(ChallengeLifetime), session.BiometricAttempts + 1);
                session.DocumentCheckJson = JsonSerializer.Serialize(check with { Pose = pose }, Json);
                await db.SaveChangesAsync(ct);
            }
            var proof = PoseProtector(session, pose.Attempt).Protect(pose.Id.ToString("N"));
            return new SelfieChallenge(proof, pose.NoseToRight, pose.ExpiresAt);
        }, ct);
    }

    public async Task<SelfieStepResult> SubmitChallengedSelfieAsync(GuestCheckInSession session, Stream selfie, Stream? turnedSelfie,
        string? challengeToken, Stream? documentPhoto, Stream? documentPhotoBack, CancellationToken ct)
    {
        var stale = new SelfieStepResult(SelfieStepStatus.ChallengeRequired, IdentityVerificationGuestResults.Retry, [], 0, null);

        if (SelfHostedOn)
            return stale;

        var (decision, alreadyVerified) = await WithLockedPoseSessionAsync(session, (stale, false), async () =>
        {
            var wasVerified = session.BiometricOutcome == BiometricOutcome.Accept;
            var step = await VerifySelfieAsync(session, selfie, turnedSelfie, challengeToken, documentPhoto, documentPhotoBack, ct);
            return (step, wasVerified);
        }, ct);

        if (decision.Result != IdentityVerificationGuestResults.Ok)
            return decision;

        string? code;
        if (alreadyVerified)
        {
            // The guest may have paid the tourist tax since the original acceptance. Do not run another comparison or send another e-mail.
            code = await accessCodes.ReleaseToGuestAsync(session.BookingId, AccessCodeReleaseChannels.CheckInScreen, ct);
        }
        else
        {
            await alerts.ResolveAsync(session.OrgId, session.BookingId, ct);
            code = await ReleaseCodeAsync(session, ct);
        }
        return decision with { LockCode = code, LockCodeHeld = await HoldReasonAsync(session, code, ct) };
    }

    private IDataProtector PoseProtector(GuestCheckInSession session, int attempt) =>
        dataProtection.CreateProtector("Casazen.CheckIn.ActivePose.v1", session.Id.ToString("N"), session.TokenHash,
            attempt.ToString(CultureInfo.InvariantCulture), session.BiometricConsentAt!.Value.Ticks.ToString(CultureInfo.InvariantCulture),
            session.BiometricConsentVersion!, session.BiometricConsentTextHash!);

    private bool IsCurrentChallenge(GuestCheckInSession session, StoredDocumentCheck check, string? proof)
    {
        if (proof is not { Length: > 0 and <= 2048 } || check.Pose is not { } pose || pose.ExpiresAt <= clock.GetUtcNow()
            || pose.Attempt != session.BiometricAttempts + 1)
            return false;
        try
        {
            return PoseProtector(session, pose.Attempt).Unprotect(proof) == pose.Id.ToString("N");
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return false;
        }
    }

    private async Task<T> WithLockedPoseSessionAsync<T>(GuestCheckInSession session, T staleSession, Func<Task<T>> work, CancellationToken ct)
    {
        var tokenHash = session.TokenHash;
        var postgres = db.Database.IsNpgsql();
        var gate = postgres ? null : LocalPoseLocks[(uint)session.Id.GetHashCode() % (uint)LocalPoseLocks.Length];
        if (gate is not null)
            await gate.WaitAsync(ct);
        try
        {
            await using var transaction = postgres ? await db.Database.BeginTransactionAsync(ct) : null;
            if (postgres)
                await db.Database.SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM "GuestCheckInSessions" WHERE "Id" = {session.Id} FOR UPDATE""").ToListAsync(ct);

            var entry = db.Entry(session);
            await entry.ReloadAsync(ct);
            if (entry.State == EntityState.Detached || session.TokenHash != tokenHash || !session.IsOpen || session.ExpiresAt <= UtcNow())
                return staleSession;

            var result = await work();
            if (transaction is not null)
                await transaction.CommitAsync(ct);
            return result;
        }
        finally
        {
            gate?.Release();
        }
    }
}

internal sealed record StoredPoseChallenge(Guid Id, bool NoseToRight, DateTimeOffset ExpiresAt, int Attempt);
