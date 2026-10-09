using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Tests.Unit.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// AM-02b over the real pipeline (routes, policies, flag, model binding, error contract, JSON, CSV): what the team does
/// (invites, accepts, changes a role, deactivates, reactivates, gives properties, removes) and what the org does (plan, name,
/// slug) is in the activity log with ids and codes only, in the order it happened; the owner and the administrators read it, page
/// it, filter it and download it as a CSV, nobody else does, and an org reads only its own; a member asks the administrators for
/// access and they are told. On PostgreSQL in CI; on the in-memory fallback locally (the seed data of the model is written first).
/// </summary>
public class OrgActivityHttpIntegrationTests(OrgInvitationsFactory factory) : IClassFixture<OrgInvitationsFactory>
{
    /// <summary>The versions of the legal documents of the test host (<c>Legal:Documents:*:Version</c>).</summary>
    private const string ConsentVersion = "2026-06-v1";

    private static string NewUserId(string name) => $"auth0|am02b-{name}-{Guid.NewGuid():N}";

    private static string NewEmail(string name) => $"{name}.{Guid.NewGuid():N}@example.com";

    private HttpClient Owner(string ownerId) => factory.CreateAuthenticatedClient(ownerId, roles: "PropertyOwner");

    /// <summary>A person of the database only: no role in the token, its rights are its membership rows.</summary>
    private HttpClient Member(string userId, string? language = null)
    {
        var client = factory.CreateAuthenticatedClient(userId);
        if (language is not null)
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(language);
        return client;
    }

    private async Task<(string OwnerId, Guid OrgId)> NewOrgAsync(PlanTier tier = PlanTier.Pro, SubscriptionStatus status = SubscriptionStatus.Active)
    {
        await OrgTeamSeed.EnsureRolesAsync(factory);
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

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.Equal(expected, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static List<JsonElement> Items(JsonElement page) => page.GetProperty("items").EnumerateArray().ToList();

    private static List<string> Types(JsonElement page) => Items(page).Select(i => i.GetProperty("type").GetString()!).ToList();

    private async Task<JsonElement> ActivityAsync(HttpClient client, string query = "") =>
        await JsonAsync(await client.GetAsync("/api/orgs/me/activity" + query));

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

    /// <summary>The owner invites <paramref name="email"/>, the person accepts with its own account; returns the account and the member row.</summary>
    private async Task<(string UserId, Guid MemberId, Guid InvitationId)> InviteAndAcceptAsync(
        HttpClient owner, string email, string name, string role = "Collaborator")
    {
        var created = await JsonAsync(
            await owner.PostAsJsonAsync("/api/orgs/me/invitations", new { email, name, role, areas = new[] { "short-rent" } }),
            HttpStatusCode.Created);
        var invitationId = created.GetProperty("invitation").GetProperty("id").GetGuid();
        var mail = factory.SentEmails().Last(e => string.Equals(e.To, email, StringComparison.OrdinalIgnoreCase) && e.Template == EmailTemplates.Names.OrgInvitation);
        var token = OrgInvitationTestKit.TokenInEmail(mail);

        var userId = NewUserId("invitee");
        using var invitee = factory.CreateAuthenticatedClient(userId, roles: null, email: email, emailVerified: true);
        await JsonAsync(await invitee.PostAsJsonAsync("/api/org-invitations/accept", AcceptBody(token)));

        var members = await JsonAsync(await owner.GetAsync("/api/orgs/me/members"));
        var member = members.GetProperty("items").EnumerateArray().Single(m => m.GetProperty("userId").GetString() == userId);
        return (userId, member.GetProperty("id").GetGuid(), invitationId);
    }

    // ─── The whole journey ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Journey_EveryStepOfTheTeam_IsInTheLog_WithIdsOnly_AndTheCsvIsTheSameList()
    {
        var (ownerId, orgId) = await NewOrgAsync();
        using var owner = Owner(ownerId);
        var annaEmail = NewEmail("anna.leone");
        var (annaId, annaMemberId, invitationId) = await InviteAndAcceptAsync(owner, annaEmail, "Anna Leone");

        await JsonAsync(await owner.PutAsJsonAsync($"/api/orgs/me/members/{annaMemberId}", new { role = "Accountant" }));
        await JsonAsync(await owner.PostAsync($"/api/orgs/me/members/{annaMemberId}/deactivate", null));
        await JsonAsync(await owner.PostAsync($"/api/orgs/me/members/{annaMemberId}/reactivate", null));

        // A collaborator limited to one of two properties: who gave what to whom.
        var brunoId = await OrgTeamHttp.AddMemberAsync(factory, orgId, OrgRole.Collaborator, ["short-rent"]);
        Guid brunoMemberId;
        Guid propertyId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            brunoMemberId = await db.OrgMembers.IgnoreQueryFilters().Where(m => m.UserId == brunoId).Select(m => m.Id).SingleAsync();
            propertyId = (await HostScopeScenario.SeedPropertiesOfAsync(db, orgId, ownerId)).Granted.Id;
        }

        await JsonAsync(await owner.PutAsJsonAsync(
            $"/api/orgs/me/members/{brunoMemberId}/properties", new { propertyScope = "Selected", propertyIds = new[] { propertyId } }));
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/orgs/me/members/{annaMemberId}")).StatusCode);

        var page = await ActivityAsync(owner);

        Assert.Equal(
            ["MemberInvited", "InvitationAccepted", "MemberRoleChanged", "MemberDeactivated", "MemberReactivated", "MemberPropertyAccessChanged", "MemberRemoved"],
            Types(page).AsEnumerable().Reverse());
        Assert.Equal((7, 1, 50), (page.GetProperty("totalCount").GetInt32(), page.GetProperty("page").GetInt32(), page.GetProperty("pageSize").GetInt32()));

        // Newest first.
        var instants = Items(page).Select(i => i.GetProperty("when").GetDateTime()).ToList();
        Assert.Equal(instants.OrderByDescending(t => t), instants);

        // The ids are the ones the client already has: the owner's account, the invitation, Anna's account, Bruno's account.
        var byType = Items(page).ToDictionary(i => i.GetProperty("type").GetString()!);
        Assert.Equal(
            (ownerId, "Invitation", invitationId.ToString(), "account"),
            (byType["MemberInvited"].GetProperty("actorUserId").GetString(), byType["MemberInvited"].GetProperty("subjectType").GetString(),
                byType["MemberInvited"].GetProperty("subjectId").GetString(), byType["MemberInvited"].GetProperty("area").GetString()));
        Assert.Equal((annaId, invitationId.ToString()), (byType["InvitationAccepted"].GetProperty("actorUserId").GetString(), byType["InvitationAccepted"].GetProperty("subjectId").GetString()));
        Assert.Equal(
            (ownerId, "Member", annaId),
            (byType["MemberRoleChanged"].GetProperty("actorUserId").GetString(), byType["MemberRoleChanged"].GetProperty("subjectType").GetString(), byType["MemberRoleChanged"].GetProperty("subjectId").GetString()));
        Assert.Equal("Collaborator", byType["MemberRoleChanged"].GetProperty("details").GetProperty("fromRole").GetString());
        Assert.Equal("Accountant", byType["MemberRoleChanged"].GetProperty("details").GetProperty("toRole").GetString());
        Assert.Equal(brunoId, byType["MemberPropertyAccessChanged"].GetProperty("subjectId").GetString());
        Assert.Equal("1", byType["MemberPropertyAccessChanged"].GetProperty("details").GetProperty("granted").GetString());
        Assert.Equal("Accountant", byType["MemberRemoved"].GetProperty("details").GetProperty("role").GetString());

        // No name, no email, nothing of the org.
        var raw = page.GetRawText();
        Assert.DoesNotContain("@", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Anna", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Leone", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.com", raw, StringComparison.Ordinal);

        // The CSV is the same list, with the same ids and the same header, and nothing else.
        var csv = await owner.GetAsync("/api/orgs/me/activity.csv");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", csv.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Matches(@"^activity-\d{8}\.csv$", csv.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Contains("no-store", csv.Headers.CacheControl?.ToString());
        var lines = (await csv.Content.ReadAsStringAsync()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("id,when,actor,area,type,subjectType,subjectId,details", lines[0]);
        Assert.Equal(7, lines.Length - 1);
        Assert.DoesNotContain("@", string.Join('\n', lines), StringComparison.Ordinal);
        Assert.Equal(
            Items(page).Select(i => i.GetProperty("id").GetString()),
            lines.Skip(1).Select(l => l.Split(',')[0]));
    }

    [Fact]
    public async Task PlanAndOrganization_ChangesOfTheOwner_AreInTheLog_WithoutTheirValues()
    {
        var (ownerId, orgId) = await NewOrgAsync(PlanTier.Pro, SubscriptionStatus.None);
        using var owner = Owner(ownerId);

        await JsonAsync(await owner.PutAsJsonAsync("/api/orgs/me/plan", new { planTier = "Starter" }));
        await JsonAsync(await owner.PutAsJsonAsync(
            "/api/orgs/me/settings",
            new { name = "Villa Mare Segreta", slug = $"villa-segreta-{Guid.NewGuid():N}"[..30], contactEmail = "segreto@example.com", contactEmailPublic = false }));
        // Only the contact email: nothing for the log.
        var current = await JsonAsync(await owner.GetAsync("/api/orgs/me/settings"));
        await JsonAsync(await owner.PutAsJsonAsync(
            "/api/orgs/me/settings",
            new { name = current.GetProperty("name").GetString(), slug = current.GetProperty("slug").GetString(), contactEmail = "altro@example.com", contactEmailPublic = false }));

        var page = await ActivityAsync(owner);

        Assert.Equal(
            ["OrgNameChanged", "OrgSlugChanged", "PlanChanged"],
            Types(page).Order());
        var plan = Items(page).Single(i => i.GetProperty("type").GetString() == "PlanChanged");
        Assert.Equal(
            (ownerId, "Org", orgId.ToString(), "Pro", "Starter", "org"),
            (plan.GetProperty("actorUserId").GetString(), plan.GetProperty("subjectType").GetString(), plan.GetProperty("subjectId").GetString(),
                plan.GetProperty("details").GetProperty("fromTier").GetString(), plan.GetProperty("details").GetProperty("toTier").GetString(),
                plan.GetProperty("details").GetProperty("source").GetString()));

        var raw = page.GetRawText();
        Assert.DoesNotContain("Villa", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("segreta", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.com", raw, StringComparison.Ordinal);
    }

    // ─── Filters, paging, the CSV with filters ──────────────────────────────────────────────────────────

    [Fact]
    public async Task List_Filters_Paging_AndTheCsvWithTheSameFilters()
    {
        var (ownerId, _) = await NewOrgAsync();
        using var owner = Owner(ownerId);
        var (annaId, annaMemberId, _) = await InviteAndAcceptAsync(owner, NewEmail("anna"), "Anna Leone");
        await JsonAsync(await owner.PutAsJsonAsync($"/api/orgs/me/members/{annaMemberId}", new { role = "Accountant" }));
        await JsonAsync(await owner.PostAsync($"/api/orgs/me/members/{annaMemberId}/deactivate", null));
        await JsonAsync(await owner.PostAsync($"/api/orgs/me/members/{annaMemberId}/reactivate", null));
        var from = DateTime.UtcNow.AddMinutes(-5).ToString("O");
        var to = DateTime.UtcNow.AddMinutes(5).ToString("O");

        // One event, several events (repeated or separated by commas), the area, the person.
        Assert.Equal(["MemberRoleChanged"], Types(await ActivityAsync(owner, "?type=MemberRoleChanged")));
        Assert.Equal(
            ["MemberReactivated", "MemberDeactivated"],
            Types(await ActivityAsync(owner, "?type=MemberDeactivated&type=MemberReactivated")));
        Assert.Equal(
            ["MemberReactivated", "MemberDeactivated"],
            Types(await ActivityAsync(owner, "?type=memberdeactivated,MEMBERREACTIVATED")));
        Assert.Equal(5, (await ActivityAsync(owner, "?area=account")).GetProperty("totalCount").GetInt32());
        Assert.Equal(0, (await ActivityAsync(owner, "?area=short-rent")).GetProperty("totalCount").GetInt32());
        // The owner invited, changed the role, deactivated and reactivated; Anna accepted.
        Assert.Equal(4, (await ActivityAsync(owner, $"?actor={Uri.EscapeDataString(ownerId)}")).GetProperty("totalCount").GetInt32());
        Assert.Equal(["InvitationAccepted"], Types(await ActivityAsync(owner, $"?actor={Uri.EscapeDataString(annaId)}")));
        Assert.Equal(0, (await ActivityAsync(owner, "?actor=system")).GetProperty("totalCount").GetInt32());
        Assert.Equal(5, (await ActivityAsync(owner, $"?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}")).GetProperty("totalCount").GetInt32());
        Assert.Equal(0, (await ActivityAsync(owner, $"?to={Uri.EscapeDataString(DateTime.UtcNow.AddDays(-1).ToString("O"))}")).GetProperty("totalCount").GetInt32());

        // Paging: two per page, three pages, no line twice.
        var seen = new List<string>();
        for (var pageNumber = 1; pageNumber <= 3; pageNumber++)
        {
            var page = await ActivityAsync(owner, $"?page={pageNumber}&pageSize=2");
            Assert.Equal((5, pageNumber, 2), (page.GetProperty("totalCount").GetInt32(), page.GetProperty("page").GetInt32(), page.GetProperty("pageSize").GetInt32()));
            seen.AddRange(Items(page).Select(i => i.GetProperty("id").GetString()!));
        }

        Assert.Equal(5, seen.Distinct().Count());

        // The CSV with a filter has only the lines of the filter; with a filter that matches nothing it is the header alone.
        var filtered = await (await owner.GetAsync("/api/orgs/me/activity.csv?type=MemberRoleChanged")).Content.ReadAsStringAsync();
        Assert.Equal(2, filtered.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        var empty = await (await owner.GetAsync("/api/orgs/me/activity.csv?actor=auth0%7Cnobody")).Content.ReadAsStringAsync();
        Assert.Equal("id,when,actor,area,type,subjectType,subjectId,details\r\n", empty);
    }

    [Theory]
    [InlineData("?type=NotAnEvent")]
    [InlineData("?type=MemberInvited,Nope")]
    [InlineData("?type=7")]
    [InlineData("?area=admin")]
    [InlineData("?area=ShortRent")]
    [InlineData("?from=2026-10-09T00:00:00Z&to=2026-10-01T00:00:00Z")]
    [InlineData("?from=not-a-date")]
    public async Task List_AFilterThatMakesNoSense_Is400_ForTheListAndTheCsv(string query)
    {
        var (ownerId, _) = await NewOrgAsync();
        using var owner = Owner(ownerId);

        var list = await owner.GetAsync("/api/orgs/me/activity" + query);
        var csv = await owner.GetAsync("/api/orgs/me/activity.csv" + query);

        Assert.Equal(HttpStatusCode.BadRequest, list.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, csv.StatusCode);
        Assert.Equal("application/problem+json", list.Content.Headers.ContentType?.MediaType);
        Assert.Equal("application/problem+json", csv.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task List_TheMessageOfAWrongFilter_IsInTheLanguageOfTheRequest()
    {
        var (ownerId, _) = await NewOrgAsync();
        using var italian = Owner(ownerId);
        using var english = Owner(ownerId);
        english.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");

        var it = await (await italian.GetAsync("/api/orgs/me/activity?area=nope")).Content.ReadFromJsonAsync<JsonElement>();
        var en = await (await english.GetAsync("/api/orgs/me/activity?area=nope")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Area non riconosciuta.", it.GetProperty("detail").GetString());
        Assert.Equal("Unknown area.", en.GetProperty("detail").GetString());
    }

    // ─── Who reads it ───────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(OrgRole.Admin, HttpStatusCode.OK)]
    [InlineData(OrgRole.PropertyManager, HttpStatusCode.Forbidden)]
    [InlineData(OrgRole.Collaborator, HttpStatusCode.Forbidden)]
    [InlineData(OrgRole.Accountant, HttpStatusCode.Forbidden)]
    public async Task Read_OnlyTheOwnerAndTheAdministrators(OrgRole role, HttpStatusCode expected)
    {
        var (ownerId, orgId) = await NewOrgAsync();
        var memberId = await OrgTeamHttp.AddMemberAsync(factory, orgId, role, ["short-rent"]);
        using var member = Member(memberId);
        using var owner = Owner(ownerId);

        Assert.Equal(expected, (await member.GetAsync("/api/orgs/me/activity")).StatusCode);
        Assert.Equal(expected, (await member.GetAsync("/api/orgs/me/activity.csv")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/orgs/me/activity")).StatusCode);
    }

    [Fact]
    public async Task Read_WithoutASignIn_Is401_AndADeactivatedAdministratorIsRefused()
    {
        var (_, orgId) = await NewOrgAsync();
        var adminId = await OrgTeamHttp.AddMemberAsync(factory, orgId, OrgRole.Admin, ["short-rent"], deactivate: true);
        using var anonymous = factory.CreateClient();
        using var deactivated = Member(adminId);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/orgs/me/activity")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/orgs/me/activity.csv")).StatusCode);
        await OrgTeamHttp.AssertProblemAsync(await deactivated.GetAsync("/api/orgs/me/activity"), HttpStatusCode.Forbidden, "member_inactive");
    }

    [Fact]
    public async Task Read_AnOrgReadsItsOwnLinesOnly()
    {
        var (ownerA, _) = await NewOrgAsync();
        var (ownerB, _) = await NewOrgAsync();
        using var asA = Owner(ownerA);
        using var asB = Owner(ownerB);
        await InviteAndAcceptAsync(asA, NewEmail("only-in-a"), "Persona A");
        await InviteAndAcceptAsync(asB, NewEmail("only-in-b1"), "Persona B1");
        await InviteAndAcceptAsync(asB, NewEmail("only-in-b2"), "Persona B2");

        var pageA = await ActivityAsync(asA);
        var pageB = await ActivityAsync(asB);

        Assert.Equal(2, pageA.GetProperty("totalCount").GetInt32());
        Assert.Equal(4, pageB.GetProperty("totalCount").GetInt32());
        Assert.All(Items(pageA), i => Assert.NotEqual(ownerB, i.GetProperty("actorUserId").GetString()));
        Assert.All(Items(pageB), i => Assert.NotEqual(ownerA, i.GetProperty("actorUserId").GetString()));
        var csvA = await (await asA.GetAsync("/api/orgs/me/activity.csv")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(ownerB, csvA, StringComparison.Ordinal);
    }

    // ─── The export of the org (GDPR) ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OrgExport_CarriesTheLogForWhoMayReadIt_AndNotForACollaboratorWhoReachesTheExport()
    {
        var (ownerId, orgId) = await NewOrgAsync();
        var collaboratorId = await OrgTeamHttp.AddMemberAsync(factory, orgId, OrgRole.Collaborator, ["short-rent"]);
        using var owner = Owner(ownerId);
        using var collaborator = Member(collaboratorId);
        var (annaId, _, invitationId) = await InviteAndAcceptAsync(owner, NewEmail("anna"), "Anna Leone");

        // The owner: the org's data and the log, the same lines and the same ids as the endpoint of the log.
        var export = await JsonAsync(await owner.GetAsync("/api/gdpr/org/export"));
        var activity = export.GetProperty("activity").EnumerateArray().ToList();
        Assert.Equal(["InvitationAccepted", "MemberInvited"], activity.Select(a => a.GetProperty("type").GetString()));
        Assert.Equal(
            (ownerId, invitationId.ToString(), "account"),
            (activity[1].GetProperty("actorUserId").GetString(), activity[1].GetProperty("subjectId").GetString(), activity[1].GetProperty("area").GetString()));
        Assert.Equal(annaId, activity[0].GetProperty("actorUserId").GetString());
        Assert.DoesNotContain("example.com", export.GetProperty("activity").GetRawText(), StringComparison.Ordinal);
        Assert.True(export.TryGetProperty("members", out _));

        // A collaborator reaches this export with its property permission (until #461 moves it under the owner's): it gets the
        // org's data as before and not the log, which it cannot read from the log's own endpoints either.
        var response = await collaborator.GetAsync("/api/gdpr/org/export");
        var asCollaborator = await JsonAsync(response);
        Assert.True(asCollaborator.TryGetProperty("members", out _));
        Assert.False(asCollaborator.TryGetProperty("activity", out _));
        Assert.Equal(HttpStatusCode.Forbidden, (await collaborator.GetAsync("/api/orgs/me/activity")).StatusCode);
    }

    // ─── Requests for access ────────────────────────────────────────────────────────────────────────────

    /// <summary>An org with its owner, an administrator, a property manager, a collaborator and an accountant.</summary>
    private async Task<(string OwnerId, Guid OrgId, string AdminId, string CollaboratorId, string OwnerEmail, string AdminEmail)> OrgWithATeamAsync()
    {
        var (ownerId, orgId) = await NewOrgAsync();
        var adminId = await OrgTeamHttp.AddMemberAsync(factory, orgId, OrgRole.Admin, ["short-rent"]);
        await OrgTeamHttp.AddMemberAsync(factory, orgId, OrgRole.PropertyManager, ["short-rent"]);
        var collaboratorId = await OrgTeamHttp.AddMemberAsync(factory, orgId, OrgRole.Collaborator, ["short-rent"]);
        await OrgTeamHttp.AddMemberAsync(factory, orgId, OrgRole.Accountant, ["short-rent"]);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var emails = await db.Users.Where(u => u.Id == ownerId || u.Id == adminId).ToDictionaryAsync(u => u.Id, u => u.Email);
        return (ownerId, orgId, adminId, collaboratorId, emails[ownerId], emails[adminId]);
    }

    private List<(string? To, EmailContent Content, string Template)> RequestEmailsTo(params string[] addresses) =>
        factory.SentEmails()
            .Where(e => e.Template == EmailTemplates.Names.OrgAccessRequest && addresses.Contains(e.To, StringComparer.OrdinalIgnoreCase))
            .ToList();

    [Fact]
    public async Task AccessRequest_ACollaboratorAsks_TheOwnerAndTheAdministratorAreTold_AndTheLogSaysWhoAskedForWhat()
    {
        var (ownerId, orgId, _, collaboratorId, ownerEmail, adminEmail) = await OrgWithATeamAsync();
        using var collaborator = Member(collaboratorId);
        using var owner = Owner(ownerId);

        var response = await collaborator.PostAsJsonAsync(
            "/api/orgs/me/access-requests", new { area = "payments", note = "Mi servono gli incassi di settembre" });

        var body = await JsonAsync(response, HttpStatusCode.Accepted);
        Assert.Equal(2, body.GetProperty("notified").GetInt32());
        var mails = RequestEmailsTo(ownerEmail, adminEmail);
        Assert.Equal(2, mails.Count);
        Assert.All(mails, mail =>
        {
            Assert.Contains("«Incassi»", mail.Content.Subject, StringComparison.Ordinal);
            Assert.Contains("Mi servono gli incassi di settembre", mail.Content.HtmlBody, StringComparison.Ordinal);
            Assert.Contains("/app/account/people", mail.Content.HtmlBody, StringComparison.Ordinal);
        });

        // The owner reads it in the log: who, in which org, for what. The note is not there.
        var page = await ActivityAsync(owner, "?type=AccessRequested");
        var line = Assert.Single(Items(page));
        Assert.Equal(
            (collaboratorId, "Org", orgId.ToString(), "account", "payments"),
            (line.GetProperty("actorUserId").GetString(), line.GetProperty("subjectType").GetString(), line.GetProperty("subjectId").GetString(),
                line.GetProperty("area").GetString(), line.GetProperty("details").GetProperty("requestedArea").GetString()));
        Assert.DoesNotContain("incassi", page.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AccessRequest_ACollaboratorCannotReadTheLog_ButCanAsk()
    {
        var (_, _, _, collaboratorId, ownerEmail, _) = await OrgWithATeamAsync();
        using var collaborator = Member(collaboratorId);

        Assert.Equal(HttpStatusCode.Forbidden, (await collaborator.GetAsync("/api/orgs/me/activity")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await collaborator.PostAsJsonAsync("/api/orgs/me/access-requests", new { area = "reports" })).StatusCode);
        Assert.Single(RequestEmailsTo(ownerEmail));
    }

    [Fact]
    public async Task AccessRequest_TheEmailIsInTheLanguageOfTheRequest()
    {
        var (_, _, _, collaboratorId, ownerEmail, _) = await OrgWithATeamAsync();
        using var english = Member(collaboratorId, language: "en");

        await JsonAsync(await english.PostAsJsonAsync("/api/orgs/me/access-requests", new { area = "billing" }), HttpStatusCode.Accepted);

        var mail = Assert.Single(RequestEmailsTo(ownerEmail));
        Assert.Contains("asks for access to \"Plan and billing\"", mail.Content.Subject, StringComparison.Ordinal);
        Assert.Contains("<html lang=\"en\">", mail.Content.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AccessRequest_ThreeADay_TheFourthIs409_AndNobodyIsToldAgain()
    {
        var (_, _, _, collaboratorId, ownerEmail, _) = await OrgWithATeamAsync();
        using var collaborator = Member(collaboratorId);

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Accepted, (await collaborator.PostAsJsonAsync("/api/orgs/me/access-requests", new { area = "billing" })).StatusCode);

        await OrgTeamHttp.AssertProblemAsync(
            await collaborator.PostAsJsonAsync("/api/orgs/me/access-requests", new { area = "reports" }),
            HttpStatusCode.Conflict,
            "access_request_limit_reached");
        Assert.Equal(3, RequestEmailsTo(ownerEmail).Count);
    }

    [Fact]
    public async Task AccessRequest_WhatCannotBeAsked_IsRefusedWithItsOwnCode()
    {
        var (_, _, _, collaboratorId, ownerEmail, _) = await OrgWithATeamAsync();
        using var collaborator = Member(collaboratorId);

        await OrgTeamHttp.AssertProblemAsync(
            await collaborator.PostAsJsonAsync("/api/orgs/me/access-requests", new { area = "salaries" }),
            HttpStatusCode.UnprocessableEntity,
            "access_request_area_unknown");
        Assert.Equal(HttpStatusCode.BadRequest, (await collaborator.PostAsJsonAsync("/api/orgs/me/access-requests", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await collaborator.PostAsJsonAsync("/api/orgs/me/access-requests", new { area = "billing", note = new string('x', 201) })).StatusCode);
        Assert.Empty(RequestEmailsTo(ownerEmail));
    }

    [Fact]
    public async Task AccessRequest_ANoteOfExactlyTwoHundredCharacters_IsAccepted()
    {
        var (_, _, _, collaboratorId, ownerEmail, _) = await OrgWithATeamAsync();
        using var collaborator = Member(collaboratorId);

        await JsonAsync(
            await collaborator.PostAsJsonAsync("/api/orgs/me/access-requests", new { area = "billing", note = new string('x', 200) }),
            HttpStatusCode.Accepted);

        Assert.Single(RequestEmailsTo(ownerEmail));
    }

    [Fact]
    public async Task AccessRequest_WhoIsNotAnActiveMemberOfAnOrg_IsRefused()
    {
        var (_, orgId, _, _, ownerEmail, _) = await OrgWithATeamAsync();
        var deactivatedId = await OrgTeamHttp.AddMemberAsync(factory, orgId, OrgRole.Collaborator, ["short-rent"], deactivate: true);
        using var anonymous = factory.CreateClient();
        using var deactivated = Member(deactivatedId);
        using var stranger = Member(NewUserId("stranger"));

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/orgs/me/access-requests", new { area = "billing" })).StatusCode);
        await OrgTeamHttp.AssertProblemAsync(
            await deactivated.PostAsJsonAsync("/api/orgs/me/access-requests", new { area = "billing" }), HttpStatusCode.Forbidden, "member_inactive");
        // A signed-in account with no org has nobody to ask.
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsJsonAsync("/api/orgs/me/access-requests", new { area = "billing" })).StatusCode);
        Assert.Empty(RequestEmailsTo(ownerEmail));
    }

    [Fact]
    public async Task AccessRequest_NobodyOfAnotherOrgIsTold()
    {
        var (_, _, _, collaboratorId, _, _) = await OrgWithATeamAsync();
        var (_, _, _, _, otherOwnerEmail, otherAdminEmail) = await OrgWithATeamAsync();
        using var collaborator = Member(collaboratorId);

        await JsonAsync(await collaborator.PostAsJsonAsync("/api/orgs/me/access-requests", new { area = "billing" }), HttpStatusCode.Accepted);

        Assert.Empty(RequestEmailsTo(otherOwnerEmail, otherAdminEmail));
    }
}

/// <summary>
/// A request for access is rate limited per person: with two permits, the third request of the same person within the window is
/// refused with 429 (the default is five in ten minutes, <c>RateLimiting__OrgAccessRequest__PermitLimit</c>), and another person
/// from the same address is not held back by it.
/// </summary>
public class OrgAccessRequestRateLimitIntegrationTests(OrgAccessRequestRateLimitIntegrationTests.Factory factory)
    : IClassFixture<OrgAccessRequestRateLimitIntegrationTests.Factory>
{
    public sealed class Factory : CasazenWebApplicationFactory
    {
        protected override bool SeedComuneSample => false;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            var inMemoryStore = $"org-access-request-limit-{Guid.NewGuid():N}";
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Features:OrgTeam"] = "true",
                    ["RateLimiting:OrgAccessRequest:PermitLimit"] = "2",
                }));
            builder.ConfigureTestServices(services =>
            {
                // Like the other hosts of the team: on the in-memory fallback a store of its own, where the seed is all there is.
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

    [Fact]
    public async Task Create_MoreRequestsThanThePolicyAllows_AreRefusedWith429_ForThatPersonOnly()
    {
        await OrgTeamSeed.EnsureRolesAsync(factory);
        var (_, orgId) = await OrgTeamHttp.SeedOwnerWithTeamRowsAsync(factory);
        var firstId = await OrgTeamHttp.AddMemberAsync(factory, orgId, OrgRole.Collaborator, ["short-rent"]);
        var secondId = await OrgTeamHttp.AddMemberAsync(factory, orgId, OrgRole.Accountant, ["short-rent"]);
        using var first = factory.CreateAuthenticatedClient(firstId);
        using var second = factory.CreateAuthenticatedClient(secondId);

        // Two permits: two answers, then 429 (the daily limit of three is not reached).
        Assert.Equal(HttpStatusCode.Accepted, (await first.PostAsJsonAsync("/api/orgs/me/access-requests", new { area = "billing" })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await first.PostAsJsonAsync("/api/orgs/me/access-requests", new { area = "reports" })).StatusCode);

        var refused = await first.PostAsJsonAsync("/api/orgs/me/access-requests", new { area = "prices" });
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.True(refused.Headers.Contains("Retry-After"));
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("rate_limited", problem.GetProperty("code").GetString());

        // The other person is counted on its own, from the same address.
        Assert.Equal(HttpStatusCode.Accepted, (await second.PostAsJsonAsync("/api/orgs/me/access-requests", new { area = "billing" })).StatusCode);
    }
}
