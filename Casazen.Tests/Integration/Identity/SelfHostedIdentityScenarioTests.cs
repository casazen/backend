using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Identity;
using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Identity;
using Moq;
using Xunit;

namespace Casazen.Tests.Integration.Identity;

public sealed class SelfHostedIdentityScenarioTests(CasazenWebApplicationFactory factory) : IClassFixture<CasazenWebApplicationFactory>
{
    private const string Operator = IdentityVerificationGuestResults.Operator;

    [Fact]
    public async Task S1_Observation_APerfectSequenceGoesToAPerson_AndTheApprovalOfThePersonDeletesTheEvidence()
    {
        await SelfHostedIdentityScripts.MainPathAsync(factory);
    }

    [Fact]
    public async Task S2_AQualifiedAutomaticSegment_WithTheGlobalFlagOn_Approves()
    {
        await using var scenario = SelfHostedIdentityScenario.Start(factory);
        var stay = await scenario.SeedStayAsync();
        await scenario.QualifySegmentAsync(stay);
        var guest = scenario.Guest();
        var owner = scenario.Operator(stay.OwnerId);
        await scenario.OpenAndConsentAsync(guest, stay);
        await scenario.CompleteDocumentAsync(guest, stay);

        var attempt = await scenario.AttemptAsync(guest, stay, scenario.Genuine, liveness: 0.99, similarity: 0.99);

        Assert.Equal(HttpStatusCode.OK, attempt.Status);
        await scenario.AssertApprovedAsync(stay, attempt);
        Assert.Equal(SelfHostedIdentityScenario.LockCode, attempt.LockCode);
        Assert.Empty(attempt.Guidance);
        var state = attempt.Body.GetProperty("state");
        Assert.Equal(IdentityVerificationSteps.Done, state.GetProperty("step").GetString());
        Assert.Equal(IdentityVerificationGuestResults.Ok, state.GetProperty("outcome").GetString());

        var row = Assert.Single(await scenario.LogRowsAsync(stay.BookingId));
        Assert.Equal((BiometricOutcome.Accept, BiometricOutcome.Accept, IdentityDecisionMode.Automatic), (row.Outcome, row.ShadowOutcome, row.DecisionMode));
        var saved = await scenario.SessionAsync(stay.SessionId);
        Assert.Equal((BiometricOutcome.Accept, IdentityOutcomeCodes.AutoApproved), (saved.BiometricOutcome, saved.IdentityOutcomeCode));
        Assert.Null(saved.OperatorRequestedAt);

        var announced = Assert.Single(await scenario.EventsAsync(stay.BookingId, DomainEventType.IdVerificationCompleted));
        Assert.Equal(stay.OrgId, announced.OrgId);
        scenario.AccessCodes.Verify(
            codes => codes.ReleaseToGuestAsync(stay.BookingId, AccessCodeReleaseChannels.CheckInScreen, It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.DoesNotContain(await scenario.DocumentImagesAsync(stay.BookingId), image => image.DeletedAt is null);
        Assert.Empty(await scenario.OpenTodosAsync(stay.BookingId));
        Assert.DoesNotContain(scenario.Push.Sent, push => push.Payload.Type == PushTypes.VerificationRequired);
        Assert.Empty(await scenario.EvidenceRowsAsync(stay.BookingId));
        Assert.DoesNotContain(await SelfHostedIdentityScenario.PendingAsync(owner), item => BookingOf(item) == stay.BookingId);

        var identity = await SelfHostedIdentityScenario.IdentityMetricsAsync(owner);
        Assert.Equal(1, identity.GetProperty("opened").GetInt32());
        Assert.Equal(1, identity.GetProperty("outcomes").GetProperty("autoApproved").GetInt32());
        Assert.Equal(1.0, identity.GetProperty("automationRate").GetProperty("value").GetDouble());
        var csv = Assert.Single(await SelfHostedIdentityScenario.PilotRowsAsync(owner));
        Assert.Equal(
            (IdentityOutcomeCodes.AutoApproved, "automatic", "approve", string.Empty),
            (csv["outcome_code"], csv["decision_mode"], csv["shadow_outcome"], csv["human_outcome"]));
    }

    [Fact]
    public async Task S2_AQualifiedAutomaticSegment_WithTheGlobalFlagOff_NeverApproves()
    {
        await using var scenario = SelfHostedIdentityScenario.Start(factory, new ScenarioOptions { AutoApproval = false });
        var stay = await scenario.SeedStayAsync();
        await scenario.QualifySegmentAsync(stay, approvalAllowed: false);
        var guest = scenario.Guest();
        await scenario.OpenAndConsentAsync(guest, stay);
        await scenario.CompleteDocumentAsync(guest, stay);

        var attempt = await scenario.AttemptAsync(guest, stay, scenario.Genuine, liveness: 0.99, similarity: 0.99);

        Assert.Equal(Operator, attempt.Result);
        Assert.Null(attempt.LockCode);
        var row = Assert.Single(await scenario.LogRowsAsync(stay.BookingId));
        SelfHostedIdentityScenario.AssertShadowApproves(row);
        Assert.Equal((BiometricOutcome.Escalate, BiometricOutcome.Accept, IdentityDecisionMode.Observation), (row.Outcome, row.ShadowOutcome, row.DecisionMode));
        Assert.True(HasReason(row, VerificationReasons.Checks.ObservationMode, IdentitySegmentReasonCodes.AutoApprovalDisabled));
        await AssertNothingWasApprovedAsync(scenario, stay);
    }

    [Fact]
    public async Task S3_AChangeOfTheWeightsOrOfThePolicy_TakesTheSegmentBackToObservation()
    {
        await using var scenario = SelfHostedIdentityScenario.Start(factory);
        var first = await scenario.SeedStayAsync();
        await scenario.QualifySegmentAsync(first);
        var guest = scenario.Guest();
        await scenario.OpenAndConsentAsync(guest, first);
        await scenario.CompleteDocumentAsync(guest, first);
        var approved = await scenario.AttemptAsync(guest, first, scenario.Genuine, liveness: 0.99, similarity: 0.99);
        await scenario.AssertApprovedAsync(first, approved);

        scenario.Store.ChangeWeights(BiometricModelRoles.FaceRecognition);
        var (weights, weightsGuest) = await scenario.ReadyStayAsync(first.OwnerId);
        Assert.Equal(first.OrgId, weights.OrgId);
        var afterWeights = await scenario.AttemptAsync(weightsGuest, weights, scenario.Genuine, liveness: 0.99, similarity: 0.99);
        Assert.Equal(Operator, afterWeights.Result);
        await AssertObservationBecauseAsync(scenario, weights, IdentitySegmentReasonCodes.QualificationMismatch);

        // A new policy is another host over the same database: its own file, so its own hash.
        await using var other = SelfHostedIdentityScenario.Start(factory, new ScenarioOptions { PolicyVersion = "identity-policy-scenario-2026.11-2" });
        Assert.NotEqual(scenario.PolicyHash, other.PolicyHash);
        var (policy, policyGuest) = await other.ReadyStayAsync(first.OwnerId);
        var afterPolicy = await other.AttemptAsync(policyGuest, policy, other.Genuine, liveness: 0.99, similarity: 0.99);
        Assert.Equal(Operator, afterPolicy.Result);
        await AssertObservationBecauseAsync(other, policy, IdentitySegmentReasonCodes.QualificationMismatch);

        await other.QualifySegmentAsync(policy);
        var (again, againGuest) = await other.ReadyStayAsync(first.OwnerId);
        var restored = await other.AttemptAsync(againGuest, again, other.Genuine, liveness: 0.99, similarity: 0.99);
        await other.AssertApprovedAsync(again, restored);
    }

    [Fact]
    public async Task S4_AChallengeNotFollowed_IsGuidedOnce_ThenReviewed()
    {
        await using var scenario = SelfHostedIdentityScenario.Start(factory);
        var (stay, guest) = await scenario.ReadyStayAsync();
        Func<IReadOnlyList<string>, ScriptedSequence> notFollowed = steps =>
            SequenceTestFrames.Genuine(scenario.Policy, steps, performed: [steps[1], steps[0], .. steps.Skip(2)]);

        var first = await scenario.AttemptAsync(guest, stay, notFollowed, liveness: 0.99, similarity: 0.99);

        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal(IdentityVerificationGuestResults.Retry, first.Result);
        Assert.Contains(IdentityGuidanceCodes.PresenceChallengeNotFollowed, first.Guidance);
        Assert.True(first.Body.GetProperty("guidedRetryAvailable").GetBoolean());
        Assert.False(first.Body.GetProperty("state").GetProperty("guidedRetryAvailable").GetBoolean());
        Assert.Null(first.LockCode);
        Assert.Empty(await scenario.LogRowsAsync(stay.BookingId));
        var afterFirst = await scenario.SessionAsync(stay.SessionId);
        Assert.Equal((1, 0, (DateTime?)null), (afterFirst.IdentityGuidedRetries, afterFirst.BiometricAttempts, afterFirst.IdentityClosedAt));
        Assert.Empty(await scenario.OpenTodosAsync(stay.BookingId));

        var second = await scenario.AttemptAsync(guest, stay, notFollowed, liveness: 0.99, similarity: 0.99);

        Assert.Equal(HttpStatusCode.OK, second.Status);
        Assert.Equal(Operator, second.Result);
        Assert.False(second.Body.GetProperty("guidedRetryAvailable").GetBoolean());
        var row = Assert.Single(await scenario.LogRowsAsync(stay.BookingId));
        Assert.Equal(BiometricOutcome.Escalate, row.Outcome);
        Assert.Equal(1, row.Attempts);
        Assert.True(HasReason(row, VerificationReasons.Checks.PresenceChallenge, VerificationReasons.Results.Failed));
        var saved = await scenario.SessionAsync(stay.SessionId);
        Assert.Equal((IdentityOutcomeCodes.Review, 1, 1), (saved.IdentityOutcomeCode, saved.IdentityGuidedRetries, saved.BiometricAttempts));
        Assert.Single(await scenario.OpenTodosAsync(stay.BookingId));
        await AssertNothingWasApprovedAsync(scenario, stay);

        await SelfHostedIdentityScenario.AssertProblemAsync(
            await guest.PostAsync($"/api/public/checkin/{stay.Token}/presence-challenge", null),
            HttpStatusCode.Conflict,
            IdentityApiErrors.IdentityAlreadyClosed);
        var late = await scenario.SendPresenceAsync(guest, stay, second.ChallengeToken, second.Sent, SelfHostedIdentityScenario.NewKey());
        Assert.Equal(HttpStatusCode.Conflict, late.Status);
        Assert.Single(await scenario.LogRowsAsync(stay.BookingId));
    }

    [Fact]
    public async Task S5_TheSameRequestTwice_IsOneAttempt()
    {
        await SelfHostedIdentityScripts.DuplicateAsync(factory, atOnce: false);
    }

    [Fact]
    public async Task S5_AnApprovalSentTwice_IsOneApproval()
    {
        await SelfHostedIdentityScripts.ApprovalTwiceAsync(factory, atOnce: false);
    }

    [Fact]
    public async Task S6_AModelFault_GoesToAPerson_NeverApproves_AndTheAssistedPathStaysOpen()
    {
        // 500 ms is the least the section allows; a stalled model would also hold up the evidence, which is not the point here.
        await using var scenario = SelfHostedIdentityScenario.Start(factory, new ScenarioOptions { SequenceTimeoutMs = 500, ReviewEvidence = false });
        var faults = new (string Name, Action<ScriptedModels> Arrange, Action<ScriptedModels> Repair)[]
        {
            ("the detector throws", models => models.DetectorFailure = new InvalidOperationException("the face model crashed"), models => models.DetectorFailure = null),
            ("the detector never answers", models => models.DetectorStalls = true, models => models.DetectorStalls = false),
        };

        foreach (var (name, arrange, repair) in faults)
        {
            var (stay, guest) = await scenario.ReadyStayAsync();
            arrange(scenario.Models);

            var attempt = await scenario.AttemptAsync(guest, stay, scenario.Genuine, liveness: 0.99, similarity: 0.99);
            repair(scenario.Models);

            Assert.True(attempt.Status == HttpStatusCode.OK, name);
            Assert.True(attempt.Result == Operator, name);
            Assert.Null(attempt.LockCode);
            var row = Assert.Single(await scenario.LogRowsAsync(stay.BookingId));
            Assert.True(row.Outcome == BiometricOutcome.Escalate, name);
            Assert.True(
                HasReason(row, VerificationReasons.Checks.Processing, VerificationReasons.Results.Error) || HasReason(row, VerificationReasons.Checks.Processing, VerificationReasons.Results.Timeout),
                name);
            var saved = await scenario.SessionAsync(stay.SessionId);
            Assert.True(saved.IdentityOutcomeCode == IdentityOutcomeCodes.TechnicalError, name);
            Assert.NotNull(saved.IdentityClosedAt);
            Assert.Single(await scenario.OpenTodosAsync(stay.BookingId));
            await AssertNothingWasApprovedAsync(scenario, stay);

            var request = await SelfHostedIdentityScenario.ReadOkAsync(await guest.PostAsync($"/api/public/checkin/{stay.Token}/request-operator", null));
            Assert.Equal(Operator, request.GetProperty("result").GetString());
            Assert.True(request.GetProperty("operatorPathOpen").GetBoolean());
            Assert.True((await scenario.SessionAsync(stay.SessionId)).IdentityOutcomeCode == IdentityOutcomeCodes.TechnicalError, name);
            Assert.Single(await scenario.OpenTodosAsync(stay.BookingId));
            Assert.Single(scenario.Push.Sent, push => push.Payload.Type == PushTypes.VerificationRequired && push.Payload.BookingId == stay.BookingId);
        }
    }

    [Fact]
    public async Task S7_TheOperatorOfAnotherOrg_SeesNothingOfTheReview()
    {
        await using var scenario = SelfHostedIdentityScenario.Start(factory);
        var (stayA, guestA) = await scenario.ReadyStayAsync();
        var review = await scenario.AttemptAsync(guestA, stayA, scenario.Genuine, liveness: 0.99, similarity: 0.99);
        Assert.Equal(Operator, review.Result);
        var evidenceA = await scenario.EvidenceRowsAsync(stayA.BookingId);
        Assert.NotEmpty(evidenceA);
        var (stayB, _) = await scenario.ReadyStayAsync();
        Assert.NotEqual(stayA.OrgId, stayB.OrgId);
        var ownerA = scenario.Operator(stayA.OwnerId);
        var ownerB = scenario.Operator(stayB.OwnerId);

        await SelfHostedIdentityScenario.AssertProblemAsync(
            await ownerB.GetAsync($"/api/verification/{stayA.BookingId}/evidence"), HttpStatusCode.NotFound, IdentityApiErrors.IdentityEvidenceNotFound);
        foreach (var item in evidenceA)
        {
            await SelfHostedIdentityScenario.AssertProblemAsync(
                await ownerB.GetAsync($"/api/verification/{stayA.BookingId}/evidence/{item.Id}"), HttpStatusCode.NotFound, IdentityApiErrors.IdentityEvidenceNotFound);
        }

        await SelfHostedIdentityScenario.AssertProblemAsync(
            await ownerB.GetAsync($"/api/verification/{stayA.BookingId}/evidence/{Guid.NewGuid()}"), HttpStatusCode.NotFound, IdentityApiErrors.IdentityEvidenceNotFound);

        Assert.DoesNotContain(await SelfHostedIdentityScenario.PendingAsync(ownerB), item => BookingOf(item) == stayA.BookingId);
        await SelfHostedIdentityScenario.AssertProblemAsync(
            await ownerB.PostAsJsonAsync($"/api/verification/{stayA.BookingId}/verify", new { outcome = true, note = "prova" }), HttpStatusCode.NotFound, "no_pending_verification");
        await SelfHostedIdentityScenario.AssertProblemAsync(
            await ownerB.PostAsJsonAsync($"/api/verification/{stayA.BookingId}/reject", new { reason = "prova" }), HttpStatusCode.NotFound, "no_pending_verification");

        var identityB = await SelfHostedIdentityScenario.IdentityMetricsAsync(ownerB);
        Assert.Equal(1, identityB.GetProperty("opened").GetInt32());
        Assert.Equal(1, identityB.GetProperty("outcomes").GetProperty("inProgress").GetInt32());
        Assert.Equal(0, identityB.GetProperty("outcomes").GetProperty("review").GetInt32());
        Assert.Equal(0.0, identityB.GetProperty("shadowApprovalRate").GetProperty("value").GetDouble());
        var csvB = Assert.Single(await SelfHostedIdentityScenario.PilotRowsAsync(ownerB));
        Assert.Equal(IdentityPilotExport.AttemptRef(stayB.SessionId, stayB.OrgId), csvB["attempt_ref"]);
        Assert.Equal((stayB.PropertyId.ToString("D"), "in_progress"), (csvB["property_id"], csvB["outcome_code"]));
        Assert.DoesNotContain(IdentityPilotExport.AttemptRef(stayA.SessionId, stayA.OrgId), string.Join(',', csvB.Values), StringComparison.Ordinal);
        Assert.DoesNotContain(stayA.PropertyId.ToString("D"), string.Join(',', csvB.Values), StringComparison.Ordinal);

        Assert.Single(await SelfHostedIdentityScenario.PendingAsync(ownerA), item => BookingOf(item) == stayA.BookingId);
        Assert.All(await scenario.EvidenceRowsAsync(stayA.BookingId), item => Assert.Null(item.DeletedAt));
        Assert.Single(await scenario.OpenTodosAsync(stayA.BookingId));
        var identityA = await SelfHostedIdentityScenario.IdentityMetricsAsync(ownerA);
        Assert.Equal((1, 1), (identityA.GetProperty("opened").GetInt32(), identityA.GetProperty("outcomes").GetProperty("review").GetInt32()));
        var csvA = Assert.Single(await SelfHostedIdentityScenario.PilotRowsAsync(ownerA));
        Assert.Equal(IdentityPilotExport.AttemptRef(stayA.SessionId, stayA.OrgId), csvA["attempt_ref"]);
    }

    [Fact]
    public async Task S7_TheProofOfAnotherOrgsSession_IsNoChallenge()
    {
        await using var scenario = SelfHostedIdentityScenario.Start(factory);
        var (stayA, guestA) = await scenario.ReadyStayAsync();
        var (stayB, guestB) = await scenario.ReadyStayAsync();
        Assert.NotEqual(stayA.OrgId, stayB.OrgId);
        var proofOfB = (await scenario.ChallengeAsync(guestB, stayB)).GetProperty("challengeToken").GetString()!;
        var (_, sent) = await scenario.PrepareAttemptAsync(guestA, stayA, scenario.Genuine, liveness: 0.99, similarity: 0.99);

        var crossed = await scenario.SendPresenceAsync(guestA, stayA, proofOfB, sent, SelfHostedIdentityScenario.NewKey());

        Assert.Equal(HttpStatusCode.Conflict, crossed.Status);
        Assert.Equal(IdentityApiErrors.PresenceChallengeRequired, crossed.Body.GetProperty("code").GetString());
        foreach (var stay in new[] { stayA, stayB })
        {
            Assert.Empty(await scenario.LogRowsAsync(stay.BookingId));
            var saved = await scenario.SessionAsync(stay.SessionId);
            Assert.Equal((0, 0, (string?)null, (DateTime?)null), (saved.BiometricAttempts, saved.IdentityGuidedRetries, saved.IdentityLastAttemptKey, saved.IdentityClosedAt));
            Assert.Empty(await scenario.OpenTodosAsync(stay.BookingId));
        }
    }

    [Fact]
    public async Task S8_TheReaderOfAPictureIsAudited_AndAReviewerOfAnotherPropertyGets404()
    {
        await using var scenario = SelfHostedIdentityScenario.Start(factory);
        var (stay, guest) = await scenario.ReadyStayAsync();
        var review = await scenario.AttemptAsync(guest, stay, scenario.Genuine, liveness: 0.99, similarity: 0.99);
        Assert.Equal(Operator, review.Result);
        var owner = scenario.Operator(stay.OwnerId);
        var colleagueId = await scenario.SeedColleagueAsync(stay.OrgId);
        var otherProperty = scenario.Operator(colleagueId);

        var items = await SelfHostedIdentityScenario.EvidenceListAsync(owner, stay.BookingId);
        Assert.True(items.Count >= 2);
        Assert.Empty(await scenario.ActivityAsync(stay.OrgId, OrgActivityType.IdentityEvidenceViewed));

        await SelfHostedIdentityScenario.AssertProblemAsync(
            await otherProperty.GetAsync($"/api/verification/{stay.BookingId}/evidence"), HttpStatusCode.NotFound, IdentityApiErrors.IdentityEvidenceNotFound);
        foreach (var item in items)
        {
            await SelfHostedIdentityScenario.AssertProblemAsync(
                await otherProperty.GetAsync($"/api/verification/{stay.BookingId}/evidence/{item.GetProperty("id").GetGuid()}"),
                HttpStatusCode.NotFound,
                IdentityApiErrors.IdentityEvidenceNotFound);
        }

        Assert.DoesNotContain(await SelfHostedIdentityScenario.PendingAsync(otherProperty), item => BookingOf(item) == stay.BookingId);
        await SelfHostedIdentityScenario.AssertProblemAsync(
            await otherProperty.PostAsJsonAsync($"/api/verification/{stay.BookingId}/verify", new { outcome = true, note = "prova" }), HttpStatusCode.NotFound, "no_pending_verification");
        Assert.Empty(await scenario.ActivityAsync(stay.OrgId, OrgActivityType.IdentityEvidenceViewed));

        Assert.Single(await SelfHostedIdentityScenario.PendingAsync(owner), item => BookingOf(item) == stay.BookingId);
        var ownerCounters = await SelfHostedIdentityScenario.IdentityMetricsAsync(owner);
        Assert.Equal((1, 1), (ownerCounters.GetProperty("opened").GetInt32(), ownerCounters.GetProperty("outcomes").GetProperty("review").GetInt32()));
        Assert.Single(await SelfHostedIdentityScenario.PilotRowsAsync(owner));
        var otherCounters = await SelfHostedIdentityScenario.IdentityMetricsAsync(otherProperty);
        Assert.Equal(
            (0, 0, 0),
            (otherCounters.GetProperty("opened").GetInt32(), otherCounters.GetProperty("outcomes").GetProperty("review").GetInt32(), otherCounters.GetProperty("outcomes").GetProperty("inProgress").GetInt32()));
        Assert.Empty(await SelfHostedIdentityScenario.PilotRowsAsync(otherProperty));

        var crop = items.First(item => KindOf(item) == IdentityEvidenceKinds.DocumentPortrait);
        var frame = items.First(item => KindOf(item) == IdentityEvidenceKinds.Frame);
        using (var served = await owner.GetAsync($"/api/verification/{stay.BookingId}/evidence/{crop.GetProperty("id").GetGuid()}"))
        {
            Assert.Equal(HttpStatusCode.OK, served.StatusCode);
            Assert.Equal(IdentityReviewEvidenceStore.PngContentType, served.Content.Headers.ContentType?.MediaType);
            Assert.True(served.Headers.CacheControl is { NoStore: true, Private: true });
            Assert.Equal("nosniff", Assert.Single(served.Headers.GetValues("X-Content-Type-Options")));
            Assert.Equal("inline", served.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, (await served.Content.ReadAsByteArrayAsync())[..4]);
        }

        using (var served = await owner.GetAsync($"/api/verification/{stay.BookingId}/evidence/{frame.GetProperty("id").GetGuid()}"))
        {
            Assert.Equal(HttpStatusCode.OK, served.StatusCode);
            Assert.Contains(served.Content.Headers.ContentType?.MediaType, new[] { IdentityReviewEvidenceStore.JpegContentType, IdentityReviewEvidenceStore.PngContentType });
            Assert.NotEmpty(await served.Content.ReadAsByteArrayAsync());
        }

        var lines = await scenario.ActivityAsync(stay.OrgId, OrgActivityType.IdentityEvidenceViewed);
        Assert.Equal(2, lines.Count);
        Assert.All(lines, line => Assert.Equal(
            (stay.OrgId, OrgActivityType.IdentityEvidenceViewed, stay.OwnerId, stay.BookingId.ToString(), OrgActivitySubjectType.Booking, OrgActivityArea.ShortRent),
            (line.OrgId, line.Type, line.ActorUserId, line.SubjectId, line.SubjectType, line.Area)));
        Assert.Equal(
            [IdentityEvidenceKinds.DocumentPortrait, IdentityEvidenceKinds.Frame],
            lines.Select(line => JsonDocument.Parse(line.DetailsJson).RootElement.GetProperty("part").GetString()!).Order(StringComparer.Ordinal));
        Assert.DoesNotContain("identity-evidence", string.Concat(lines.Select(line => line.DetailsJson)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task S8_ACollaboratorOfSomeProperties_IsRefusedOnEveryRouteOfTheReview_WhicheverPropertyItWasGiven()
    {
        await using var scenario = SelfHostedIdentityScenario.Start(factory);
        var (stay, guest) = await scenario.ReadyStayAsync();
        var review = await scenario.AttemptAsync(guest, stay, scenario.Genuine, liveness: 0.99, similarity: 0.99);
        Assert.Equal(Operator, review.Result);
        var owner = scenario.Operator(stay.OwnerId);
        var items = await SelfHostedIdentityScenario.EvidenceListAsync(owner, stay.BookingId);
        Assert.NotEmpty(items);
        var picture = items[0].GetProperty("id").GetGuid();

        // The role of a collaborator holds no permission to decide a verification: the permission answers (403) before the reach is asked, so the grants change nothing.
        foreach (var onTheStay in new[] { false, true })
        {
            var collaborator = scenario.Operator(await scenario.SeedSelectedCollaboratorAsync(stay, onTheStay), roles: string.Empty);

            var statuses = new[]
            {
                await StatusOfAsync(collaborator.GetAsync($"/api/verification/{stay.BookingId}/evidence")),
                await StatusOfAsync(collaborator.GetAsync($"/api/verification/{stay.BookingId}/evidence/{picture}")),
                await StatusOfAsync(collaborator.GetAsync("/api/verification/pending")),
                await StatusOfAsync(collaborator.PostAsJsonAsync($"/api/verification/{stay.BookingId}/verify", new { outcome = true, note = "prova" })),
                await StatusOfAsync(collaborator.PostAsJsonAsync($"/api/verification/{stay.BookingId}/reject", new { reason = "prova" })),
                await StatusOfAsync(collaborator.GetAsync("/api/verification/metrics")),
                await StatusOfAsync(collaborator.GetAsync("/api/verification/metrics/export.csv")),
            };

            Assert.All(statuses, status => Assert.Equal(HttpStatusCode.Forbidden, status));
        }

        Assert.Empty(await scenario.ActivityAsync(stay.OrgId, OrgActivityType.IdentityEvidenceViewed));
        Assert.Single(await SelfHostedIdentityScenario.PendingAsync(owner), item => BookingOf(item) == stay.BookingId);
        Assert.All(await scenario.EvidenceRowsAsync(stay.BookingId), item => Assert.Null(item.DeletedAt));
        Assert.Single(await scenario.OpenTodosAsync(stay.BookingId));
    }

    [Fact]
    public async Task S10_TheLegacySelfieRoutes_AreClosedUnderTheGate_AndWorkAsBeforeWithoutIt()
    {
        await using (var gated = SelfHostedIdentityScenario.Start(factory))
        {
            var (stay, guest) = await gated.ReadyStayAsync();

            await SelfHostedIdentityScenario.AssertProblemAsync(
                await guest.PostAsync($"/api/public/checkin/{stay.Token}/selfie-challenge", null), HttpStatusCode.Conflict, IdentityApiErrors.PresenceSequenceRequired);
            using (var form = SelfieForm())
            {
                await SelfHostedIdentityScenario.AssertProblemAsync(
                    await guest.PostAsync($"/api/public/checkin/{stay.Token}/selfie", form), HttpStatusCode.Conflict, IdentityApiErrors.PresenceSequenceRequired);
            }

            var saved = await gated.SessionAsync(stay.SessionId);
            Assert.Equal((0, (DateTime?)null, (BiometricOutcome?)null), (saved.BiometricAttempts, saved.IdentityClosedAt, saved.BiometricOutcome));
            Assert.Empty(await gated.LogRowsAsync(stay.BookingId));
        }

        await using var legacy = SelfHostedIdentityScenario.Start(factory, new ScenarioOptions { SelfHosted = false });
        var legacyStay = await legacy.SeedStayAsync();
        var legacyGuest = legacy.Guest();
        var owner = legacy.Operator(legacyStay.OwnerId);

        var context = await SelfHostedIdentityScenario.ReadOkAsync(await legacyGuest.GetAsync($"/api/public/checkin/{legacyStay.Token}"));
        var verification = context.GetProperty("identityVerification");
        Assert.False(verification.GetProperty("selfHosted").GetBoolean());
        Assert.Equal(IdentityVerificationSteps.Consent, verification.GetProperty("step").GetString());

        var texts = await SelfHostedIdentityScenario.ReadOkAsync(await legacyGuest.GetAsync($"/api/public/checkin/{legacyStay.Token}/legal-texts"));
        var version = texts.GetProperty("texts").EnumerateArray().First(text => text.GetProperty("kind").GetString() == CheckInLegalTextKinds.BiometricConsent).GetProperty("version").GetString()!;
        var consent = await SelfHostedIdentityScenario.ReadOkAsync(
            await legacyGuest.PostAsJsonAsync($"/api/public/checkin/{legacyStay.Token}/biometric-consent", new { consentGiven = true, consentVersion = version }));
        Assert.True(consent.GetProperty("consentRecorded").GetBoolean());
        Assert.False(consent.GetProperty("state").GetProperty("selfHosted").GetBoolean());
        Assert.False(consent.GetProperty("state").GetProperty("opened").GetBoolean());

        await SelfHostedIdentityScenario.AssertProblemAsync(
            await legacyGuest.PostAsync($"/api/public/checkin/{legacyStay.Token}/selfie-challenge", null), HttpStatusCode.Conflict, "selfie_challenge_unavailable");
        foreach (var route in new[] { "identity/open", "presence-challenge", "presence" })
        {
            using var refused = await legacyGuest.PostAsync($"/api/public/checkin/{legacyStay.Token}/{route}", null);
            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        }

        var metrics = await SelfHostedIdentityScenario.ReadOkAsync(await owner.GetAsync("/api/verification/metrics"));
        Assert.False(metrics.TryGetProperty("identity", out _));
        using (var csv = await owner.GetAsync("/api/verification/metrics/export.csv"))
        {
            Assert.Equal(HttpStatusCode.NotFound, csv.StatusCode);
        }

        using (var evidence = await owner.GetAsync($"/api/verification/{legacyStay.BookingId}/evidence"))
        {
            Assert.Equal(HttpStatusCode.NotFound, evidence.StatusCode);
        }
    }

    private static Guid BookingOf(JsonElement item) => item.GetProperty("bookingId").GetGuid();

    private static string KindOf(JsonElement item) => item.GetProperty("kind").GetString()!;

    private static async Task<HttpStatusCode> StatusOfAsync(Task<HttpResponseMessage> call)
    {
        using var response = await call;
        return response.StatusCode;
    }

    private static bool HasReason(BiometricVerificationLog row, string check, string result) =>
        VerificationReasons.Parse(row.ReasonsJson).Any(reason => reason.Check == check && reason.Result == result);

    private static async Task AssertObservationBecauseAsync(SelfHostedIdentityScenario scenario, ScenarioStay stay, string reasonCode)
    {
        var row = Assert.Single(await scenario.LogRowsAsync(stay.BookingId));
        SelfHostedIdentityScenario.AssertShadowApproves(row);
        Assert.Equal((BiometricOutcome.Escalate, BiometricOutcome.Accept, IdentityDecisionMode.Observation), (row.Outcome, row.ShadowOutcome, row.DecisionMode));
        Assert.True(HasReason(row, VerificationReasons.Checks.ObservationMode, reasonCode));
        await AssertNothingWasApprovedAsync(scenario, stay);
    }

    private static async Task AssertNothingWasApprovedAsync(SelfHostedIdentityScenario scenario, ScenarioStay stay)
    {
        Assert.DoesNotContain(await scenario.LogRowsAsync(stay.BookingId), row => row.Outcome == BiometricOutcome.Accept);
        var saved = await scenario.SessionAsync(stay.SessionId);
        Assert.NotEqual(BiometricOutcome.Accept, saved.BiometricOutcome);
        Assert.NotEqual(IdentityOutcomeCodes.AutoApproved, saved.IdentityOutcomeCode);
        Assert.Empty(await scenario.EventsAsync(stay.BookingId, DomainEventType.IdVerificationCompleted));
        scenario.AccessCodes.Verify(codes => codes.ReleaseToGuestAsync(stay.BookingId, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static MultipartFormDataContent SelfieForm()
    {
        var image = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D]);
        image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return new MultipartFormDataContent { { image, "selfie", "selfie.png" } };
    }
}

// In a class of its own: the clock of its host goes forward by days and the hourly job works on every org it finds in the database.
public sealed class SelfHostedIdentityRetentionScenarioTests(CasazenWebApplicationFactory factory) : IClassFixture<CasazenWebApplicationFactory>
{
    [Fact]
    public async Task S9_TheRetentionDeletesTheExpiredEvidence_AndTheImageOfAnAbandonedSession()
    {
        await using var scenario = SelfHostedIdentityScenario.Start(factory);

        var (reviewed, reviewedGuest) = await scenario.ReadyStayAsync();
        var review = await scenario.AttemptAsync(reviewedGuest, reviewed, scenario.Genuine, liveness: 0.99, similarity: 0.99);
        Assert.Equal(IdentityVerificationGuestResults.Operator, review.Result);
        var evidence = await scenario.EvidenceRowsAsync(reviewed.BookingId);
        Assert.NotEmpty(evidence);
        var (abandoned, _) = await scenario.ReadyStayAsync();
        var kept = (await scenario.DocumentImagesAsync(abandoned.BookingId)).Where(image => image.DeletedAt is null).ToList();
        Assert.NotEmpty(kept);
        foreach (var key in KeysOf(kept))
            Assert.True(await scenario.ObjectExistsAsync(key));

        await scenario.RunRetentionAsync();
        Assert.All(await scenario.EvidenceRowsAsync(reviewed.BookingId), item => Assert.Null(item.DeletedAt));
        Assert.Contains(await scenario.DocumentImagesAsync(abandoned.BookingId), image => image.DeletedAt is null);
        Assert.Null((await scenario.SessionAsync(abandoned.SessionId)).IdentityClosedAt);

        scenario.Clock.Advance(TimeSpan.FromHours(49));
        var owner = scenario.Operator(reviewed.OwnerId);
        Assert.Empty(await SelfHostedIdentityScenario.EvidenceListAsync(owner, reviewed.BookingId));
        await SelfHostedIdentityScenario.AssertProblemAsync(
            await owner.GetAsync($"/api/verification/{reviewed.BookingId}/evidence/{evidence[0].Id}"), HttpStatusCode.NotFound, IdentityApiErrors.IdentityEvidenceNotFound);
        var overdue = (await scenario.AdminStatusAsync()).GetProperty("retention");
        Assert.True(overdue.GetProperty("overdueEvidence").GetInt32() >= evidence.Count);
        Assert.True(overdue.GetProperty("overdueDocumentImages").GetInt32() >= 1);

        await scenario.RunRetentionAsync();

        foreach (var item in await scenario.EvidenceRowsAsync(reviewed.BookingId))
        {
            Assert.NotNull(item.DeletedAt);
            Assert.Equal(IdentityEvidenceDeletionReasons.Expired, item.DeletionReason);
            Assert.False(await scenario.ObjectExistsAsync(item.StorageKey));
        }

        var closed = await scenario.SessionAsync(abandoned.SessionId);
        Assert.NotNull(closed.IdentityClosedAt);
        Assert.Equal(IdentityOutcomeCodes.Abandoned, closed.IdentityOutcomeCode);
        Assert.DoesNotContain(await scenario.DocumentImagesAsync(abandoned.BookingId), image => image.DeletedAt is null);
        foreach (var key in KeysOf(kept))
            Assert.False(await scenario.ObjectExistsAsync(key));

        var status = (await scenario.AdminStatusAsync()).GetProperty("retention");
        Assert.Equal((0, 0), (status.GetProperty("overdueEvidence").GetInt32(), status.GetProperty("overdueDocumentImages").GetInt32()));
        Assert.NotEqual(JsonValueKind.Null, status.GetProperty("lastRunAt").ValueKind);
        var counts = status.GetProperty("lastRunCounts");
        Assert.True(counts.GetProperty("expiredEvidence").GetInt32() >= evidence.Count);
        Assert.True(counts.GetProperty("abandonedSessions").GetInt32() >= 1);
        Assert.True(counts.GetProperty("abandonedDocumentImages").GetInt32() >= 1);
        Assert.Equal(0, counts.GetProperty("failedOrgs").GetInt32());

        Assert.Empty(await SelfHostedIdentityScenario.EvidenceListAsync(owner, reviewed.BookingId));
    }

    private static IEnumerable<string> KeysOf(IEnumerable<DocumentImage> images) =>
        images.SelectMany(image => new[] { image.FrontImageUrl, image.BackImageUrl }).Where(key => !string.IsNullOrWhiteSpace(key)).Select(key => key!);
}

internal static class SelfHostedIdentityScripts
{
    public static async Task<(ScenarioStay Stay, Guid LogId)> MainPathAsync(CasazenWebApplicationFactory factory)
    {
        await using var scenario = SelfHostedIdentityScenario.Start(factory);
        var stay = await scenario.SeedStayAsync();
        var guest = scenario.Guest();
        var owner = scenario.Operator(stay.OwnerId);

        await scenario.OpenAndConsentAsync(guest, stay);
        await scenario.CompleteDocumentAsync(guest, stay);
        var verification = (await SelfHostedIdentityScenario.ReadOkAsync(await guest.GetAsync($"/api/public/checkin/{stay.Token}"))).GetProperty("identityVerification");
        Assert.Equal(IdentityVerificationSteps.Presence, verification.GetProperty("step").GetString());
        Assert.Equal(SelfHostedIdentityScenario.FormatId, verification.GetProperty("formatId").GetString());
        Assert.True(verification.GetProperty("selfHosted").GetBoolean());
        Assert.True(verification.GetProperty("documentChecked").GetBoolean());

        var attempt = await scenario.AttemptAsync(guest, stay, scenario.Genuine, liveness: 0.99, similarity: 0.99);
        Assert.Equal(HttpStatusCode.OK, attempt.Status);
        Assert.Equal(IdentityVerificationGuestResults.Operator, attempt.Result);
        Assert.Empty(attempt.Guidance);
        Assert.Null(attempt.LockCode);
        Assert.False(attempt.Body.GetProperty("guidedRetryAvailable").GetBoolean());
        var state = attempt.Body.GetProperty("state");
        Assert.True(state.GetProperty("closed").GetBoolean());
        Assert.Equal(IdentityVerificationGuestResults.Operator, state.GetProperty("outcome").GetString());
        Assert.True(state.GetProperty("operatorPathOpen").GetBoolean());

        var row = Assert.Single(await scenario.LogRowsAsync(stay.BookingId));
        SelfHostedIdentityScenario.AssertShadowApproves(row);
        Assert.Equal(
            (BiometricOutcome.Escalate, BiometricOutcome.Accept, IdentityDecisionMode.Observation, VerificationMethod.Biometric),
            (row.Outcome, row.ShadowOutcome, row.DecisionMode, row.Method));
        Assert.Equal(
            (scenario.Policy.Version, SelfHostedIdentityScenario.FormatId, IdentityChannels.AndroidChrome, attempt.Key),
            (row.PolicyVersion, row.FormatId, row.Channel, row.IdempotencyKey));
        Assert.Equal(scenario.PolicyHash, row.PolicyHash, ignoreCase: true);
        Assert.Contains(
            VerificationReasons.Parse(row.ReasonsJson),
            reason => reason.Check == VerificationReasons.Checks.ObservationMode && reason.Result == IdentitySegmentReasonCodes.SegmentObservation);
        var saved = await scenario.SessionAsync(stay.SessionId);
        Assert.Equal((BiometricOutcome.Escalate, IdentityOutcomeCodes.Review, 1, 0), (saved.BiometricOutcome, saved.IdentityOutcomeCode, saved.BiometricAttempts, saved.IdentityGuidedRetries));
        Assert.NotNull(saved.IdentityClosedAt);
        Assert.NotNull(saved.OperatorRequestedAt);
        Assert.Empty(await scenario.EventsAsync(stay.BookingId, DomainEventType.IdVerificationCompleted));
        scenario.AccessCodes.Verify(codes => codes.ReleaseToGuestAsync(stay.BookingId, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        Assert.Single(await scenario.OpenTodosAsync(stay.BookingId));
        var push = Assert.Single(scenario.Push.Sent, sent => sent.Payload.Type == PushTypes.VerificationRequired && sent.Payload.BookingId == stay.BookingId);
        Assert.Equal((PushAudienceKind.VerificationOperators, stay.BookingId), (push.Audience.Kind, push.Audience.Id));
        Assert.DoesNotContain(DocumentStepSelfHostedTests.Surname, push.Payload.Title + " " + push.Payload.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(await scenario.DocumentImagesAsync(stay.BookingId), image => image.DeletedAt is null);

        var evidence = await scenario.EvidenceRowsAsync(stay.BookingId);
        Assert.True(evidence.Count >= 2);
        Assert.Contains(evidence, item => item.Kind == IdentityEvidenceKind.DocumentPortrait);
        Assert.Contains(evidence, item => item.Kind == IdentityEvidenceKind.Frame);
        foreach (var item in evidence)
        {
            Assert.Null(item.DeletedAt);
            Assert.Equal(item.CreatedAt.AddHours(48), item.ExpiresAt);
            Assert.Equal(row.Id, item.VerificationLogId);
            Assert.StartsWith($"{IdentityReviewEvidenceStore.StorageKeyPrefix}{stay.OrgId}/{stay.BookingId}/", item.StorageKey, StringComparison.Ordinal);
            var head = await scenario.ObjectHeadAsync(item.StorageKey, 4);
            Assert.NotEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, head);
            Assert.False(head.Length >= 2 && head[0] == 0xFF && head[1] == 0xD8, "A picture of the evidence is in the bucket in clear.");
        }

        var queued = Assert.Single(await SelfHostedIdentityScenario.PendingAsync(owner), item => item.GetProperty("bookingId").GetGuid() == stay.BookingId);
        Assert.Equal("Escalate", queued.GetProperty("biometricOutcome").GetString());
        Assert.Equal(("observation", "accept"), (queued.GetProperty("decisionMode").GetString(), queued.GetProperty("shadowOutcome").GetString()));
        Assert.Equal(evidence.Count, queued.GetProperty("evidence").GetProperty("count").GetInt32());
        Assert.Equal(evidence.Count, (await SelfHostedIdentityScenario.EvidenceListAsync(owner, stay.BookingId)).Count);

        var identity = await SelfHostedIdentityScenario.IdentityMetricsAsync(owner);
        Assert.Equal(1, identity.GetProperty("opened").GetInt32());
        Assert.Equal(1, identity.GetProperty("outcomes").GetProperty("review").GetInt32());
        Assert.Equal(0, identity.GetProperty("outcomes").GetProperty("autoApproved").GetInt32());
        Assert.Equal(1.0, identity.GetProperty("shadowApprovalRate").GetProperty("value").GetDouble());
        Assert.Equal(0.0, identity.GetProperty("automationRate").GetProperty("value").GetDouble());
        Assert.Equal(0, identity.GetProperty("shadowVsHuman").GetProperty("approveApprove").GetInt32());
        var cell = Assert.Single(identity.GetProperty("coverage").EnumerateArray().ToList());
        Assert.Equal(
            (SelfHostedIdentityScenario.FormatId, IdentityChannels.AndroidChrome, 1, 0, 1),
            (cell.GetProperty("formatId").GetString(), cell.GetProperty("channel").GetString(), cell.GetProperty("opened").GetInt32(), cell.GetProperty("autoApproved").GetInt32(), cell.GetProperty("shadowApproved").GetInt32()));

        var pilot = Assert.Single(await SelfHostedIdentityScenario.PilotRowsAsync(owner));
        Assert.Equal(IdentityPilotExport.AttemptRef(stay.SessionId, stay.OrgId), pilot["attempt_ref"]);
        Assert.Equal(
            (stay.PropertyId.ToString("D"), SelfHostedIdentityScenario.FormatId, IdentityChannels.AndroidChrome, IdentityOutcomeCodes.Review, "observation", "approve", string.Empty),
            (pilot["property_id"], pilot["format_id"], pilot["channel"], pilot["outcome_code"], pilot["decision_mode"], pilot["shadow_outcome"], pilot["human_outcome"]));
        Assert.Contains("observation_mode:segment_observation", pilot["reason_codes"], StringComparison.Ordinal);

        foreach (var secret in new[] { DocumentStepSelfHostedTests.Surname, DocumentStepSelfHostedTests.GivenNames, DocumentStepSelfHostedTests.DocumentNumber })
        {
            Assert.DoesNotContain(secret, attempt.Body.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secret, row.ReasonsJson + row.ThresholdsJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secret, string.Join(',', pilot.Values), StringComparison.OrdinalIgnoreCase);
        }

        scenario.Clock.Advance(TimeSpan.FromMinutes(7));
        using (var approved = await owner.PostAsJsonAsync($"/api/verification/{stay.BookingId}/verify", new { outcome = true, note = "Il documento e il volto coincidono." }))
        {
            Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        }

        foreach (var item in await scenario.EvidenceRowsAsync(stay.BookingId))
        {
            Assert.NotNull(item.DeletedAt);
            Assert.Equal(IdentityEvidenceDeletionReasons.Decided, item.DeletionReason);
            Assert.False(await scenario.ObjectExistsAsync(item.StorageKey));
        }

        Assert.Empty(await SelfHostedIdentityScenario.EvidenceListAsync(owner, stay.BookingId));
        Assert.Empty(await scenario.OpenTodosAsync(stay.BookingId));
        Assert.DoesNotContain(await SelfHostedIdentityScenario.PendingAsync(owner), item => item.GetProperty("bookingId").GetGuid() == stay.BookingId);
        var rows = await scenario.LogRowsAsync(stay.BookingId);
        Assert.Equal(2, rows.Count);
        var person = Assert.Single(rows, item => item.Method == VerificationMethod.Operator);
        Assert.Equal((BiometricOutcome.Accept, stay.OwnerId), (person.Outcome, person.OperatorUserId));
        Assert.Single(await scenario.EventsAsync(stay.BookingId, DomainEventType.IdVerificationCompleted));
        scenario.AccessCodes.Verify(codes => codes.ReleaseToGuestAsync(stay.BookingId, AccessCodeReleaseChannels.Operator, It.IsAny<CancellationToken>()), Times.Once);

        var decided = Assert.Single(await SelfHostedIdentityScenario.PilotRowsAsync(owner));
        Assert.Equal(("approve", "approve", "420"), (decided["shadow_outcome"], decided["human_outcome"], decided["review_seconds"]));
        Assert.False(string.IsNullOrEmpty(decided["human_decided_at"]));
        var afterwards = await SelfHostedIdentityScenario.IdentityMetricsAsync(owner);
        Assert.Equal(1, afterwards.GetProperty("shadowVsHuman").GetProperty("approveApprove").GetInt32());
        Assert.Equal((1, 420.0), (afterwards.GetProperty("reviewSeconds").GetProperty("n").GetInt32(), afterwards.GetProperty("reviewSeconds").GetProperty("p50").GetDouble()));
        return (stay, row.Id);
    }

    public static async Task ApprovalTwiceAsync(CasazenWebApplicationFactory factory, bool atOnce)
    {
        await using var scenario = SelfHostedIdentityScenario.Start(factory);
        var stay = await scenario.SeedStayAsync();
        await scenario.QualifySegmentAsync(stay);
        var guest = scenario.Guest();
        var second = scenario.Guest();
        await scenario.OpenAndConsentAsync(guest, stay);
        await scenario.CompleteDocumentAsync(guest, stay);
        var (challengeToken, sent) = await scenario.PrepareAttemptAsync(guest, stay, scenario.Genuine, liveness: 0.99, similarity: 0.99);
        var key = SelfHostedIdentityScenario.NewKey();

        PresenceAttempt[] answers;
        if (atOnce)
        {
            answers = await Task.WhenAll(
                scenario.SendPresenceAsync(guest, stay, challengeToken, sent, key),
                scenario.SendPresenceAsync(second, stay, challengeToken, sent, key));
        }
        else
        {
            answers =
            [
                await scenario.SendPresenceAsync(guest, stay, challengeToken, sent, key),
                await scenario.SendPresenceAsync(second, stay, challengeToken, sent, key),
            ];
        }

        await scenario.AssertApprovedAsync(stay, answers[0]);
        Assert.All(answers, answer =>
        {
            Assert.Equal(HttpStatusCode.OK, answer.Status);
            Assert.Equal(IdentityVerificationGuestResults.Ok, answer.Result);
            Assert.Equal(SelfHostedIdentityScenario.LockCode, answer.LockCode);
        });
        Assert.Equal(answers[0].Body.GetRawText(), answers[1].Body.GetRawText());
        var row = Assert.Single(await scenario.LogRowsAsync(stay.BookingId));
        Assert.Equal((BiometricOutcome.Accept, IdentityDecisionMode.Automatic, key), (row.Outcome, row.DecisionMode, row.IdempotencyKey));
        Assert.Single(await scenario.EventsAsync(stay.BookingId, DomainEventType.IdVerificationCompleted));
        Assert.Equal(1, (await scenario.SessionAsync(stay.SessionId)).BiometricAttempts);
        Assert.DoesNotContain(await scenario.DocumentImagesAsync(stay.BookingId), image => image.DeletedAt is null);
    }

    public static async Task DuplicateAsync(CasazenWebApplicationFactory factory, bool atOnce)
    {
        await using var scenario = SelfHostedIdentityScenario.Start(factory);
        var (stay, guest) = await scenario.ReadyStayAsync();
        var second = scenario.Guest();
        var (challengeToken, sent) = await scenario.PrepareAttemptAsync(guest, stay, scenario.Genuine, liveness: 0.99, similarity: 0.99);
        var key = SelfHostedIdentityScenario.NewKey();

        PresenceAttempt[] answers;
        if (atOnce)
        {
            answers = await Task.WhenAll(
                scenario.SendPresenceAsync(guest, stay, challengeToken, sent, key),
                scenario.SendPresenceAsync(second, stay, challengeToken, sent, key));
        }
        else
        {
            answers =
            [
                await scenario.SendPresenceAsync(guest, stay, challengeToken, sent, key),
                await scenario.SendPresenceAsync(second, stay, challengeToken, sent, key),
            ];
        }

        Assert.All(answers, answer =>
        {
            Assert.Equal(HttpStatusCode.OK, answer.Status);
            Assert.Equal(IdentityVerificationGuestResults.Operator, answer.Result);
            Assert.Null(answer.LockCode);
        });
        Assert.Equal(answers[0].Body.GetRawText(), answers[1].Body.GetRawText());

        var row = Assert.Single(await scenario.LogRowsAsync(stay.BookingId));
        Assert.Equal((key, BiometricOutcome.Escalate, 1), (row.IdempotencyKey, row.Outcome, row.Attempts));
        var saved = await scenario.SessionAsync(stay.SessionId);
        Assert.Equal((1, key), (saved.BiometricAttempts, saved.IdentityLastAttemptKey));
        Assert.Single(await scenario.OpenTodosAsync(stay.BookingId));
        Assert.Single(scenario.Push.Sent, push => push.Payload.Type == PushTypes.VerificationRequired && push.Payload.BookingId == stay.BookingId);
        var evidence = await scenario.EvidenceRowsAsync(stay.BookingId);
        Assert.NotEmpty(evidence);
        Assert.Equal(row.Id, Assert.Single(evidence.Select(item => item.VerificationLogId).Distinct()));
        Assert.All(evidence, item => Assert.Null(item.DeletedAt));
        Assert.DoesNotContain(await scenario.DocumentImagesAsync(stay.BookingId), image => image.DeletedAt is null);

        var other = await scenario.SendPresenceAsync(guest, stay, challengeToken, sent, SelfHostedIdentityScenario.NewKey());
        Assert.Equal(HttpStatusCode.Conflict, other.Status);
        Assert.Equal(IdentityApiErrors.IdentityAlreadyClosed, other.Body.GetProperty("code").GetString());
        Assert.Single(await scenario.LogRowsAsync(stay.BookingId));
    }
}
