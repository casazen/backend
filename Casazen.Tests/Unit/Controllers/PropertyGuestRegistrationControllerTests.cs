using System.Reflection;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Features;
using Casazen.Core.Regulatory.GuestRegistration;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Authorization;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Compliance;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

public class PropertyGuestRegistrationControllerTests
{
    private const string Member = "auth0|member";
    private static readonly Guid OrgId = Guid.Parse("7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11");
    private static readonly Guid GivenProperty = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OtherProperty = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid OtherOrgProperty = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

    private readonly Mock<IGuestRegistrationRegimeResolver> _regimes = new();
    private readonly Mock<IGuestNotificationOverrideService> _overrides = new();

    [Fact]
    public async Task GuestRegistration_OtherOrganizationCannotRead_Test()
    {
        SetUpResolution();
        var controller = NewController(Everything());

        var read = await controller.Get(OtherOrgProperty, CancellationToken.None);
        var write = await controller.Put(OtherOrgProperty, Request(GuestNotificationOverride.NotRequired), CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<ObjectResult>(read.Result).StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<ObjectResult>(write.Result).StatusCode);
        _regimes.Verify(r => r.ResolveAsync(OtherOrgProperty, It.IsAny<CancellationToken>()), Times.Never);
        _overrides.Verify(
            o => o.SetAsync(OtherOrgProperty, It.IsAny<GuestNotificationOverride?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        var own = await controller.Get(GivenProperty, CancellationToken.None);
        var card = Assert.IsType<GuestRegistrationDto>(Assert.IsType<OkObjectResult>(own.Result).Value);
        Assert.Equal("CH-TI", card.Regime!.Code);
        Assert.Equal(GuestRegistrationEffectiveDuty.Required, card.EffectiveDuty);
        Assert.Equal(["ticino_structure_id_missing"], card.IdentifierMissing);
    }

    [Fact]
    public async Task Endpoint_ACallerWhoseScopeDoesNotReachTheProperty_IsRefusedAndNothingIsReadNorStored()
    {
        SetUpResolution();
        var controller = NewController(GivenPropertyOnly());

        Assert.IsType<OkObjectResult>((await controller.Get(GivenProperty, CancellationToken.None)).Result);
        Assert.IsType<OkObjectResult>((await controller.Put(GivenProperty, Request(GuestNotificationOverride.Required), CancellationToken.None)).Result);

        var read = await controller.Get(OtherProperty, CancellationToken.None);
        var write = await controller.Put(OtherProperty, Request(GuestNotificationOverride.Required), CancellationToken.None);

        Assert.IsType<ForbidResult>(read.Result);
        Assert.IsType<ForbidResult>(write.Result);
        _regimes.Verify(r => r.ResolveAsync(OtherProperty, It.IsAny<CancellationToken>()), Times.Never);
        _overrides.Verify(
            o => o.SetAsync(OtherProperty, It.IsAny<GuestNotificationOverride?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Put_ARoleThatCannotWriteTheProperty_IsRefused_AndNothingIsStored()
    {
        SetUpResolution();
        var controller = NewController(Everything(), hasPermission: (_, permission) => permission != "property.write");

        var read = await controller.Get(GivenProperty, CancellationToken.None);
        var write = await controller.Put(GivenProperty, Request(GuestNotificationOverride.NotRequired), CancellationToken.None);

        Assert.IsType<OkObjectResult>(read.Result);
        Assert.IsType<ForbidResult>(write.Result);
        _overrides.Verify(
            o => o.SetAsync(It.IsAny<Guid>(), It.IsAny<GuestNotificationOverride?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Put_SavesTheSetting_AndAnswersTheCardAfterTheChange()
    {
        SetUpResolution();
        var controller = NewController(Everything());

        var answer = await controller.Put(GivenProperty, Request(GuestNotificationOverride.NotRequired), CancellationToken.None);

        Assert.IsType<OkObjectResult>(answer.Result);
        _overrides.Verify(o => o.SetAsync(GivenProperty, GuestNotificationOverride.NotRequired, Member, It.IsAny<CancellationToken>()), Times.Once);
        _regimes.Verify(r => r.ResolveAsync(GivenProperty, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Put_NullFollowsTheRegimeAgain_AndABodyWithoutTheMemberIsA400()
    {
        SetUpResolution();
        var controller = NewController(Everything());
        var nullBody = new UpdateGuestRegistrationRequest { Override = null };

        var followed = await controller.Put(GivenProperty, nullBody, CancellationToken.None);
        var missing = await controller.Put(GivenProperty, new UpdateGuestRegistrationRequest(), CancellationToken.None);

        Assert.IsType<OkObjectResult>(followed.Result);
        _overrides.Verify(o => o.SetAsync(GivenProperty, null, Member, It.IsAny<CancellationToken>()), Times.Once);
        var problem = Assert.IsAssignableFrom<ObjectResult>(missing.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<ValidationProblemDetails>(problem.Value).Status);
        _overrides.Verify(o => o.SetAsync(GivenProperty, It.IsAny<GuestNotificationOverride?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Get_ACountryWithoutARegime_Is422()
    {
        _regimes
            .Setup(r => r.ResolveAsync(GivenProperty, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GuestRegistrationResolution(GivenProperty, null, null, null, GuestRegistrationEffectiveDuty.Ask, []));
        var controller = NewController(Everything());

        var answer = await controller.Get(GivenProperty, CancellationToken.None);

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, Assert.IsAssignableFrom<ObjectResult>(answer.Result).StatusCode);
    }

    [Fact]
    public async Task Get_AnswersTheContractOfTheCard()
    {
        SetUpResolution();
        var controller = NewController(Everything());

        var card = Assert.IsType<GuestRegistrationDto>(Assert.IsType<OkObjectResult>((await controller.Get(GivenProperty, CancellationToken.None)).Result).Value);

        var regime = card.Regime!;
        Assert.Equal(
            ("CH-TI", "CH", (string?)"TI", GuestRegistrationObligation.ReportToAuthority, GuestRegistrationChannel.PortalFileUpload, GuestRegistrationDeadlineKind.Hours24FromArrival),
            (regime.Code, regime.CountryCode, regime.CantonCode, regime.Obligation, regime.Channel, regime.DeadlineKind));
        Assert.Equal(
            (GuestRegistrationCertainty.Verified, GuestRegistrationSupport.FileExport, (string?)"https://www.notifiche.ti.ch", (int?)5, false),
            (regime.Certainty, regime.CasaZenSupport, regime.PortalUrl, regime.RetentionYears, regime.RetainDocumentImages));
        Assert.NotEmpty(regime.Sources);
        Assert.Equal(new DateOnly(2026, 11, 20), regime.VerifiedOn);
        Assert.Equal("GuestRegime_CH_TI_Summary", regime.Summary);
        Assert.Null(regime.DeadlineNote);
        Assert.Equal((GuestNotificationOverride?)null, card.Override);
        Assert.NotNull(card.Place);
        Assert.Equal("TI", card.Place.CantonCode);
    }

    [Fact]
    public void TheController_IsBehindTheFlag_AndAsksTheSamePermissionsAsTheOtherPropertySettings()
    {
        var type = typeof(PropertyGuestRegistrationController);

        Assert.Equal([FeatureFlags.GuestRegistrationRegimes], type.GetCustomAttributes<FeatureGateAttribute>().Select(a => a.Flag));
        Assert.Equal(CasazenPolicies.PropertyRead, Assert.Single(type.GetCustomAttributes<AuthorizeAttribute>()).Policy);
        Assert.Null(type.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.NotNull(type.GetCustomAttribute<PrivateAnswerAttribute>());
        Assert.Equal(
            CasazenPolicies.PropertyWrite,
            Assert.Single(type.GetMethod(nameof(PropertyGuestRegistrationController.Put))!.GetCustomAttributes<AuthorizeAttribute>()).Policy);
        Assert.Empty(type.GetMethod(nameof(PropertyGuestRegistrationController.Get))!.GetCustomAttributes<AuthorizeAttribute>());
        Assert.Equal("api/properties/{propertyId:guid}/guest-registration", type.GetCustomAttribute<RouteAttribute>()!.Template);
    }

    private void SetUpResolution()
    {
        var ticino = GuestRegistrationRegimeCatalog.ForCanton("TI");
        var lugano = new SwissPlace("5192", "Lugano", "TI", SwissPlaceSource.Derived, Ambiguous: false);
        _regimes
            .Setup(r => r.ResolveAsync(GivenProperty, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GuestRegistrationResolution(
                GivenProperty, ticino, lugano, null, GuestRegistrationEffectiveDuty.Required, [GuestRegistrationReadiness.TicinoStructureIdMissing]));
        _overrides
            .Setup(o => o.SetAsync(It.IsAny<Guid>(), It.IsAny<GuestNotificationOverride?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private static UpdateGuestRegistrationRequest Request(GuestNotificationOverride? value) => new() { Override = value };

    private static UserAuthorizationSnapshot Everything() => new(
        Exists: true,
        IsActive: true,
        Role: UserRole.None,
        SupplierOrgId: null,
        Memberships: [],
        OrgMember: new OrgMemberSnapshot(OrgId, OrgRole.Admin, OrgMemberStatus.Active, PropertyScope.All, new HashSet<Guid>()));

    private static UserAuthorizationSnapshot GivenPropertyOnly() => new(
        Exists: true,
        IsActive: true,
        Role: UserRole.None,
        SupplierOrgId: null,
        Memberships: [],
        OrgMember: new OrgMemberSnapshot(OrgId, OrgRole.Collaborator, OrgMemberStatus.Active, PropertyScope.Selected, new HashSet<Guid> { GivenProperty }));

    private PropertyGuestRegistrationController NewController(UserAuthorizationSnapshot snapshot, Func<string, string, bool>? hasPermission = null)
    {
        var resources = new Mock<IHostResourceLookup>();
        resources
            .Setup(r => r.ForPropertyAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => id == OtherOrgProperty ? null : new HostResource(OrgId, "auth0|creator", id));

        var authorization = HostAuthorizationTestHarness.Create(
            OrgId, hasPermission, HostAuthorizationTestHarness.ScopeResolver(snapshot));

        var localizer = new Mock<IStringLocalizer<SharedResources>>();
        localizer.Setup(l => l[It.IsAny<string>()]).Returns((string key) => new LocalizedString(key, key));

        var problems = new Mock<ProblemDetailsFactory>();
        problems
            .Setup(f => f.CreateValidationProblemDetails(
                It.IsAny<HttpContext>(), It.IsAny<ModelStateDictionary>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns((HttpContext _, ModelStateDictionary state, int? status, string? _, string? _, string? _, string? _) =>
                new ValidationProblemDetails(state) { Status = status ?? StatusCodes.Status400BadRequest });

        return new PropertyGuestRegistrationController(
            _regimes.Object,
            _overrides.Object,
            resources.Object,
            authorization,
            localizer.Object,
            Mock.Of<ILogger<PropertyGuestRegistrationController>>())
        {
            ProblemDetailsFactory = problems.Object,
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
}
