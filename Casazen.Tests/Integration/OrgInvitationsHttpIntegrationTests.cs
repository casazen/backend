using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Tests.Unit.Email;
using Casazen.Tests.Unit.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// The web host with the invitation emails recorded instead of queued, so a test can read the link the invited person would
/// receive.
/// </summary>
public sealed class OrgInvitationsFactory : CasazenWebApplicationFactory
{
    private readonly RecordingEmailQueue _emails = new();

    /// <summary>What the host queued so far: recipient, content and template name.</summary>
    public IReadOnlyList<(string? To, EmailContent Content, string Template)> SentEmails() => _emails.Snapshot();

    /// <summary>The team needs no comuni, and two hosts starting together on the shared in-memory store would both import them.</summary>
    protected override bool SeedComuneSample => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        var inMemoryStore = $"org-invitations-{Guid.NewGuid():N}";
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEmailQueue>();
            services.AddSingleton<IEmailQueue>(_emails);

            // The in-memory fallback of the host is one store for every test host of the process, and other suites write
            // roles and organisations into it: this host gets a store of its own, where the seed of the tests is all there is.
            if (!UsesPostgreSql)
            {
                RemoveAllOf<DbContextOptions<AppDbContext>>(services);
                RemoveAllOf<IDbContextOptionsConfiguration<AppDbContext>>(services);
                services.AddDbContext<AppDbContext>(options =>
                {
                    options.UseInMemoryDatabase(inMemoryStore);
                    options.ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));
                });
            }
        });
    }
}

/// <summary>
/// AM-02 over the real pipeline (routes, policies, model binding, error contract, JSON): an owner invites, the invitee opens
/// the link and accepts, the owner lists, deactivates and reactivates the member; and every refusal a client has to handle,
/// with its status and code. On PostgreSQL in CI; on the in-memory fallback locally (the seeded roles are created first).
/// </summary>
public class OrgInvitationsHttpIntegrationTests(OrgInvitationsFactory factory) : IClassFixture<OrgInvitationsFactory>
{
    /// <summary>The versions of the legal documents of the test host (<c>Legal:Documents:*:Version</c>).</summary>
    private const string ConsentVersion = "2026-06-v1";

    private static object InvitationBody(string email, string role = "Collaborator", string name = "Anna Leone") =>
        new { email, name, role, areas = new[] { "short-rent" } };

    private static object AcceptBody(string token) => new
    {
        token,
        consents = new
        {
            tosAccepted = true,
            tosVersion = ConsentVersion,
            privacyAccepted = true,
            privacyVersion = ConsentVersion,
            dpaAccepted = true,
            dpaVersion = ConsentVersion,
            subprocessorsAcknowledged = true,
            subprocessorsVersion = ConsentVersion,
        },
    };

    private static string NewEmail(string name) => $"{name}.{Guid.NewGuid():N}@example.com";

    private static string NewUserId(string name) => $"auth0|am02-{name}-{Guid.NewGuid():N}";

    /// <summary>An owner as the onboarding leaves it, in an org on the given plan.</summary>
    private async Task<(string OwnerId, Guid OrgId)> OwnerOnPlanAsync(PlanTier tier, SubscriptionStatus status)
    {
        await EnsureSeedsAsync();
        var (ownerId, orgId) = await OrgTeamHttp.SeedOwnerWithTeamRowsAsync(factory);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = await db.Orgs.SingleAsync(o => o.Id == orgId);
        org.PlanTier = tier;
        org.SubscriptionStatus = status;
        org.SubscriptionId = status == SubscriptionStatus.None ? null : $"sub_{Guid.NewGuid():N}";
        await db.SaveChangesAsync();
        return (ownerId, orgId);
    }

    private static readonly SemaphoreSlim SeedLock = new(1, 1);

    /// <summary>
    /// On PostgreSQL the migrations seed the contexts, roles and permissions. The in-memory fallback of the host has none (and
    /// <c>EnsureCreated</c> seeds only a store nobody has touched, which the host's start-up already did): write the seed data
    /// of the model, once, so the same tests run locally.
    /// </summary>
    private async Task EnsureSeedsAsync()
    {
        if (factory.UsesPostgreSql)
            return;

        await SeedLock.WaitAsync();
        try
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (await db.Roles.AnyAsync())
                return;

            var model = ((IInfrastructure<IServiceProvider>)db).Instance.GetRequiredService<IDesignTimeModel>().Model;
            foreach (var entityType in model.GetEntityTypes().Where(e => e.ClrType.Name is "AppContext" or "Role" or "RolePermission"))
            {
                foreach (var row in entityType.GetSeedData())
                {
                    var entity = Activator.CreateInstance(entityType.ClrType)!;
                    foreach (var (name, value) in row)
                        entityType.ClrType.GetProperty(name)!.SetValue(entity, value);
                    db.Add(entity);
                }
            }

            await db.SaveChangesAsync();
        }
        finally
        {
            SeedLock.Release();
        }
    }

    private HttpClient OwnerClient(string ownerId) => factory.CreateAuthenticatedClient(ownerId, roles: "PropertyOwner");

    private HttpClient InviteeClient(string userId, string email, bool verified = true, string? roles = null) =>
        factory.CreateAuthenticatedClient(userId, roles, email, verified);

    private string LinkTokenSentTo(string email, string template)
    {
        var mail = factory.SentEmails().Last(e => string.Equals(e.To, email, StringComparison.OrdinalIgnoreCase) && e.Template == template);
        return OrgInvitationTestKit.TokenInEmail(mail);
    }

    /// <summary>The owner invites <paramref name="email"/> and the token of the link in the email comes back with the invitation id.</summary>
    private async Task<(Guid InvitationId, string Token)> InviteAsync(HttpClient owner, string email, string role = "Collaborator")
    {
        var response = await owner.PostAsJsonAsync("/api/orgs/me/invitations", InvitationBody(email, role));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("invitation").GetProperty("id").GetGuid(), LinkTokenSentTo(email, EmailTemplates.Names.OrgInvitation));
    }

    // ─── The whole journey ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Journey_InviteLookupAcceptThenDeactivateAndReactivate()
    {
        var (ownerId, orgId) = await OwnerOnPlanAsync(PlanTier.Pro, SubscriptionStatus.Active);
        using var owner = OwnerClient(ownerId);
        var annaId = NewUserId("anna");
        var annaEmail = NewEmail("anna");

        // The owner invites: 201, the invitation is pending and the answer never carries the secret of the link.
        var created = await owner.PostAsJsonAsync("/api/orgs/me/invitations", InvitationBody(annaEmail));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(createdBody.GetProperty("emailQueued").GetBoolean());
        var invitation = createdBody.GetProperty("invitation");
        Assert.Equal(("Pending", annaEmail, "Collaborator"), (
            invitation.GetProperty("status").GetString(), invitation.GetProperty("email").GetString(), invitation.GetProperty("role").GetString()));
        var token = LinkTokenSentTo(annaEmail, EmailTemplates.Names.OrgInvitation);
        Assert.DoesNotContain(token, createdBody.GetRawText());

        var listed = await (await owner.GetAsync("/api/orgs/me/invitations")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(invitation.GetProperty("id").GetGuid(), Assert.Single(listed.EnumerateArray()).GetProperty("id").GetGuid());

        // Anyone with the link sees what it is for, before signing in.
        using var anonymous = factory.CreateClient();
        var lookup = await anonymous.PostAsJsonAsync("/api/org-invitations/lookup", new { token });
        Assert.Equal(HttpStatusCode.OK, lookup.StatusCode);
        var preview = await lookup.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((annaEmail, "Collaborator"), (preview.GetProperty("email").GetString(), preview.GetProperty("role").GetString()));

        // Anna signs in (a new account, verified email) and accepts; sending it twice finds it done.
        using var anna = InviteeClient(annaId, annaEmail);
        var accepted = await anna.PostAsJsonAsync("/api/org-invitations/accept", AcceptBody(token));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var acceptedBody = await accepted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(orgId, acceptedBody.GetProperty("orgId").GetGuid());
        Assert.Equal(("Collaborator", false), (acceptedBody.GetProperty("role").GetString(), acceptedBody.GetProperty("leftEmptyOrg").GetBoolean()));
        Assert.Equal(HttpStatusCode.OK, (await anna.PostAsJsonAsync("/api/org-invitations/accept", AcceptBody(token))).StatusCode);

        // The link is spent for everybody, and she works in the area she was invited to, nowhere else.
        await OrgTeamHttp.AssertProblemAsync(await anonymous.PostAsJsonAsync("/api/org-invitations/lookup", new { token }), HttpStatusCode.Gone, "invitation_invalid");
        Assert.Equal(HttpStatusCode.OK, (await anna.GetAsync("/api/users/me")).StatusCode);
        Assert.Equal(["short-rent"], await OrgTeamHttp.ContextKeysAsync(anna));
        Assert.Empty(await (await owner.GetAsync("/api/orgs/me/invitations")).Content.ReadFromJsonAsync<JsonElement[]>() ?? []);

        // The owner sees her among the people, and the plan's seats follow.
        var members = await (await owner.GetAsync("/api/orgs/me/members")).Content.ReadFromJsonAsync<JsonElement>();
        var annaRow = members.GetProperty("items").EnumerateArray().Single(m => m.GetProperty("userId").GetString() == annaId);
        Assert.Equal(("Collaborator", "Active", annaEmail), (
            annaRow.GetProperty("role").GetString(), annaRow.GetProperty("status").GetString(), annaRow.GetProperty("email").GetString()));
        Assert.Equal(["short-rent"], annaRow.GetProperty("areas").EnumerateArray().Select(a => a.GetString()));
        var seats = members.GetProperty("seats");
        Assert.Equal((10, 2, 2, 0, true), (
            seats.GetProperty("max").GetInt32(), seats.GetProperty("used").GetInt32(), seats.GetProperty("activeMembers").GetInt32(),
            seats.GetProperty("pendingInvitations").GetInt32(), seats.GetProperty("canInvite").GetBoolean()));
        var entitlement = await (await owner.GetAsync("/api/orgs/me/entitlement")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((10, 2, true), (
            entitlement.GetProperty("limits").GetProperty("maxSeats").GetInt32(),
            entitlement.GetProperty("usage").GetProperty("seats").GetInt32(),
            entitlement.GetProperty("canInviteMember").GetBoolean()));

        // Deactivated, she is refused on the very next request; reactivated, she is back.
        var memberId = annaRow.GetProperty("id").GetGuid();
        var deactivated = await owner.PostAsync($"/api/orgs/me/members/{memberId}/deactivate", null);
        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
        Assert.Equal("Deactivated", (await deactivated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        await OrgTeamHttp.AssertProblemAsync(await anna.GetAsync("/api/users/me"), HttpStatusCode.Forbidden, "member_inactive");

        var reactivated = await owner.PostAsync($"/api/orgs/me/members/{memberId}/reactivate", null);
        Assert.Equal(HttpStatusCode.OK, reactivated.StatusCode);
        Assert.Equal("Active", (await reactivated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, (await anna.GetAsync("/api/users/me")).StatusCode);
    }

    [Fact]
    public async Task Journey_ChangeTheRoleOfAMember_AndTheOwnerCannotBeTouched()
    {
        var (ownerId, orgId) = await OwnerOnPlanAsync(PlanTier.Pro, SubscriptionStatus.Active);
        var memberUserId = await OrgTeamHttp.AddMemberAsync(factory, orgId, OrgRole.Collaborator, ["short-rent"]);
        using var owner = OwnerClient(ownerId);
        var members = await (await owner.GetAsync("/api/orgs/me/members")).Content.ReadFromJsonAsync<JsonElement>();
        var rows = members.GetProperty("items").EnumerateArray().ToList();
        var memberId = rows.Single(m => m.GetProperty("userId").GetString() == memberUserId).GetProperty("id").GetGuid();
        var ownerRowId = rows.Single(m => m.GetProperty("role").GetString() == "Owner").GetProperty("id").GetGuid();

        var changed = await owner.PutAsJsonAsync($"/api/orgs/me/members/{memberId}", new { role = "Accountant" });

        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal("Accountant", (await changed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("role").GetString());
        await OrgTeamHttp.AssertProblemAsync(await owner.PostAsync($"/api/orgs/me/members/{ownerRowId}/deactivate", null), HttpStatusCode.Conflict, "org_last_owner");
        await OrgTeamHttp.AssertProblemAsync(await owner.PutAsJsonAsync($"/api/orgs/me/members/{memberId}", new { role = "Owner" }), HttpStatusCode.UnprocessableEntity, "org_owner_not_assignable");
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/orgs/me/members/{memberId}")).StatusCode);
        await OrgTeamHttp.AssertProblemAsync(await owner.PostAsync($"/api/orgs/me/members/{memberId}/deactivate", null), HttpStatusCode.NotFound, "org_member_not_found");
    }

    // ─── Who may do what ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Team_AMemberWhoIsNotAManager_IsRefusedOnEveryEndpointOfThePeople()
    {
        var (_, orgId) = await OwnerOnPlanAsync(PlanTier.Pro, SubscriptionStatus.Active);
        var collaborator = await OrgTeamHttp.AddMemberAsync(factory, orgId, OrgRole.Collaborator, ["short-rent"]);
        var accountant = await OrgTeamHttp.AddMemberAsync(factory, orgId, OrgRole.Accountant, ["short-rent"]);
        var someone = Guid.NewGuid();

        foreach (var userId in new[] { collaborator, accountant })
        {
            using var client = factory.CreateAuthenticatedClient(userId);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/orgs/me/members")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/orgs/me/invitations")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/orgs/me/invitations", InvitationBody(NewEmail("x")))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/orgs/me/members/{someone}/deactivate", null)).StatusCode);
        }
    }

    [Fact]
    public async Task Team_AnAdministratorInvitesEveryoneButAnAdministrator()
    {
        var (_, orgId) = await OwnerOnPlanAsync(PlanTier.Pro, SubscriptionStatus.Active);
        var adminId = await OrgTeamHttp.AddMemberAsync(factory, orgId, OrgRole.Admin, ["short-rent"]);
        using var admin = factory.CreateAuthenticatedClient(adminId);

        var refused = await admin.PostAsJsonAsync("/api/orgs/me/invitations", InvitationBody(NewEmail("ada"), "Admin"));
        var allowed = await admin.PostAsJsonAsync("/api/orgs/me/invitations", InvitationBody(NewEmail("pia"), "PropertyManager"));

        await OrgTeamHttp.AssertProblemAsync(refused, HttpStatusCode.Forbidden, "org_owner_required");
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
    }

    [Fact]
    public async Task Invite_WhatTheRequestGetsWrong_IsRefusedWithItsOwnCode()
    {
        var (ownerId, _) = await OwnerOnPlanAsync(PlanTier.Pro, SubscriptionStatus.Active);
        using var owner = OwnerClient(ownerId);

        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/orgs/me/invitations", InvitationBody("not an email"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/orgs/me/invitations", InvitationBody(NewEmail("x"), name: "<b>Anna</b>"))).StatusCode);
        await OrgTeamHttp.AssertProblemAsync(
            await owner.PostAsJsonAsync("/api/orgs/me/invitations", InvitationBody(NewEmail("x"), "Owner")),
            HttpStatusCode.UnprocessableEntity,
            "org_owner_not_assignable");
        await OrgTeamHttp.AssertProblemAsync(
            await owner.PostAsJsonAsync("/api/orgs/me/invitations", new { email = NewEmail("x"), name = "Anna", role = "Collaborator", areas = Array.Empty<string>() }),
            HttpStatusCode.UnprocessableEntity,
            "org_member_area_required");
    }

    [Fact]
    public async Task Invite_TheSeatsOfThePlan_AreTheLimit_AndARevokedInvitationFreesItsSeat()
    {
        var (ownerId, _) = await OwnerOnPlanAsync(PlanTier.Starter, SubscriptionStatus.None);
        using var owner = OwnerClient(ownerId);
        var (firstId, _) = await InviteAsync(owner, NewEmail("first"));
        var second = NewEmail("second");

        await OrgTeamHttp.AssertProblemAsync(
            await owner.PostAsJsonAsync("/api/orgs/me/invitations", InvitationBody(second)), HttpStatusCode.Conflict, "org_seat_limit_reached");
        var entitlement = await (await owner.GetAsync("/api/orgs/me/entitlement")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((2, 2, false), (
            entitlement.GetProperty("limits").GetProperty("maxSeats").GetInt32(),
            entitlement.GetProperty("usage").GetProperty("seats").GetInt32(),
            entitlement.GetProperty("canInviteMember").GetBoolean()));

        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/orgs/me/invitations/{firstId}/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/orgs/me/invitations/{firstId}/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/orgs/me/invitations", InvitationBody(second))).StatusCode);
        await OrgTeamHttp.AssertProblemAsync(
            await owner.PostAsJsonAsync("/api/orgs/me/invitations", InvitationBody(second)), HttpStatusCode.Conflict, "org_invitation_already_pending");
    }

    // ─── What the invited person can get wrong ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Accept_EveryRefusal_HasItsStatusAndCode_AndTheInvitationStaysUsable()
    {
        var (ownerId, _) = await OwnerOnPlanAsync(PlanTier.Pro, SubscriptionStatus.Active);
        using var owner = OwnerClient(ownerId);
        var annaId = NewUserId("anna");
        var annaEmail = NewEmail("anna");
        var (invitationId, token) = await InviteAsync(owner, annaEmail);
        using var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/org-invitations/accept", AcceptBody(token))).StatusCode);
        using (var stranger = InviteeClient(NewUserId("stranger"), NewEmail("stranger")))
            await OrgTeamHttp.AssertProblemAsync(await stranger.PostAsJsonAsync("/api/org-invitations/accept", AcceptBody(token)), HttpStatusCode.Forbidden, "invitation_email_mismatch");
        using (var unverified = InviteeClient(annaId, annaEmail, verified: false))
            await OrgTeamHttp.AssertProblemAsync(await unverified.PostAsJsonAsync("/api/org-invitations/accept", AcceptBody(token)), HttpStatusCode.Forbidden, "invitation_email_not_verified");
        using (var staff = InviteeClient(NewUserId("staff"), annaEmail, roles: "Admin"))
            await OrgTeamHttp.AssertProblemAsync(await staff.PostAsJsonAsync("/api/org-invitations/accept", AcceptBody(token)), HttpStatusCode.Forbidden, "invitation_platform_admin");

        using var anna = InviteeClient(annaId, annaEmail);
        await OrgTeamHttp.AssertProblemAsync(await anna.PostAsJsonAsync("/api/org-invitations/accept", new { token }), HttpStatusCode.BadRequest, "consents_incomplete");
        await OrgTeamHttp.AssertProblemAsync(await anna.PostAsJsonAsync("/api/org-invitations/accept", AcceptBody(new string('0', 64))), HttpStatusCode.Gone, "invitation_invalid");
        await OrgTeamHttp.AssertProblemAsync(await anna.PostAsJsonAsync("/api/org-invitations/accept", AcceptBody("not a token")), HttpStatusCode.Gone, "invitation_invalid");

        // None of those used the invitation: Anna accepts, and then the link is used for everybody else.
        Assert.Equal(HttpStatusCode.OK, (await anna.PostAsJsonAsync("/api/org-invitations/accept", AcceptBody(token))).StatusCode);
        using var bruno = InviteeClient(NewUserId("bruno"), NewEmail("bruno"));
        await OrgTeamHttp.AssertProblemAsync(await bruno.PostAsJsonAsync("/api/org-invitations/accept", AcceptBody(token)), HttpStatusCode.Gone, "invitation_used");
        await OrgTeamHttp.AssertProblemAsync(await owner.PostAsync($"/api/orgs/me/invitations/{invitationId}/revoke", null), HttpStatusCode.Conflict, "org_invitation_not_pending");
    }

    [Fact]
    public async Task Accept_ARevokedAndAnExpiredInvitation_AreGoneWithTheirOwnCodes()
    {
        var (ownerId, _) = await OwnerOnPlanAsync(PlanTier.Pro, SubscriptionStatus.Active);
        using var owner = OwnerClient(ownerId);
        var revokedEmail = NewEmail("revoked");
        var expiredEmail = NewEmail("expired");
        var (revokedId, revokedToken) = await InviteAsync(owner, revokedEmail);
        var (expiredId, expiredToken) = await InviteAsync(owner, expiredEmail);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/orgs/me/invitations/{revokedId}/revoke", null)).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var expiredRow = await db.OrgInvitations.IgnoreQueryFilters().SingleAsync(i => i.Id == expiredId);
            expiredRow.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            // An invitation expires a week after its last email: the test forces the expiry, so its last send is old too (AM-02c:
            // «reinvia» waits an hour after the last one).
            expiredRow.LastSentAt = DateTime.UtcNow.AddDays(-8);
            expiredRow.SendLog = [];
            await db.SaveChangesAsync();
        }

        using var revoked = InviteeClient(NewUserId("revoked"), revokedEmail);
        using var expired = InviteeClient(NewUserId("expired"), expiredEmail);
        using var anonymous = factory.CreateClient();

        await OrgTeamHttp.AssertProblemAsync(await revoked.PostAsJsonAsync("/api/org-invitations/accept", AcceptBody(revokedToken)), HttpStatusCode.Gone, "invitation_revoked");
        await OrgTeamHttp.AssertProblemAsync(await expired.PostAsJsonAsync("/api/org-invitations/accept", AcceptBody(expiredToken)), HttpStatusCode.Gone, "invitation_expired");
        // The lookup of the same links says nothing more than "invalid".
        await OrgTeamHttp.AssertProblemAsync(await anonymous.PostAsJsonAsync("/api/org-invitations/lookup", new { token = revokedToken }), HttpStatusCode.Gone, "invitation_invalid");
        await OrgTeamHttp.AssertProblemAsync(await anonymous.PostAsJsonAsync("/api/org-invitations/lookup", new { token = expiredToken }), HttpStatusCode.Gone, "invitation_invalid");

        // The expired one comes back with the owner's "send again", with a new link.
        var resent = await owner.PostAsync($"/api/orgs/me/invitations/{expiredId}/resend", null);
        Assert.Equal(HttpStatusCode.OK, resent.StatusCode);
        var newToken = LinkTokenSentTo(expiredEmail, EmailTemplates.Names.OrgInvitation);
        Assert.NotEqual(expiredToken, newToken);
        Assert.Equal(HttpStatusCode.OK, (await expired.PostAsJsonAsync("/api/org-invitations/accept", AcceptBody(newToken))).StatusCode);
    }

    // ─── A person who already has an org ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Accept_APersonWhoLeavesAnEmptyOrgOfItsOwn_JoinsTheOtherOne_AndTheOwnersTokenOpensNoBilling()
    {
        var (ownerId, orgId) = await OwnerOnPlanAsync(PlanTier.Pro, SubscriptionStatus.Active);
        using var owner = OwnerClient(ownerId);
        var frankId = NewUserId("frank");
        var frankEmail = NewEmail("frank");
        var (oldOrg, _) = await new OrgInvitationTestKit(NewHostContext).SeedPersonWithEmptyOrgAsync(frankId, frankEmail);
        var (_, token) = await InviteAsync(owner, frankEmail);
        using var frank = InviteeClient(frankId, frankEmail, roles: "PropertyOwner");

        var accepted = await frank.PostAsJsonAsync("/api/org-invitations/accept", AcceptBody(token));

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var body = await accepted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((orgId, true), (body.GetProperty("orgId").GetGuid(), body.GetProperty("leftEmptyOrg").GetBoolean()));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False((await db.Orgs.SingleAsync(o => o.Id == oldOrg.Id)).IsActive);
            var member = await db.OrgMembers.IgnoreQueryFilters().SingleAsync(m => m.UserId == frankId);
            Assert.Equal((orgId, OrgRole.Collaborator), (member.OrgId, member.Role));
        }

        // The Auth0 role of the owner is still in his token (the removal is the host's job, not done in a test host): as a
        // collaborator of the other org he must not be its billing administrator.
        Assert.Equal(HttpStatusCode.Forbidden, (await frank.GetAsync("/api/orgs/me/entitlement")).StatusCode);
    }

    [Fact]
    public async Task Accept_APersonWhoseOrgIsInUse_IsRefusedAndKeepsEverything()
    {
        var (ownerId, _) = await OwnerOnPlanAsync(PlanTier.Pro, SubscriptionStatus.Active);
        using var owner = OwnerClient(ownerId);
        var eveId = NewUserId("eve");
        var eveEmail = NewEmail("eve");
        var property = await factory.SeedPropertyAsync(eveId);
        var (invitationId, token) = await InviteAsync(owner, eveEmail);
        using var eve = InviteeClient(eveId, eveEmail, roles: "PropertyOwner");

        await OrgTeamHttp.AssertProblemAsync(
            await eve.PostAsJsonAsync("/api/org-invitations/accept", AcceptBody(token)), HttpStatusCode.Conflict, "invitation_user_has_organization");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(property.OrgId, (await db.Users.SingleAsync(u => u.Id == eveId)).OrgId);
        Assert.Equal(OrgInvitationStatus.Pending, (await db.OrgInvitations.IgnoreQueryFilters().SingleAsync(i => i.Id == invitationId)).Status);
        Assert.False(await db.OrgMembers.IgnoreQueryFilters().AnyAsync(m => m.UserId == eveId));
    }

    // ─── The lookup tells a stranger nothing ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Lookup_EveryLinkThatDoesNotWork_GetsExactlyTheSameAnswer()
    {
        var (ownerId, _) = await OwnerOnPlanAsync(PlanTier.Pro, SubscriptionStatus.Active);
        using var owner = OwnerClient(ownerId);
        using var anonymous = factory.CreateClient();
        var usedEmail = NewEmail("used");
        var (_, used) = await InviteAsync(owner, usedEmail);
        using (var invitee = InviteeClient(NewUserId("used"), usedEmail))
            Assert.Equal(HttpStatusCode.OK, (await invitee.PostAsJsonAsync("/api/org-invitations/accept", AcceptBody(used))).StatusCode);
        var (revokedId, revoked) = await InviteAsync(owner, NewEmail("revoked"));
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/orgs/me/invitations/{revokedId}/revoke", null)).StatusCode);
        var (expiredId, expired) = await InviteAsync(owner, NewEmail("expired"));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.OrgInvitations.IgnoreQueryFilters().SingleAsync(i => i.Id == expiredId)).ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        var answers = new List<(int Status, string? Code, string? Title, string? Detail)>();
        foreach (var token in new[] { used, revoked, expired, new string('a', 64), "not a token", new string('0', 64) })
        {
            var response = await anonymous.PostAsJsonAsync("/api/org-invitations/lookup", new { token });
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            answers.Add((
                (int)response.StatusCode,
                problem.GetProperty("code").GetString(),
                problem.TryGetProperty("title", out var title) ? title.GetString() : null,
                problem.GetProperty("detail").GetString()));
        }

        Assert.Single(answers.Distinct());
        Assert.Equal((410, "invitation_invalid"), (answers[0].Status, answers[0].Code));
    }

    // ─── The completions of AM-02c ──────────────────────────────────────────────────────────────────────

    private async Task<Guid> MemberRowIdAsync(HttpClient owner, string userId)
    {
        var members = await (await owner.GetAsync("/api/orgs/me/members")).Content.ReadFromJsonAsync<JsonElement>();
        return members.GetProperty("items").EnumerateArray()
            .Single(m => m.GetProperty("userId").GetString() == userId)
            .GetProperty("id").GetGuid();
    }

    private async Task<Guid> SeedPropertyAsync(Guid orgId, string ownerId, string name, RentalMode mode = RentalMode.Short)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var property = HostScopeScenario.NewProperty(orgId, ownerId, name);
        property.RentalMode = mode;
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property.Id;
    }

    private static async Task<List<Guid>> PropertyIdsAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/properties");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = rows.ValueKind == JsonValueKind.Array ? rows : rows.GetProperty("items");
        return items.EnumerateArray().Select(row => row.GetProperty("id").GetGuid()).ToList();
    }

    [Fact]
    public async Task Roles_TheOwnerReadsTheMatrixOfTheFiveRoles_AndAnyMemberReadsItWithNothingToGive()
    {
        var (ownerId, orgId) = await OwnerOnPlanAsync(PlanTier.Pro, SubscriptionStatus.Active);
        using var owner = OwnerClient(ownerId);

        var response = await owner.GetAsync("/api/orgs/me/roles");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var roles = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("roles").EnumerateArray().ToList();
        Assert.Equal(["Owner", "Admin", "PropertyManager", "Collaborator", "Accountant"], roles.Select(r => r.GetProperty("role").GetString()));
        // The owner gives every role but its own; only the collaborator can be limited to some properties.
        Assert.Equal([false, true, true, true, true], roles.Select(r => r.GetProperty("assignable").GetBoolean()));
        Assert.Equal(["Collaborator"], roles.Where(r => r.GetProperty("scopeSupported").GetBoolean()).Select(r => r.GetProperty("role").GetString()));
        Assert.All(roles, r => Assert.Equal(12, r.GetProperty("capabilities").GetArrayLength()));

        // The levels are read from the permissions of the seeded roles: the collaborator reads the prices, handles the guests and has
        // no account; the property manager holds the restricted membership of the account.
        var collaborator = roles.Single(r => r.GetProperty("role").GetString() == "Collaborator");
        var levels = collaborator.GetProperty("capabilities").EnumerateArray()
            .ToDictionary(c => c.GetProperty("id").GetString()!, c => c.GetProperty("level").GetString());
        Assert.Equal(("Read", "Yes", "No", "No"), (levels["prices"], levels["guests"], levels["people"], levels["billing"]));
        var collaboratorContexts = collaborator.GetProperty("permissionsByContext");
        Assert.Equal("staff", collaboratorContexts.GetProperty("short-rent").GetProperty("roleKey").GetString());
        Assert.False(collaboratorContexts.TryGetProperty("account", out _));
        var manager = roles.Single(r => r.GetProperty("role").GetString() == "PropertyManager");
        Assert.Equal("org_manager", manager.GetProperty("permissionsByContext").GetProperty("account").GetProperty("roleKey").GetString());

        // A member who is no manager reads the same matrix, and can give nothing.
        var memberUserId = await OrgTeamHttp.AddMemberAsync(factory, orgId, OrgRole.Collaborator, ["short-rent"]);
        using var member = factory.CreateAuthenticatedClient(memberUserId);
        var seen = (await (await member.GetAsync("/api/orgs/me/roles")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("roles").EnumerateArray();
        Assert.All(seen, r => Assert.False(r.GetProperty("assignable").GetBoolean()));
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/orgs/me/roles")).StatusCode);
    }

    [Fact]
    public async Task PropertyManager_HasTheAccountContextWithTheSuppliersOnly_AndThePeopleStayClosedToIt()
    {
        var (_, orgId) = await OwnerOnPlanAsync(PlanTier.Pro, SubscriptionStatus.Active);
        var managerId = await OrgTeamHttp.AddMemberAsync(factory, orgId, OrgRole.PropertyManager, ["short-rent"]);
        using var manager = factory.CreateAuthenticatedClient(managerId);

        Assert.Equal(["account", "short-rent"], (await OrgTeamHttp.ContextKeysAsync(manager)).Order());
        foreach (var path in new[] { "/api/orgs/me/members", "/api/orgs/me/invitations", "/api/orgs/me/activity", "/api/orgs/me/overview" })
            Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync("/api/orgs/me/invitations", InvitationBody(NewEmail("x")))).StatusCode);
    }

    [Fact]
    public async Task Areas_TheOwnerChangesTheAreasOfAMember_AndTheMemberOpensTheNewContextsAtOnce()
    {
        var (ownerId, orgId) = await OwnerOnPlanAsync(PlanTier.Pro, SubscriptionStatus.Active);
        var userId = await OrgTeamHttp.AddMemberAsync(factory, orgId, OrgRole.Collaborator, ["short-rent"]);
        using var owner = OwnerClient(ownerId);
        using var member = factory.CreateAuthenticatedClient(userId);
        var memberId = await MemberRowIdAsync(owner, userId);
        var path = $"/api/orgs/me/members/{memberId}/areas";
        Assert.Equal(["short-rent"], await OrgTeamHttp.ContextKeysAsync(member));

        var both = await owner.PutAsJsonAsync(path, new { areas = new[] { "short-rent", "long-rent" } });

        Assert.Equal(HttpStatusCode.OK, both.StatusCode);
        var row = await both.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("Collaborator", "Active"), (row.GetProperty("role").GetString(), row.GetProperty("status").GetString()));
        Assert.Equal(["long-rent", "short-rent"], row.GetProperty("areas").EnumerateArray().Select(a => a.GetString()).Order());
        Assert.Equal(["long-rent", "short-rent"], (await OrgTeamHttp.ContextKeysAsync(member)).Order());

        // The same set again writes nothing; taking an area away is as direct.
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(path, new { areas = new[] { "long-rent", "short-rent" } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(path, new { areas = new[] { "long-rent" } })).StatusCode);
        Assert.Equal(["long-rent"], await OrgTeamHttp.ContextKeysAsync(member));

        // Two lines in the log, not three: who changed whose areas, from what to what.
        var log = await (await owner.GetAsync("/api/orgs/me/activity?type=MemberAreasChanged")).Content.ReadFromJsonAsync<JsonElement>();
        var lines = log.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal(
            ("short-rent", "long-rent.short-rent"),
            (lines[1].GetProperty("details").GetProperty("fromAreas").GetString(), lines[1].GetProperty("details").GetProperty("toAreas").GetString()));
        Assert.Equal(
            ("long-rent.short-rent", "long-rent"),
            (lines[0].GetProperty("details").GetProperty("fromAreas").GetString(), lines[0].GetProperty("details").GetProperty("toAreas").GetString()));
        Assert.All(lines, l => Assert.Equal(ownerId, l.GetProperty("actorUserId").GetString()));

        // What the request can get wrong, with its own code; none of it touched the member.
        await OrgTeamHttp.AssertProblemAsync(
            await owner.PutAsJsonAsync(path, new { areas = Array.Empty<string>() }), HttpStatusCode.UnprocessableEntity, "org_member_area_required");
        await OrgTeamHttp.AssertProblemAsync(
            await owner.PutAsJsonAsync(path, new { areas = new[] { "admin" } }), HttpStatusCode.UnprocessableEntity, "org_member_area_required");
        await OrgTeamHttp.AssertProblemAsync(
            await owner.PutAsJsonAsync(path, new { }), HttpStatusCode.UnprocessableEntity, "org_member_area_required");
        var members = await (await owner.GetAsync("/api/orgs/me/members")).Content.ReadFromJsonAsync<JsonElement>();
        var ownerRowId = members.GetProperty("items").EnumerateArray().Single(m => m.GetProperty("role").GetString() == "Owner").GetProperty("id").GetGuid();
        await OrgTeamHttp.AssertProblemAsync(
            await owner.PutAsJsonAsync($"/api/orgs/me/members/{ownerRowId}/areas", new { areas = new[] { "short-rent" } }),
            HttpStatusCode.Conflict,
            "org_last_owner");
        await OrgTeamHttp.AssertProblemAsync(
            await owner.PutAsJsonAsync($"/api/orgs/me/members/{Guid.NewGuid()}/areas", new { areas = new[] { "short-rent" } }),
            HttpStatusCode.NotFound,
            "org_member_not_found");
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync(path, new { areas = new[] { "short-rent" } })).StatusCode);
        Assert.Equal(["long-rent"], await OrgTeamHttp.ContextKeysAsync(member));
    }

    [Fact]
    public async Task Overview_TheOwnerReadsTheHomeInOneRequest_AndAMemberWhoIsNoManagerIsRefused()
    {
        var (ownerId, orgId) = await OwnerOnPlanAsync(PlanTier.Pro, SubscriptionStatus.Active);
        using var owner = OwnerClient(ownerId);
        await SeedPropertyAsync(orgId, ownerId, "Casa Breve Uno");
        await SeedPropertyAsync(orgId, ownerId, "Casa Breve Due");
        await SeedPropertyAsync(orgId, ownerId, "Casa Lunga", RentalMode.Long);
        await InviteAsync(owner, NewEmail("attesa"));

        var response = await owner.GetAsync("/api/orgs/me/overview");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var home = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", home.GetProperty("standing").GetString());
        Assert.True(home.GetProperty("openAccess").ValueKind is JsonValueKind.True or JsonValueKind.False);
        var seats = home.GetProperty("seats");
        Assert.Equal((10, 2, false), (seats.GetProperty("max").GetInt32(), seats.GetProperty("used").GetInt32(), seats.GetProperty("unlimited").GetBoolean()));
        var properties = home.GetProperty("properties");
        Assert.Equal(
            (3, 2, 1, 2),
            (properties.GetProperty("total").GetInt32(), properties.GetProperty("short").GetInt32(), properties.GetProperty("long").GetInt32(), properties.GetProperty("toComplete").GetInt32()));
        var invitations = home.GetProperty("invitations");
        Assert.Equal((1, 0), (invitations.GetProperty("pending").GetInt32(), invitations.GetProperty("oldestPendingDays").GetInt32()));
        Assert.Equal(0, home.GetProperty("accessRequests").GetProperty("pending").GetInt32());
        Assert.Equal(0, home.GetProperty("suppliers").GetProperty("trusted").GetInt32());
        Assert.Equal(2, home.GetProperty("alloggiati").GetProperty("propertiesWithoutCredentials").GetInt32());
        var recent = home.GetProperty("recentActivity").EnumerateArray().ToList();
        Assert.Equal("MemberInvited", Assert.Single(recent).GetProperty("type").GetString());

        var memberUserId = await OrgTeamHttp.AddMemberAsync(factory, orgId, OrgRole.Collaborator, ["short-rent"]);
        using var member = factory.CreateAuthenticatedClient(memberUserId);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/orgs/me/overview")).StatusCode);
    }

    [Fact]
    public async Task Invite_ACollaboratorSoloAlcuniWithItsProperties_ReachesThemTheMomentItAccepts()
    {
        var (ownerId, orgId) = await OwnerOnPlanAsync(PlanTier.Pro, SubscriptionStatus.Active);
        using var owner = OwnerClient(ownerId);
        var first = await SeedPropertyAsync(orgId, ownerId, "Casa Uno");
        var second = await SeedPropertyAsync(orgId, ownerId, "Casa Due");
        await SeedPropertyAsync(orgId, ownerId, "Casa Tre");
        var annaId = NewUserId("anna");
        var annaEmail = NewEmail("anna");

        var created = await owner.PostAsJsonAsync("/api/orgs/me/invitations", new
        {
            email = annaEmail,
            name = "Anna Leone",
            role = "Collaborator",
            areas = new[] { "short-rent" },
            propertyScope = "Selected",
            propertyIds = new[] { first, second },
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var invitation = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("invitation");
        Assert.Equal(new[] { first, second }.Order(), invitation.GetProperty("propertyIds").EnumerateArray().Select(p => p.GetGuid()).Order());
        Assert.NotEqual(JsonValueKind.Null, invitation.GetProperty("lastSentAt").ValueKind);
        var listed = Assert.Single((await (await owner.GetAsync("/api/orgs/me/invitations")).Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
        Assert.Equal(new[] { first, second }.Order(), listed.GetProperty("propertyIds").EnumerateArray().Select(p => p.GetGuid()).Order());

        // She accepts and reaches the two properties she was invited with, and not the third: no second step for the owner.
        var token = LinkTokenSentTo(annaEmail, EmailTemplates.Names.OrgInvitation);
        using var anna = InviteeClient(annaId, annaEmail);
        Assert.Equal(HttpStatusCode.OK, (await anna.PostAsJsonAsync("/api/org-invitations/accept", AcceptBody(token))).StatusCode);
        Assert.Equal(new[] { first, second }.Order(), (await PropertyIdsAsync(anna)).Order());
        var memberId = await MemberRowIdAsync(owner, annaId);
        var access = await (await owner.GetAsync($"/api/orgs/me/members/{memberId}/properties")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Selected", access.GetProperty("propertyScope").GetString());
        Assert.Equal(
            new[] { first, second }.Order(),
            access.GetProperty("properties").EnumerateArray().Where(p => p.GetProperty("granted").GetBoolean()).Select(p => p.GetProperty("propertyId").GetGuid()).Order());
    }

    [Fact]
    public async Task Invite_ThePropertiesOfTheInvitation_AreCheckedWhenItIsWritten()
    {
        var (ownerId, orgId) = await OwnerOnPlanAsync(PlanTier.Pro, SubscriptionStatus.Active);
        var (otherOwnerId, otherOrgId) = await OwnerOnPlanAsync(PlanTier.Pro, SubscriptionStatus.Active);
        using var owner = OwnerClient(ownerId);
        var mine = await SeedPropertyAsync(orgId, ownerId, "Casa Mia");
        var foreign = await SeedPropertyAsync(otherOrgId, otherOwnerId, "Casa Altrui");
        object Body(string role, string scope, params Guid[] ids) =>
            new { email = NewEmail("x"), name = "Anna Leone", role, areas = new[] { "short-rent" }, propertyScope = scope, propertyIds = ids };

        // A property that is not the org's (unknown, of another org) is refused; so is one given to a role or a scope that cannot have it.
        await OrgTeamHttp.AssertProblemAsync(
            await owner.PostAsJsonAsync("/api/orgs/me/invitations", Body("Collaborator", "Selected", Guid.NewGuid())),
            HttpStatusCode.UnprocessableEntity,
            "org_member_property_unknown");
        await OrgTeamHttp.AssertProblemAsync(
            await owner.PostAsJsonAsync("/api/orgs/me/invitations", Body("Collaborator", "Selected", mine, foreign)),
            HttpStatusCode.UnprocessableEntity,
            "org_member_property_unknown");
        await OrgTeamHttp.AssertProblemAsync(
            await owner.PostAsJsonAsync("/api/orgs/me/invitations", Body("Collaborator", "All", mine)),
            HttpStatusCode.UnprocessableEntity,
            "org_member_scope_not_supported");
        await OrgTeamHttp.AssertProblemAsync(
            await owner.PostAsJsonAsync("/api/orgs/me/invitations", Body("Accountant", "Selected", mine)),
            HttpStatusCode.UnprocessableEntity,
            "org_member_scope_not_supported");
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await owner.PostAsJsonAsync("/api/orgs/me/invitations", Body("Collaborator", "Selected", Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).ToArray()))).StatusCode);

        // None of them wrote an invitation or sent an email.
        Assert.Empty(await (await owner.GetAsync("/api/orgs/me/invitations")).Content.ReadFromJsonAsync<JsonElement[]>() ?? []);
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/orgs/me/invitations", Body("Collaborator", "Selected", mine))).StatusCode);
    }

    [Fact]
    public async Task Invite_TheCapOnTheEmails_AnswersTooManyRequests_AndARevokedInvitationStillCounts()
    {
        var (ownerId, _) = await OwnerOnPlanAsync(PlanTier.Pro, SubscriptionStatus.Active);
        using var owner = OwnerClient(ownerId);

        // Ten emails in the day (the default of OrgTeam:InvitationDailyLimit). Each invitation is revoked at once: that frees its seat
        // and gives back no email.
        for (var i = 0; i < 10; i++)
        {
            var (invitationId, _) = await InviteAsync(owner, NewEmail($"giorno{i}"));
            Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/orgs/me/invitations/{invitationId}/revoke", null)).StatusCode);
        }

        var refused = await owner.PostAsJsonAsync("/api/orgs/me/invitations", InvitationBody(NewEmail("undicesima")));

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invitation_daily_limit_reached", problem.GetProperty("code").GetString());
        // The message says the limit the org ran into.
        Assert.Contains("10", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resend_AnHourBetweenTwoSends_AnswersTooManyRequestsUntilTheHourHasPassed()
    {
        var (ownerId, _) = await OwnerOnPlanAsync(PlanTier.Pro, SubscriptionStatus.Active);
        using var owner = OwnerClient(ownerId);
        var email = NewEmail("fretta");
        var (invitationId, firstToken) = await InviteAsync(owner, email);

        var tooSoon = await owner.PostAsync($"/api/orgs/me/invitations/{invitationId}/resend", null);

        Assert.Equal(HttpStatusCode.TooManyRequests, tooSoon.StatusCode);
        var problem = await tooSoon.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invitation_resend_too_soon", problem.GetProperty("code").GetString());
        // The message says how many minutes are left.
        Assert.Matches("\\d", problem.GetProperty("detail").GetString()!);
        // Nothing went out, and the link of the first email still works.
        Assert.Single(factory.SentEmails(), e => e.Template == EmailTemplates.Names.OrgInvitation && string.Equals(e.To, email, StringComparison.OrdinalIgnoreCase));
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsJsonAsync("/api/org-invitations/lookup", new { token = firstToken })).StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.OrgInvitations.IgnoreQueryFilters().SingleAsync(i => i.Id == invitationId);
            row.LastSentAt = DateTime.UtcNow.AddMinutes(-61);
            await db.SaveChangesAsync();
        }

        var again = await owner.PostAsync($"/api/orgs/me/invitations/{invitationId}/resend", null);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(2, factory.SentEmails().Count(e => e.Template == EmailTemplates.Names.OrgInvitation && string.Equals(e.To, email, StringComparison.OrdinalIgnoreCase)));
        // The first link stopped working with the new one.
        await OrgTeamHttp.AssertProblemAsync(
            await anonymous.PostAsJsonAsync("/api/org-invitations/lookup", new { token = firstToken }), HttpStatusCode.Gone, "invitation_invalid");
    }

    /// <summary>A context on the host's database, for the data a test writes directly (the scope lives as long as the test).</summary>
    private AppDbContext NewHostContext() => factory.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();
}

/// <summary>
/// The lookup of an invitation is anonymous and answers every link that does not work the same way, so it is limited per
/// client: three calls here, the fourth is refused with 429 (the default is 20 a minute, <c>RateLimiting__PublicInvitationLookup__PermitLimit</c>).
/// </summary>
public class OrgInvitationsLookupRateLimitIntegrationTests(OrgInvitationsLookupRateLimitIntegrationTests.Factory factory)
    : IClassFixture<OrgInvitationsLookupRateLimitIntegrationTests.Factory>
{
    public sealed class Factory : CasazenWebApplicationFactory
    {
        protected override bool SeedComuneSample => false;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RateLimiting:PublicInvitationLookup:PermitLimit"] = "3",
                }));
        }
    }

    [Fact]
    public async Task Lookup_MoreCallsThanThePolicyAllows_AreRefusedWith429()
    {
        using var client = factory.CreateClient();
        var body = new { token = new string('a', 64) };

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Gone, (await client.PostAsJsonAsync("/api/org-invitations/lookup", body)).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/api/org-invitations/lookup", body)).StatusCode);
    }
}
