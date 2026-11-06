using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Security.Claims;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// E05/F03 (NF-65): <c>api/me/notification-preferences</c> acts for the caller only (the token's <c>sub</c>, never a field of the body), maps the
/// channels of the body to the flags of the service, lets the refusals of the service (422 <c>notification_legal_channel_required</c>,
/// <c>notification_channel_unavailable</c>, <c>notification_preferences_empty</c>, <c>notification_event_duplicated</c>; 404 for a caller with no
/// host org) reach the error handling untouched, and answers 400 to a malformed body before the action runs. The rules themselves are tested with
/// the service.
/// </summary>
public class MeNotificationPreferencesControllerTests
{
    private const string UserId = "auth0|anna";

    private readonly Mock<INotificationPreferenceService> _service = new(MockBehavior.Strict);

    private MeNotificationPreferencesController Controller(string? sub = UserId)
    {
        var http = new DefaultHttpContext();
        if (sub is not null)
            http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", sub)], "test"));

        return new MeNotificationPreferencesController(_service.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private static NotificationPreferencesView AView() => new(
        new QuietHoursSettings(new TimeOnly(22, 0), new TimeOnly(8, 0), true),
        [
            new NotificationEventSetting(NotificationEventType.NewGuestMessage, NotificationChannels.CounterOnly | NotificationChannels.MobilePush, true),
            new NotificationEventSetting(NotificationEventType.LegalDeadline, NotificationChannels.Email, false),
        ],
        [
            new NotificationChannelAvailability(NotificationChannels.MobilePush, true, null),
            new NotificationChannelAvailability(NotificationChannels.BrowserPush, false, "web_push_not_available"),
        ]);

    private static SaveNotificationPreferencesRequest OneEvent() => new()
    {
        Events = [new NotificationEventRequest { EventType = NotificationEventType.NewGuestMessage, Channels = [NotificationChannels.CounterOnly] }],
    };

    // ─── The contract ───

    [Fact]
    public void Controller_IsForSignedInUsersOnly_UnderApiMeNotificationPreferences()
    {
        var type = typeof(MeNotificationPreferencesController);

        Assert.Equal("api/me/notification-preferences", type.GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.Equal(CasazenPolicies.Authenticated, Assert.Single(type.GetCustomAttributes<AuthorizeAttribute>()).Policy);
        Assert.Empty(type.GetCustomAttributes<AllowAnonymousAttribute>());

        // [ApiController] is what answers 400 to a body that does not validate, before the action runs; the answers are private.
        Assert.NotNull(type.GetCustomAttribute<ApiControllerAttribute>());
        Assert.Contains(type.GetCustomAttributes(inherit: true), a => a.GetType().Name == "PrivateAnswerAttribute");
    }

    [Fact]
    public void Actions_AreGetAndPutOfTheSameRoute_AndDocumentTheirRefusals()
    {
        var type = typeof(MeNotificationPreferencesController);
        var get = type.GetMethod(nameof(MeNotificationPreferencesController.Get))!;
        var save = type.GetMethod(nameof(MeNotificationPreferencesController.Save))!;

        Assert.Equal(["GET"], Assert.Single(get.GetCustomAttributes<HttpMethodAttribute>()).HttpMethods);
        Assert.Null(Assert.Single(get.GetCustomAttributes<HttpMethodAttribute>()).Template);
        Assert.Equal(["PUT"], Assert.Single(save.GetCustomAttributes<HttpMethodAttribute>()).HttpMethods);
        Assert.Null(Assert.Single(save.GetCustomAttributes<HttpMethodAttribute>()).Template);
        Assert.Equal(
            [StatusCodes.Status200OK, StatusCodes.Status401Unauthorized, StatusCodes.Status404NotFound, StatusCodes.Status422UnprocessableEntity],
            save.GetCustomAttributes<ProducesResponseTypeAttribute>().Select(a => a.StatusCode).Order());
    }

    // ─── GET ───

    [Fact]
    public async Task Get_GivesTheViewOfTheCaller_AsTheContractSays()
    {
        _service.Setup(s => s.GetAsync(UserId, It.IsAny<CancellationToken>())).ReturnsAsync(AView());

        var result = await Controller().Get(CancellationToken.None);

        var dto = Assert.IsType<NotificationPreferencesDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(new TimeOnly(22, 0), dto.QuietHours.Start);
        Assert.Equal(new TimeOnly(8, 0), dto.QuietHours.End);
        Assert.True(dto.QuietHours.LegalAlertsOverride);

        // One name for each channel, in the order of the page, and «chose nothing» marked as the default.
        var message = Assert.Single(dto.Events, e => e.EventType == NotificationEventType.NewGuestMessage);
        Assert.Equal([NotificationChannels.MobilePush, NotificationChannels.CounterOnly], message.Channels);
        Assert.True(message.IsDefault);
        var legal = Assert.Single(dto.Events, e => e.EventType == NotificationEventType.LegalDeadline);
        Assert.Equal([NotificationChannels.Email], legal.Channels);
        Assert.False(legal.IsDefault);
        var browser = Assert.Single(dto.AvailableChannels, c => c.Channel == NotificationChannels.BrowserPush);
        Assert.False(browser.Available);
        Assert.Equal("web_push_not_available", browser.Reason);
    }

    [Fact]
    public async Task Get_WithoutASub_IsUnauthorized_AndTheServiceIsNeverAsked()
    {
        var result = await Controller(sub: null).Get(CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result.Result);
        _service.VerifyNoOtherCalls();
    }

    // ─── PUT ───

    [Fact]
    public async Task Save_MapsTheChannelsOfEachKindToTheFlagsOfTheService_ForTheCallerOnly()
    {
        NotificationPreferencesUpdate? sent = null;
        _service
            .Setup(s => s.SaveAsync(UserId, It.IsAny<NotificationPreferencesUpdate>(), It.IsAny<CancellationToken>()))
            .Callback<string, NotificationPreferencesUpdate, CancellationToken>((_, update, _) => sent = update)
            .ReturnsAsync(AView());
        var request = new SaveNotificationPreferencesRequest
        {
            QuietHours = new QuietHoursRequest { Start = new TimeOnly(22, 30), End = new TimeOnly(7, 0) },
            Events =
            [
                new NotificationEventRequest
                {
                    EventType = NotificationEventType.NewGuestMessage,
                    Channels = [NotificationChannels.MobilePush, NotificationChannels.Email, NotificationChannels.MobilePush],
                },
                new NotificationEventRequest { EventType = NotificationEventType.LegalDeadline, Channels = [] },
            ],
        };

        var result = await Controller().Save(request, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(sent);
        Assert.Equal(new QuietHoursUpdate(new TimeOnly(22, 30), new TimeOnly(7, 0)), sent.QuietHours);
        Assert.Equal(
            [
                new NotificationEventUpdate(NotificationEventType.NewGuestMessage, NotificationChannels.MobilePush | NotificationChannels.Email),
                new NotificationEventUpdate(NotificationEventType.LegalDeadline, NotificationChannels.None),
            ],
            sent.Events);
    }

    [Fact]
    public async Task Save_ABodyWithNothingInIt_ReachesTheServiceAsNothingToSave_NeverAsEverything()
    {
        NotificationPreferencesUpdate? sent = null;
        _service
            .Setup(s => s.SaveAsync(UserId, It.IsAny<NotificationPreferencesUpdate>(), It.IsAny<CancellationToken>()))
            .Callback<string, NotificationPreferencesUpdate, CancellationToken>((_, update, _) => sent = update)
            .ReturnsAsync(AView());

        await Controller().Save(new SaveNotificationPreferencesRequest(), CancellationToken.None);

        // The service answers it 422 notification_preferences_empty.
        Assert.NotNull(sent);
        Assert.Null(sent.QuietHours);
        Assert.Null(sent.Events);
    }

    [Fact]
    public async Task Save_WithoutASub_IsUnauthorized_AndNothingIsSaved()
    {
        var result = await Controller(sub: null).Save(OneEvent(), CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result.Result);
        _service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Save_TheRefusalsOfTheService_ReachTheErrorHandlingUntouched()
    {
        // 422 with the four codes of the contract, 404 for a caller with no host org: the middleware turns them into the answers.
        Exception[] refusals =
        [
            new DomainRuleException(NotificationPreferenceRules.LegalChannelRequiredCode, "NotificationLegalChannelRequired"),
            new DomainRuleException(NotificationPreferenceRules.ChannelUnavailableCode, "NotificationChannelUnavailable"),
            new DomainRuleException("notification_preferences_empty", "NotificationPreferencesEmpty"),
            new DomainRuleException("notification_event_duplicated", "NotificationEventDuplicated"),
            new NotFoundException("Notification preferences not found."),
        ];

        foreach (var refusal in refusals)
        {
            _service.Reset();
            _service
                .Setup(s => s.SaveAsync(UserId, It.IsAny<NotificationPreferencesUpdate>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(refusal);

            var thrown = await Assert.ThrowsAnyAsync<Exception>(() => Controller().Save(OneEvent(), CancellationToken.None));

            Assert.Same(refusal, thrown);
        }
    }

    // ─── 400: the body that does not validate never reaches the action ───

    [Fact]
    public void Body_ANullKindOfEvent_IsABadRequest_NotAnExceptionOfTheAction()
    {
        var request = new SaveNotificationPreferencesRequest { Events = [null!] };

        var errors = Validate(request);

        var error = Assert.Single(errors);
        Assert.Equal([nameof(SaveNotificationPreferencesRequest.Events)], error.MemberNames);
        Assert.Equal(SaveNotificationPreferencesRequest.EventInvalidMessageKey, error.ErrorMessage);
    }

    [Fact]
    public void Body_AKindOfEventWithoutItsChannels_IsABadRequest_AnEmptyListIsNot()
    {
        // «channels»: [] is the explicit «nothing at all»; a missing «channels» is never taken for it.
        var without = new NotificationEventRequest { EventType = NotificationEventType.NewGuestMessage };
        var empty = new NotificationEventRequest { EventType = NotificationEventType.NewGuestMessage, Channels = [] };

        Assert.Equal([nameof(NotificationEventRequest.Channels)], Assert.Single(Validate(without)).MemberNames);
        Assert.Empty(Validate(empty));
        Assert.Equal(
            [nameof(NotificationEventRequest.EventType)],
            Assert.Single(Validate(new NotificationEventRequest { Channels = [] })).MemberNames);
    }

    [Fact]
    public void Body_QuietHoursWithoutOneOfTheTwoTimes_IsABadRequest()
    {
        Assert.Equal(
            [nameof(QuietHoursRequest.End)],
            Assert.Single(Validate(new QuietHoursRequest { Start = new TimeOnly(22, 0) })).MemberNames);
        Assert.Equal(
            [nameof(QuietHoursRequest.Start)],
            Assert.Single(Validate(new QuietHoursRequest { End = new TimeOnly(8, 0) })).MemberNames);
        Assert.Empty(Validate(new QuietHoursRequest { Start = new TimeOnly(22, 0), End = new TimeOnly(8, 0) }));
    }

    [Fact]
    public void Body_AValidRequest_HasNoError()
    {
        Assert.Empty(Validate(OneEvent()));
        Assert.Empty(Validate(new SaveNotificationPreferencesRequest()));
    }

    private static List<ValidationResult> Validate(object instance)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(instance, new ValidationContext(instance), results, validateAllProperties: true);
        return results;
    }
}
