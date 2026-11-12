using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Authorization;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Assistant;
using Casazen.Web.Controllers;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Assistant;

/// <summary>
/// E05/F05/US01/T02 (NF-66), the two rules of the knowledge base and the rules of the assistant that cost trust if they are wrong: <b>tenant</b> (what organization A wrote is never read,
/// listed or retrieved for organization B) and <b>permission</b> (a collaborator «Solo alcuni» reaches the knowledge, the exceptions and the entries of the properties it was
/// given and nothing else, with the real scope resolver and the real resource handler behind the controllers).
/// </summary>
public class AssistantKnowledgeProofTests
{
    private const string Host = "auth0|host";
    private const string SecretOfA = "SEGRETO-DI-A: la password del Wi-Fi e Zefirino-2026";
    private const string SecretOfOrganizationA = "SEGRETO-ORG-A: chiama Marco al numero riservato";
    private const string SignatureOfA = "Firma riservata di A";

    // ─── tenant ───

    [Fact]
    public async Task AssistantKnowledge_OtherOrg_NotVisible_Test()
    {
        await using var db = AssistantTestKit.NewDb();
        var world = await HostScopeScenario.SeedAsync(db);
        var knowledge = AssistantTestKit.Knowledge(db);
        var rules = AssistantTestKit.Rules(db);
        var orgA = world.OrgId;
        var orgB = world.OtherOrgId;
        var propertyOfA = world.Granted;
        var propertyOfB = world.OtherOrgProperty;

        // Organization A writes: an entry of its property, one of the whole organization, its rules and the exceptions of its property.
        var entryOfA = await knowledge.CreateEntryAsync(orgA, propertyOfA.Id, AssistantTestKit.Fact(KnowledgeTopic.WiFi, SecretOfA), Host);
        var wideOfA = await knowledge.CreateEntryAsync(orgA, null, AssistantTestKit.Fact(KnowledgeTopic.Contacts, SecretOfOrganizationA), Host);
        await rules.SaveAsync(orgA, AssistantTestKit.RulesInput(signature: SignatureOfA, forbidden: ["Pets"], tone: AssistantTone.Formal), Host);
        await rules.SavePropertySettingsAsync(orgA, propertyOfA.Id, new PropertyAssistantSettingsInput(false, AssistantTone.Friendly, "Firma di A sulla casa", null, null, 0.99m, ["Noise"]), Host);
        var versionOfA = (await rules.GetAsync(orgA)).InstructionVersion;

        // Organization B writes its own.
        var entryOfB = await knowledge.CreateEntryAsync(orgB, propertyOfB.Id, AssistantTestKit.Fact(KnowledgeTopic.WiFi, "Rete di B."), Host);

        // Listed: organization B sees none of A's, not even through the organization-wide list.
        var listedForB = await knowledge.ListOrganizationEntriesAsync(orgB);
        Assert.Empty(listedForB);
        Assert.DoesNotContain(listedForB, e => e.Id == wideOfA.Id);

        // Read: A's property is a 404 for B, in the view, in the settings and in what the pipeline retrieves.
        foreach (var attempt in new Func<Task>[]
                 {
                     () => knowledge.GetViewAsync(orgB, propertyOfA.Id, "it"),
                     () => knowledge.RetrieveAsync(orgB, propertyOfA.Id, [KnowledgeTopic.WiFi, KnowledgeTopic.Contacts], "it"),
                     () => rules.GetPropertySettingsAsync(orgB, propertyOfA.Id),
                 })
        {
            Assert.Equal("property_not_found", (await Assert.ThrowsAsync<NotFoundException>(attempt)).Code);
        }

        // The rules: B has the defaults, never A's, and A's exceptions do not apply to B.
        var rulesOfB = await rules.GetAsync(orgB);
        Assert.Null(rulesOfB.Signature);
        Assert.Equal(AssistantTone.Neutral, rulesOfB.Tone);
        Assert.Empty(rulesOfB.ForbiddenTopics);
        var effectiveForB = await rules.ResolveEffectiveAsync(orgB, propertyOfA.Id);
        Assert.Null(effectiveForB.Signature);
        Assert.Equal(AssistantTone.Neutral, effectiveForB.Tone);
        Assert.False(effectiveForB.Enabled);
        Assert.Equal(0.85m, effectiveForB.ConfidenceThreshold);
        Assert.Empty(effectiveForB.ForbiddenTopics);

        // B's own property shows B's entry and none of A's, the organization-wide one included.
        var viewOfB = await knowledge.GetViewAsync(orgB, propertyOfB.Id, "it");
        Assert.Equal([entryOfB.Id], viewOfB.Entries.Select(e => e.Id));
        var retrievedForB = await knowledge.RetrieveAsync(orgB, propertyOfB.Id, [KnowledgeTopic.WiFi, KnowledgeTopic.Contacts], "it");
        Assert.DoesNotContain(retrievedForB, f => f.Text.Contains("SEGRETO", StringComparison.Ordinal));

        // Written: B changes or deletes none of A's entries, through its own routes, A's property's or the organization's, and creates nothing on A's property.
        var edit = AssistantTestKit.Fact(KnowledgeTopic.WiFi, "Cambiato da B.");
        foreach (var attempt in new Func<Task>[]
                 {
                     () => knowledge.UpdateEntryAsync(orgB, propertyOfA.Id, entryOfA.Id, edit, Host),
                     () => knowledge.DeleteEntryAsync(orgB, propertyOfA.Id, entryOfA.Id),
                     () => knowledge.UpdateEntryAsync(orgB, propertyOfB.Id, entryOfA.Id, edit, Host),
                     () => knowledge.UpdateEntryAsync(orgB, null, wideOfA.Id, edit, Host),
                     () => knowledge.DeleteEntryAsync(orgB, null, wideOfA.Id),
                     () => knowledge.CreateEntryAsync(orgB, propertyOfA.Id, edit, Host),
                     () => rules.SavePropertySettingsAsync(orgB, propertyOfA.Id, new PropertyAssistantSettingsInput(true, null, null, null, null, null, null), Host),
                     () => rules.DeletePropertySettingsAsync(orgB, propertyOfA.Id),
                 })
        {
            await Assert.ThrowsAsync<NotFoundException>(attempt);
        }

        // Organization A is as it was: its words, its rules, its exceptions and its version.
        Assert.Equal(SecretOfA, (await db.PropertyKnowledgeEntries.AsNoTracking().SingleAsync(e => e.Id == entryOfA.Id)).Text);
        Assert.Equal(SecretOfOrganizationA, (await db.PropertyKnowledgeEntries.AsNoTracking().SingleAsync(e => e.Id == wideOfA.Id)).Text);
        Assert.Equal(SignatureOfA, (await rules.GetAsync(orgA)).Signature);
        Assert.Equal("Firma di A sulla casa", (await rules.GetPropertySettingsAsync(orgA, propertyOfA.Id)).Signature);
        Assert.Equal(versionOfA, (await rules.GetAsync(orgA)).InstructionVersion);
        Assert.Equal(2, await db.PropertyKnowledgeEntries.AsNoTracking().CountAsync(e => e.OrgId == orgA));

        // And A reads its own: the entries, the organization-wide one under its property, and what the pipeline retrieves.
        var viewOfA = await knowledge.GetViewAsync(orgA, propertyOfA.Id, "it");
        Assert.Equal(new[] { entryOfA.Id, wideOfA.Id }.Order(), viewOfA.Entries.Select(e => e.Id).Order());
        Assert.Contains(await knowledge.RetrieveAsync(orgA, propertyOfA.Id, [KnowledgeTopic.WiFi], "it"), f => f.Text == SecretOfA);
        Assert.DoesNotContain(viewOfB.Entries, e => e.Text == SecretOfA);
    }

    // ─── permission ───

    [Fact]
    public async Task AssistantKnowledge_Collaborator_OnlyReachableProperties_Test()
    {
        await using var db = AssistantTestKit.NewDb();
        var world = await HostScopeScenario.SeedAsync(db);
        var knowledge = AssistantTestKit.Knowledge(db);
        var rules = AssistantTestKit.Rules(db);
        var orgId = world.OrgId;
        var granted = world.Granted;
        var hidden = world.Hidden;

        // The administrator writes on both properties, and for the whole organization.
        var entryOfGranted = await knowledge.CreateEntryAsync(orgId, granted.Id, AssistantTestKit.Fact(KnowledgeTopic.Parking, "Parcheggio del Trullo."), Host);
        var entryOfHidden = await knowledge.CreateEntryAsync(orgId, hidden.Id, AssistantTestKit.Fact(KnowledgeTopic.WiFi, SecretOfA), Host);
        var wide = await knowledge.CreateEntryAsync(orgId, null, AssistantTestKit.Fact(KnowledgeTopic.Contacts, SecretOfOrganizationA), Host);
        await rules.SavePropertySettingsAsync(orgId, hidden.Id, new PropertyAssistantSettingsInput(null, AssistantTone.Formal, "Firma di Casa Bianca", null, null, null, null), Host);

        var authorization = HostAuthorizationTestHarness.Create(orgId, scopeResolver: ResolverOf(world));
        var asCollaborator = HostAuthorizationTestHarness.User(world.CollaboratorId);
        var knowledgeController = KnowledgeController(db, authorization, asCollaborator, orgId);
        var rulesController = RulesController(db, authorization, asCollaborator, orgId);
        var entriesBefore = await db.PropertyKnowledgeEntries.AsNoTracking().CountAsync();
        var versionBefore = (await rules.GetAsync(orgId)).InstructionVersion;

        // The property it was given: it reads the knowledge, writes an entry, and reads and saves the exceptions.
        var view = Assert.IsType<OkObjectResult>((await knowledgeController.GetForProperty(granted.Id, "it", CancellationToken.None)).Result).Value as AssistantKnowledgeViewDto;
        Assert.Contains(view!.Entries, e => e.Id == entryOfGranted.Id);
        Assert.DoesNotContain(view.Entries, e => e.Id == entryOfHidden.Id);
        Assert.Equal(
            StatusCodes.Status201Created,
            Assert.IsType<ObjectResult>((await knowledgeController.CreateForProperty(granted.Id, Request(KnowledgeTopic.Pets, "Cani ammessi."), CancellationToken.None)).Result).StatusCode);
        Assert.IsType<OkObjectResult>((await rulesController.SaveForProperty(granted.Id, new SavePropertyAssistantSettingsRequest { Tone = AssistantTone.Friendly }, CancellationToken.None)).Result);
        Assert.IsType<OkObjectResult>((await rulesController.GetForProperty(granted.Id, CancellationToken.None)).Result);

        // The property it was not given: the same 404 as a property that does not exist, for every read and every write, and nothing leaks into the answer.
        var missing = Guid.NewGuid();
        foreach (var propertyId in new[] { hidden.Id, missing })
        {
            AssertNotFound((await knowledgeController.GetForProperty(propertyId, "it", CancellationToken.None)).Result);
            AssertNotFound((await knowledgeController.CreateForProperty(propertyId, Request(KnowledgeTopic.Pets, "Intruso."), CancellationToken.None)).Result);
            AssertNotFound((await knowledgeController.UpdateForProperty(propertyId, entryOfHidden.Id, Request(KnowledgeTopic.WiFi, "Intruso."), CancellationToken.None)).Result);
            AssertNotFound(await knowledgeController.DeleteForProperty(propertyId, entryOfHidden.Id, CancellationToken.None));
            AssertNotFound((await rulesController.GetForProperty(propertyId, CancellationToken.None)).Result);
            AssertNotFound((await rulesController.SaveForProperty(propertyId, new SavePropertyAssistantSettingsRequest { Enabled = false }, CancellationToken.None)).Result);
            AssertNotFound(await rulesController.DeleteForProperty(propertyId, CancellationToken.None));
        }

        // An entry is asked for through its own property: the hidden property's entry, and the organization's, through the property it was given are 404 too.
        foreach (var attempt in new Func<Task>[]
                 {
                     () => knowledgeController.UpdateForProperty(granted.Id, entryOfHidden.Id, Request(KnowledgeTopic.WiFi, "Intruso."), CancellationToken.None),
                     () => knowledgeController.DeleteForProperty(granted.Id, entryOfHidden.Id, CancellationToken.None),
                     () => knowledgeController.UpdateForProperty(granted.Id, wide.Id, Request(KnowledgeTopic.Contacts, "Intruso."), CancellationToken.None),
                     () => knowledgeController.DeleteForProperty(granted.Id, wide.Id, CancellationToken.None),
                 })
        {
            Assert.Equal("assistant_entry_not_found", (await Assert.ThrowsAsync<NotFoundException>(attempt)).Code);
        }

        // Nothing of the hidden property changed: its entry, its exceptions, and no row was added for it (only the one the collaborator wrote on its own property).
        Assert.Equal(SecretOfA, (await db.PropertyKnowledgeEntries.AsNoTracking().SingleAsync(e => e.Id == entryOfHidden.Id)).Text);
        Assert.Equal(SecretOfOrganizationA, (await db.PropertyKnowledgeEntries.AsNoTracking().SingleAsync(e => e.Id == wide.Id)).Text);
        Assert.Equal("Firma di Casa Bianca", (await rules.GetPropertySettingsAsync(orgId, hidden.Id)).Signature);
        Assert.Equal(entriesBefore + 1, await db.PropertyKnowledgeEntries.AsNoTracking().CountAsync());
        Assert.Equal(1, await db.PropertyKnowledgeEntries.AsNoTracking().CountAsync(e => e.PropertyId == hidden.Id));
        Assert.True((await rules.GetAsync(orgId)).InstructionVersion > versionBefore);

        // The administrator of the organization (every property) reaches both.
        var administrator = HostAuthorizationTestHarness.User(world.OwnerUserId, "PropertyOwner");
        var allProperties = HostAuthorizationTestHarness.Create(orgId, scopeResolver: ResolverOf(world, administrator: true));
        var asAdministrator = KnowledgeController(db, allProperties, administrator, orgId);
        foreach (var propertyId in new[] { granted.Id, hidden.Id })
            Assert.IsType<OkObjectResult>((await asAdministrator.GetForProperty(propertyId, "it", CancellationToken.None)).Result);
    }

    [Fact]
    public void TheControllers_AreBehindTheFlag_PrivateAndEveryActionHasItsPolicy()
    {
        var expected = new Dictionary<string, string[]>
        {
            [$"{nameof(AssistantKnowledgeController)}.{nameof(AssistantKnowledgeController.GetForProperty)}"] = [CasazenPolicies.PropertyRead],
            [$"{nameof(AssistantKnowledgeController)}.{nameof(AssistantKnowledgeController.CreateForProperty)}"] = [CasazenPolicies.PropertyWrite],
            [$"{nameof(AssistantKnowledgeController)}.{nameof(AssistantKnowledgeController.UpdateForProperty)}"] = [CasazenPolicies.PropertyWrite],
            [$"{nameof(AssistantKnowledgeController)}.{nameof(AssistantKnowledgeController.DeleteForProperty)}"] = [CasazenPolicies.PropertyWrite],
            [$"{nameof(AssistantKnowledgeController)}.{nameof(AssistantKnowledgeController.ListForOrganization)}"] = [CasazenPolicies.BookingRead],
            [$"{nameof(AssistantKnowledgeController)}.{nameof(AssistantKnowledgeController.CreateForOrganization)}"] = [CasazenPolicies.BookingRead, CasazenPolicies.OrgBillingAdmin],
            [$"{nameof(AssistantKnowledgeController)}.{nameof(AssistantKnowledgeController.UpdateForOrganization)}"] = [CasazenPolicies.BookingRead, CasazenPolicies.OrgBillingAdmin],
            [$"{nameof(AssistantKnowledgeController)}.{nameof(AssistantKnowledgeController.DeleteForOrganization)}"] = [CasazenPolicies.BookingRead, CasazenPolicies.OrgBillingAdmin],
            [$"{nameof(AssistantRulesController)}.{nameof(AssistantRulesController.Get)}"] = [CasazenPolicies.BookingRead],
            [$"{nameof(AssistantRulesController)}.{nameof(AssistantRulesController.Save)}"] = [CasazenPolicies.BookingRead, CasazenPolicies.OrgBillingAdmin],
            [$"{nameof(AssistantRulesController)}.{nameof(AssistantRulesController.GetForProperty)}"] = [CasazenPolicies.PropertyRead],
            [$"{nameof(AssistantRulesController)}.{nameof(AssistantRulesController.SaveForProperty)}"] = [CasazenPolicies.PropertyWrite],
            [$"{nameof(AssistantRulesController)}.{nameof(AssistantRulesController.DeleteForProperty)}"] = [CasazenPolicies.PropertyWrite],
        };

        foreach (var type in new[] { typeof(AssistantKnowledgeController), typeof(AssistantRulesController) })
        {
            Assert.Equal("api/assistant", ((RouteAttribute)Attribute.GetCustomAttribute(type, typeof(RouteAttribute))!).Template);
            Assert.Equal(FeatureFlags.MessageAssistant, ((FeatureGateAttribute)Attribute.GetCustomAttribute(type, typeof(FeatureGateAttribute))!).Flag);
            Assert.NotNull(Attribute.GetCustomAttribute(type, typeof(PrivateAnswerAttribute)));
            Assert.Empty(type.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true));
            Assert.Empty(type.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true));

            foreach (var action in type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
            {
                var policies = action.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).Cast<AuthorizeAttribute>().Select(a => a.Policy!).ToArray();
                Assert.True(expected.TryGetValue($"{type.Name}.{action.Name}", out var wanted), $"{type.Name}.{action.Name} is not in the list of the actions");
                Assert.Equal(wanted!.Order(), policies.Order());
                Assert.Empty(action.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true));
            }
        }

        Assert.Equal(expected.Count, new[] { typeof(AssistantKnowledgeController), typeof(AssistantRulesController) }
            .Sum(type => type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly).Length));
    }

    // ─── the controllers over the real services, the real scope resolver and the real resource handler ───

    private static SaveKnowledgeEntryRequest Request(KnowledgeTopic topic, string text) =>
        new() { Topic = topic, Kind = KnowledgeEntryKind.Fact, Text = text };

    /// <summary>
    /// The real scope resolver: the collaborator of the scenario is «Solo alcuni» and was given the first property only; the administrator reaches the whole organization.
    /// </summary>
    private static IHostScopeResolver ResolverOf(HostScopeWorld world, bool administrator = false)
    {
        var member = administrator
            ? new OrgMemberSnapshot(world.OrgId, OrgRole.Owner, OrgMemberStatus.Active)
            : new OrgMemberSnapshot(world.OrgId, OrgRole.Collaborator, OrgMemberStatus.Active, PropertyScope.Selected, new HashSet<Guid> { world.Granted.Id });
        return HostAuthorizationTestHarness.ScopeResolver(
            new UserAuthorizationSnapshot(Exists: true, IsActive: true, Role: UserRole.None, SupplierOrgId: null, Memberships: [], OrgId: world.OrgId, OrgMember: member));
    }

    private static AssistantKnowledgeController KnowledgeController(AppDbContext db, IAuthorizationService authorization, ClaimsPrincipal user, Guid orgId) =>
        new(AssistantTestKit.Knowledge(db), Properties(db), Org(orgId), authorization) { ControllerContext = ContextOf(user) };

    private static AssistantRulesController RulesController(AppDbContext db, IAuthorizationService authorization, ClaimsPrincipal user, Guid orgId) =>
        new(AssistantTestKit.Rules(db), Properties(db), Org(orgId), authorization) { ControllerContext = ContextOf(user) };

    /// <summary>The property records as the application reads them: the row, whatever the org (the controller checks the org and the reach itself).</summary>
    private static IPropertyService Properties(AppDbContext db)
    {
        var properties = new Mock<IPropertyService>();
        properties
            .Setup(p => p.GetPropertyRecordAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid id) => db.Properties.AsNoTracking().FirstOrDefault(p => p.Id == id));
        return properties.Object;
    }

    private static IOrgContextResolver Org(Guid orgId)
    {
        var resolver = new Mock<IOrgContextResolver>();
        resolver.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(orgId);
        return resolver.Object;
    }

    private static ControllerContext ContextOf(ClaimsPrincipal user) => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = user,
            RequestServices = new ServiceCollection().AddSingleton<IStringLocalizer<SharedResources>>(new KeyLocalizer()).BuildServiceProvider(),
        },
    };

    private static void AssertNotFound(IActionResult? result)
    {
        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
        Assert.Equal("property_not_found", Assert.IsAssignableFrom<ProblemDetails>(problem.Value).Extensions["code"]);
    }

    private sealed class KeyLocalizer : IStringLocalizer<SharedResources>
    {
        public LocalizedString this[string name] => new(name, name);

        public LocalizedString this[string name, params object[] arguments] => new(name, name);

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
}
