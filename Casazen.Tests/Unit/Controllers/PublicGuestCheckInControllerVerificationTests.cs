using Casazen.Core.Entities;
using Casazen.Core.Features;
using Casazen.Core.Identity;
using Casazen.Core.Services;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.CheckIn;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

public class PublicGuestCheckInControllerVerificationTests
{
    private const string Token = "token";

    private readonly Mock<IGuestCheckInService> _checkIn = new();
    private readonly Mock<IIdentityVerificationFlowService> _flow = new();

    [Theory]
    [InlineData(SelfieStepStatus.ConsentRequired, StatusCodes.Status409Conflict, "biometric_consent_required")]
    [InlineData(SelfieStepStatus.DocumentRequired, StatusCodes.Status409Conflict, "document_check_required")]
    [InlineData(SelfieStepStatus.DocumentPhotoRequired, StatusCodes.Status400BadRequest, "document_photo_required")]
    [InlineData(SelfieStepStatus.DocumentPhotoMismatch, StatusCodes.Status409Conflict, "document_photo_mismatch")]
    public async Task UploadSelfie_AStepThatDidNotRun_AnswersItsStatusAndNoCode(SelfieStepStatus status, int httpStatus, string code)
    {
        SetupSession();
        _flow.Setup(f => f.SubmitChallengedSelfieAsync(It.IsAny<GuestCheckInSession>(), It.IsAny<Stream>(), It.IsAny<Stream?>(), It.IsAny<string?>(), It.IsAny<Stream?>(), It.IsAny<Stream?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SelfieStepResult(status, IdentityVerificationGuestResults.Retry, [], 3, null));

        var result = await Controller().UploadSelfie(Token, Image("selfie"));

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(httpStatus, problem.StatusCode);
        Assert.Equal(code, Assert.IsType<ProblemDetails>(problem.Value).Extensions["code"]);
    }

    [Fact]
    public async Task UploadSelfie_ADecision_AnswersTheResultAndTheCodeOnlyOnOk()
    {
        SetupSession();
        _flow.Setup(f => f.SubmitChallengedSelfieAsync(It.IsAny<GuestCheckInSession>(), It.IsAny<Stream>(), It.IsAny<Stream?>(), It.IsAny<string?>(), It.IsAny<Stream?>(), It.IsAny<Stream?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SelfieStepResult(SelfieStepStatus.Done, IdentityVerificationGuestResults.Ok, [], 3, "7391"));

        var ok = Assert.IsType<OkObjectResult>((await Controller().UploadSelfie(Token, Image("selfie"))).Result);

        var response = Assert.IsType<SelfieUploadResponse>(ok.Value);
        Assert.Equal(IdentityVerificationGuestResults.Ok, response.Result);
        Assert.Equal("7391", response.LockCode);
        Assert.True(response.OperatorPathOpen);
    }

    [Fact]
    public async Task UploadSelfie_OkWithTheCodeHeldBack_AnswersWhyAndNoCode()
    {
        SetupSession();
        _flow.Setup(f => f.SubmitChallengedSelfieAsync(It.IsAny<GuestCheckInSession>(), It.IsAny<Stream>(), It.IsAny<Stream?>(), It.IsAny<string?>(), It.IsAny<Stream?>(), It.IsAny<Stream?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SelfieStepResult(SelfieStepStatus.Done, IdentityVerificationGuestResults.Ok, [], 3, null, AccessCodeHoldReasons.TouristTaxDue));

        var ok = Assert.IsType<OkObjectResult>((await Controller().UploadSelfie(Token, Image("selfie"))).Result);

        var response = Assert.IsType<SelfieUploadResponse>(ok.Value);
        Assert.Equal(IdentityVerificationGuestResults.Ok, response.Result);
        Assert.Null(response.LockCode);
        Assert.Equal("tourist_tax_due", response.LockCodeHeld);
        var json = System.Text.Json.JsonSerializer.Serialize(response, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.DoesNotContain("\"lockCode\":", json, StringComparison.Ordinal);
        Assert.Contains("\"lockCodeHeld\":\"tourist_tax_due\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadSelfie_NotAPicture_Answers400AndTheFlowNeverSeesIt()
    {
        SetupSession();

        var result = await Controller().UploadSelfie(Token, Image("selfie", "text/plain"));

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Equal("selfie_type_unsupported", Assert.IsType<ProblemDetails>(problem.Value).Extensions["code"]);
        _flow.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UploadSelfie_TheFlowFails_Answers500WithoutTheError()
    {
        SetupSession();
        _flow.Setup(f => f.SubmitChallengedSelfieAsync(It.IsAny<GuestCheckInSession>(), It.IsAny<Stream>(), It.IsAny<Stream?>(), It.IsAny<string?>(), It.IsAny<Stream?>(), It.IsAny<Stream?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("storage is down: the name of the guest is Mario Rossi"));

        var result = await Controller().UploadSelfie(Token, Image("selfie"));

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status500InternalServerError, problem.StatusCode);
        var details = Assert.IsType<ProblemDetails>(problem.Value);
        Assert.Equal("selfie_processing_failed", details.Extensions["code"]);
        Assert.DoesNotContain("Mario", details.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadDocument_NotAPicture_Answers400AndTheFlowNeverSeesIt()
    {
        SetupSession();

        var result = await Controller().UploadDocument(Token, Image("document", "application/pdf"));

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Equal("document_type_unsupported", Assert.IsType<ProblemDetails>(problem.Value).Extensions["code"]);
        _flow.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RecordBiometricConsent_NoAnswer_Answers400AndNothingIsRecorded()
    {
        SetupSession();

        var result = await Controller().RecordBiometricConsent(Token, new BiometricConsentRequest { ConsentGiven = null, ConsentVersion = "v1" });

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Equal("biometric_consent_answer_required", Assert.IsType<ProblemDetails>(problem.Value).Extensions["code"]);
        _flow.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RecordBiometricConsent_ConsentWithoutItsVersion_Answers400()
    {
        SetupSession();
        _flow.Setup(f => f.RecordConsentAsync(It.IsAny<GuestCheckInSession>(), true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BiometricConsentResult(BiometricConsentStatus.VersionRequired, State()));

        var result = await Controller().RecordBiometricConsent(Token, new BiometricConsentRequest { ConsentGiven = true });

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Equal("biometric_consent_version_required", Assert.IsType<ProblemDetails>(problem.Value).Extensions["code"]);
    }

    [Fact]
    public async Task RecordBiometricConsent_RefusalOrNotOffered_AnswersOperatorAndNotRecorded()
    {
        SetupSession();
        _flow.Setup(f => f.RecordConsentAsync(It.IsAny<GuestCheckInSession>(), false, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BiometricConsentResult(BiometricConsentStatus.Refused, State()));

        var ok = Assert.IsType<OkObjectResult>((await Controller().RecordBiometricConsent(Token, new BiometricConsentRequest { ConsentGiven = false })).Result);

        var response = Assert.IsType<BiometricConsentResponse>(ok.Value);
        Assert.False(response.ConsentRecorded);
        Assert.Equal(IdentityVerificationGuestResults.Operator, response.Result);
        Assert.True(response.OperatorPathOpen);
    }

    [Fact]
    public async Task TheVerificationEndpoints_UnknownOrClosedLink_Answer404AndTheFlowNeverRuns()
    {
        _checkIn.Setup(s => s.GetSessionByTokenAsync(Token)).ReturnsAsync((GuestCheckInSession?)null);
        var controller = Controller();

        Assert.IsType<NotFoundResult>((await controller.RecordBiometricConsent(Token, new BiometricConsentRequest { ConsentGiven = true, ConsentVersion = "v1" })).Result);
        Assert.IsType<NotFoundResult>((await controller.UploadDocument(Token, Image("document"))).Result);
        Assert.IsType<NotFoundResult>((await controller.UploadSelfie(Token, Image("selfie"))).Result);
        Assert.IsType<NotFoundResult>((await controller.RequestOperator(Token)).Result);
        _flow.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task OpenIdentity_EachOutcomeOfTheFlow_AnswersItsStatus()
    {
        SetupSession();
        var closedState = State() with { SelfHosted = true, Opened = true, Closed = true, Outcome = IdentityVerificationGuestResults.Operator };
        _flow.SetupSequence(f => f.OpenIdentityAsync(It.IsAny<GuestCheckInSession>(), "v1", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IdentityOpenResult(IdentityOpenStatus.Answered, State() with { SelfHosted = true, Opened = true, NoticeVersion = "v1" }))
            .ReturnsAsync(new IdentityOpenResult(IdentityOpenStatus.NoticeVersionUnknown, State()))
            .ReturnsAsync(new IdentityOpenResult(IdentityOpenStatus.Closed, closedState))
            .ReturnsAsync(new IdentityOpenResult(IdentityOpenStatus.SessionUnavailable, null));
        var controller = SelfHostedController();
        var request = new IdentityOpenRequest { NoticeVersion = "v1" };

        var opened = Assert.IsType<IdentityOpenResponse>(Assert.IsType<OkObjectResult>((await controller.OpenIdentity(Token, request)).Result).Value);
        Assert.True(opened.State.SelfHosted);
        Assert.True(opened.State.Opened);
        Assert.Equal("v1", opened.State.NoticeVersion);

        var unknown = Assert.IsType<ObjectResult>((await controller.OpenIdentity(Token, request)).Result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, unknown.StatusCode);
        Assert.Equal("notice_version_unknown", Assert.IsType<ProblemDetails>(unknown.Value).Extensions["code"]);

        var closed = Assert.IsType<ObjectResult>((await controller.OpenIdentity(Token, request)).Result);
        Assert.Equal(StatusCodes.Status409Conflict, closed.StatusCode);
        var details = Assert.IsType<ProblemDetails>(closed.Value);
        Assert.Equal("identity_already_closed", details.Extensions["code"]);
        var state = Assert.IsType<PublicIdentityVerificationStateDto>(details.Extensions[PublicGuestCheckInController.IdentityStateExtension]);
        Assert.True(state.Closed);
        Assert.Equal(IdentityVerificationGuestResults.Operator, state.Outcome);

        Assert.IsType<NotFoundResult>((await controller.OpenIdentity(Token, request)).Result);
    }

    [Fact]
    public async Task OpenIdentity_SelfHostedOff_Answers404_AndTheFlowNeverRuns()
    {
        SetupSession();

        var result = await SelfHostedController(new TestFeatureFlags(FeatureFlags.CheckInIdentityVerification)).OpenIdentity(Token, new IdentityOpenRequest { NoticeVersion = "v1" });

        Assert.IsType<NotFoundResult>(result.Result);
        _flow.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task OpenIdentityAndConsent_TheFlowGetsTheFamilyOfTheBrowser_NeverItsUserAgent()
    {
        SetupSession();
        var seen = new List<string?>();
        _flow.Setup(f => f.OpenIdentityAsync(It.IsAny<GuestCheckInSession>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<GuestCheckInSession, string?, string?, CancellationToken>((_, _, channel, _) => seen.Add(channel))
            .ReturnsAsync(new IdentityOpenResult(IdentityOpenStatus.Answered, State()));
        _flow.Setup(f => f.RecordConsentAsync(It.IsAny<GuestCheckInSession>(), true, "v1", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<GuestCheckInSession, bool, string?, string?, CancellationToken>((_, _, _, channel, _) => seen.Add(channel))
            .ReturnsAsync(new BiometricConsentResult(BiometricConsentStatus.Recorded, State()));
        var controller = SelfHostedController(userAgent: "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:123.0) Gecko/20100101 Firefox/123.0");

        Assert.IsType<OkObjectResult>((await controller.OpenIdentity(Token, new IdentityOpenRequest { NoticeVersion = "v1" })).Result);
        Assert.IsType<OkObjectResult>((await controller.RecordBiometricConsent(Token, new BiometricConsentRequest { ConsentGiven = true, ConsentVersion = "v1" })).Result);

        Assert.Equal([IdentityChannels.DesktopFirefox, IdentityChannels.DesktopFirefox], seen);
        _flow.Verify(f => f.RecordConsentAsync(It.IsAny<GuestCheckInSession>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UploadDocument_AClosedSelfHostedSession_Answers409WithTheState_AndTheFlowNeverSeesThePicture()
    {
        _checkIn.Setup(s => s.GetSessionByTokenAsync(Token)).ReturnsAsync(new GuestCheckInSession
        {
            Id = Guid.NewGuid(),
            BookingId = Guid.NewGuid(),
            OrgId = Guid.NewGuid(),
            IdentityOpenedAt = DateTime.UtcNow.AddMinutes(-5),
            IdentityClosedAt = DateTime.UtcNow,
            IdentityOutcomeCode = IdentityOutcomeCodes.Review,
        });
        _flow.Setup(f => f.GetStateAsync(It.IsAny<GuestCheckInSession>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(State() with { SelfHosted = true, Opened = true, Closed = true, Outcome = IdentityVerificationGuestResults.Operator });

        var result = await SelfHostedController().UploadDocument(Token, Image("document"));

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, problem.StatusCode);
        Assert.Equal("identity_already_closed", Assert.IsType<ProblemDetails>(problem.Value).Extensions["code"]);
        _flow.Verify(f => f.SubmitDocumentAsync(It.IsAny<GuestCheckInSession>(), It.IsAny<Stream>(), It.IsAny<DocumentSide>(), It.IsAny<CancellationToken>()), Times.Never);

        _flow.Setup(f => f.SubmitDocumentAsync(It.IsAny<GuestCheckInSession>(), It.IsAny<Stream>(), DocumentSide.Single, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentStepResult(DocumentReading.Nothing, false, [], false, false));
        Assert.IsType<OkObjectResult>((await Controller().UploadDocument(Token, Image("document"))).Result);
    }

    [Fact]
    public void OpenIdentity_IsBehindBothFlags_TheIdentityRateLimit_AndNeverCached()
    {
        var method = typeof(PublicGuestCheckInController).GetMethod(nameof(PublicGuestCheckInController.OpenIdentity))!;

        Assert.Equal(
            new[] { FeatureFlags.CheckInIdentityVerification, FeatureFlags.CheckInIdentitySelfHosted }.Order(StringComparer.Ordinal),
            method.GetCustomAttributes(typeof(FeatureGateAttribute), inherit: true).Cast<FeatureGateAttribute>().Select(gate => gate.Flag).Order(StringComparer.Ordinal));
        Assert.Equal(RateLimitPolicies.GuestIdentity, Assert.IsType<EnableRateLimitingAttribute>(Assert.Single(method.GetCustomAttributes(typeof(EnableRateLimitingAttribute), inherit: true))).PolicyName);
        Assert.True(Assert.IsType<ResponseCacheAttribute>(Assert.Single(method.GetCustomAttributes(typeof(ResponseCacheAttribute), inherit: true))).NoStore);
        Assert.Equal("{token}/identity/open", Assert.IsType<HttpPostAttribute>(Assert.Single(method.GetCustomAttributes(typeof(HttpPostAttribute), inherit: true))).Template);
    }

    private PublicGuestCheckInController SelfHostedController(IFeatureFlags? flags = null, string? userAgent = null)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
        };
        if (userAgent is not null)
            context.Request.Headers.UserAgent = userAgent;

        return new PublicGuestCheckInController(
            _checkIn.Object,
            Mock.Of<IAlloggiatiCodeTableService>(),
            Mock.Of<ICheckInDocumentReader>(),
            Mock.Of<IAlloggiatiReportScheduler>(),
            _flow.Object,
            flags ?? new TestFeatureFlags(FeatureFlags.CheckInIdentityVerification, FeatureFlags.CheckInIdentitySelfHosted),
            Mock.Of<IStringLocalizer<SharedResources>>(),
            NullLogger<PublicGuestCheckInController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
        };
    }

    private static IdentityVerificationState State() =>
        new(IdentityVerificationSteps.Operator, IdentityVerificationGuestResults.Operator, true, false, true, false, 3, true, false, 0);

    private static FormFile Image(string field, string contentType = "image/png")
    {
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, field, $"{field}.png")
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType,
        };
    }

    private void SetupSession() =>
        _checkIn.Setup(s => s.GetSessionByTokenAsync(Token)).ReturnsAsync(new GuestCheckInSession { Id = Guid.NewGuid(), BookingId = Guid.NewGuid(), OrgId = Guid.NewGuid() });

    private PublicGuestCheckInController Controller() => new(
        _checkIn.Object,
        Mock.Of<IAlloggiatiCodeTableService>(),
        Mock.Of<ICheckInDocumentReader>(),
        Mock.Of<IAlloggiatiReportScheduler>(),
        _flow.Object,
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
