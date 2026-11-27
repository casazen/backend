using System.Reflection;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Features;
using Casazen.Core.Identity;
using Casazen.Core.Services;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.CheckIn;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E73/F01/US01/T02 (NF-192, AC-NF192-4, ADR-013-R17): with <c>Features:CheckInIdentitySelfHosted</c> on the two-frame selfie of the legacy path is closed (its routes answer 409
/// <c>presence_sequence_required</c> and the flow itself runs no two-frame check) and the step after the document is <c>presence</c>; off, the selfie is the step, as before. And the
/// endpoint of the presence: its attributes, its key, its statuses and the body of its answer.
/// </summary>
public sealed class LegacySelfieClosedTests
{
    private const string Token = "token";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly Mock<IGuestCheckInService> _checkIn = new();
    private readonly Mock<IIdentityVerificationFlowService> _flow = new();

    [Fact]
    public async Task LegacySelfieClosed_WithSelfHosted_Returns409()
    {
        // Proof (security): the weaker two-frame check is not usable with the self-hosted flow on: both legacy routes refuse before anything is read, and the flow never runs.
        SetupOpenSession();
        var controller = Controller(new TestFeatureFlags(FeatureFlags.CheckInIdentityVerification, FeatureFlags.CheckInIdentitySelfHosted));

        AssertPresenceSequenceRequired((await controller.IssueSelfieChallenge(Token)).Result);
        AssertPresenceSequenceRequired((await controller.UploadSelfie(Token, Image("selfie"), turnedSelfie: Image("turnedSelfie"), challengeToken: "anything")).Result);

        _flow.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LegacySelfieClosed_WithSelfHostedOff_TheSelfieStepIsAsBefore()
    {
        SetupOpenSession();
        _flow.Setup(flow => flow.IssueSelfieChallengeAsync(It.IsAny<GuestCheckInSession>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SelfieChallenge("proof", true, DateTimeOffset.UtcNow.AddMinutes(2)));
        _flow.Setup(flow => flow.SubmitChallengedSelfieAsync(
                It.IsAny<GuestCheckInSession>(), It.IsAny<Stream>(), It.IsAny<Stream?>(), It.IsAny<string?>(), It.IsAny<Stream?>(), It.IsAny<Stream?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SelfieStepResult(SelfieStepStatus.Done, IdentityVerificationGuestResults.Retry, [], 2, null));
        var controller = Controller(new TestFeatureFlags(FeatureFlags.CheckInIdentityVerification));

        Assert.IsType<OkObjectResult>((await controller.IssueSelfieChallenge(Token)).Result);
        Assert.IsType<OkObjectResult>((await controller.UploadSelfie(Token, Image("selfie"), turnedSelfie: Image("turnedSelfie"), challengeToken: "proof")).Result);
        _flow.Verify(flow => flow.IssueSelfieChallengeAsync(It.IsAny<GuestCheckInSession>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LegacySelfieClosed_TheFlowItself_RunsNoTwoFrameCheck_AndTheStepIsThePresence()
    {
        // Defence in depth: called directly (not through the routes), the flow with the self-hosted flow on issues no pose challenge, reads and compares nothing and writes nothing;
        // the step after a complete document is the presence. With the flow off the same session is at the selfie.
        await using var world = await PresenceClosureWorld.CreateAsync();
        var session = await world.ReadyStayAsync();
        var before = (await world.ReadBackAsync(session.Id)).DocumentCheckJson;
        var flow = world.Flow();

        Assert.Null(await flow.IssueSelfieChallengeAsync(session, Ct));
        using var selfie = new MemoryStream([1, 2, 3]);
        using var turned = new MemoryStream([4, 5, 6]);
        var answer = await flow.SubmitChallengedSelfieAsync(session, selfie, turned, "anything", null, null, Ct);

        Assert.Equal((SelfieStepStatus.ChallengeRequired, IdentityVerificationGuestResults.Retry), (answer.Status, answer.Result));
        var after = await world.ReadBackAsync(session.Id);
        Assert.Equal((before, 0, (DateTime?)null), (after.DocumentCheckJson, after.BiometricAttempts, after.OperatorRequestedAt));
        Assert.Empty(await world.LogRowsAsync(session));
        Assert.Equal(IdentityVerificationSteps.Presence, (await flow.GetStateAsync(session, Ct)).Step);

        world.Flags.Set(FeatureFlags.CheckInIdentitySelfHosted, false);
        Assert.Equal(IdentityVerificationSteps.Selfie, (await world.Flow().GetStateAsync(session, Ct)).Step);
    }

    [Fact]
    public void PresenceEndpoint_IsAPostBehindBothFlags_RateLimitedForUploads_NeverCached_AndBufferedInMemoryUnderAHardCap()
    {
        var method = typeof(PublicGuestCheckInController).GetMethod(nameof(PublicGuestCheckInController.SubmitPresence))!;

        Assert.Equal("{token}/presence", Assert.Single(method.GetCustomAttributes<HttpPostAttribute>()).Template);
        Assert.Equal(
            new[] { FeatureFlags.CheckInIdentitySelfHosted, FeatureFlags.CheckInIdentityVerification }.Order(),
            method.GetCustomAttributes<FeatureGateAttribute>().Select(gate => gate.Flag).Order());
        Assert.Equal(RateLimitPolicies.GuestIdentityUpload, Assert.Single(method.GetCustomAttributes<EnableRateLimitingAttribute>()).PolicyName);
        var cache = Assert.Single(method.GetCustomAttributes<ResponseCacheAttribute>());
        Assert.True(cache.NoStore);

        // AC7: 12 MB and 64 KB at most, all of it buffered in memory (never a temporary file); the configured limits of the sequence are checked by the reader under it.
        Assert.Equal((12 * 1024 * 1024) + (64 * 1024), PublicGuestCheckInController.MaxPresenceRequestBytes);
        Assert.Equal(
            (long?)PublicGuestCheckInController.MaxPresenceRequestBytes,
            ((IRequestSizeLimitMetadata)Assert.Single(method.GetCustomAttributes<RequestSizeLimitAttribute>())).MaxRequestBodySize);
        var form = Assert.Single(method.GetCustomAttributes<RequestFormLimitsAttribute>());
        Assert.Equal(PublicGuestCheckInController.MaxPresenceRequestBytes, form.MemoryBufferThreshold);
        Assert.Equal(PublicGuestCheckInController.MaxPresenceRequestBytes, form.MultipartBodyLengthLimit);

        // Anonymous like its neighbours (the token of the link is the proof), and no authorization of its own.
        Assert.NotNull(typeof(PublicGuestCheckInController).GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Empty(method.GetCustomAttributes<AuthorizeAttribute>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("has spaces in it")]
    [InlineData("a,b,c,d,e,f,g,h")]
    public async Task PresenceEndpoint_WithoutAValidKey_Answers400_AndTheFlowNeverRuns(string? key)
    {
        SetupOpenSession();
        var presence = new Mock<IIdentityPresenceFlow>();

        var answer = await SelfHostedController().SubmitPresence(Token, presence.Object, [Image("frames", "image/jpeg")], "[0]", "proof", idempotencyKey: key);

        var problem = Problem(answer.Result, StatusCodes.Status400BadRequest);
        Assert.Equal(IdentityApiErrors.IdempotencyKeyRequired, problem.Extensions["code"]);
        presence.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(PresenceStepStatus.ChallengeRequired, StatusCodes.Status409Conflict, "presence_challenge_required")]
    [InlineData(PresenceStepStatus.ChallengeExpired, StatusCodes.Status409Conflict, "presence_challenge_expired")]
    [InlineData(PresenceStepStatus.ConsentRequired, StatusCodes.Status409Conflict, "biometric_consent_required")]
    [InlineData(PresenceStepStatus.DocumentRequired, StatusCodes.Status409Conflict, "document_check_required")]
    [InlineData(PresenceStepStatus.DocumentPhotoRequired, StatusCodes.Status400BadRequest, "document_photo_required")]
    [InlineData(PresenceStepStatus.DocumentPhotoMismatch, StatusCodes.Status409Conflict, "document_photo_mismatch")]
    [InlineData(PresenceStepStatus.SequenceTooLarge, StatusCodes.Status413PayloadTooLarge, "presence_sequence_too_large")]
    [InlineData(PresenceStepStatus.SequenceInvalid, StatusCodes.Status422UnprocessableEntity, "presence_sequence_invalid")]
    [InlineData(PresenceStepStatus.AlreadyClosed, StatusCodes.Status409Conflict, "identity_already_closed")]
    [InlineData(PresenceStepStatus.IdempotencyKeyRequired, StatusCodes.Status400BadRequest, "idempotency_key_required")]
    public async Task PresenceEndpoint_AStepThatWasNotRun_AnswersItsCode(PresenceStepStatus status, int httpStatus, string code)
    {
        SetupOpenSession();
        var legacy = new IdentityVerificationState(IdentityVerificationSteps.Operator, IdentityVerificationGuestResults.Operator, true, true, false, true, 0, true, false, 0);
        var state = legacy with { SelfHosted = true, Opened = true, Closed = true, Outcome = IdentityVerificationGuestResults.Operator };
        var presence = new Mock<IIdentityPresenceFlow>();
        presence.Setup(flow => flow.SubmitPresenceSequenceAsync(It.IsAny<GuestCheckInSession>(), It.IsAny<PresenceSubmission>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PresenceStepResult(status, null, [], false, status == PresenceStepStatus.AlreadyClosed ? state : null));

        var answer = await SelfHostedController().SubmitPresence(Token, presence.Object, [Image("frames", "image/jpeg")], "[0]", "proof", idempotencyKey: Guid.NewGuid().ToString());

        var problem = Problem(answer.Result, httpStatus);
        Assert.Equal(code, problem.Extensions["code"]);
        if (status == PresenceStepStatus.AlreadyClosed)
            Assert.True(Assert.IsType<PublicIdentityVerificationStateDto>(problem.Extensions[PublicGuestCheckInController.IdentityStateExtension]).Closed);
    }

    [Fact]
    public async Task PresenceEndpoint_TheFramesTheKeyAndThePicturesReachTheFlow_AndTheAnswerIsTheBodyOfTheContract()
    {
        SetupOpenSession();
        PresenceSubmission? seen = null;
        var frameCount = 0;
        var legacy = new IdentityVerificationState(IdentityVerificationSteps.Presence, IdentityVerificationGuestResults.Retry, true, true, false, true, 3, false, false, 0);
        var presence = new Mock<IIdentityPresenceFlow>();
        presence.Setup(flow => flow.SubmitPresenceSequenceAsync(It.IsAny<GuestCheckInSession>(), It.IsAny<PresenceSubmission>(), It.IsAny<CancellationToken>()))
            .Callback<GuestCheckInSession, PresenceSubmission, CancellationToken>((_, submission, _) =>
            {
                seen = submission;
                frameCount = submission.Frames.Count(frame => frame.Content.CanRead && frame.Length > 0);
            })
            .ReturnsAsync(new PresenceStepResult(
                PresenceStepStatus.Done,
                IdentityVerificationGuestResults.Retry,
                [IdentityGuidanceCodes.PresenceTooShort],
                true,
                legacy with { SelfHosted = true, Opened = true }));
        var key = Guid.NewGuid().ToString();
        var frames = new[] { new TrackedFile("frames", "image/jpeg"), new TrackedFile("frames", "image/jpeg") };
        var document = new TrackedFile("documentPhoto", "image/png");

        var answer = await SelfHostedController().SubmitPresence(Token, presence.Object, [.. frames], "[0,125]", "proof", documentPhoto: document, idempotencyKey: key);

        Assert.NotNull(seen);
        Assert.Equal((key, "proof", "[0,125]", 2), (seen.IdempotencyKey, seen.ChallengeToken, seen.TimestampsJson, frameCount));
        Assert.NotNull(seen.DocumentPhoto);
        Assert.Null(seen.DocumentPhotoBack);

        // The streams of the parts are closed when the request ends.
        Assert.All(frames, frame => Assert.True(frame.Closed));
        Assert.True(document.Closed);

        var body = Assert.IsType<PresenceStepResponseDto>(Assert.IsType<OkObjectResult>(answer.Result).Value);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(["guidance", "guidedRetryAvailable", "result", "state"], json.RootElement.EnumerateObject().Select(member => member.Name).Order());
        Assert.Equal("retry", json.RootElement.GetProperty("result").GetString());
        Assert.Equal(["presence_too_short"], json.RootElement.GetProperty("guidance").EnumerateArray().Select(code => code.GetString()));
        Assert.True(json.RootElement.GetProperty("guidedRetryAvailable").GetBoolean());
        Assert.Equal("presence", json.RootElement.GetProperty("state").GetProperty("step").GetString());
    }

    [Fact]
    public async Task PresenceEndpoint_TheFlowFails_Answers500WithoutTheError_AndAPictureOfAnotherKindIsRefused()
    {
        SetupOpenSession();
        var presence = new Mock<IIdentityPresenceFlow>();
        presence.Setup(flow => flow.SubmitPresenceSequenceAsync(It.IsAny<GuestCheckInSession>(), It.IsAny<PresenceSubmission>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("storage is down: the guest is Mario Rossi"));
        var controller = SelfHostedController();

        var failed = await controller.SubmitPresence(Token, presence.Object, [Image("frames", "image/jpeg")], "[0]", "proof", idempotencyKey: Guid.NewGuid().ToString());

        var problem = Problem(failed.Result, StatusCodes.Status500InternalServerError);
        Assert.Equal("presence_processing_failed", problem.Extensions["code"]);
        Assert.DoesNotContain("Mario", problem.Detail ?? string.Empty, StringComparison.Ordinal);

        var wrongKind = await controller.SubmitPresence(
            Token, presence.Object, [Image("frames", "image/jpeg")], "[0]", "proof", documentPhoto: Image("documentPhoto", "application/pdf"), idempotencyKey: Guid.NewGuid().ToString());
        Assert.Equal("document_photo_type_unsupported", Problem(wrongKind.Result, StatusCodes.Status400BadRequest).Extensions["code"]);
    }

    [Fact]
    public async Task PresenceEndpoint_WithAFlagOff_IsNotThere_AndAnUnknownLinkIs404()
    {
        var presence = new Mock<IIdentityPresenceFlow>();
        SetupOpenSession();

        var off = await Controller(new TestFeatureFlags(FeatureFlags.CheckInIdentityVerification)).SubmitPresence(
            Token, presence.Object, [Image("frames", "image/jpeg")], "[0]", "proof", idempotencyKey: Guid.NewGuid().ToString());
        Assert.IsType<NotFoundResult>(off.Result);

        _checkIn.Setup(service => service.GetSessionByTokenAsync(Token)).ReturnsAsync((GuestCheckInSession?)null);
        var unknown = await SelfHostedController().SubmitPresence(Token, presence.Object, [Image("frames", "image/jpeg")], "[0]", "proof", idempotencyKey: Guid.NewGuid().ToString());
        Assert.IsType<NotFoundResult>(unknown.Result);
        presence.VerifyNoOtherCalls();
    }

    private static void AssertPresenceSequenceRequired(IActionResult? result)
    {
        var problem = Problem(result, StatusCodes.Status409Conflict);
        Assert.Equal(IdentityApiErrors.PresenceSequenceRequired, problem.Extensions["code"]);
    }

    private static ProblemDetails Problem(IActionResult? result, int status)
    {
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(status, objectResult.StatusCode);
        return Assert.IsType<ProblemDetails>(objectResult.Value);
    }

    private static FormFile Image(string field, string contentType = "image/png")
    {
        var bytes = field == "frames" ? TestJpeg.Noise(16, 16, 3) : [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, field, $"{field}.bin")
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType,
        };
    }

    /// <summary>A part of the form whose stream says when it was closed.</summary>
    private sealed class TrackedFile(string name, string contentType) : IFormFile
    {
        private readonly byte[] _bytes = TestJpeg.Noise(16, 16, 5);
        private TrackedStream? _stream;

        public bool Closed => _stream?.Closed == true;

        public string ContentType => contentType;

        public string ContentDisposition => $"form-data; name=\"{name}\"; filename=\"{name}.bin\"";

        public IHeaderDictionary Headers { get; } = new HeaderDictionary();

        public long Length => _bytes.Length;

        public string Name => name;

        public string FileName => $"{name}.bin";

        public Stream OpenReadStream() => _stream = new TrackedStream(_bytes);

        public void CopyTo(Stream target) => target.Write(_bytes);

        public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default) => target.WriteAsync(_bytes, cancellationToken).AsTask();
    }

    private sealed class TrackedStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public bool Closed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Closed = true;
            base.Dispose(disposing);
        }
    }

    private void SetupOpenSession() =>
        _checkIn.Setup(service => service.GetSessionByTokenAsync(Token))
            .ReturnsAsync(new GuestCheckInSession { Id = Guid.NewGuid(), BookingId = Guid.NewGuid(), OrgId = Guid.NewGuid(), IdentityOpenedAt = DateTime.UtcNow });

    private PublicGuestCheckInController SelfHostedController() =>
        Controller(new TestFeatureFlags(FeatureFlags.CheckInIdentityVerification, FeatureFlags.CheckInIdentitySelfHosted));

    private PublicGuestCheckInController Controller(IFeatureFlags flags) => new(
        _checkIn.Object,
        Mock.Of<IAlloggiatiCodeTableService>(),
        Mock.Of<ICheckInDocumentReader>(),
        Mock.Of<IAlloggiatiReportScheduler>(),
        _flow.Object,
        flags,
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
