using System.Data;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Identity;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Infrastructure;
using Casazen.Tests.Unit.Logging;
using Casazen.Tests.Unit.Services;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Configuration;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Admin;
using Casazen.Web.Extensions;
using Hangfire;
using Hangfire.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E73/F01/US02/T03 (NF-195, decision D9): the retention and the verifiable deletion of what the self-hosted identity verification keeps. The evidence past its time (and of a
/// booking decided) is deleted, object and row marked; an abandoned session is closed as <c>abandoned</c> and loses its kept pictures and its challenge; the checks of the
/// document and the logs past their retention go; every organization is judged with its own rows; a second run finds nothing; the erasure of a guest deletes its evidence; the
/// platform administrators see what is overdue and the last run; the hourly job is scheduled only with one of the two flags on and carries no data.
/// </summary>
public sealed class IdentityRetentionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task IdentityRetention_ExpiredEvidence_IsDeletedWithFile()
    {
        // Proof (data, AC1): the evidence past its expiry loses its object and its row is marked deleted (expired); a live one stays; a second run deletes nothing more.
        await using var world = new RetentionWorld();
        var booking = await world.StayAsync(world.OrgA);
        var expired = await world.EvidenceAsync(world.OrgA, booking, expiresAt: Now.UtcDateTime.AddMinutes(-1));
        var live = await world.EvidenceAsync(world.OrgA, booking, expiresAt: Now.UtcDateTime.AddHours(5));

        var counts = await world.Service().PurgeAsync(Now.UtcDateTime, Ct);

        Assert.Equal(1, counts.ExpiredEvidence);
        var rows = await world.EvidenceRowsAsync();
        var deleted = rows.Single(row => row.Id == expired.Id);
        Assert.Equal((Now.UtcDateTime, IdentityEvidenceDeletionReasons.Expired), (deleted.DeletedAt, deleted.DeletionReason));
        Assert.False(world.Storage.Objects.ContainsKey(expired.StorageKey));
        Assert.Null(rows.Single(row => row.Id == live.Id).DeletedAt);
        Assert.True(world.Storage.Objects.ContainsKey(live.StorageKey));

        var again = await world.Service().PurgeAsync(Now.UtcDateTime, Ct);
        Assert.Equal(IdentityRetentionCounts.None, again);
    }

    [Fact]
    public async Task IdentityRetention_TheEvidenceOfADecidedBooking_IsDeleted_WhateverItsExpiry()
    {
        // The safety sweep: a decision after the evidence was kept (an operator's approval or rejection, or an approval of the policy) deletes it (decided); the reopening of an
        // operator (a new photo asked, Escalate) is no decision; a decision before the evidence was kept does not count.
        await using var world = new RetentionWorld();
        var decided = await world.StayAsync(world.OrgA);
        var reopened = await world.StayAsync(world.OrgA);
        var old = await world.StayAsync(world.OrgA);
        var evidence = await world.EvidenceAsync(world.OrgA, decided, expiresAt: Now.UtcDateTime.AddHours(40));
        var waiting = await world.EvidenceAsync(world.OrgA, reopened, expiresAt: Now.UtcDateTime.AddHours(40));
        var before = await world.EvidenceAsync(world.OrgA, old, expiresAt: Now.UtcDateTime.AddHours(40));
        await world.LogAsync(world.OrgA, decided, VerificationMethod.Operator, BiometricOutcome.Reject, Now.UtcDateTime.AddMinutes(-5));
        await world.LogAsync(world.OrgA, reopened, VerificationMethod.Operator, BiometricOutcome.Escalate, Now.UtcDateTime.AddMinutes(-5));
        await world.LogAsync(world.OrgA, old, VerificationMethod.Operator, BiometricOutcome.Accept, Now.UtcDateTime.AddHours(-20));

        var counts = await world.Service().PurgeAsync(Now.UtcDateTime, Ct);

        Assert.Equal(1, counts.DecidedEvidence);
        var rows = await world.EvidenceRowsAsync();
        Assert.Equal(IdentityEvidenceDeletionReasons.Decided, rows.Single(row => row.Id == evidence.Id).DeletionReason);
        Assert.False(world.Storage.Objects.ContainsKey(evidence.StorageKey));
        Assert.Null(rows.Single(row => row.Id == waiting.Id).DeletedAt);
        Assert.Null(rows.Single(row => row.Id == before.Id).DeletedAt);
    }

    [Fact]
    public async Task IdentityRetention_AbandonedSession_DeletesDocumentImages()
    {
        // Proof (data, AC2): a verification opened 25 hours ago and never closed is closed as abandoned, its presence challenge cleared, its evidence deleted (abandoned) and the
        // kept pictures of its document deleted, objects and rows. A verification opened an hour ago keeps everything.
        await using var world = new RetentionWorld();
        var abandonedStay = await world.StayAsync(world.OrgA);
        var activeStay = await world.StayAsync(world.OrgA);
        var abandoned = await world.SessionAsync(world.OrgA, abandonedStay, openedAt: Now.UtcDateTime.AddHours(-25), withChallenge: true);
        var active = await world.SessionAsync(world.OrgA, activeStay, openedAt: Now.UtcDateTime.AddHours(-1), withChallenge: true);
        var abandonedImage = await world.DocumentImageAsync(world.OrgA, abandonedStay);
        var activeImage = await world.DocumentImageAsync(world.OrgA, activeStay);
        var evidence = await world.EvidenceAsync(world.OrgA, abandonedStay, expiresAt: Now.UtcDateTime.AddHours(30), sessionId: abandoned.Id);

        var counts = await world.Service().PurgeAsync(Now.UtcDateTime, Ct);

        Assert.Equal((1, 1, 1), (counts.AbandonedSessions, counts.AbandonedDocumentImages, counts.AbandonedEvidence));
        await using var read = world.NewContext();
        var closed = await read.GuestCheckInSessions.IgnoreQueryFilters().AsNoTracking().SingleAsync(session => session.Id == abandoned.Id);
        Assert.Equal((Now.UtcDateTime, IdentityOutcomeCodes.Abandoned), (closed.IdentityClosedAt, closed.IdentityOutcomeCode));
        Assert.False(HasChallenge(closed.DocumentCheckJson));
        Assert.Contains("\"photoSha256\"", closed.DocumentCheckJson!, StringComparison.Ordinal);
        Assert.False(await read.DocumentImages.IgnoreQueryFilters().AnyAsync(image => image.Id == abandonedImage.Id));
        Assert.False(world.Storage.Objects.ContainsKey(abandonedImage.FrontImageUrl));
        Assert.Equal(IdentityEvidenceDeletionReasons.Abandoned, (await world.EvidenceRowsAsync()).Single(row => row.Id == evidence.Id).DeletionReason);

        var open = await read.GuestCheckInSessions.IgnoreQueryFilters().AsNoTracking().SingleAsync(session => session.Id == active.Id);
        Assert.Null(open.IdentityClosedAt);
        Assert.True(HasChallenge(open.DocumentCheckJson));
        Assert.True(await read.DocumentImages.IgnoreQueryFilters().AnyAsync(image => image.Id == activeImage.Id));
        Assert.True(world.Storage.Objects.ContainsKey(activeImage.FrontImageUrl));

        Assert.Equal(IdentityRetentionCounts.None, await world.Service().PurgeAsync(Now.UtcDateTime, Ct));
    }

    [Fact]
    public async Task IdentityRetention_AnExpiredSession_LeavesThePicturesOfTheSessionThatReplacedItInUse()
    {
        // The pictures are of the booking: an expired session (never opened, on the legacy path) of a booking whose new link is in use leaves them; once no session uses them
        // (the new one expired too) they go.
        await using var world = new RetentionWorld();
        var stay = await world.StayAsync(world.OrgA);
        await world.SessionAsync(world.OrgA, stay, openedAt: null, expiresAt: Now.UtcDateTime.AddDays(-1));
        var replacement = await world.SessionAsync(world.OrgA, stay, openedAt: null, expiresAt: Now.UtcDateTime.AddDays(6));
        var image = await world.DocumentImageAsync(world.OrgA, stay);

        Assert.Equal(0, (await world.Service().PurgeAsync(Now.UtcDateTime, Ct)).AbandonedDocumentImages);
        Assert.True(world.Storage.Objects.ContainsKey(image.FrontImageUrl));

        await using (var db = world.NewContext())
        {
            var session = await db.GuestCheckInSessions.IgnoreQueryFilters().SingleAsync(row => row.Id == replacement.Id);
            session.ExpiresAt = Now.UtcDateTime.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, (await world.Service().PurgeAsync(Now.UtcDateTime, Ct)).AbandonedDocumentImages);
        Assert.False(world.Storage.Objects.ContainsKey(image.FrontImageUrl));
    }

    [Fact]
    public async Task IdentityRetention_TheChecksOfTheDocument_AreClearedPastTheirDays_OnlyOnceTheSessionCannotBeUsed()
    {
        await using var world = new RetentionWorld();
        var stay = await world.StayAsync(world.OrgA);
        var expiredLongAgo = await world.SessionAsync(world.OrgA, stay, openedAt: null, expiresAt: Now.UtcDateTime.AddDays(-31));
        var expiredRecently = await world.SessionAsync(world.OrgA, stay, openedAt: null, expiresAt: Now.UtcDateTime.AddDays(-29));
        var closedStillOpen = await world.SessionAsync(
            world.OrgA, stay, openedAt: Now.UtcDateTime.AddDays(-40), expiresAt: Now.UtcDateTime.AddDays(3), closedAt: Now.UtcDateTime.AddDays(-35));
        var closedAndExpired = await world.SessionAsync(
            world.OrgA, stay, openedAt: Now.UtcDateTime.AddDays(-40), expiresAt: Now.UtcDateTime.AddDays(-1), closedAt: Now.UtcDateTime.AddDays(-35));

        var counts = await world.Service().PurgeAsync(Now.UtcDateTime, Ct);

        Assert.Equal(2, counts.DocumentChecksCleared);
        await using var read = world.NewContext();
        var json = await read.GuestCheckInSessions.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(session => session.Id, session => session.DocumentCheckJson);
        Assert.Null(json[expiredLongAgo.Id]);
        Assert.Null(json[closedAndExpired.Id]);
        Assert.NotNull(json[expiredRecently.Id]);
        Assert.NotNull(json[closedStillOpen.Id]);
    }

    [Fact]
    public async Task IdentityRetention_LogsOfOtherOrgUntouched()
    {
        // Proof (data, isolation, AC3): the logs of the stays of org A whose check-out is older than 12 months are deleted; the logs of org B are judged with the stays of org B only
        // (a row of B that names a booking of A is not deleted with A's), and a stay of A that ended 11 months ago keeps its logs.
        await using var world = new RetentionWorld();
        var oldStayOfA = await world.StayAsync(world.OrgA, checkOut: new DateTime(2025, 11, 1, 0, 0, 0, DateTimeKind.Utc));
        var recentStayOfA = await world.StayAsync(world.OrgA, checkOut: new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc));
        var stayOfB = await world.StayAsync(world.OrgB, checkOut: new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc));
        var oldLogOfA = await world.LogAsync(world.OrgA, oldStayOfA, VerificationMethod.Biometric, BiometricOutcome.Accept, new DateTime(2025, 10, 30, 9, 0, 0, DateTimeKind.Utc));
        var recentLogOfA = await world.LogAsync(world.OrgA, recentStayOfA, VerificationMethod.Biometric, BiometricOutcome.Accept, new DateTime(2026, 1, 8, 9, 0, 0, DateTimeKind.Utc));
        var logOfB = await world.LogAsync(world.OrgB, stayOfB, VerificationMethod.Biometric, BiometricOutcome.Escalate, Now.UtcDateTime.AddDays(-8));
        var crossedLogOfB = await world.LogAsync(world.OrgB, oldStayOfA, VerificationMethod.Biometric, BiometricOutcome.Escalate, new DateTime(2025, 10, 30, 9, 0, 0, DateTimeKind.Utc));

        var counts = await world.Service().PurgeAsync(Now.UtcDateTime, Ct);

        Assert.Equal(1, counts.VerificationLogsDeleted);
        await using var read = world.NewContext();
        var left = await read.BiometricVerificationLogs.IgnoreQueryFilters().AsNoTracking().Select(log => log.Id).ToListAsync();
        Assert.DoesNotContain(oldLogOfA.Id, left);
        Assert.Contains(recentLogOfA.Id, left);
        Assert.Contains(logOfB.Id, left);
        Assert.Contains(crossedLogOfB.Id, left);
    }

    [Fact]
    public async Task IdentityRetention_TheOperatorRowsOfAnOldStay_StayAsTheProofThatItsHandOffWasAnswered()
    {
        // Proof (data, review of S015): the retention deletes the rows of the automatic verification of the old stays and never a row of an operator, which is what tells the queue
        // that a hand-off was answered (VerificationHandOff.Unanswered, read with no date filter). A stay an operator rejected, and a stay approved after a new link replaced the
        // session that had handed it over, do not wait for an operator again once their logs are gone; a stay nobody answered waits before and after.
        await using var world = new RetentionWorld();
        var checkOut = new DateTime(2025, 10, 31, 0, 0, 0, DateTimeKind.Utc);
        var rejected = await world.StayAsync(world.OrgA, checkOut);
        var approved = await world.StayAsync(world.OrgA, checkOut);
        var waiting = await world.StayAsync(world.OrgA, checkOut);
        var handedOff = new DateTime(2025, 10, 29, 10, 0, 0, DateTimeKind.Utc);
        await world.HandedOffSessionAsync(world.OrgA, rejected, handedOff, BiometricOutcome.Reject, GuestCheckInSessionStatus.Scaduto);
        await world.HandedOffSessionAsync(world.OrgA, approved, handedOff.AddDays(-1), BiometricOutcome.Escalate, GuestCheckInSessionStatus.Scaduto);
        await world.HandedOffSessionAsync(world.OrgA, approved, handedOff, BiometricOutcome.Accept, GuestCheckInSessionStatus.Completo);
        await world.HandedOffSessionAsync(world.OrgA, waiting, handedOff, BiometricOutcome.Escalate, GuestCheckInSessionStatus.InCompilazione);
        foreach (var stay in new[] { rejected, approved, waiting })
            await world.LogAsync(world.OrgA, stay, VerificationMethod.Biometric, BiometricOutcome.Escalate, handedOff.AddMinutes(-30));

        await world.LogAsync(world.OrgA, rejected, VerificationMethod.Operator, BiometricOutcome.Reject, handedOff.AddDays(1));
        await world.LogAsync(world.OrgA, approved, VerificationMethod.Operator, BiometricOutcome.Accept, handedOff.AddDays(1));

        // Three sessions need an operator (the rejected one, the replaced link of the approved stay, the waiting one): only the rows of an operator tell that two were answered.
        await using (var db = world.NewContext())
            Assert.Equal(3, await db.GuestCheckInSessions.IgnoreQueryFilters().Where(VerificationOperatorRules.NeedsOperator).CountAsync());

        Assert.Equal([waiting], await world.WaitingForAnOperatorAsync(world.OrgA));

        var counts = await world.Service().PurgeAsync(Now.UtcDateTime, Ct);

        Assert.Equal(3, counts.VerificationLogsDeleted);
        Assert.Equal([waiting], await world.WaitingForAnOperatorAsync(world.OrgA));
        await using var read = world.NewContext();
        var left = await read.BiometricVerificationLogs.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        Assert.Equal(2, left.Count);
        Assert.All(left, log => Assert.Equal(VerificationMethod.Operator, log.Method));
    }

    [Fact]
    public async Task IdentityRetention_TheFailureOfOneOrg_DoesNotStopTheOthers_AndTheRunIsReportedFailed()
    {
        await using var world = new RetentionWorld();
        var stayOfA = await world.StayAsync(world.OrgA);
        var stayOfB = await world.StayAsync(world.OrgB);
        var ofA = await world.EvidenceAsync(world.OrgA, stayOfA, expiresAt: Now.UtcDateTime.AddMinutes(-1));
        var ofB = await world.EvidenceAsync(world.OrgB, stayOfB, expiresAt: Now.UtcDateTime.AddMinutes(-1));
        world.Storage.FailDeleteOf = ofA.StorageKey;
        var logger = new CapturingLogger<IdentityRetentionService>();

        var failure = await Assert.ThrowsAsync<IdentityRetentionFailedException>(() => world.Service(logger).PurgeAsync(Now.UtcDateTime, Ct));

        Assert.Equal(1, failure.FailedOrgs);
        var rows = await world.EvidenceRowsAsync();
        Assert.Null(rows.Single(row => row.Id == ofA.Id).DeletedAt);
        Assert.Equal(IdentityEvidenceDeletionReasons.Expired, rows.Single(row => row.Id == ofB.Id).DeletionReason);
        Assert.Equal(1, world.RunLog.Last!.FailedOrgs);
        Assert.DoesNotContain("refused the face", logger.AllOutput, StringComparison.Ordinal);
        Assert.Contains(nameof(IOException), logger.AllOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdentityRetention_WithBothFlagsOff_DeletesOnlyTheEvidencePastItsTime()
    {
        // Proof (privacy, review of S015, point 2): a flag switched off never leaves an evidence with no expiry, so with both flags off a run deletes the evidence past its time (and
        // only that: the sweep of the decided stays, the abandoned sessions, the kept pictures, the checks of the document and the logs wait for a flag).
        await using var world = new RetentionWorld();
        world.Flags.Set(FeatureFlags.CheckInIdentitySelfHosted, false);
        world.Flags.Set(FeatureFlags.CheckInIdentityReviewEvidence, false);
        var stay = await world.StayAsync(world.OrgA);
        var decidedStay = await world.StayAsync(world.OrgA);
        var oldStay = await world.StayAsync(world.OrgA, new DateTime(2025, 10, 31, 0, 0, 0, DateTimeKind.Utc));
        var expired = await world.EvidenceAsync(world.OrgA, stay, expiresAt: Now.UtcDateTime.AddMinutes(-1));
        var live = await world.EvidenceAsync(world.OrgA, stay, expiresAt: Now.UtcDateTime.AddHours(5));
        var decided = await world.EvidenceAsync(world.OrgA, decidedStay, expiresAt: Now.UtcDateTime.AddHours(40));
        await world.LogAsync(world.OrgA, decidedStay, VerificationMethod.Operator, BiometricOutcome.Reject, Now.UtcDateTime.AddMinutes(-5));
        var abandoned = await world.SessionAsync(world.OrgA, stay, openedAt: Now.UtcDateTime.AddHours(-30), withChallenge: true);
        var image = await world.DocumentImageAsync(world.OrgA, stay);
        var oldLog = await world.LogAsync(world.OrgA, oldStay, VerificationMethod.Biometric, BiometricOutcome.Accept, new DateTime(2025, 10, 30, 9, 0, 0, DateTimeKind.Utc));
        var stale = await world.SessionAsync(world.OrgA, oldStay, openedAt: null, expiresAt: Now.UtcDateTime.AddDays(-40));

        var counts = await world.Service().PurgeAsync(Now.UtcDateTime, Ct);

        Assert.Equal(IdentityRetentionCounts.None with { ExpiredEvidence = 1 }, counts);
        var rows = await world.EvidenceRowsAsync();
        Assert.Equal((Now.UtcDateTime, IdentityEvidenceDeletionReasons.Expired), (rows.Single(row => row.Id == expired.Id).DeletedAt, rows.Single(row => row.Id == expired.Id).DeletionReason));
        Assert.False(world.Storage.Objects.ContainsKey(expired.StorageKey));
        Assert.All(new[] { live, decided }, row => Assert.Null(rows.Single(kept => kept.Id == row.Id).DeletedAt));
        Assert.True(world.Storage.Objects.ContainsKey(live.StorageKey));
        Assert.True(world.Storage.Objects.ContainsKey(decided.StorageKey));
        await using (var read = world.NewContext())
        {
            Assert.Null((await read.GuestCheckInSessions.IgnoreQueryFilters().AsNoTracking().SingleAsync(session => session.Id == abandoned.Id)).IdentityClosedAt);
            Assert.NotNull((await read.GuestCheckInSessions.IgnoreQueryFilters().AsNoTracking().SingleAsync(session => session.Id == stale.Id)).DocumentCheckJson);
            Assert.True(await read.DocumentImages.IgnoreQueryFilters().AnyAsync(row => row.Id == image.Id));
            Assert.True(await read.BiometricVerificationLogs.IgnoreQueryFilters().AnyAsync(row => row.Id == oldLog.Id));
        }

        Assert.Equal(Now.UtcDateTime, world.RunLog.Last!.RanAt);

        // One of the two flags is enough for the whole retention: the rest of the categories run.
        world.Flags.Set(FeatureFlags.CheckInIdentityReviewEvidence, true);
        var whole = await world.Service().PurgeAsync(Now.UtcDateTime, Ct);

        Assert.Equal((0, 1, 1, 1, 1), (whole.ExpiredEvidence, whole.DecidedEvidence, whole.AbandonedSessions, whole.AbandonedDocumentImages, whole.VerificationLogsDeleted));
        Assert.True(whole.DocumentChecksCleared >= 1);
    }

    [Fact]
    public async Task IdentityRetention_ALimitOutsideTheRetentionOutOfRange_DoesNotStopTheRun()
    {
        // Review of S015, point 4: the run needs the three periods of IdentityVerification:Retention and nothing else of the section. A limit of the capture that is out of its range
        // (Presence:MaxFrames) makes the section as a whole invalid for the capture, and must not make the retention skip its categories and end green with zeros.
        await using var world = new RetentionWorld();
        var stay = await world.StayAsync(world.OrgA);
        var expired = await world.EvidenceAsync(world.OrgA, stay, expiresAt: Now.UtcDateTime.AddMinutes(-1));
        var abandoned = await world.SessionAsync(world.OrgA, stay, openedAt: Now.UtcDateTime.AddHours(-25));
        var image = await world.DocumentImageAsync(world.OrgA, stay);
        var options = new IdentityVerificationOptions { Presence = new IdentityPresenceOptions { MaxFrames = 1 } };
        Assert.NotEmpty(options.Validate());
        Assert.Empty(options.ValidateRetention());

        var counts = await world.Service(options: options).PurgeAsync(Now.UtcDateTime, Ct);

        Assert.Equal((1, 1, 1), (counts.ExpiredEvidence, counts.AbandonedSessions, counts.AbandonedDocumentImages));
        Assert.False(world.Storage.Objects.ContainsKey(expired.StorageKey));
        Assert.False(world.Storage.Objects.ContainsKey(image.FrontImageUrl));
        await using var read = world.NewContext();
        Assert.Equal(IdentityOutcomeCodes.Abandoned, (await read.GuestCheckInSessions.IgnoreQueryFilters().AsNoTracking().SingleAsync(session => session.Id == abandoned.Id)).IdentityOutcomeCode);
        Assert.Equal(0, world.RunLog.Last!.FailedOrgs);
    }

    [Fact]
    public async Task IdentityRetention_ARetentionPeriodOutOfRange_FailsTheRun_AfterTheEvidenceWasPurged()
    {
        // Review of S015, point 4: a period of IdentityVerification:Retention out of its range means the abandoned sessions, the kept pictures, the checks of the document and the logs
        // cannot be judged: the evidence still goes (its time is on its row) and the run is FAILED (Hangfire shows it and runs it again), never done with zeros.
        await using var world = new RetentionWorld();
        var stay = await world.StayAsync(world.OrgA);
        var expired = await world.EvidenceAsync(world.OrgA, stay, expiresAt: Now.UtcDateTime.AddMinutes(-1));
        var abandoned = await world.SessionAsync(world.OrgA, stay, openedAt: Now.UtcDateTime.AddHours(-25));
        var image = await world.DocumentImageAsync(world.OrgA, stay);
        var options = new IdentityVerificationOptions { Retention = new IdentityRetentionOptions { AbandonedAfterHours = 0 } };
        Assert.NotEmpty(options.ValidateRetention());
        var logger = new CapturingLogger<IdentityRetentionService>();

        var failure = await Assert.ThrowsAsync<IdentityRetentionFailedException>(() => world.Service(logger, options).PurgeAsync(Now.UtcDateTime, Ct));

        Assert.True(failure.PeriodsInvalid);
        Assert.Equal(0, failure.FailedOrgs);
        Assert.Contains("IdentityVerification:Retention", failure.Message, StringComparison.Ordinal);
        Assert.Contains("retention_config_invalid", logger.AllOutput, StringComparison.Ordinal);
        Assert.False(world.Storage.Objects.ContainsKey(expired.StorageKey));
        Assert.Equal(1, world.RunLog.Last!.Counts.ExpiredEvidence);
        await using var read = world.NewContext();
        Assert.Null((await read.GuestCheckInSessions.IgnoreQueryFilters().AsNoTracking().SingleAsync(session => session.Id == abandoned.Id)).IdentityClosedAt);
        Assert.True(world.Storage.Objects.ContainsKey(image.FrontImageUrl));

        // The same periods with the flags off: nothing of the periods is needed, the evidence goes and the run is not a failure.
        world.Flags.Set(FeatureFlags.CheckInIdentitySelfHosted, false);
        world.Flags.Set(FeatureFlags.CheckInIdentityReviewEvidence, false);
        await world.EvidenceAsync(world.OrgA, stay, expiresAt: Now.UtcDateTime.AddMinutes(-2));
        Assert.Equal(1, (await world.Service(options: options).PurgeAsync(Now.UtcDateTime, Ct)).ExpiredEvidence);
    }

    [Fact]
    public async Task IdentityRetention_TheErasureOfAGuest_DeletesItsEvidence()
    {
        // Proof (data, AC5): the GDPR erasure of a guest deletes the evidence of the verifications of its bookings (objects, rows marked erased); another guest's stays.
        await using var world = new RetentionWorld();
        var stay = await world.StayAsync(world.OrgA);
        var otherStay = await world.StayAsync(world.OrgA);
        var ofTheGuest = await world.EvidenceAsync(world.OrgA, stay, expiresAt: Now.UtcDateTime.AddHours(30));
        var ofAnother = await world.EvidenceAsync(world.OrgA, otherStay, expiresAt: Now.UtcDateTime.AddHours(30));

        await using (var db = world.NewContext())
        {
            var guest = await db.Guests.IgnoreQueryFilters().SingleAsync(g => db.Bookings.Any(b => b.Id == stay && b.GuestId == g.Id));
            var eraser = new GuestDataEraser(db, world.Storage, NullLogger<GuestDataEraser>.Instance);
            var outcome = await eraser.AnonymizeAsync(guest, Now.UtcDateTime, Ct);
            await db.SaveChangesAsync();
            Assert.Equal(1, outcome.FilesDeleted);
        }

        var rows = await world.EvidenceRowsAsync();
        Assert.Equal((Now.UtcDateTime, IdentityEvidenceDeletionReasons.Erased), (rows.Single(row => row.Id == ofTheGuest.Id).DeletedAt, rows.Single(row => row.Id == ofTheGuest.Id).DeletionReason));
        Assert.False(world.Storage.Objects.ContainsKey(ofTheGuest.StorageKey));
        Assert.Null(rows.Single(row => row.Id == ofAnother.Id).DeletedAt);
        Assert.True(world.Storage.Objects.ContainsKey(ofAnother.StorageKey));
    }

    [Fact]
    public async Task AdminBiometricsStatus_ReportsOverdueEvidence()
    {
        // Proof (data, verifiable deletion, AC5): the status of the platform administrators counts the evidence past its time and the pictures of documents nobody uses any more,
        // across the organizations, and the last run; after the purge nothing overdue remains.
        await using var world = new RetentionWorld();
        var stayOfA = await world.StayAsync(world.OrgA);
        var stayOfB = await world.StayAsync(world.OrgB);
        await world.EvidenceAsync(world.OrgA, stayOfA, expiresAt: Now.UtcDateTime.AddMinutes(-1));
        await world.EvidenceAsync(world.OrgB, stayOfB, expiresAt: Now.UtcDateTime.AddMinutes(-1));
        await world.EvidenceAsync(world.OrgB, stayOfB, expiresAt: Now.UtcDateTime.AddHours(1));
        await world.SessionAsync(world.OrgA, stayOfA, openedAt: Now.UtcDateTime.AddHours(-30));
        await world.DocumentImageAsync(world.OrgA, stayOfA);

        var before = await AdminStatusAsync(world);

        Assert.Equal((2, 1), (before.OverdueEvidence, before.OverdueDocumentImages));
        Assert.Null(before.LastRunAt);
        Assert.Null(before.LastRunCounts);

        await world.Service().PurgeAsync(Now.UtcDateTime, Ct);
        var after = await AdminStatusAsync(world);

        Assert.Equal((0, 0), (after.OverdueEvidence, after.OverdueDocumentImages));
        Assert.Equal(Now.UtcDateTime, after.LastRunAt);
        Assert.Equal((2, 1, 1, 0), (after.LastRunCounts!.ExpiredEvidence, after.LastRunCounts.AbandonedSessions, after.LastRunCounts.AbandonedDocumentImages, after.LastRunCounts.FailedOrgs));

        // A closed shape: counts and a time, nothing of a guest, a key or a picture.
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(after, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(
            ["lastRunAt", "lastRunCounts", "overdueDocumentImages", "overdueEvidence"],
            json.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.All(json.RootElement.GetProperty("lastRunCounts").EnumerateObject(), property => Assert.Equal(JsonValueKind.Number, property.Value.ValueKind));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void IdentityEvidencePurgeJob_IsScheduledHourly_WhateverTheFlagsSay_AndCarriesNoData(bool selfHosted, bool evidence)
    {
        // Review of S015, point 2: a flag switched off must never leave an evidence with no expiry, so the job is always there (as GuestDocumentPurgeJob and
        // SwissComplianceRetentionJob are); what a run does with both flags off is proved by IdentityRetention_WithBothFlagsOff_DeletesOnlyTheEvidencePastItsTime.
        var flags = new TestFeatureFlags();
        flags.Set(FeatureFlags.CheckInIdentitySelfHosted, selfHosted);
        flags.Set(FeatureFlags.CheckInIdentityReviewEvidence, evidence);
        var registered = new Dictionary<string, (Job Job, string Cron, RecurringJobOptions Options)>();
        var manager = new Mock<IRecurringJobManager>();
        manager
            .Setup(m => m.AddOrUpdate(It.IsAny<string>(), It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<RecurringJobOptions>()))
            .Callback<string, Job, string, RecurringJobOptions>((id, job, cron, options) => registered[id] = (job, cron, options));

        RecurringJobsRegistration.Configure(manager.Object, flags);

        Assert.Equal("identity-evidence-purge", IdentityEvidencePurgeJob.RecurringJobId);
        Assert.True(registered.ContainsKey(IdentityEvidencePurgeJob.RecurringJobId));
        manager.Verify(m => m.RemoveIfExists(IdentityEvidencePurgeJob.RecurringJobId), Times.Never());

        var (job, cron, options) = registered[IdentityEvidencePurgeJob.RecurringJobId];
        Assert.Equal(("40 * * * *", TimeZoneInfo.Utc), (cron, options.TimeZone));
        Assert.Equal((typeof(IdentityEvidencePurgeJob), nameof(IdentityEvidencePurgeJob.ExecuteAsync)), (job.Type, job.Method.Name));

        // No data in the arguments: the token Hangfire gives, nothing else.
        Assert.Equal([typeof(CancellationToken)], job.Method.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.All(job.Args, argument => Assert.True(argument is null or CancellationToken));

        var concurrency = typeof(IdentityEvidencePurgeJob).GetCustomAttribute<DisableConcurrentExecutionAttribute>();
        Assert.NotNull(concurrency);
        Assert.InRange(concurrency.TimeoutSec, 1, (int)HangfireStorageSettings.DefaultDistributedLockTimeout.TotalSeconds);
    }

    [Fact]
    public async Task IdentityEvidencePurgeJob_RunsTheRetentionAtTheTimeOfTheClock()
    {
        await using var world = new RetentionWorld();
        var stay = await world.StayAsync(world.OrgA);
        var expired = await world.EvidenceAsync(world.OrgA, stay, expiresAt: Now.UtcDateTime.AddMinutes(-1));

        await new IdentityEvidencePurgeJob(world.Service(), world.Clock).ExecuteAsync(Ct);

        Assert.False(world.Storage.Objects.ContainsKey(expired.StorageKey));
        Assert.Equal(Now.UtcDateTime, world.RunLog.Last!.RanAt);
    }

    [Fact]
    public void AddReviewServices_RegistersTheStoreTheRetentionItsRunLogAndTheJob_AsTheWholeListOfThePart()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSelfHostedIdentityVerification();

        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services, d => d.ServiceType == typeof(IdentityReviewEvidenceStore)).Lifetime);
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services, d => d.ServiceType == typeof(IdentityRetentionService)).Lifetime);
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, d => d.ServiceType == typeof(IdentityRetentionRunLog)).Lifetime);
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services, d => d.ServiceType == typeof(IdentityEvidencePurgeJob)).Lifetime);
    }

    [Fact]
    public async Task IdentityRetention_EveryQueryOfARun_TranslatesOnNpgsql_AndNamesItsOrg()
    {
        // The SQL the purge becomes on the real provider (nothing is executed, NpgsqlTranslationProbe): InMemory accepts what Npgsql cannot translate, and the job runs where
        // nobody sees a translation error until the hour it fails. The organizations found are visited with queries that name the org; the job has no tenant filter.
        var org = Guid.NewGuid();
        var statements = new List<string>();
        var db = NpgsqlTranslationProbe.NewContext(statements, count: 0, rows: sql =>
        {
            if (Regex.IsMatch(sql, "SELECT DISTINCT \\w+\\.\"OrgId\"", RegexOptions.IgnoreCase) && sql.Contains("\"IdentityReviewEvidences\"", StringComparison.Ordinal))
            {
                var table = new DataTable();
                table.Columns.Add("OrgId", typeof(Guid));
                table.Rows.Add(org);
                return table;
            }

            return null;
        });
        var flags = new TestFeatureFlags(FeatureFlags.CheckInIdentitySelfHosted);
        var service = new IdentityRetentionService(
            db,
            new FailingMemoryStorage(),
            flags,
            Options.Create(new IdentityVerificationOptions()),
            new IdentityRetentionRunLog(),
            new FakeTimeProvider(Now),
            NullLogger<IdentityRetentionService>.Instance);

        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => service.PurgeAsync(Now.UtcDateTime, Ct));
        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => service.StatusAsync(Ct));

        foreach (var table in new[] { "IdentityReviewEvidences", "GuestCheckInSessions", "DocumentImages", "BiometricVerificationLogs" })
        {
            // The statements of the organization found: not the search of the organizations (DISTINCT) and not the counts of the status, which span every organization on purpose.
            var ofTheOrg = statements
                .Where(sql => Root(sql) == table
                              && !sql.Contains("DISTINCT", StringComparison.OrdinalIgnoreCase)
                              && !sql.Contains("count(", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.True(ofTheOrg.Count > 0, $"no query of a run reads {table}");
            Assert.All(ofTheOrg, sql => Assert.Matches("\"OrgId\" = @", sql));
        }

        Assert.DoesNotContain(statements, sql => sql.Contains("@ef_filter", StringComparison.Ordinal));
    }

    private static async Task<IdentityRetentionStatusDto> AdminStatusAsync(RetentionWorld world)
    {
        var reader = new Mock<IBiometricModelStatusReader>();
        reader.Setup(r => r.Read()).Returns(new BiometricModelsStatus(false, "disabled", null, []));
        var controller = new AdminBiometricsController(reader.Object, Mock.Of<IBiometricSettingsService>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "auth0|staff")], "test")) },
            },
        };

        var ok = Assert.IsType<OkObjectResult>((await controller.GetStatus(world.Service(), Ct)).Result);
        return Assert.IsType<BiometricsStatusDto>(ok.Value).Retention!;
    }

    private static string Root(string sql) => Regex.Match(sql, "FROM \"(\\w+)\"").Groups[1].Value;

    private static bool HasChallenge(string? json)
    {
        if (json is null)
            return false;

        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("presence", out var presence) && presence.ValueKind == JsonValueKind.Object;
    }

    /// <summary>Two orgs (A and B) on a database in memory, a bucket in memory, the flags of the self-hosted verification on, the clock at <see cref="Now"/>.</summary>
    private sealed class RetentionWorld : IAsyncDisposable
    {
        private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

        private readonly DbContextOptions<AppDbContext> _options =
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"identity-retention-{Guid.NewGuid()}").Options;

        public Guid OrgA { get; } = Guid.NewGuid();

        public Guid OrgB { get; } = Guid.NewGuid();

        public RetentionStorage Storage { get; } = new();

        public FakeTimeProvider Clock { get; } = new(Now);

        public TestFeatureFlags Flags { get; } =
            new(FeatureFlags.CheckInIdentityVerification, FeatureFlags.CheckInIdentitySelfHosted, FeatureFlags.CheckInIdentityReviewEvidence);

        public IdentityRetentionRunLog RunLog { get; } = new();

        public AppDbContext NewContext() => new(_options);

        /// <summary>The service on a context of its own, as the job has it.</summary>
        public IdentityRetentionService Service(Microsoft.Extensions.Logging.ILogger<IdentityRetentionService>? logger = null, IdentityVerificationOptions? options = null) =>
            new(NewContext(), Storage, Flags, Options.Create(options ?? new IdentityVerificationOptions()), RunLog, Clock, logger ?? NullLogger<IdentityRetentionService>.Instance);

        /// <summary>A stay of a guest of <paramref name="orgId"/>, at a property of it; check-out in three days unless said.</summary>
        public async Task<Guid> StayAsync(Guid orgId, DateTime? checkOut = null)
        {
            await using var db = NewContext();
            var property = new Property
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                OwnerId = "auth0|owner",
                Name = "Casa Test",
                Address = "Via Test 1",
                City = "Roma",
                PostalCode = "00100",
                Country = "IT",
                NightlyRate = 100m,
                MaxGuests = 2,
            };
            var guest = new Guest { Id = Guid.NewGuid(), OrgId = orgId, FirstName = "Anna", LastName = "Bianchi", Email = $"anna.{Guid.NewGuid():N}@example.com" };
            var departure = checkOut ?? new DateTime(2026, 11, 30, 0, 0, 0, DateTimeKind.Utc);
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                PropertyId = property.Id,
                GuestId = guest.Id,
                CheckInDate = departure.AddDays(-2),
                CheckOutDate = departure,
                Status = BookingStatus.Confirmed,
                Source = BookingSource.Direct,
                NumberOfGuests = 1,
                TotalPrice = 200m,
            };
            db.Properties.Add(property);
            db.Guests.Add(guest);
            db.Bookings.Add(booking);
            await db.SaveChangesAsync();
            return booking.Id;
        }

        public async Task<IdentityReviewEvidence> EvidenceAsync(Guid orgId, Guid bookingId, DateTime expiresAt, Guid? sessionId = null)
        {
            await using var db = NewContext();
            var id = Guid.NewGuid();
            var row = new IdentityReviewEvidence
            {
                Id = id,
                OrgId = orgId,
                BookingId = bookingId,
                CheckInSessionId = sessionId ?? Guid.NewGuid(),
                Kind = IdentityEvidenceKind.Frame,
                StorageKey = IdentityReviewEvidenceStore.StorageKeyOf(orgId, bookingId, id),
                ContentType = IdentityReviewEvidenceStore.JpegContentType,
                Sha256 = new string('a', 64),
                CreatedAt = expiresAt.AddHours(-48),
                ExpiresAt = expiresAt,
            };
            db.IdentityReviewEvidences.Add(row);
            await db.SaveChangesAsync();
            Storage.Objects[row.StorageKey] = [1, 2, 3];
            return row;
        }

        public async Task<GuestCheckInSession> SessionAsync(
            Guid orgId,
            Guid bookingId,
            DateTime? openedAt,
            DateTime? expiresAt = null,
            DateTime? closedAt = null,
            bool withChallenge = false)
        {
            await using var db = NewContext();
            var challenge = withChallenge
                ? $",\"presence\":{{\"id\":\"{Guid.NewGuid()}\",\"steps\":[\"turn_left\"],\"issuedAt\":\"{Now.AddMinutes(-1):O}\",\"expiresAt\":\"{Now.AddMinutes(1):O}\",\"attempt\":1}}"
                : string.Empty;
            var session = new GuestCheckInSession
            {
                BookingId = bookingId,
                OrgId = orgId,
                TokenHash = Guid.NewGuid().ToString("N"),
                Status = GuestCheckInSessionStatus.InCompilazione,
                ExpiresAt = expiresAt ?? Now.UtcDateTime.AddDays(6),
                IdentityOpenedAt = openedAt,
                IdentityClosedAt = closedAt,
                IdentityOutcomeCode = closedAt is null ? null : IdentityOutcomeCodes.Review,
                DocumentCheckJson = "{\"check\":{\"mrzRead\":true,\"checkDigitsValid\":true,\"holderIsMinor\":false},\"photoSha256\":\"AB\",\"checkedAt\":\"2026-11-26T09:00:00Z\"" + challenge + "}",
                CreatedAt = (openedAt ?? Now.UtcDateTime).AddMinutes(-5),
                UpdatedAt = openedAt ?? Now.UtcDateTime,
            };
            db.GuestCheckInSessions.Add(session);
            await db.SaveChangesAsync();
            return session;
        }

        /// <summary>A session of the legacy path that was handed to an operator at <paramref name="handedOffAt"/> and is in the given state.</summary>
        public async Task<GuestCheckInSession> HandedOffSessionAsync(
            Guid orgId,
            Guid bookingId,
            DateTime handedOffAt,
            BiometricOutcome outcome,
            GuestCheckInSessionStatus status)
        {
            await using var db = NewContext();
            var session = new GuestCheckInSession
            {
                BookingId = bookingId,
                OrgId = orgId,
                TokenHash = Guid.NewGuid().ToString("N"),
                Status = status,
                ExpiresAt = handedOffAt.AddDays(7),
                BiometricOutcome = outcome,
                BiometricAttempts = 1,
                OperatorRequestedAt = handedOffAt,
                CreatedAt = handedOffAt.AddHours(-1),
                UpdatedAt = handedOffAt,
            };
            db.GuestCheckInSessions.Add(session);
            await db.SaveChangesAsync();
            return session;
        }

        /// <summary>
        /// The stays of <paramref name="orgId"/> that wait for an operator: what the queue of the operators lists, which must be what the one definition of <c>Unanswered</c> selects.
        /// </summary>
        public async Task<List<Guid>> WaitingForAnOperatorAsync(Guid orgId)
        {
            await using var db = NewContext();
            var links = new PublicSiteLinks(Options.Create(new PublicSiteOptions { PublicSiteBaseUrl = "https://site.example.test" }));
            var queue = new VerificationQueueService(
                db, Mock.Of<IEmailQueue>(), links, Mock.Of<IGuestCheckInService>(), NullLogger<VerificationQueueService>.Instance, timeProvider: Clock);
            var listed = (await queue.GetPendingVerificationsAsync("auth0|owner", new HostScope(orgId))).Select(item => item.BookingId).Order().ToList();
            var unanswered = (await db.GuestCheckInSessions.IgnoreQueryFilters().Unanswered(db).Select(session => session.BookingId).Distinct().ToListAsync()).Order().ToList();
            Assert.Equal(unanswered, listed);
            return listed;
        }

        public async Task<DocumentImage> DocumentImageAsync(Guid orgId, Guid bookingId)
        {
            await using var db = NewContext();
            var guestId = await db.Bookings.IgnoreQueryFilters().Where(b => b.Id == bookingId).Select(b => b.GuestId).SingleAsync();
            var image = new DocumentImage
            {
                OrgId = orgId,
                GuestId = guestId,
                BookingId = bookingId,
                FrontImageUrl = StorageKeys.GuestDocument(orgId, guestId, $"{Guid.NewGuid():N}.bin"),
                UploadedAt = Now.UtcDateTime.AddHours(-2),
                ProcessedAt = Now.UtcDateTime.AddHours(-2),
            };
            db.DocumentImages.Add(image);
            await db.SaveChangesAsync();
            Storage.Objects[image.FrontImageUrl] = [4, 5, 6];
            return image;
        }

        public async Task<BiometricVerificationLog> LogAsync(Guid orgId, Guid bookingId, VerificationMethod method, BiometricOutcome outcome, DateTime createdAt)
        {
            await using var db = NewContext();
            var guestId = await db.Bookings.IgnoreQueryFilters().Where(b => b.Id == bookingId).Select(b => b.GuestId).SingleAsync();
            var log = new BiometricVerificationLog
            {
                OrgId = orgId,
                BookingId = bookingId,
                GuestId = guestId,
                Outcome = outcome,
                Method = method,
                Attempts = 1,
                ModelName = "SFace",
                ModelVersion = "2021dec",
                CreatedAt = createdAt,
            };
            db.BiometricVerificationLogs.Add(log);
            await db.SaveChangesAsync();
            return log;
        }

        public async Task<List<IdentityReviewEvidence>> EvidenceRowsAsync()
        {
            await using var read = NewContext();
            return await read.IdentityReviewEvidences.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A private bucket in memory that can refuse the deletion of one object (a storage down for that key).</summary>
    private sealed class RetentionStorage : IFileStorage
    {
        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

        public string? FailDeleteOf { get; set; }

        public Task PutAsync(StorageBucket bucket, string key, Stream content, string contentType, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The retention never writes.");

        public Task<Stream?> OpenReadAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream?>(Objects.TryGetValue(key, out var bytes) ? new MemoryStream(bytes, writable: false) : null);

        public Task<bool> ExistsAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default) => Task.FromResult(Objects.ContainsKey(key));

        public Task DeleteAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default)
        {
            if (string.Equals(key, FailDeleteOf, StringComparison.Ordinal))
                return Task.FromException(new IOException("The bucket refused the face of Anna Bianchi."));

            Objects.Remove(key);
            return Task.CompletedTask;
        }

        public string GetPublicUrl(string key) => throw new NotSupportedException("The private bucket has no public URL.");

        public string? TryGetPublicKey(string url) => null;

        public Task<Uri?> GetSignedReadUrlAsync(string key, TimeSpan lifetime, string? downloadFileName, CancellationToken cancellationToken = default) =>
            Task.FromResult<Uri?>(null);
    }
}
