using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Regulatory.GuestRegistration;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Authorization;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.SwissCompliance;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// E31/F02/US01/T01 (NF-131): the routes of the assisted notification of the other cantons hold the personal data of the guests and declare a legal communication done, so a caller of another org
/// cannot see the property, a caller whose scope does not reach it cannot use them, and the service is never called for either. The authorization is the production one (the resource handler and the
/// scope resolver over the caller's snapshot), as for the Ticino routes.
/// </summary>
public class SwissNotificationControllerTests
{
    private const string Member = "auth0|member";
    private static readonly Guid OrgId = Guid.Parse("7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11");
    private static readonly Guid GivenProperty = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OtherProperty = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid OtherOrgProperty = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
    private static readonly DateOnly Day = new(2026, 11, 19);
    private static readonly DateTime DeclaredAt = new(2026, 11, 19, 15, 0, 0, DateTimeKind.Utc);

    private readonly Mock<ISwissNotificationService> _service = new();

    private SwissNotificationDay DayOf(Guid propertyId) => new(
        propertyId,
        Day,
        GuestRegistrationRegimeCatalog.ForCanton("SH"),
        GuestRegistrationEffectiveDuty.Required,
        1,
        [new SwissNotificationGuest(Guid.NewGuid(), "7Q3KZ-8M2XB", 0, Day, Day.AddDays(2), "Anna", "Rossi", new DateOnly(1984, 1, 4), null, "SVIZZERA", "CHE", SwissDocumentTypes.Passport, "X1234567", null, null, null, null, null)],
        [new SwissNotificationIssue(Guid.NewGuid(), 0, "Anna Rossi", ["documentType"])],
        new DateTime(2026, 11, 20, 1, 0, 0, DateTimeKind.Utc),
        null,
        null,
        [new SwissPendingDay(OrgId, propertyId, "Casa", "CH-SH", Day, 1, new DateTime(2026, 11, 20, 1, 0, 0, DateTimeKind.Utc))]);

    private void SetUpService(Guid propertyId)
    {
        _service.Setup(s => s.GetDayAsync(propertyId, It.IsAny<DateOnly>(), It.IsAny<CancellationToken>())).ReturnsAsync(DayOf(propertyId));
        _service
            .Setup(s => s.DeclareDayAsync(propertyId, It.IsAny<DateOnly>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SwissDeclaration(propertyId, Day, "CH-SH", DeclaredAt, "Anna Test", true));
        _service.Setup(s => s.UndeclareDayAsync(propertyId, It.IsAny<DateOnly>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
    }

    public static TheoryData<string> Endpoints => new() { "day", "declare", "undeclare" };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Endpoint_ACallerWhoseScopeDoesNotReachTheProperty_IsRefusedAndTheServiceIsNeverCalled(string endpoint)
    {
        SetUpService(GivenProperty);
        var controller = NewController(GivenPropertyOnly());

        // The property it was given is served: the denial below comes from the scope, not from the wiring.
        Assert.IsNotType<ForbidResult>(await CallAsync(controller, endpoint, GivenProperty));

        var refused = await CallAsync(controller, endpoint, OtherProperty);

        Assert.IsType<ForbidResult>(refused);
        VerifyNeverCalledFor(OtherProperty);
    }

    [Fact]
    public async Task Assisted_OtherOrganizationForbidden_Test()
    {
        // PROOF TEST (permission): the day holds the names, the documents and the addresses of the guests, and a declaration closes a legal duty. A property of another org is not even visible to
        // the caller (404), one of the caller's org outside its scope is refused (403): the service is never called for either, whatever the route. The property the caller was given is served,
        // the declaration is made in the name of the caller's own subject, and the answers that hold personal data are never cached.
        SetUpService(GivenProperty);
        var resources = new Mock<IHostResourceLookup>();
        resources
            .Setup(r => r.ForPropertyAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => id == OtherOrgProperty ? null : new HostResource(OrgId, "auth0|creator", id));
        var controller = NewController(GivenPropertyOnly(), resources.Object);

        foreach (var endpoint in new[] { "day", "declare", "undeclare" })
        {
            var otherOrg = await CallAsync(controller, endpoint, OtherOrgProperty);
            var outOfScope = await CallAsync(controller, endpoint, OtherProperty);

            Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<ObjectResult>(otherOrg).StatusCode);
            Assert.IsType<ForbidResult>(outOfScope);
        }

        VerifyNeverCalledFor(OtherOrgProperty);
        VerifyNeverCalledFor(OtherProperty);

        var day = (await controller.GetNotificationDay(GivenProperty, Day, CancellationToken.None)).Result;
        Assert.Equal("private, no-store", controller.Response.Headers.CacheControl.ToString());
        var answer = Assert.IsType<SwissNotificationDayResponse>(Assert.IsType<OkObjectResult>(day).Value);
        Assert.Equal((GivenProperty, "CH-SH"), (answer.PropertyId, answer.RegimeCode));

        controller.Response.Headers.Remove("Cache-Control");
        var declared = (await controller.DeclareNotification(GivenProperty, new DeclareSwissNotificationRequest { ArrivalDay = Day }, CancellationToken.None)).Result;
        Assert.Equal("private, no-store", controller.Response.Headers.CacheControl.ToString());
        var declaration = Assert.IsType<SwissDeclarationResponse>(Assert.IsType<OkObjectResult>(declared).Value);
        Assert.Equal((GivenProperty, Day, "CH-SH", DeclaredAt, "Anna Test", true), (declaration.PropertyId, declaration.ArrivalDay, declaration.RegimeCode, declaration.DeclaredAt, declaration.DeclaredBy, declaration.Created));
        _service.Verify(s => s.DeclareDayAsync(GivenProperty, Day, Member, It.IsAny<CancellationToken>()), Times.Once);

        Assert.IsType<NoContentResult>(await controller.UndeclareNotification(GivenProperty, Day, CancellationToken.None));
        _service.Verify(s => s.UndeclareDayAsync(GivenProperty, Day, Member, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(nameof(SwissNotificationController.DeclareNotification))]
    [InlineData(nameof(SwissNotificationController.UndeclareNotification))]
    public async Task Declare_AndUndo_NeedTheSamePermissionAsRecordingAReceipt_Test(string action)
    {
        // The permission that records a receipt (alloggiati.submit: the owner and the property managers) is the one that declares a day and undoes it: a person who can only read the
        // bookings or the guests cannot close a legal duty. By the attribute, and by the production handler: a role without it is refused (403) and the service is not called.
        var method = typeof(SwissNotificationController).GetMethod(action)!;
        var policy = method.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).Cast<AuthorizeAttribute>().Single().Policy;
        var receiptPolicy = typeof(SwissComplianceController).GetMethod(nameof(SwissComplianceController.UploadReceipt))!
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).Cast<AuthorizeAttribute>().Single().Policy;
        Assert.Equal(CasazenPolicies.AlloggiatiSubmit, policy);
        Assert.Equal(receiptPolicy, policy);

        SetUpService(GivenProperty);
        var controller = NewController(Everything(), hasPermission: (_, permission) => permission != "alloggiati.submit");

        var result = await CallAsync(controller, action == nameof(SwissNotificationController.DeclareNotification) ? "declare" : "undeclare", GivenProperty);

        Assert.IsType<ForbidResult>(result);
        VerifyNeverCalledFor(GivenProperty);
    }

    [Fact]
    public void TheDay_AsksToReadTheGuests_AndTheWholeController_AsksToReadTheBookingsBehindTheFlag()
    {
        // The day lists the guests by name with the number of their document: guest.read on top of the class's booking.read; and the class is behind Features:GuestRegistrationRegimes
        // (404 before authentication while it is off), like the card of the regime.
        var day = typeof(SwissNotificationController).GetMethod(nameof(SwissNotificationController.GetNotificationDay))!;
        Assert.Equal(CasazenPolicies.GuestRead, day.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).Cast<AuthorizeAttribute>().Single().Policy);
        Assert.Equal(
            CasazenPolicies.BookingRead,
            typeof(SwissNotificationController).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).Cast<AuthorizeAttribute>().Single().Policy);
        Assert.Equal(
            FeatureFlags.GuestRegistrationRegimes,
            Assert.Single(typeof(SwissNotificationController).GetCustomAttributes(typeof(FeatureGateAttribute), inherit: true).Cast<FeatureGateAttribute>()).Flag);
    }

    [Fact]
    public void TheRoutes_AreTheOnesOfTheBrief_UnderTheRouteOfTheSwissFiles()
    {
        var route = Assert.Single(typeof(SwissNotificationController).GetCustomAttributes(typeof(RouteAttribute), inherit: false).Cast<RouteAttribute>());
        Assert.Equal("api/properties/{propertyId:guid}/swiss", route.Template);

        string TemplateOf<T>(string action) where T : HttpMethodAttribute =>
            typeof(SwissNotificationController).GetMethod(action)!.GetCustomAttributes(typeof(T), inherit: false).Cast<T>().Single().Template!;

        Assert.Equal("notification-day", TemplateOf<HttpGetAttribute>(nameof(SwissNotificationController.GetNotificationDay)));
        Assert.Equal("notification-declare", TemplateOf<HttpPostAttribute>(nameof(SwissNotificationController.DeclareNotification)));
        Assert.Equal("notification-declare", TemplateOf<HttpDeleteAttribute>(nameof(SwissNotificationController.UndeclareNotification)));
    }

    [Fact]
    public async Task Day_WithoutADate_IsTheOldestDayStillToNotify()
    {
        SetUpService(GivenProperty);
        var oldest = new DateOnly(2026, 11, 17);
        _service
            .Setup(s => s.GetDayAsync(GivenProperty, It.Is<DateOnly>(d => d != oldest), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DayOf(GivenProperty) with { PendingDays = [new SwissPendingDay(OrgId, GivenProperty, "Casa", "CH-SH", oldest, 2, null)] });
        _service
            .Setup(s => s.GetDayAsync(GivenProperty, oldest, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DayOf(GivenProperty) with { Day = oldest, PendingDays = [new SwissPendingDay(OrgId, GivenProperty, "Casa", "CH-SH", oldest, 2, null)] });
        var controller = NewController(GivenPropertyOnly());

        var answer = Assert.IsType<SwissNotificationDayResponse>(Assert.IsType<OkObjectResult>((await controller.GetNotificationDay(GivenProperty, null, CancellationToken.None)).Result).Value);

        Assert.Equal(oldest, answer.Day);
        Assert.Null(Assert.Single(answer.PendingDays).DueAt);
    }

    [Fact]
    public async Task Undeclare_WithoutTheDay_Is400_AndNothingIsUndone()
    {
        var controller = NewController(GivenPropertyOnly());

        var result = await controller.UndeclareNotification(GivenProperty, null, CancellationToken.None);

        var problem = Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(result).Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Equal("validation_error", (string?)problem.Extensions["code"]);
        _service.Verify(s => s.UndeclareDayAsync(It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void TheJsonOfTheClients_KeepsTheNamesOfTheBrief()
    {
        // The frontend slice (E31/F02/US02/T02) reads these names: they are the contract.
        var web = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        var localizer = new Mock<IStringLocalizer<SharedResources>>();
        localizer.Setup(l => l[It.IsAny<string>()]).Returns((string key) => new LocalizedString(key, key));

        var json = System.Text.Json.JsonSerializer.SerializeToElement(SwissNotificationDayResponse.From(DayOf(GivenProperty), localizer.Object), web);

        Assert.Equal(
            [
                "alreadyDeclared", "cantonCode", "certainty", "channel", "day", "deadlineKind", "deadlineNote", "declaredAt", "declaredBy", "dueAt", "effectiveDuty", "guests", "issues",
                "pendingDays", "portalUrl", "propertyId", "regimeCode", "stayCount",
            ],
            json.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(("CH-SH", "SH", "https://gast.sh.ch", "DailyByTime", "Verified", "Required"),
            (json.GetProperty("regimeCode").GetString(), json.GetProperty("cantonCode").GetString(), json.GetProperty("portalUrl").GetString(),
                json.GetProperty("deadlineKind").GetString(), json.GetProperty("certainty").GetString(), json.GetProperty("effectiveDuty").GetString()));
        Assert.Equal("GuestRegime_CH_SH_Deadline", json.GetProperty("deadlineNote").GetString());

        var guest = json.GetProperty("guests")[0];
        Assert.Equal(
            [
                "arrival", "birthDate", "bookingCode", "bookingId", "city", "departure", "documentNumber", "documentType", "firstName", "lastName", "nationality", "nationalityCode",
                "placeOfBirth", "plate", "position", "postalCode", "residenceCountryCode", "street",
            ],
            guest.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["bookingId", "guestName", "missing", "position"], json.GetProperty("issues")[0].EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["day", "dueAt", "stayCount"], json.GetProperty("pendingDays")[0].EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));

        var request = System.Text.Json.JsonSerializer.Deserialize<DeclareSwissNotificationRequest>("""{"arrivalDay":"2026-11-19"}""", web)!;
        Assert.Equal(Day, request.ArrivalDay);
        var answer = System.Text.Json.JsonSerializer.SerializeToElement(SwissDeclarationResponse.From(new SwissDeclaration(GivenProperty, Day, "CH-SH", DeclaredAt, "Anna Test", true)), web);
        Assert.Equal(
            ["arrivalDay", "created", "declaredAt", "declaredBy", "propertyId", "regimeCode"],
            answer.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    // ─── Helpers ───

    private void VerifyNeverCalledFor(Guid propertyId)
    {
        _service.Verify(s => s.GetDayAsync(propertyId, It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never);
        _service.Verify(s => s.DeclareDayAsync(propertyId, It.IsAny<DateOnly>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _service.Verify(s => s.UndeclareDayAsync(propertyId, It.IsAny<DateOnly>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static UserAuthorizationSnapshot GivenPropertyOnly() => new(
        Exists: true,
        IsActive: true,
        Role: UserRole.None,
        SupplierOrgId: null,
        Memberships: [],
        OrgMember: new OrgMemberSnapshot(
            OrgId, OrgRole.Collaborator, OrgMemberStatus.Active, PropertyScope.Selected, new HashSet<Guid> { GivenProperty }));

    private static UserAuthorizationSnapshot Everything() => new(
        Exists: true,
        IsActive: true,
        Role: UserRole.None,
        SupplierOrgId: null,
        Memberships: [],
        OrgMember: new OrgMemberSnapshot(OrgId, OrgRole.Admin, OrgMemberStatus.Active, PropertyScope.All, new HashSet<Guid>()));

    private SwissNotificationController NewController(
        UserAuthorizationSnapshot snapshot,
        IHostResourceLookup? lookup = null,
        Func<string, string, bool>? hasPermission = null)
    {
        var resources = new Mock<IHostResourceLookup>();
        resources
            .Setup(r => r.ForPropertyAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => new HostResource(OrgId, "auth0|creator", id));

        var authorization = HostAuthorizationTestHarness.Create(OrgId, hasPermission, HostAuthorizationTestHarness.ScopeResolver(snapshot));
        var localizer = new Mock<IStringLocalizer<SharedResources>>();
        localizer.Setup(l => l[It.IsAny<string>()]).Returns((string key) => new LocalizedString(key, key));

        return new SwissNotificationController(
            _service.Object, lookup ?? resources.Object, authorization, localizer.Object, Mock.Of<ILogger<SwissNotificationController>>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = HostAuthorizationTestHarness.User(Member),
                    RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
                },
            },
        };
    }

    private static async Task<ActionResult?> CallAsync(SwissNotificationController controller, string endpoint, Guid propertyId) => endpoint switch
    {
        "day" => (await controller.GetNotificationDay(propertyId, Day, CancellationToken.None)).Result,
        "declare" => (await controller.DeclareNotification(propertyId, new DeclareSwissNotificationRequest { ArrivalDay = Day }, CancellationToken.None)).Result,
        _ => await controller.UndeclareNotification(propertyId, Day, CancellationToken.None),
    };
}
