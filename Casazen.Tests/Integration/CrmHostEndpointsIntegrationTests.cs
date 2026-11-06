using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>The consent-collection host with the module of the CRM open (<c>Features:CrmMarketing</c>), so the endpoints of the host answer.</summary>
public class CrmHostConsentFactory : ConsentCollectionFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Features:CrmMarketing"] = "true" }));
    }
}

/// <summary>
/// E07/F04/US01/T01 and T03 (NF-90): the permissions of the two endpoints of the host, over the real pipeline on PostgreSQL (the roles and the
/// contexts come from the migration seed). <c>POST api/crm/contacts/{id}/send-confirmation</c>: a contact of another org is a 404, a collaborator
/// «Solo alcuni» reaches the contacts of a profile with stays at its properties and no other (a lead with no profile never), the owner reaches the
/// whole org, and each outcome of the service has its status and its code. <c>api/properties/{id}/welcome-link</c>: a property of another org is a
/// 404, a property the caller does not reach or may not edit is a 403, and nothing is written for a refused call.
/// </summary>
public class CrmHostEndpointsIntegrationTests(CrmHostConsentFactory factory) : IClassFixture<CrmHostConsentFactory>
{
    private sealed record Contact(Guid Id, string Address);

    private sealed record World(
        Guid OrgId,
        string OwnerId,
        string CollaboratorId,
        Guid CasaA,
        Guid CasaB,
        Guid CasaOfAnotherOrg,
        Contact Reachable,
        Contact Hidden,
        Contact Lead,
        Contact OfAnotherOrg);

    // ─── send-confirmation ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Permission rule (scope): the collaborator who was given Casa A reaches the contact of the profile that stayed at Casa A and gets the same
    /// 404 as a contact that does not exist for the one of Casa B, for a lead with no profile and for another org's; nothing is queued for those.
    /// The owner reaches the whole org (Casa B, the lead) and never another org's contact.
    /// </summary>
    [PostgresFact]
    public async Task SendConfirmation_CollaboratorOfSomeProperties_ReachesOnlyTheContactsOfItsProperties_Test()
    {
        var world = await NewWorldAsync();
        using var collaborator = factory.CreateAuthenticatedClient(world.CollaboratorId);
        using var owner = factory.CreateAuthenticatedClient(world.OwnerId, roles: "PropertyOwner");

        var own = await collaborator.PostAsync(SendConfirmation(world.Reachable.Id), null);

        Assert.Equal(HttpStatusCode.Accepted, own.StatusCode);
        Assert.True((await own.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sent").GetBoolean());
        foreach (var other in new[] { world.Hidden, world.Lead, world.OfAnotherOrg })
        {
            await OrgTeamHttp.AssertProblemAsync(
                await collaborator.PostAsync(SendConfirmation(other.Id), null), HttpStatusCode.NotFound, "crm_contact_not_found");
        }

        Assert.Equal(world.Reachable.Address, Assert.Single(factory.SentEmails(), mail => mail.To == world.Reachable.Address).To);
        Assert.DoesNotContain(
            factory.SentEmails(),
            mail => mail.To == world.Hidden.Address || mail.To == world.Lead.Address || mail.To == world.OfAnotherOrg.Address);

        Assert.Equal(HttpStatusCode.Accepted, (await owner.PostAsync(SendConfirmation(world.Hidden.Id), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await owner.PostAsync(SendConfirmation(world.Lead.Id), null)).StatusCode);
        await OrgTeamHttp.AssertProblemAsync(
            await owner.PostAsync(SendConfirmation(world.OfAnotherOrg.Id), null), HttpStatusCode.NotFound, "crm_contact_not_found");
        Assert.DoesNotContain(factory.SentEmails(), mail => mail.To == world.OfAnotherOrg.Address);
    }

    /// <summary>
    /// Data rule (the outcomes of the service): a consent that waits is 202 <c>sent</c> and a repeat inside ten minutes is 202 <c>not sent</c>
    /// with one message queued; a contact that is not an e-mail with a consent is 422, one at the maximum of sends and one that opted out are
    /// 409 with their own codes; a queue that refuses the message is 503 and costs the contact nothing, so the next call goes out.
    /// </summary>
    [PostgresFact]
    public async Task SendConfirmation_EachOutcome_AnswersItsOwnStatusAndCode_Test()
    {
        var world = await NewWorldAsync();
        using var owner = factory.CreateAuthenticatedClient(world.OwnerId, roles: "PropertyOwner");
        var waiting = await SeedEmailContactAsync(world.OrgId);
        var whatsApp = await SeedWhatsAppContactAsync(world.OrgId);
        var atTheMaximum = await SeedEmailContactAsync(world.OrgId, sentCount: 3);
        var optedOut = await SeedEmailContactAsync(world.OrgId, optedOut: true);
        var refused = await SeedEmailContactAsync(world.OrgId);

        var first = await owner.PostAsync(SendConfirmation(waiting.Id), null);
        var second = await owner.PostAsync(SendConfirmation(waiting.Id), null);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.True((await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sent").GetBoolean());
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.False((await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sent").GetBoolean());
        Assert.Single(factory.SentEmails(), mail => mail.To == waiting.Address);

        await OrgTeamHttp.AssertProblemAsync(
            await owner.PostAsync(SendConfirmation(whatsApp.Id), null), HttpStatusCode.UnprocessableEntity, "crm_confirmation_not_required");
        await OrgTeamHttp.AssertProblemAsync(
            await owner.PostAsync(SendConfirmation(atTheMaximum.Id), null), HttpStatusCode.Conflict, "crm_confirmation_limit_reached");
        await OrgTeamHttp.AssertProblemAsync(
            await owner.PostAsync(SendConfirmation(optedOut.Id), null), HttpStatusCode.Conflict, "crm_contact_suppressed");
        Assert.DoesNotContain(factory.SentEmails(), mail => mail.To == atTheMaximum.Address || mail.To == optedOut.Address);

        factory.RefuseEmails = true;
        try
        {
            await OrgTeamHttp.AssertProblemAsync(
                await owner.PostAsync(SendConfirmation(refused.Id), null),
                HttpStatusCode.ServiceUnavailable,
                "marketing_confirmation_unavailable");
        }
        finally
        {
            factory.RefuseEmails = false;
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stored = await db.GuestContacts.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == refused.Id);
            Assert.Equal((0, GuestContactConfirmation.Unconfirmed), (stored.ConfirmationSentCount, stored.Confirmation));
            Assert.Null(stored.ConfirmationSentAt);
        }

        var again = await owner.PostAsync(SendConfirmation(refused.Id), null);

        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        Assert.True((await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sent").GetBoolean());
        Assert.Single(factory.SentEmails(), mail => mail.To == refused.Address);
    }

    // ─── welcome-link ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Permission rule: the owner makes, reads and replaces the link of any property of its org and a property of another org is a 404 for all
    /// three calls; a person who may not write properties (the collaborator) and an account that reaches only the properties it created get 403
    /// on a property of the org they cannot edit, and nothing is written for a refused call; without a token the answer is 401.
    /// </summary>
    [PostgresFact]
    public async Task WelcomeLink_HostEndpoints_AnswerByTheOrgTheReachAndThePermissionOfTheCaller_Test()
    {
        var world = await NewWorldAsync();
        var legacyId = await AddAccountWithoutTeamAsync(world.OrgId);
        var casaOfLegacy = await AddPropertyAsync(world.OrgId, legacyId, "Casa creata dall'account");
        using var owner = factory.CreateAuthenticatedClient(world.OwnerId, roles: "PropertyOwner");
        using var collaborator = factory.CreateAuthenticatedClient(world.CollaboratorId);
        using var legacy = factory.CreateAuthenticatedClient(legacyId);
        using var anonymous = factory.CreateClient();

        var none = await owner.GetFromJsonAsync<JsonElement>(WelcomeLink(world.CasaB));
        var created = await owner.PostAsync(WelcomeLink(world.CasaB), null);
        var again = await owner.PostAsync(WelcomeLink(world.CasaB), null);
        var rotated = await owner.PostAsync(WelcomeLink(world.CasaB, "/rotate"), null);

        Assert.False(none.GetProperty("active").GetBoolean());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var firstUrl = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString();
        Assert.Contains("/benvenuto/", firstUrl, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(firstUrl, (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString());
        Assert.Equal(HttpStatusCode.Created, rotated.StatusCode);
        var secondUrl = (await rotated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString();
        Assert.NotEqual(firstUrl, secondUrl);

        // A property of another org does not exist for the owner: the same 404 for the three calls.
        await OrgTeamHttp.AssertProblemAsync(
            await owner.GetAsync(WelcomeLink(world.CasaOfAnotherOrg)), HttpStatusCode.NotFound, "property_not_found");
        await OrgTeamHttp.AssertProblemAsync(
            await owner.PostAsync(WelcomeLink(world.CasaOfAnotherOrg), null), HttpStatusCode.NotFound, "property_not_found");
        await OrgTeamHttp.AssertProblemAsync(
            await owner.PostAsync(WelcomeLink(world.CasaOfAnotherOrg, "/rotate"), null), HttpStatusCode.NotFound, "property_not_found");

        // The collaborator may not write properties at all; the account without a team reaches the property it created and no other.
        Assert.Equal(HttpStatusCode.Forbidden, (await collaborator.PostAsync(WelcomeLink(world.CasaA), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await legacy.PostAsync(WelcomeLink(casaOfLegacy), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await legacy.GetAsync(WelcomeLink(world.CasaB))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await legacy.PostAsync(WelcomeLink(world.CasaB), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await legacy.PostAsync(WelcomeLink(world.CasaB, "/rotate"), null)).StatusCode);

        // Nothing was written for the refused calls: the link of Casa B is the one the owner replaced, Casa A has none.
        var kept = await owner.GetFromJsonAsync<JsonElement>(WelcomeLink(world.CasaB));
        Assert.Equal(secondUrl, kept.GetProperty("url").GetString());
        Assert.False((await owner.GetFromJsonAsync<JsonElement>(WelcomeLink(world.CasaA))).GetProperty("active").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(WelcomeLink(world.CasaB))).StatusCode);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────────────────────

    private static string SendConfirmation(Guid contactId) => $"/api/crm/contacts/{contactId}/send-confirmation";

    private static string WelcomeLink(Guid propertyId, string suffix = "") => $"/api/properties/{propertyId}/welcome-link{suffix}";

    /// <summary>
    /// An org with its owner, a collaborator «Solo alcuni» who was given Casa A only, a second property (Casa B) and a second org with a property
    /// of its own; a consent contact of the profile that stayed at Casa A, one of the profile that stayed at Casa B, a lead with no profile and
    /// one of the other org.
    /// </summary>
    private async Task<World> NewWorldAsync()
    {
        var (ownerId, orgId) = await OrgTeamHttp.SeedOwnerWithTeamRowsAsync(factory);
        var collaboratorId = await OrgTeamHttp.AddMemberAsync(factory, orgId, OrgRole.Collaborator, ["short-rent"]);
        var (otherOwnerId, otherOrgId) = await OrgTeamHttp.SeedOwnerWithTeamRowsAsync(factory);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var casaA = HostScopeScenario.NewProperty(orgId, ownerId, "Casa A");
        var casaB = HostScopeScenario.NewProperty(orgId, ownerId, "Casa B");
        var casaOfAnotherOrg = HostScopeScenario.NewProperty(otherOrgId, otherOwnerId, "Casa di un altro host");
        db.Properties.AddRange(casaA, casaB, casaOfAnotherOrg);

        var member = await db.OrgMembers.IgnoreQueryFilters().SingleAsync(m => m.UserId == collaboratorId);
        member.PropertyScope = PropertyScope.Selected;
        db.PropertyMemberAccesses.Add(new PropertyMemberAccess { OrgId = orgId, UserId = collaboratorId, PropertyId = casaA.Id });

        var profileA = NewProfile(orgId, casaA.Id);
        var profileB = NewProfile(orgId, casaB.Id);
        var profileOfAnotherOrg = NewProfile(otherOrgId, casaOfAnotherOrg.Id);
        db.GuestCrmProfiles.AddRange(profileA, profileB, profileOfAnotherOrg);
        await db.SaveChangesAsync();

        var contacts = scope.ServiceProvider.GetRequiredService<ICrmContactService>();
        return new World(
            orgId,
            ownerId,
            collaboratorId,
            casaA.Id,
            casaB.Id,
            casaOfAnotherOrg.Id,
            await ConsentAsync(contacts, orgId, profileA.Id),
            await ConsentAsync(contacts, orgId, profileB.Id),
            await ConsentAsync(contacts, orgId, null),
            await ConsentAsync(contacts, otherOrgId, profileOfAnotherOrg.Id));
    }

    private static GuestCrmProfile NewProfile(Guid orgId, Guid propertyId) => new()
    {
        OrgId = orgId,
        CompositeKeyHash = Guid.NewGuid().ToString("N"),
        PropertyIdsJson = JsonSerializer.Serialize(new[] { propertyId }),
        TagsJson = "[]",
        FirstStayAt = DateTime.UtcNow,
        LastStayAt = DateTime.UtcNow,
    };

    private static async Task<Contact> ConsentAsync(ICrmContactService contacts, Guid orgId, Guid? profileId)
    {
        var address = $"ospite.{Guid.NewGuid():N}@example.com";
        var contact = await contacts.RecordConsentAsync(new CrmConsentInput(
            orgId,
            GuestContactChannel.Email,
            address,
            GuestContactOrigin.GuestPage,
            ConsentCollectionFactory.MarketingVersion,
            GuestConsentSource.GuestPage,
            GuestCrmProfileId: profileId));
        Assert.NotNull(contact);
        return new Contact(contact.Id, address);
    }

    /// <summary>A consent on an e-mail address, with <paramref name="sentCount"/> messages sent long ago, or that the person then withdrew.</summary>
    private async Task<Contact> SeedEmailContactAsync(Guid orgId, int sentCount = 0, bool optedOut = false)
    {
        using var scope = factory.Services.CreateScope();
        var contacts = scope.ServiceProvider.GetRequiredService<ICrmContactService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var contact = await ConsentAsync(contacts, orgId, null);

        if (sentCount > 0)
        {
            var stored = await db.GuestContacts.IgnoreQueryFilters().SingleAsync(c => c.Id == contact.Id);
            stored.Confirmation = GuestContactConfirmation.Pending;
            stored.ConfirmationSentCount = sentCount;
            stored.ConfirmationSentAt = DateTime.UtcNow.AddDays(-1);
            await db.SaveChangesAsync();
        }

        if (optedOut)
            await contacts.WithdrawAsync(orgId, contact.Id, "unsubscribe", GuestConsentSource.GuestPage);

        return contact;
    }

    private async Task<Contact> SeedWhatsAppContactAsync(Guid orgId)
    {
        using var scope = factory.Services.CreateScope();
        var contacts = scope.ServiceProvider.GetRequiredService<ICrmContactService>();
        var phone = $"+39333{Random.Shared.Next(1_000_000, 9_999_999)}";
        var contact = await contacts.RecordConsentAsync(new CrmConsentInput(
            orgId,
            GuestContactChannel.WhatsApp,
            phone,
            GuestContactOrigin.GuestPage,
            ConsentCollectionFactory.MarketingVersion,
            GuestConsentSource.GuestPage));
        Assert.NotNull(contact);
        return new Contact(contact.Id, phone);
    }

    /// <summary>A host account of the org with no team row (an account from before the team): it reaches the properties it created.</summary>
    private async Task<string> AddAccountWithoutTeamAsync(Guid orgId)
    {
        var userId = $"auth0|crm-host-{Guid.NewGuid():N}";
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User
        {
            Id = userId,
            Email = $"{Guid.NewGuid():N}@example.com",
            FirstName = "Account",
            LastName = "Senza team",
            OrgId = orgId,
            Role = UserRole.None,
            IsActive = true,
        };
        db.Users.Add(user);
        await HostOnboardingSeed.MarkOnboardedAsync(db, user, orgId, scope.ServiceProvider.GetRequiredService<ILegalDocumentService>());
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<IUserContextMembershipService>().GrantAsync(userId, [UserRole.PropertyOwner]);
        return userId;
    }

    private async Task<Guid> AddPropertyAsync(Guid orgId, string ownerId, string name)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var property = HostScopeScenario.NewProperty(orgId, ownerId, name);
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property.Id;
    }
}
