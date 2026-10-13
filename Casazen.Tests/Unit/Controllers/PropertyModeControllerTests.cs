using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// PM-02: the endpoints of the scheduled change of rental mode. The property is authorized as a row (404 for another org's,
/// 403 when the operation is not allowed) before the service is called; the preview, the creation and the cancellation need
/// the write permission of the property, the state the read one; the target is parsed by name; the dates are calendar days.
/// </summary>
public class PropertyModeControllerTests
{
    private static readonly Guid PropertyId = Guid.Parse("00000000-0000-0000-0000-0000000000aa");
    private static readonly Guid OrgId = Guid.Parse("00000000-0000-0000-0000-0000000000bb");
    private static readonly DateTime Today = new(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Mock<IPropertyModeService> _service = new();
    private readonly Mock<IHostResourceLookup> _resources = new();
    private readonly Mock<IAuthorizationService> _authorization = new();
    private readonly PropertyModeController _controller;
    private IAuthorizationRequirement? _requirementAsked;

    public PropertyModeControllerTests()
    {
        _resources
            .Setup(r => r.ForPropertyAsync(PropertyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostResource(OrgId, "auth0|host"));
        _authorization
            .Setup(a => a.AuthorizeAsync(
                It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<IEnumerable<IAuthorizationRequirement>>()))
            .Callback((ClaimsPrincipal _, object? _, IEnumerable<IAuthorizationRequirement> requirements) =>
                _requirementAsked = requirements.Single())
            .ReturnsAsync(AuthorizationResult.Success());
        _controller = new PropertyModeController(
            _service.Object, _resources.Object, _authorization.Object, Mock.Of<ILogger<PropertyModeController>>());
        SetUser("auth0|host");
    }

    // ─── Wiring ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Controller_IsBehindTheFlagAndTheRoutesAreTheOnesOfTheTask()
    {
        var gate = typeof(PropertyModeController).GetCustomAttribute<FeatureGateAttribute>();
        var route = typeof(PropertyModeController).GetCustomAttribute<RouteAttribute>();

        Assert.Equal(FeatureFlags.PropertyModeChange, gate!.Flag);
        Assert.Equal("api/properties/{propertyId:guid}/mode", route!.Template);
        Assert.Equal("preview", Template(nameof(PropertyModeController.Preview)));
        Assert.Equal("change", Template(nameof(PropertyModeController.Schedule)));
        Assert.Equal("change/{changeId:guid}", Template(nameof(PropertyModeController.Cancel)));
        Assert.IsType<HttpGetAttribute>(Method(nameof(PropertyModeController.Preview)).GetCustomAttribute<HttpMethodAttribute>());
        Assert.IsType<HttpPostAttribute>(Method(nameof(PropertyModeController.Schedule)).GetCustomAttribute<HttpMethodAttribute>());
        Assert.IsType<HttpDeleteAttribute>(Method(nameof(PropertyModeController.Cancel)).GetCustomAttribute<HttpMethodAttribute>());
    }

    [Fact]
    public void Policies_TheStateIsReadTheRestIsWrite_BothOfTheSharedPropertyCore()
    {
        var classPolicy = typeof(PropertyModeController).GetCustomAttribute<AuthorizeAttribute>()!.Policy;

        Assert.Equal(CasazenPolicies.SharedPropertyRead, classPolicy);
        Assert.Null(Method(nameof(PropertyModeController.GetState)).GetCustomAttribute<AuthorizeAttribute>());
        foreach (var action in new[]
                 {
                     nameof(PropertyModeController.Preview),
                     nameof(PropertyModeController.Schedule),
                     nameof(PropertyModeController.Cancel),
                 })
        {
            Assert.Equal(CasazenPolicies.SharedPropertyWrite, Method(action).GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        }
    }

    // ─── The row is authorized first ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryAction_PropertyOfAnotherOrgOrUnknown_Is404WithTheCodeAndTheServiceIsNotCalled()
    {
        _resources
            .Setup(r => r.ForPropertyAsync(PropertyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((HostResource?)null);

        var results = new ActionResult?[]
        {
            (await _controller.GetState(PropertyId, CancellationToken.None)).Result,
            (await _controller.Preview(PropertyId, "long", null, CancellationToken.None)).Result,
            (await _controller.Schedule(PropertyId, Request(), CancellationToken.None)).Result,
            await _controller.Cancel(PropertyId, Guid.NewGuid(), CancellationToken.None) as ActionResult,
        };

        foreach (var result in results)
        {
            var problem = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
            Assert.Equal("property_not_found", Assert.IsType<Microsoft.AspNetCore.Mvc.ProblemDetails>(problem.Value).Extensions["code"]);
        }

        _service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EveryAction_NotAllowed_Is403AndTheServiceIsNotCalled()
    {
        _authorization
            .Setup(a => a.AuthorizeAsync(
                It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<IEnumerable<IAuthorizationRequirement>>()))
            .ReturnsAsync(AuthorizationResult.Failed());

        var results = new ActionResult?[]
        {
            (await _controller.GetState(PropertyId, CancellationToken.None)).Result,
            (await _controller.Preview(PropertyId, "long", null, CancellationToken.None)).Result,
            (await _controller.Schedule(PropertyId, Request(), CancellationToken.None)).Result,
            await _controller.Cancel(PropertyId, Guid.NewGuid(), CancellationToken.None) as ActionResult,
        };

        Assert.All(results, result => Assert.IsType<ForbidResult>(result));
        _service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetState_AsksTheReadOperation_TheOthersAskTheWriteOne()
    {
        _service.Setup(s => s.GetStateAsync(PropertyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PropertyModeState(PropertyId, RentalMode.Short, null, null));
        _service.Setup(s => s.PreviewAsync(PropertyId, RentalMode.Long, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(NewPreview());

        await _controller.GetState(PropertyId, CancellationToken.None);
        Assert.Equal(SharedPropertyOperations.Read, _requirementAsked);
        await _controller.Preview(PropertyId, "long", null, CancellationToken.None);
        Assert.Equal(SharedPropertyOperations.Write, _requirementAsked);
    }

    // ─── State ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetState_GivesTheModeAndTheChanges()
    {
        var scheduled = NewChange(PropertyModeChangeStatus.Scheduled);
        var last = NewChange(PropertyModeChangeStatus.Failed, reason: PropertyModeErrorCodes.BlockedByBookings);
        _service.Setup(s => s.GetStateAsync(PropertyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PropertyModeState(PropertyId, RentalMode.Short, scheduled, last));

        var result = await _controller.GetState(PropertyId, CancellationToken.None);

        var body = Assert.IsType<PropertyModeStateResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal((PropertyId, RentalMode.Short), (body.PropertyId, body.RentalMode));
        Assert.Equal(scheduled.Id, body.ScheduledChange!.Id);
        Assert.Equal("property_mode_blocked_by_bookings", body.LastChange!.FailureReason);
    }

    // ─── Preview ────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("short", RentalMode.Short)]
    [InlineData("long", RentalMode.Long)]
    [InlineData("Long", RentalMode.Long)]
    [InlineData(" LONG ", RentalMode.Long)]
    public async Task Preview_TheTargetIsParsedByName(string to, RentalMode expected)
    {
        _service.Setup(s => s.PreviewAsync(PropertyId, expected, null, It.IsAny<CancellationToken>())).ReturnsAsync(NewPreview());

        var result = await _controller.Preview(PropertyId, to, null, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        _service.Verify(s => s.PreviewAsync(PropertyId, expected, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("both")]
    [InlineData("long,short")]
    public async Task Preview_ATargetThatIsNotAMode_Is400WithTheCode(string? to)
    {
        var result = await _controller.Preview(PropertyId, to, null, CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Equal("property_mode_target_invalid", Assert.IsType<Microsoft.AspNetCore.Mvc.ProblemDetails>(problem.Value).Extensions["code"]);
        _service.Verify(
            s => s.PreviewAsync(It.IsAny<Guid>(), It.IsAny<RentalMode>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Preview_TheDateIsACalendarDay_MidnightUtc()
    {
        DateTime? received = null;
        _service
            .Setup(s => s.PreviewAsync(PropertyId, RentalMode.Long, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, RentalMode _, DateTime? date, CancellationToken _) => received = date)
            .ReturnsAsync(NewPreview());

        await _controller.Preview(PropertyId, "long", new DateOnly(2026, 12, 1), CancellationToken.None);

        Assert.Equal(new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc), received);
        Assert.Equal(DateTimeKind.Utc, received!.Value.Kind);
    }

    [Fact]
    public async Task Preview_Answers200EvenWhenTheChangeIsNotPossible_WithWhatIsInTheWay()
    {
        var stay = new PropertyModeBlocker(
            PropertyModeBlockerKind.Stay, Guid.NewGuid(), Today.AddDays(2), Today.AddDays(10), Today.AddDays(11), "Confirmed", "Direct");
        var preview = new PropertyModePreview(
            PropertyId, RentalMode.Short, RentalMode.Long, Today, Today.AddDays(11), Today.AddDays(5), [stay],
            [PropertyModeErrorCodes.BlockedByBookings], null);
        _service.Setup(s => s.PreviewAsync(PropertyId, RentalMode.Long, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(preview);

        var result = await _controller.Preview(PropertyId, "long", new DateOnly(2026, 10, 15), CancellationToken.None);

        var body = Assert.IsType<PropertyModePreviewResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.False(body.CanSchedule);
        Assert.Equal(["property_mode_blocked_by_bookings"], body.Issues);
        Assert.Equal(new DateOnly(2026, 10, 21), body.EarliestDate);
        Assert.Equal(new DateOnly(2026, 10, 15), body.Date);
        var listed = Assert.Single(body.Blockers);
        Assert.Equal((PropertyModeBlockerKind.Stay, stay.Id), (listed.Kind, listed.Id));
        Assert.Equal(new DateOnly(2026, 10, 21), listed.FreeFrom);
        // To long-term the calendar would stay closed for two years from the day evaluated.
        Assert.Equal(new DateOnly(2028, 10, 15), body.CalendarClosedUntil);
    }

    [Fact]
    public void PreviewResponse_ToShort_HasNoCalendarClosure()
    {
        var preview = new PropertyModePreview(
            PropertyId, RentalMode.Long, RentalMode.Short, Today, Today.AddDays(1), Today.AddDays(1), [], [], null);

        Assert.Null(PropertyModePreviewResponse.From(preview).CalendarClosedUntil);
        Assert.True(PropertyModePreviewResponse.From(preview).CanSchedule);
    }

    [Fact]
    public void PreviewResponse_Json_UsesNamesForEnumsAndPlainDatesAndCamelCase()
    {
        var draft = new PropertyModeBlocker(
            PropertyModeBlockerKind.DraftLease, Guid.NewGuid(), Today.AddDays(100), Today.AddDays(465), null, "Draft");
        var scheduled = NewChange(PropertyModeChangeStatus.Scheduled);
        var preview = new PropertyModePreview(
            PropertyId, RentalMode.Long, RentalMode.Short, Today, Today.AddDays(1), Today.AddDays(1), [draft],
            [PropertyModeErrorCodes.ChangeExists, PropertyModeErrorCodes.BlockedByDraftLease], scheduled);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(PropertyModePreviewResponse.From(preview), WebJson));
        var root = json.RootElement;

        Assert.Equal("Long", root.GetProperty("currentMode").GetString());
        Assert.Equal("Short", root.GetProperty("targetMode").GetString());
        Assert.Equal("2026-10-10", root.GetProperty("today").GetString());
        Assert.Equal("2026-10-11", root.GetProperty("earliestDate").GetString());
        Assert.False(root.GetProperty("canSchedule").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("calendarClosedUntil").ValueKind);
        var blocker = root.GetProperty("blockers")[0];
        Assert.Equal("DraftLease", blocker.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, blocker.GetProperty("freeFrom").ValueKind);
        Assert.Equal("Draft", blocker.GetProperty("status").GetString());
        Assert.Equal("Scheduled", root.GetProperty("scheduledChange").GetProperty("status").GetString());
        Assert.Equal("2026-10-20", root.GetProperty("scheduledChange").GetProperty("effectiveDate").GetString());
    }

    // ─── Schedule ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Schedule_CallsTheServiceWithTheCallerAndTheDayAndAnswers201()
    {
        var created = NewChange(PropertyModeChangeStatus.Scheduled);
        _service
            .Setup(s => s.ScheduleAsync(
                PropertyId, RentalMode.Long, new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc), "auth0|host", It.IsAny<CancellationToken>()))
            .ReturnsAsync(created);

        var result = await _controller.Schedule(PropertyId, Request(), CancellationToken.None);

        var createdResult = Assert.IsType<CreatedResult>(result.Result);
        Assert.Equal($"/api/properties/{PropertyId}/mode", createdResult.Location);
        var body = Assert.IsType<PropertyModeChangeDto>(createdResult.Value);
        Assert.Equal(created.Id, body.Id);
        Assert.Equal((RentalMode.Short, RentalMode.Long), (body.FromMode, body.ToMode));
        Assert.Equal(new DateOnly(2026, 10, 20), body.EffectiveDate);
        Assert.Equal(PropertyModeChangeStatus.Scheduled, body.Status);
    }

    [Fact]
    public async Task Schedule_WithoutAUserId_Is401()
    {
        SetUser(userId: null);

        var result = await _controller.Schedule(PropertyId, Request(), CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result.Result);
        _service.VerifyNoOtherCalls();
    }

    // ─── Cancel ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancel_CallsTheServiceAndAnswers204()
    {
        var changeId = Guid.NewGuid();

        var result = await _controller.Cancel(PropertyId, changeId, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        _service.Verify(s => s.CancelAsync(PropertyId, changeId, "auth0|host", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Cancel_WithoutAUserId_Is401()
    {
        SetUser(userId: null);

        var result = await _controller.Cancel(PropertyId, Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
        _service.VerifyNoOtherCalls();
    }

    // ─── The request ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Request_WithoutTargetOrDay_FailsValidationWithTheResourceKeys()
    {
        var request = new ScheduleModeChangeRequest();
        var results = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);

        Assert.False(valid);
        Assert.Contains(results, r => r.ErrorMessage == "PropertyModeTargetRequired");
        Assert.Contains(results, r => r.ErrorMessage == "PropertyModeDateRequired");
    }

    [Fact]
    public void Request_Json_TheTargetIsAModeNameAndTheDayIsADate()
    {
        var request = JsonSerializer.Deserialize<ScheduleModeChangeRequest>(
            """{ "to": "Long", "effectiveDate": "2026-12-01" }""", WebJson)!;

        Assert.Equal(RentalMode.Long, request.To);
        Assert.Equal(new DateOnly(2026, 12, 1), request.EffectiveDate);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────────────────────

    private static MethodInfo Method(string name) => typeof(PropertyModeController).GetMethod(name)!;

    private static string Template(string action) => Method(action).GetCustomAttribute<HttpMethodAttribute>()!.Template!;

    private static ScheduleModeChangeRequest Request() => new() { To = RentalMode.Long, EffectiveDate = new DateOnly(2026, 10, 20) };

    private static PropertyModePreview NewPreview() => new(
        PropertyId, RentalMode.Short, RentalMode.Long, Today, Today.AddDays(1), Today.AddDays(1), [], [], null);

    private static PropertyModeChange NewChange(PropertyModeChangeStatus status, string? reason = null) => new()
    {
        Id = Guid.NewGuid(),
        OrgId = OrgId,
        PropertyId = PropertyId,
        FromMode = RentalMode.Short,
        ToMode = RentalMode.Long,
        EffectiveDate = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc),
        Status = status,
        CreatedByUserId = "auth0|host",
        CreatedAt = new DateTime(2026, 10, 10, 10, 0, 0, DateTimeKind.Utc),
        FailureReason = reason,
    };

    private void SetUser(string? userId)
    {
        var claims = new List<Claim>();
        if (userId is not null)
            claims.Add(new Claim("sub", userId));

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")),
                RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
            },
        };
    }
}
