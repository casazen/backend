using System.Reflection;
using System.Security.Claims;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Assistant;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Assistant;

/// <summary>
/// E05/F05/US01/T03 (NF-66, NF-67): the endpoints of the situations. Behind the flag and private, each action with its policy, a situation named by its code or by the name of its member,
/// the template shown only to the caller who may edit it, and the reach of a collaborator «Solo alcuni» over the exceptions of the properties (the real scope resolver and the real
/// resource handler behind the controller).
/// </summary>
public class AssistantSituationsControllerTests
{
    private const string Host = "auth0|host";

    [Fact]
    public void TheController_IsBehindTheFlag_Private_AndEveryActionHasItsPolicy()
    {
        var expected = new Dictionary<string, string[]>
        {
            [nameof(AssistantSituationsController.ListForOrganization)] = [CasazenPolicies.BookingRead],
            [nameof(AssistantSituationsController.SaveForOrganization)] = [CasazenPolicies.BookingRead, CasazenPolicies.OrgBillingAdmin],
            [nameof(AssistantSituationsController.ListForProperty)] = [CasazenPolicies.PropertyRead],
            [nameof(AssistantSituationsController.SaveForProperty)] = [CasazenPolicies.PropertyWrite],
            [nameof(AssistantSituationsController.DeleteForProperty)] = [CasazenPolicies.PropertyWrite],
        };
        var type = typeof(AssistantSituationsController);

        Assert.Equal("api/assistant", ((RouteAttribute)Attribute.GetCustomAttribute(type, typeof(RouteAttribute))!).Template);
        Assert.Equal(FeatureFlags.MessageAssistant, ((FeatureGateAttribute)Attribute.GetCustomAttribute(type, typeof(FeatureGateAttribute))!).Flag);
        Assert.NotNull(Attribute.GetCustomAttribute(type, typeof(PrivateAnswerAttribute)));
        Assert.Empty(type.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true));
        Assert.Empty(type.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true));

        var actions = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.Equal(expected.Keys.Order(), actions.Select(a => a.Name).Order());
        foreach (var action in actions)
        {
            var policies = action.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).Cast<AuthorizeAttribute>().Select(a => a.Policy!).ToArray();
            Assert.Equal(expected[action.Name].Order(), policies.Order());
            Assert.Empty(action.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true));
        }
    }

    [Fact]
    public async Task TheRoute_NamesASituationByItsCodeOrByTheNameOfItsMember_AnythingElseIsA404()
    {
        await using var db = AssistantTestKit.NewDb();
        var world = await HostScopeScenario.SeedAsync(db);
        var controller = AdministratorController(db, world);

        foreach (var name in new[] { "check_in_time", "CheckInTime", "CHECK_IN_TIME", " checkintime " })
        {
            var saved = Assert.IsType<OkObjectResult>((await controller.SaveForOrganization(name, new SaveAssistantSituationRequest { Mode = AssistantMode.Draft }, CancellationToken.None)).Result);
            Assert.Equal("check_in_time", Assert.IsType<EffectiveSituationDto>(saved.Value).Code);
        }

        foreach (var name in new[] { "gossip", "7", "check-in-time", " " })
        {
            var result = (await controller.SaveForOrganization(name, new SaveAssistantSituationRequest { Mode = AssistantMode.Draft }, CancellationToken.None)).Result;
            var problem = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
            Assert.Equal("assistant_situation_not_found", Assert.IsAssignableFrom<ProblemDetails>(problem.Value).Extensions["code"]);
        }

        // The property routes answer the same, and a property that is not the caller's is answered first.
        var property = (await controller.SaveForProperty(world.Granted.Id, "nonsense", new SaveAssistantSituationRequest(), CancellationToken.None)).Result;
        Assert.Equal("assistant_situation_not_found", Assert.IsAssignableFrom<ProblemDetails>(Assert.IsType<ObjectResult>(property).Value).Extensions["code"]);
        var deleted = await controller.DeleteForProperty(world.Granted.Id, "nonsense", CancellationToken.None);
        Assert.Equal("assistant_situation_not_found", Assert.IsAssignableFrom<ProblemDetails>(Assert.IsType<ObjectResult>(deleted).Value).Extensions["code"]);
    }

    [Fact]
    public async Task Save_WithNoMode_IsTheDefaultOfTheSituation()
    {
        await using var db = AssistantTestKit.NewDb();
        var world = await HostScopeScenario.SeedAsync(db);
        var controller = AdministratorController(db, world);

        var emergency = Assert.IsType<EffectiveSituationDto>(Assert.IsType<OkObjectResult>((await controller.SaveForOrganization("emergency", new SaveAssistantSituationRequest(), CancellationToken.None)).Result).Value);
        var parking = Assert.IsType<EffectiveSituationDto>(Assert.IsType<OkObjectResult>((await controller.SaveForProperty(world.Granted.Id, "parking", new SaveAssistantSituationRequest(), CancellationToken.None)).Result).Value);

        Assert.Equal(AssistantMode.Escalate, emergency.Mode);
        Assert.Equal(AssistantMode.Draft, parking.Mode);
        Assert.Equal("organization", emergency.Origin);
        Assert.Equal("property", parking.Origin);
    }

    [Fact]
    public async Task TheTemplate_IsShownOnlyToTheCallerWhoMayEditIt_EverybodyElseReadsThatThereIsOne()
    {
        await using var db = AssistantTestKit.NewDb();
        var world = await HostScopeScenario.SeedAsync(db);
        var service = AssistantTestKit.Situations(db);
        await service.SaveAsync(world.OrgId, null, AssistantSituation.Parking, AssistantMode.Draft, "Il parcheggio è gratuito, codice cancello nel messaggio.", null, Host);
        await service.SaveAsync(world.OrgId, world.Granted.Id, AssistantSituation.WiFi, AssistantMode.Draft, "La password è nel cassetto.", null, Host);

        // The organization's situations: the administrators of the organization edit them, everybody else with booking.read only reads.
        foreach (var (isAdministrator, shown) in new[] { (true, true), (false, false) })
        {
            var authorization = new Mock<IAuthorizationService>();
            authorization
                .Setup(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), CasazenPolicies.OrgBillingAdmin))
                .ReturnsAsync(isAdministrator ? AuthorizationResult.Success() : AuthorizationResult.Failed());
            var controller = new AssistantSituationsController(service, AssistantTestKit.Properties(db), AssistantTestKit.Org(world.OrgId), authorization.Object)
            {
                ControllerContext = AssistantTestKit.ContextOf(AssistantTestKit.Collaborator(world)),
            };

            var list = Assert.IsAssignableFrom<IReadOnlyList<EffectiveSituationDto>>(Assert.IsType<OkObjectResult>((await controller.ListForOrganization(CancellationToken.None)).Result).Value);

            var parking = list.Single(s => s.Situation == AssistantSituation.Parking);
            Assert.True(parking.HasTemplate);
            Assert.Equal(shown ? "Il parcheggio è gratuito, codice cancello nel messaggio." : null, parking.Template);
            Assert.False(list.Single(s => s.Situation == AssistantSituation.Pets).HasTemplate);
        }

        // A property's situations. The text of the exception of the property is read by whoever may write the property; the text of a row of the organization (what the property inherits)
        // only by the administrators of the organization, as on the organization's own list: a caller who has just property.write reads that the row has a template, not what it says.
        var callers = new[]
        {
            (Principal: AssistantTestKit.Collaborator(world), Administrator: false, CanWrite: true, ReadsTheException: true, ReadsTheOrganizationsRow: false),
            (Principal: AssistantTestKit.Administrator(world), Administrator: true, CanWrite: true, ReadsTheException: true, ReadsTheOrganizationsRow: true),
            (Principal: AssistantTestKit.Collaborator(world), Administrator: false, CanWrite: false, ReadsTheException: false, ReadsTheOrganizationsRow: false),
            (Principal: AssistantTestKit.Administrator(world), Administrator: true, CanWrite: false, ReadsTheException: false, ReadsTheOrganizationsRow: false),
        };
        foreach (var caller in callers)
        {
            var authorization = AssistantTestKit.Authorization(world, caller.Administrator, (_, permission) => caller.CanWrite || permission != "property.write");
            var controller = new AssistantSituationsController(service, AssistantTestKit.Properties(db), AssistantTestKit.Org(world.OrgId), authorization)
            {
                ControllerContext = AssistantTestKit.ContextOf(caller.Principal),
            };

            var list = Assert.IsAssignableFrom<IReadOnlyList<EffectiveSituationDto>>(Assert.IsType<OkObjectResult>((await controller.ListForProperty(world.Granted.Id, CancellationToken.None)).Result).Value);

            // The exception of the property.
            var wifi = list.Single(s => s.Situation == AssistantSituation.WiFi);
            Assert.Equal("property", wifi.Origin);
            Assert.True(wifi.HasTemplate);
            Assert.Equal(caller.ReadsTheException ? "La password è nel cassetto." : null, wifi.Template);

            // The row of the organization: the same text that the organization's list hides from everybody but its administrators.
            var parking = list.Single(s => s.Situation == AssistantSituation.Parking);
            Assert.Equal("organization", parking.Origin);
            Assert.True(parking.HasTemplate);
            Assert.Equal(caller.ReadsTheOrganizationsRow ? "Il parcheggio è gratuito, codice cancello nel messaggio." : null, parking.Template);
        }
    }

    [Fact]
    public async Task ACollaboratorSoloAlcuni_ReadsAndChangesTheSituationsOfTheGivenPropertyOnly_TheOthersAreThe404OfAPropertyThatDoesNotExist()
    {
        await using var db = AssistantTestKit.NewDb();
        var world = await HostScopeScenario.SeedAsync(db);
        var service = AssistantTestKit.Situations(db);
        await service.SaveAsync(world.OrgId, world.Hidden.Id, AssistantSituation.Pets, AssistantMode.Escalate, "Niente animali alla Casa Bianca.", null, Host);
        await service.SaveAsync(world.OrgId, null, AssistantSituation.Parking, AssistantMode.Draft, "Parcheggio per tutti.", null, Host);
        var rowsBefore = await db.AssistantSituationSettings.AsNoTracking().CountAsync();
        var versionBefore = (await AssistantTestKit.Rules(db).GetAsync(world.OrgId)).InstructionVersion;
        var controller = new AssistantSituationsController(
            service,
            AssistantTestKit.Properties(db),
            AssistantTestKit.Org(world.OrgId),
            AssistantTestKit.Authorization(world))
        {
            ControllerContext = AssistantTestKit.ContextOf(AssistantTestKit.Collaborator(world)),
        };

        // The property it was given: it reads the situations (with the organization's value under its own), saves an exception and removes it.
        var list = Assert.IsAssignableFrom<IReadOnlyList<EffectiveSituationDto>>(Assert.IsType<OkObjectResult>((await controller.ListForProperty(world.Granted.Id, CancellationToken.None)).Result).Value);
        Assert.Equal("organization", list.Single(s => s.Situation == AssistantSituation.Parking).Origin);
        Assert.Equal("default", list.Single(s => s.Situation == AssistantSituation.Pets).Origin);
        Assert.IsType<OkObjectResult>((await controller.SaveForProperty(world.Granted.Id, "pets", new SaveAssistantSituationRequest { Mode = AssistantMode.Draft }, CancellationToken.None)).Result);
        Assert.IsType<NoContentResult>(await controller.DeleteForProperty(world.Granted.Id, "pets", CancellationToken.None));

        // The property it was not given, and one that does not exist: the same 404, for the read, the save and the delete, and nothing of the hidden one in the answer.
        foreach (var propertyId in new[] { world.Hidden.Id, Guid.NewGuid() })
        {
            AssertPropertyNotFound((await controller.ListForProperty(propertyId, CancellationToken.None)).Result);
            AssertPropertyNotFound((await controller.SaveForProperty(propertyId, "pets", new SaveAssistantSituationRequest { Mode = AssistantMode.Draft }, CancellationToken.None)).Result);
            AssertPropertyNotFound(await controller.DeleteForProperty(propertyId, "pets", CancellationToken.None));
        }

        // Nothing of the hidden property changed.
        var hidden = (await service.ListEffectiveAsync(world.OrgId, world.Hidden.Id)).Single(s => s.Situation == AssistantSituation.Pets);
        Assert.Equal(AssistantMode.Escalate, hidden.Mode);
        Assert.Equal("Niente animali alla Casa Bianca.", hidden.Template);
        Assert.Equal(rowsBefore, await db.AssistantSituationSettings.AsNoTracking().CountAsync());
        Assert.True((await AssistantTestKit.Rules(db).GetAsync(world.OrgId)).InstructionVersion > versionBefore);
    }

    private static AssistantSituationsController AdministratorController(AppDbContext db, HostScopeWorld world) => new(
        AssistantTestKit.Situations(db),
        AssistantTestKit.Properties(db),
        AssistantTestKit.Org(world.OrgId),
        AssistantTestKit.Authorization(world, administrator: true))
    {
        ControllerContext = AssistantTestKit.ContextOf(AssistantTestKit.Administrator(world)),
    };

    private static void AssertPropertyNotFound(IActionResult? result)
    {
        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
        Assert.Equal("property_not_found", Assert.IsAssignableFrom<ProblemDetails>(problem.Value).Extensions["code"]);
    }
}
