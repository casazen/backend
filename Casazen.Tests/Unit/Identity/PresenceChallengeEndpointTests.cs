using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Identity;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.CheckIn;
using Casazen.Web.Extensions;
using Casazen.Web.Infrastructure;
using Casazen.Web.Middleware;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E72/F01/US01/T01 (NF-192, ADR-013-R17): <c>POST api/public/checkin/{token}/presence-challenge</c> and the flow behind it. The challenge is issued only to a session whose
/// identity step is opened and not closed, whose consent is the current one, whose document step is complete and that no person has, while the policy and the models are
/// ready and both flags are on; it is stored under the lock of the session, replaces the previous one, and its proof is bound to the session, the token of its link, the
/// attempt, the time of the consent and the challenge, and expires. The endpoint is 404 with a flag off, rate limited by <c>GuestIdentity</c> and never cached.
/// </summary>
public sealed class PresenceChallengeEndpointTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions FlowJson = (JsonSerializerOptions)typeof(IdentityVerificationFlowService)
        .GetField("Json", BindingFlags.NonPublic | BindingFlags.Static)!
        .GetValue(null)!;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "casazen-presence-challenge-" + Guid.NewGuid().ToString("N"));

    public PresenceChallengeEndpointTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task IssuePresenceChallenge_WhenTheSessionIsReady_StoresTheChallengeAndAnswersWithTheNumbersOfThePolicy()
    {
        await using var world = await World.CreateAsync(_directory);
        var session = await world.ReadyStayAsync();
        var before = session.DocumentCheckJson;

        var issued = await world.Flow().IssuePresenceChallengeAsync(session, CancellationToken.None);

        Assert.NotNull(issued);
        Assert.Equal(3, issued.Steps.Count);
        Assert.All(issued.Steps, step => Assert.Contains(step, PresenceActions.All));
        Assert.Equal((1500, 1000, Now.AddSeconds(90), 8, 60, 720), (issued.StepMs, issued.LeadInMs, issued.ExpiresAt, issued.TargetFps, issued.MaxFrames, issued.MaxFrameSide));
        Assert.False(string.IsNullOrWhiteSpace(issued.ChallengeToken));

        using var stored = JsonDocument.Parse(session.DocumentCheckJson!);
        var presence = stored.RootElement.GetProperty("presence");
        Assert.Equal(["attempt", "expiresAt", "id", "issuedAt", "steps"], presence.EnumerateObject().Select(member => member.Name).Order());
        Assert.Equal(issued.Steps, presence.GetProperty("steps").EnumerateArray().Select(step => step.GetString()));
        Assert.Equal(1, presence.GetProperty("attempt").GetInt32());
        Assert.DoesNotContain(issued.ChallengeToken, session.DocumentCheckJson, StringComparison.Ordinal);
        using var original = JsonDocument.Parse(before!);
        foreach (var member in original.RootElement.EnumerateObject().Where(entry => entry.Name != "presence"))
            Assert.Equal(member.Value.GetRawText(), stored.RootElement.GetProperty(member.Name).GetRawText());

        var reloaded = await world.Db.GuestCheckInSessions.AsNoTracking().SingleAsync(s => s.Id == session.Id);
        Assert.Equal(session.DocumentCheckJson, reloaded.DocumentCheckJson);
    }

    [Theory]
    [InlineData(40, 640, 40, 640)]
    [InlineData(100, 1280, 60, 1280)]
    public async Task IssuePresenceChallenge_TheLimitsOfTheSequenceAreTheSmallerOfThePolicyAndTheOptions(int optionFrames, int optionSide, int frames, int side)
    {
        await using var world = await World.CreateAsync(_directory);
        world.Limits.Presence.MaxFrames = optionFrames;
        world.Limits.Presence.MaxFrameSide = optionSide;
        var session = await world.ReadyStayAsync();

        var issued = await world.Flow().IssuePresenceChallengeAsync(session, CancellationToken.None);

        Assert.NotNull(issued);
        Assert.Equal((frames, side), (issued.MaxFrames, issued.MaxFrameSide));
    }

    [Theory]
    [InlineData(2, 160, 500, 2)]
    [InlineData(300, 1_920, 30_000, 60)]
    public async Task IssuePresenceChallenge_ALimitOnTheEdgeOfItsRange_StillIssuesTheChallenge(int frames, int side, int timeoutMs, int announcedFrames)
    {
        await using var world = await World.CreateAsync(_directory);
        world.Limits.Presence.MaxFrames = frames;
        world.Limits.Presence.MaxFrameSide = side;
        world.Limits.Presence.SequenceTimeoutMs = timeoutMs;
        Assert.Empty(world.Limits.Validate());
        var session = await world.ReadyStayAsync();

        var issued = await world.Flow().IssuePresenceChallengeAsync(session, CancellationToken.None);

        Assert.NotNull(issued);
        Assert.Equal((announcedFrames, side), (issued.MaxFrames, issued.MaxFrameSide));
    }

    [Theory]
    [InlineData("MaxFrames", 1)]
    [InlineData("MaxFrames", 301)]
    [InlineData("MaxFrames", 100_000)]
    [InlineData("MaxFrameBytes", 8_191)]
    [InlineData("MaxSequenceBytes", 65_535)]
    [InlineData("MaxFrameSide", 159)]
    [InlineData("MaxFrameSide", 1_921)]
    [InlineData("SequenceTimeoutMs", 499)]
    [InlineData("SequenceTimeoutMs", 30_001)]
    public async Task IssuePresenceChallenge_ALimitOfTheSectionOutOfItsRange_AnswersNothing_StoresNothing_AndTheLogNamesTheSettingNeverTheValue(string setting, int value)
    {
        await using var world = await World.CreateAsync(_directory);
        var session = await world.ReadyStayAsync();
        typeof(IdentityPresenceOptions).GetProperty(setting)!.SetValue(world.Limits.Presence, value);
        var before = session.DocumentCheckJson;
        var logger = new CapturingLogger<IdentityVerificationFlowService>();

        var issued = await world.Flow(logger).IssuePresenceChallengeAsync(session, CancellationToken.None);

        Assert.Null(issued);
        Assert.Equal(before, session.DocumentCheckJson);
        Assert.False(HasStoredChallenge(session));
        var problem = Assert.Single(logger.Entries, entry => entry.Message.Contains("section IdentityVerification is invalid", StringComparison.Ordinal));
        Assert.Contains($"Presence:{setting}", problem.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(value.ToString(CultureInfo.InvariantCulture), problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IssuePresenceChallenge_ASettingOfTheSectionOutsideThePresenceThatIsInvalid_MakesTheWholeSectionNotAvailable()
    {
        await using var world = await World.CreateAsync(_directory);
        var session = await world.ReadyStayAsync();
        world.Limits.FormatsDirectory = " ";

        var issued = await world.Flow().IssuePresenceChallengeAsync(session, CancellationToken.None);

        Assert.Null(issued);
        Assert.False(HasStoredChallenge(session));
    }

    [Theory]
    [InlineData("verification flag off")]
    [InlineData("self-hosted flag off")]
    [InlineData("identity step not opened")]
    [InlineData("assisted path chosen")]
    [InlineData("verification closed")]
    [InlineData("consent missing")]
    [InlineData("consent text changed")]
    [InlineData("no document")]
    [InlineData("document of two sides incomplete")]
    [InlineData("a person has the verification")]
    [InlineData("attempts used")]
    [InlineData("already verified")]
    [InlineData("policy hash does not match")]
    [InlineData("models not ready")]
    [InlineData("options out of range")]
    public async Task IssuePresenceChallenge_WhileOneConditionIsMissing_AnswersNothing_AndStoresNothing(string missing)
    {
        await using var world = await World.CreateAsync(_directory, pinThePolicy: missing != "policy hash does not match", modelsReady: missing != "models not ready");
        var session = await world.ReadyStayAsync();
        switch (missing)
        {
            case "verification flag off":
                world.Flags.Set(FeatureFlags.CheckInIdentityVerification, false);
                break;
            case "self-hosted flag off":
                world.Flags.Set(FeatureFlags.CheckInIdentitySelfHosted, false);
                break;
            case "assisted path chosen":
                session.IdentityChoice = IdentityChoices.Assisted;
                break;
            case "identity step not opened":
                session.IdentityOpenedAt = null;
                break;
            case "verification closed":
                session.IdentityClosedAt = Now.UtcDateTime;
                session.IdentityOutcomeCode = IdentityOutcomeCodes.Review;
                break;
            case "consent missing":
                session.BiometricConsentAt = null;
                break;
            case "consent text changed":
                world.Registry.Setup(registry => registry.GetActiveVersion(CheckInLegalTextKinds.BiometricConsent))
                    .Returns(new CheckInLegalVersion(CheckInLegalTextKinds.BiometricConsent, "TEST-biometric-v2", false, new string('b', 64)));
                break;
            case "no document":
                session.DocumentCheckJson = null;
                break;
            case "document of two sides incomplete":
                session.DocumentCheckJson = JsonSerializer.Serialize(DocumentCheckOf(twoSided: true), FlowJson);
                break;
            case "a person has the verification":
                session.OperatorRequestedAt = Now.UtcDateTime;
                break;
            case "attempts used":
                session.BiometricAttempts = 3;
                break;
            case "already verified":
                session.BiometricOutcome = BiometricOutcome.Accept;
                break;
            case "options out of range":
                world.Limits.Presence.MaxFrames = 100_000;
                break;
        }

        await world.Db.SaveChangesAsync();
        var before = session.DocumentCheckJson;

        var issued = await world.Flow().IssuePresenceChallengeAsync(session, CancellationToken.None);

        Assert.Null(issued);
        Assert.Equal(before, session.DocumentCheckJson);
        Assert.False(HasStoredChallenge(session));
    }

    [Fact]
    public async Task IssuePresenceChallenge_ANewChallengeReplacesTheStoredOne_AndTheOldProofStopsWorking()
    {
        await using var world = await World.CreateAsync(_directory);
        var session = await world.ReadyStayAsync();
        var flow = world.Flow();

        var first = await flow.IssuePresenceChallengeAsync(session, CancellationToken.None);
        var firstId = StoredChallengeId(session);
        var second = await flow.IssuePresenceChallengeAsync(session, CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(firstId, StoredChallengeId(session));
        Assert.NotEqual(first.ChallengeToken, second.ChallengeToken);
        Assert.False(flow.TryValidatePresenceToken(session, first.ChallengeToken, out var replaced));
        Assert.Equal(PresenceTokenStatus.Required, replaced);
        Assert.True(flow.TryValidatePresenceToken(session, second.ChallengeToken, out var current));
        Assert.Equal(PresenceTokenStatus.Valid, current);
    }

    [Fact]
    public async Task IssuePresenceChallenge_TwoCallsAtOnce_LeaveOneChallenge_AndOnlyItsProofWorks()
    {
        await using var world = await World.CreateAsync(_directory);
        var session = await world.ReadyStayAsync();
        var flow = world.Flow();

        var issued = await Task.WhenAll(
            flow.IssuePresenceChallengeAsync(session, CancellationToken.None),
            flow.IssuePresenceChallengeAsync(session, CancellationToken.None));

        Assert.All(issued, challenge => Assert.NotNull(challenge));
        var working = issued.Count(challenge => flow.TryValidatePresenceToken(session, challenge!.ChallengeToken, out _));
        Assert.Equal(1, working);
    }

    [Fact]
    public async Task PresenceChallengeToken_OtherSessionOrAttempt_IsRejected()
    {
        await using var world = await World.CreateAsync(_directory);
        var left = await world.ReadyStayAsync();
        var right = await world.ReadyStayAsync();
        var flow = world.Flow();
        var leftProof = (await flow.IssuePresenceChallengeAsync(left, CancellationToken.None))!.ChallengeToken;

        Assert.True(flow.TryValidatePresenceToken(left, leftProof, out _));
        Assert.False(flow.TryValidatePresenceToken(right, leftProof, out var withoutChallenge));
        Assert.Equal(PresenceTokenStatus.Required, withoutChallenge);

        var rightProof = (await flow.IssuePresenceChallengeAsync(right, CancellationToken.None))!.ChallengeToken;
        Assert.False(flow.TryValidatePresenceToken(right, leftProof, out _));
        Assert.False(flow.TryValidatePresenceToken(left, rightProof, out _));
        Assert.True(flow.TryValidatePresenceToken(right, rightProof, out _));

        left.BiometricAttempts = 1;
        await world.Db.SaveChangesAsync();
        Assert.False(flow.TryValidatePresenceToken(left, leftProof, out var pastAttempt));
        Assert.Equal(PresenceTokenStatus.Required, pastAttempt);
        var nextProof = (await flow.IssuePresenceChallengeAsync(left, CancellationToken.None))!.ChallengeToken;
        Assert.True(flow.TryValidatePresenceToken(left, nextProof, out _));
        Assert.False(flow.TryValidatePresenceToken(left, leftProof, out _));
    }

    [Theory]
    [InlineData("none", true)]
    [InlineData("session", false)]
    [InlineData("token of the link", false)]
    [InlineData("attempt", false)]
    [InlineData("consent", false)]
    [InlineData("challenge", false)]
    public async Task PresenceChallengeToken_IsBoundToTheSessionTheLinkTheAttemptTheConsentAndTheChallenge(string altered, bool accepted)
    {
        await using var world = await World.CreateAsync(_directory);
        var session = await world.ReadyStayAsync();
        var flow = world.Flow();
        Assert.NotNull(await flow.IssuePresenceChallengeAsync(session, CancellationToken.None));
        var challengeId = StoredChallengeId(session);
        string[] bound =
        [
            session.Id.ToString("N"),
            session.TokenHash,
            "1",
            session.BiometricConsentAt!.Value.Ticks.ToString(CultureInfo.InvariantCulture),
            challengeId.ToString("N"),
        ];
        var index = altered switch
        {
            "session" => 0,
            "token of the link" => 1,
            "attempt" => 2,
            "consent" => 3,
            "challenge" => 4,
            _ => -1,
        };
        if (index >= 0)
            bound[index] = altered + "-changed";

        var proof = world.DataProtection.CreateProtector(IdentityVerificationFlowService.PresenceTokenPurpose, bound).Protect(challengeId.ToString("N"));

        Assert.Equal(accepted, flow.TryValidatePresenceToken(session, proof, out _));
    }

    [Fact]
    public async Task TryValidatePresenceToken_AnExpiredChallenge_IsExpired_AndWhateverIsNotAProofIsRequired()
    {
        await using var world = await World.CreateAsync(_directory);
        var session = await world.ReadyStayAsync();
        var flow = world.Flow();
        var proof = (await flow.IssuePresenceChallengeAsync(session, CancellationToken.None))!.ChallengeToken;

        world.Clock.Advance(TimeSpan.FromSeconds(89));
        Assert.True(flow.TryValidatePresenceToken(session, proof, out var alive));
        Assert.Equal(PresenceTokenStatus.Valid, alive);

        world.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(flow.TryValidatePresenceToken(session, proof, out var expired));
        Assert.Equal(PresenceTokenStatus.Expired, expired);

        foreach (var notAProof in new[] { null, string.Empty, "invalid", new string('x', 3000) })
        {
            Assert.False(flow.TryValidatePresenceToken(session, notAProof, out var status));
            Assert.Equal(PresenceTokenStatus.Required, status);
        }

        var renewed = await flow.IssuePresenceChallengeAsync(session, CancellationToken.None);
        Assert.NotNull(renewed);
        Assert.True(flow.TryValidatePresenceToken(session, renewed.ChallengeToken, out _));
    }

    [Fact]
    public async Task TryValidatePresenceToken_ANewConsent_RequiresANewChallenge()
    {
        await using var world = await World.CreateAsync(_directory);
        var session = await world.ReadyStayAsync();
        var flow = world.Flow();
        var proof = (await flow.IssuePresenceChallengeAsync(session, CancellationToken.None))!.ChallengeToken;

        session.BiometricConsentAt = Now.UtcDateTime.AddMinutes(5);
        await world.Db.SaveChangesAsync();

        Assert.False(flow.TryValidatePresenceToken(session, proof, out var status));
        Assert.Equal(PresenceTokenStatus.Required, status);
        session.BiometricConsentAt = null;
        Assert.False(flow.TryValidatePresenceToken(session, proof, out _));
    }

    [Fact]
    public async Task IssuePresenceChallenge_TheAttemptIsTheAttemptsMadePlusTheGuidedRetriesUsedPlusOne()
    {
        await using var world = await World.CreateAsync(_directory);
        var session = await world.ReadyStayAsync();
        session.BiometricAttempts = 1;
        session.IdentityGuidedRetries = 1;
        await world.Db.SaveChangesAsync();

        var issued = await world.Flow().IssuePresenceChallengeAsync(session, CancellationToken.None);

        Assert.NotNull(issued);
        using var stored = JsonDocument.Parse(session.DocumentCheckJson!);
        Assert.Equal(3, stored.RootElement.GetProperty("presence").GetProperty("attempt").GetInt32());
        Assert.True(world.Flow().TryValidatePresenceToken(session, issued.ChallengeToken, out _));
    }

    [Fact]
    public void PresenceChallengeEndpoint_IsAPostBehindBothFlags_RateLimitedByGuestIdentity_AndNeverCached()
    {
        var method = typeof(PublicGuestCheckInController).GetMethod(nameof(PublicGuestCheckInController.IssuePresenceChallenge))!;

        Assert.Equal("{token}/presence-challenge", Assert.Single(method.GetCustomAttributes<HttpPostAttribute>()).Template);
        Assert.Equal(
            new[] { FeatureFlags.CheckInIdentitySelfHosted, FeatureFlags.CheckInIdentityVerification }.Order(),
            method.GetCustomAttributes<FeatureGateAttribute>().Select(gate => gate.Flag).Order());
        Assert.Equal(RateLimitPolicies.GuestIdentity, Assert.Single(method.GetCustomAttributes<EnableRateLimitingAttribute>()).PolicyName);
        var cache = Assert.Single(method.GetCustomAttributes<ResponseCacheAttribute>());
        Assert.True(cache.NoStore);
        Assert.Equal(ResponseCacheLocation.None, cache.Location);

        Assert.NotNull(typeof(PublicGuestCheckInController).GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Empty(method.GetCustomAttributes<AuthorizeAttribute>());
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public async Task PresenceChallengeEndpoint_WithAFlagOff_AnswersLikeARouteThatDoesNotExist(bool verification, bool selfHosted, bool reached)
    {
        var flags = new TestFeatureFlags();
        flags.Set(FeatureFlags.CheckInIdentityVerification, verification);
        flags.Set(FeatureFlags.CheckInIdentitySelfHosted, selfHosted);
        var next = 0;
        var middleware = new FeatureGateMiddleware(
            _ =>
            {
                next++;
                return Task.CompletedTask;
            },
            flags,
            NullLogger<FeatureGateMiddleware>.Instance);
        var gates = typeof(PublicGuestCheckInController).GetMethod(nameof(PublicGuestCheckInController.IssuePresenceChallenge))!
            .GetCustomAttributes<FeatureGateAttribute>().Cast<object>().ToArray();
        var context = new DefaultHttpContext();
        context.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(gates), "presence-challenge"));

        await middleware.InvokeAsync(context);

        Assert.Equal(reached ? 1 : 0, next);
        Assert.Equal(reached ? StatusCodes.Status200OK : StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Fact]
    public async Task IssuePresenceChallenge_AnUnknownLink_Is404_AndNothingIsAsked()
    {
        var presence = new Mock<IIdentityPresenceFlow>();
        var controller = Controller(session: null, new Mock<IIdentityVerificationFlowService>());

        var answer = await controller.IssuePresenceChallenge("token", presence.Object);

        Assert.IsType<NotFoundResult>(answer.Result);
        presence.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task IssuePresenceChallenge_AClosedVerification_Is409IdentityAlreadyClosed_AndNothingIsIssued()
    {
        var presence = new Mock<IIdentityPresenceFlow>();
        var closed = new GuestCheckInSession { Id = Guid.NewGuid(), IdentityClosedAt = Now.UtcDateTime };
        var controller = Controller(closed, new Mock<IIdentityVerificationFlowService>());

        var answer = await controller.IssuePresenceChallenge("token", presence.Object);

        var problem = Problem(answer.Result, StatusCodes.Status409Conflict);
        Assert.Equal("identity_already_closed", problem.Extensions["code"]);
        Assert.Equal(IdentityApiErrors.IdentityAlreadyClosed, problem.Extensions["code"]);
        presence.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task IssuePresenceChallenge_WhenTheFlowAnswersNothing_Is409PresenceChallengeUnavailable_WithTheStateOfTheVerification()
    {
        var session = new GuestCheckInSession { Id = Guid.NewGuid() };
        var presence = new Mock<IIdentityPresenceFlow>();
        presence.Setup(flow => flow.IssuePresenceChallengeAsync(session, It.IsAny<CancellationToken>())).ReturnsAsync((PresenceChallengeIssue?)null);
        var verification = new Mock<IIdentityVerificationFlowService>();
        verification.Setup(flow => flow.GetStateAsync(session, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IdentityVerificationState(IdentityVerificationSteps.Document, null, true, true, false, false, 3, false, false, 0));
        var controller = Controller(session, verification);

        var answer = await controller.IssuePresenceChallenge("token", presence.Object);

        var problem = Problem(answer.Result, StatusCodes.Status409Conflict);
        Assert.Equal("presence_challenge_unavailable", problem.Extensions["code"]);
        var state = Assert.IsType<PublicIdentityVerificationStateDto>(problem.Extensions["state"]);
        Assert.Equal((IdentityVerificationSteps.Document, true, 3, true), (state.Step, state.ConsentRecorded, state.AttemptsRemaining, state.OperatorPathOpen));
    }

    [Fact]
    public async Task IssuePresenceChallenge_WhenTheFlowIssues_Is200WithTheChallengeOfTheContract()
    {
        var session = new GuestCheckInSession { Id = Guid.NewGuid() };
        var issue = new PresenceChallengeIssue(
            "proof", [PresenceActions.TurnLeft, PresenceActions.LookUp, PresenceActions.TurnRight], 1500, 1000, Now.AddSeconds(90), 8, 60, 720);
        var presence = new Mock<IIdentityPresenceFlow>();
        presence.Setup(flow => flow.IssuePresenceChallengeAsync(session, It.IsAny<CancellationToken>())).ReturnsAsync(issue);
        var controller = Controller(session, new Mock<IIdentityVerificationFlowService>());

        var answer = await controller.IssuePresenceChallenge("token", presence.Object);

        var ok = Assert.IsType<OkObjectResult>(answer.Result);
        var challenge = Assert.IsType<PresenceChallengeDto>(ok.Value);
        Assert.Equal(("proof", 1500, 1000, Now.AddSeconds(90), 8, 60, 720), (challenge.ChallengeToken, challenge.StepMs, challenge.LeadInMs, challenge.ExpiresAt, challenge.TargetFps, challenge.MaxFrames, challenge.MaxFrameSide));
        Assert.Equal(issue.Steps, challenge.Steps);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(challenge, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(
            new[] { "challengeToken", "steps", "stepMs", "leadInMs", "expiresAt", "targetFps", "maxFrames", "maxFrameSide" }.Order(),
            json.RootElement.EnumerateObject().Select(member => member.Name).Order());
    }

    [Fact]
    public void AddSelfHostedIdentityVerification_RegistersThePresenceInterfaceAsTheSameScopedInstanceAsTheFlow()
    {
        var flow = new Mock<IIdentityVerificationFlowService>();
        flow.As<IIdentityPresenceFlow>();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddScoped(_ => flow.Object);
        services.AddSelfHostedIdentityVerification();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        using var scope = provider.CreateScope();
        var presence = scope.ServiceProvider.GetRequiredService<IIdentityPresenceFlow>();

        Assert.Same(scope.ServiceProvider.GetRequiredService<IIdentityVerificationFlowService>(), presence);
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IIdentityPresenceFlow)).Lifetime);
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, descriptor => descriptor.ServiceType == typeof(PresenceChallengeIssuer)).Lifetime);
    }

    private static bool HasStoredChallenge(GuestCheckInSession session)
    {
        if (session.DocumentCheckJson is null)
            return false;

        using var stored = JsonDocument.Parse(session.DocumentCheckJson);
        return stored.RootElement.TryGetProperty("presence", out var presence) && presence.ValueKind == JsonValueKind.Object;
    }

    private static Guid StoredChallengeId(GuestCheckInSession session)
    {
        using var stored = JsonDocument.Parse(session.DocumentCheckJson!);
        return stored.RootElement.GetProperty("presence").GetProperty("id").GetGuid();
    }

    private static ProblemDetails Problem(IActionResult? result, int status)
    {
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(status, objectResult.StatusCode);
        return Assert.IsType<ProblemDetails>(objectResult.Value);
    }

    private static StoredDocumentCheck DocumentCheckOf(bool twoSided = false) =>
        new(new DocumentCheck(true, true, false, true, true, null, null, false), "5F1A", Now.UtcDateTime, TwoSided: twoSided);

    private static PublicGuestCheckInController Controller(GuestCheckInSession? session, Mock<IIdentityVerificationFlowService> flow)
    {
        var checkIn = new Mock<IGuestCheckInService>();
        checkIn.Setup(service => service.GetSessionByTokenAsync("token")).ReturnsAsync(session);

        return new PublicGuestCheckInController(
            checkIn.Object,
            Mock.Of<IAlloggiatiCodeTableService>(),
            Mock.Of<ICheckInDocumentReader>(),
            Mock.Of<IAlloggiatiReportScheduler>(),
            flow.Object,
            Mock.Of<IFeatureFlags>(),
            Mock.Of<IStringLocalizer<SharedResources>>(),
            NullLogger<PublicGuestCheckInController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
                },
            },
        };
    }

    private sealed class World : IAsyncDisposable
    {
        private readonly Guid _orgId = Guid.NewGuid();
        private readonly Guid _propertyId = Guid.NewGuid();
        private readonly string _directory;
        private readonly bool _pinThePolicy;
        private readonly bool _modelsReady;

        private World(string directory, bool pinThePolicy, bool modelsReady)
        {
            _directory = directory;
            _pinThePolicy = pinThePolicy;
            _modelsReady = modelsReady;
            Db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"presence-challenge-{Guid.NewGuid()}").Options, null, DataProtection);
        }

        public AppDbContext Db { get; }

        public IDataProtectionProvider DataProtection { get; } = TestDataProtection.Shared;

        public Casazen.Tests.Unit.FakeTimeProvider Clock { get; } = new(Now);

        public TestFeatureFlags Flags { get; } = new(FeatureFlags.CheckInIdentityVerification, FeatureFlags.CheckInIdentitySelfHosted);

        public Mock<ICheckInLegalTexts> Registry { get; } = ActivePoseFlowTestVerifier.CreateRegistry();

        public IdentityVerificationOptions Limits { get; } = new();

        public static async Task<World> CreateAsync(string directory, bool pinThePolicy = true, bool modelsReady = true)
        {
            var world = new World(directory, pinThePolicy, modelsReady);
            world.Db.Orgs.Add(new OrgEntity
            {
                Id = world._orgId,
                Name = "Agenzia presenza",
                Slug = $"presenza-{world._orgId:N}"[..20],
                DisplayName = "Agenzia presenza",
                ContactEmail = "org@example.com",
                PlanTier = PlanTier.Agency,
            });
            world.Db.Properties.Add(new Property
            {
                Id = world._propertyId,
                OrgId = world._orgId,
                OwnerId = "auth0|owner",
                Name = "Casa Test",
                Address = "Via Test 1",
                City = "Roma",
                PostalCode = "00100",
                Country = "IT",
                Bedrooms = 1,
                Bathrooms = 1,
                MaxGuests = 2,
                NightlyRate = 100m,
            });
            await world.Db.SaveChangesAsync();
            return world;
        }

        public IdentityVerificationFlowService Flow(ILogger<IdentityVerificationFlowService>? logger = null)
        {
            var file = $"policy-{_pinThePolicy}.json";
            File.WriteAllText(Path.Combine(_directory, file), IdentityPolicyTestFiles.ShippedText(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var pin = _pinThePolicy ? IdentityPolicyTestFiles.ShippedCanonicalHash : new string('0', 64);
            var policy = new IdentityPolicyProvider(
                Options.Create(new IdentityVerificationOptions { PolicyFile = file, PolicySha256 = pin }),
                NullLogger<IdentityPolicyProvider>.Instance,
                _directory);

            return new IdentityVerificationFlowService(
                Db,
                Mock.Of<IIdentityVerificationAgent>(),
                Mock.Of<IVerificationOperatorAlerts>(),
                Mock.Of<IAccessCodeService>(),
                Mock.Of<IEmailQueue>(),
                Mock.Of<IMrzExtractionService>(),
                Mock.Of<IDocumentOcrService>(),
                new StayGuestService(Db, Mock.Of<IAlloggiatiCodeTableService>()),
                Mock.Of<IFileStorage>(),
                DataProtection,
                Clock,
                logger ?? NullLogger<IdentityVerificationFlowService>.Instance,
                legalTexts: Registry.Object,
                featureFlags: Flags,
                identityPolicy: policy,
                biometricModels: new FakeBiometricModelStore(_modelsReady),
                identityOptions: Options.Create(Limits),
                presenceIssuer: new PresenceChallengeIssuer());
        }

        public async Task<GuestCheckInSession> ReadyStayAsync()
        {
            var guest = new Guest
            {
                Id = Guid.NewGuid(),
                OrgId = _orgId,
                FirstName = "Anna",
                LastName = "Bianchi",
                Email = "anna@example.com",
                DateOfBirth = new DateTime(1990, 1, 1),
            };
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                OrgId = _orgId,
                PropertyId = _propertyId,
                GuestId = guest.Id,
                CheckInDate = new DateTime(2026, 11, 28),
                CheckOutDate = new DateTime(2026, 11, 30),
                Status = BookingStatus.Confirmed,
                Source = BookingSource.Direct,
                NumberOfGuests = 1,
                TotalPrice = 200m,
            };
            var session = new GuestCheckInSession
            {
                BookingId = booking.Id,
                OrgId = _orgId,
                TokenHash = Guid.NewGuid().ToString("N"),
                ExpiresAt = Now.UtcDateTime.AddDays(7),
                Status = GuestCheckInSessionStatus.InCompilazione,
                Language = "it",
                CreatedAt = Now.UtcDateTime,
                UpdatedAt = Now.UtcDateTime,
                BiometricConsentAt = Now.UtcDateTime.AddMinutes(-2),
                BiometricConsentVersion = "TEST-biometric-v1",
                BiometricConsentTextHash = new string('a', 64),
                IdentityOpenedAt = Now.UtcDateTime.AddMinutes(-3),
                IdentityChoice = IdentityChoices.Automatic,
                DocumentCheckJson = JsonSerializer.Serialize(DocumentCheckOf(), FlowJson),
            };
            Db.Guests.Add(guest);
            Db.Bookings.Add(booking);
            Db.GuestCheckInSessions.Add(session);
            await Db.SaveChangesAsync();

            return await Db.GuestCheckInSessions
                .Include(s => s.Booking).ThenInclude(b => b.Property)
                .Include(s => s.Booking).ThenInclude(b => b.Guest)
                .SingleAsync(s => s.Id == session.Id);
        }

        public async ValueTask DisposeAsync() => await Db.DisposeAsync();
    }
}
