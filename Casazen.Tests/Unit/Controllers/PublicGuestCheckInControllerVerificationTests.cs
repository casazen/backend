using System.Reflection;
using Casazen.Core.Entities;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.CheckIn;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// E03/F04/US02/T04: what the public endpoints of the identity verification answer to each outcome of the flow (the flag that closes them is the
/// <c>FeatureGate</c> of the integration test): the codes and the statuses the page translates, and nothing sent to the flow when the request is wrong.
/// </summary>
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
        _flow.Setup(f => f.SubmitSelfieAsync(It.IsAny<GuestCheckInSession>(), It.IsAny<Stream>(), It.IsAny<Stream?>(), It.IsAny<CancellationToken>()))
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
        _flow.Setup(f => f.SubmitSelfieAsync(It.IsAny<GuestCheckInSession>(), It.IsAny<Stream>(), It.IsAny<Stream?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SelfieStepResult(SelfieStepStatus.Done, IdentityVerificationGuestResults.Ok, [], 3, "7391"));

        var ok = Assert.IsType<OkObjectResult>((await Controller().UploadSelfie(Token, Image("selfie"))).Result);

        var response = Assert.IsType<SelfieUploadResponse>(ok.Value);
        Assert.Equal(IdentityVerificationGuestResults.Ok, response.Result);
        Assert.Equal("7391", response.LockCode);
        Assert.True(response.OperatorPathOpen);
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
        _flow.Setup(f => f.SubmitSelfieAsync(It.IsAny<GuestCheckInSession>(), It.IsAny<Stream>(), It.IsAny<Stream?>(), It.IsAny<CancellationToken>()))
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
    public void ReadDocument_BuffersThePhotoInMemory_AndAcceptsMultipartFramingAroundTheImageCap()
    {
        var method = typeof(PublicGuestCheckInController).GetMethod(nameof(PublicGuestCheckInController.ReadDocument))!;
        var formLimits = method.GetCustomAttribute<RequestFormLimitsAttribute>();
        var sizeLimit = method.GetCustomAttribute<RequestSizeLimitAttribute>();

        Assert.NotNull(formLimits);
        Assert.NotNull(sizeLimit);
        var requestBytes = Assert.IsType<long>(((IRequestSizeLimitMetadata)sizeLimit).MaxRequestBodySize);
        Assert.True(requestBytes > CheckInDocumentLimits.MaxImageBytes);
        Assert.True(formLimits.MemoryBufferThreshold >= requestBytes);
        Assert.True(formLimits.MultipartBodyLengthLimit >= requestBytes);
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
