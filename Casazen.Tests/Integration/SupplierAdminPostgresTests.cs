using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// Platform admin management of suppliers (SU-12, A4-29) over the real pipeline on PostgreSQL: the paginated list, the
/// suspension and reactivation with their audit trail, the rule that a suspended supplier performs no action on the
/// requests, and the invites (list, resend, revoke).
/// </summary>
public class SupplierAdminPostgresTests(SupplierRegistrationIntegrationTests.PilotComuniFactory factory)
    : IClassFixture<SupplierRegistrationIntegrationTests.PilotComuniFactory>
{
    private const string Admin = "Admin";
    private const string Host = "PropertyOwner";
    private const string Supplier = "Supplier";
    private const string Comune = "H501";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // ─── List ───

    [PostgresFact]
    public async Task List_AsAdmin_ReturnsANewestFirstPageInSqlWithTheTotalAndTheOpenRequests()
    {
        var prefix = NewTag();
        var oldest = await SeedSupplierAsync($"{prefix} Alfa", createdAt: DaysAgo(30));
        var middle = await SeedSupplierAsync($"{prefix} Beta", createdAt: DaysAgo(20));
        var newest = await SeedSupplierAsync($"{prefix} Gamma", createdAt: DaysAgo(10));
        var host = await SeedHostAsync();
        // Two open requests (new, taken) and one closed: only the open ones count.
        await SeedRequestAsync(host, middle.OrgId, ServiceRequestStatus.Richiesto);
        await SeedRequestAsync(host, middle.OrgId, ServiceRequestStatus.PresoInCarico);
        await SeedRequestAsync(host, middle.OrgId, ServiceRequestStatus.Completato);
        using var admin = factory.CreateAuthenticatedClient(NewAdminId(), Admin);

        var first = await GetJsonAsync(admin, $"/api/admin/suppliers?search={prefix}&page=1&pageSize=2");
        var second = await GetJsonAsync(admin, $"/api/admin/suppliers?search={prefix}&page=2&pageSize=2");

        Assert.Equal(3, first.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, first.GetProperty("pageSize").GetInt32());
        var firstItems = first.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(new[] { newest.OrgId, middle.OrgId }, firstItems.Select(i => i.GetProperty("orgId").GetGuid()));
        Assert.Equal(2, firstItems[1].GetProperty("openRequests").GetInt32());
        Assert.Equal(0, firstItems[0].GetProperty("openRequests").GetInt32());
        Assert.Equal("Active", firstItems[0].GetProperty("status").GetString());
        var secondItems = second.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(oldest.OrgId, Assert.Single(secondItems).GetProperty("orgId").GetGuid());
        Assert.Equal(new[] { "cleaning" }, firstItems[0].GetProperty("categories").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal(new[] { Comune }, firstItems[0].GetProperty("comuni").EnumerateArray().Select(c => c.GetString()));
    }

    [PostgresFact]
    public async Task List_StatusFilter_ReturnsOnlySuppliersInThatStatus()
    {
        var prefix = NewTag();
        await SeedSupplierAsync($"{prefix} Attivo");
        var suspended = await SeedSupplierAsync($"{prefix} Sospeso", SupplierStatus.Suspended);
        await SeedSupplierAsync($"{prefix} Attesa", SupplierStatus.Pending, tosAccepted: false);
        using var admin = factory.CreateAuthenticatedClient(NewAdminId(), Admin);

        var result = await GetJsonAsync(admin, $"/api/admin/suppliers?search={prefix}&status=Suspended");

        Assert.Equal(1, result.GetProperty("totalCount").GetInt32());
        var item = Assert.Single(result.GetProperty("items").EnumerateArray());
        Assert.Equal(suspended.OrgId, item.GetProperty("orgId").GetGuid());
        Assert.Equal("Suspended", item.GetProperty("status").GetString());
    }

    [PostgresFact]
    public async Task List_SearchWithLikeWildcards_MatchesTheCharactersAndNotEverything()
    {
        var tag = NewTag();
        var literal = await SeedSupplierAsync($"{tag} 100% Pulizie");
        await SeedSupplierAsync($"{tag} 100X Pulizie");
        using var admin = factory.CreateAuthenticatedClient(NewAdminId(), Admin);

        var result = await GetJsonAsync(admin, $"/api/admin/suppliers?search={Uri.EscapeDataString($"{tag} 100%")}");

        var item = Assert.Single(result.GetProperty("items").EnumerateArray());
        Assert.Equal(literal.OrgId, item.GetProperty("orgId").GetGuid());
    }

    [PostgresFact]
    public async Task List_SearchByEmail_FindsTheSupplierCaseInsensitively()
    {
        var tag = NewTag();
        var supplier = await SeedSupplierAsync($"{tag} Srl");
        using var admin = factory.CreateAuthenticatedClient(NewAdminId(), Admin);

        var result = await GetJsonAsync(admin, $"/api/admin/suppliers?search={supplier.Email.ToUpperInvariant()}");

        Assert.Equal(supplier.OrgId, Assert.Single(result.GetProperty("items").EnumerateArray()).GetProperty("orgId").GetGuid());
    }

    [PostgresFact]
    public async Task List_PageAndPageSizeOutOfRange_AreClampedInsteadOfFailing()
    {
        using var admin = factory.CreateAuthenticatedClient(NewAdminId(), Admin);

        var result = await GetJsonAsync(admin, "/api/admin/suppliers?page=-3&pageSize=0");

        Assert.Equal(1, result.GetProperty("page").GetInt32());
        Assert.Equal(1, result.GetProperty("pageSize").GetInt32());
    }

    [PostgresTheory]
    [InlineData(Host)]
    [InlineData(Supplier)]
    public async Task AdminEndpoints_NonAdminCaller_Return403(string role)
    {
        var supplier = await SeedSupplierAsync($"{NewTag()} Srl");
        using var client = factory.CreateAuthenticatedClient($"auth0|su12-{Guid.NewGuid():N}", role);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/suppliers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/suppliers/invites")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/admin/suppliers/{supplier.OrgId}/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Suspend(client, supplier.OrgId, "motivo")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/admin/suppliers/{supplier.OrgId}/reactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/admin/suppliers/invites/{Guid.NewGuid()}/resend", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/admin/suppliers/invites/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(SupplierStatus.Active, (await ReadProfileAsync(supplier.OrgId)).Status);
    }

    [PostgresFact]
    public async Task AdminEndpoints_Anonymous_Return401()
    {
        using var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/suppliers")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/suppliers/invites")).StatusCode);
    }

    // ─── Suspend / reactivate ───

    [PostgresFact]
    public async Task Suspend_ActiveSupplier_SuspendsItRecordsTheAuditAndKeepsItsOpenRequests()
    {
        var supplier = await SeedSupplierAsync($"{NewTag()} Srl");
        var host = await SeedHostAsync();
        var requestId = await SeedRequestAsync(host, supplier.OrgId, ServiceRequestStatus.PresoInCarico);
        var adminId = await SeedAdminAsync("Anna", "Admin");
        using var admin = factory.CreateAuthenticatedClient(adminId, Admin);

        var response = await Suspend(admin, supplier.OrgId, "  Segnalato da due host  ");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("Suspended", body.GetProperty("status").GetString());
        Assert.Equal("Segnalato da due host", body.GetProperty("suspensionReason").GetString());
        Assert.Equal(1, body.GetProperty("openRequests").GetInt32());
        var profile = await ReadProfileAsync(supplier.OrgId);
        Assert.Equal(SupplierStatus.Suspended, profile.Status);
        Assert.NotNull(profile.SuspendedAt);
        Assert.Equal("Segnalato da due host", profile.SuspensionReason);
        // The open request stays as it is: the admin decides what to do with it.
        Assert.Equal(ServiceRequestStatus.PresoInCarico, (await ReadRequestAsync(requestId)).Status);
        var entry = Assert.Single(await ReadAuditAsync(supplier.OrgId));
        Assert.Equal(SupplierAdminAuditAction.Suspended, entry.Action);
        Assert.Equal(adminId, entry.ActorUserId);
        Assert.Equal("Segnalato da due host", entry.Reason);
        Assert.Equal(SupplierStatus.Active, entry.PreviousStatus);
        Assert.Equal(SupplierStatus.Suspended, entry.NewStatus);
    }

    [PostgresTheory]
    [InlineData("{}")]
    [InlineData("""{"reason":null}""")]
    [InlineData("""{"reason":"   "}""")]
    public async Task Suspend_WithoutReason_Returns400AndLeavesTheSupplierActive(string body)
    {
        var supplier = await SeedSupplierAsync($"{NewTag()} Srl");
        using var admin = factory.CreateAuthenticatedClient(NewAdminId(), Admin);

        var response = await admin.PostAsync(
            $"/api/admin/suppliers/{supplier.OrgId}/suspend", new StringContent(body, Encoding.UTF8, "application/json"));

        var errors = await AssertValidationErrorAsync(response);
        Assert.Equal("Indica il motivo della sospensione.", errors.GetProperty("Reason")[0].GetString());
        Assert.Equal(SupplierStatus.Active, (await ReadProfileAsync(supplier.OrgId)).Status);
        Assert.Empty(await ReadAuditAsync(supplier.OrgId));
    }

    [PostgresFact]
    public async Task Suspend_ReasonOver500Characters_Returns400()
    {
        var supplier = await SeedSupplierAsync($"{NewTag()} Srl");
        using var admin = factory.CreateAuthenticatedClient(NewAdminId(), Admin);

        var response = await Suspend(admin, supplier.OrgId, new string('r', 501));

        var errors = await AssertValidationErrorAsync(response);
        Assert.Equal("Il motivo della sospensione può avere al massimo 500 caratteri.", errors.GetProperty("Reason")[0].GetString());
        Assert.Equal(SupplierStatus.Active, (await ReadProfileAsync(supplier.OrgId)).Status);
    }

    [PostgresFact]
    public async Task Suspend_UnknownSupplier_Returns404SupplierNotFound()
    {
        using var admin = factory.CreateAuthenticatedClient(NewAdminId(), Admin);

        var response = await Suspend(admin, Guid.NewGuid(), "motivo");

        await AssertProblemAsync(response, HttpStatusCode.NotFound, SupplierAdminErrorCodes.SupplierNotFound);
    }

    [PostgresFact]
    public async Task Suspend_AlreadySuspendedSupplier_Returns409AndWritesNoSecondAuditEntry()
    {
        var supplier = await SeedSupplierAsync($"{NewTag()} Srl");
        using var admin = factory.CreateAuthenticatedClient(NewAdminId(), Admin);
        Assert.Equal(HttpStatusCode.OK, (await Suspend(admin, supplier.OrgId, "primo motivo")).StatusCode);

        var again = await Suspend(admin, supplier.OrgId, "secondo motivo");

        await AssertProblemAsync(again, HttpStatusCode.Conflict, SupplierAdminErrorCodes.AlreadySuspended);
        Assert.Equal("primo motivo", (await ReadProfileAsync(supplier.OrgId)).SuspensionReason);
        Assert.Single(await ReadAuditAsync(supplier.OrgId));
    }

    [PostgresFact]
    public async Task Reactivate_SuspendedSupplierThatWasActive_ReturnsToActiveAndClearsTheSuspension()
    {
        var supplier = await SeedSupplierAsync($"{NewTag()} Srl");
        var adminId = await SeedAdminAsync("Anna", "Admin");
        using var admin = factory.CreateAuthenticatedClient(adminId, Admin);
        Assert.Equal(HttpStatusCode.OK, (await Suspend(admin, supplier.OrgId, "motivo")).StatusCode);

        var response = await admin.PostAsync($"/api/admin/suppliers/{supplier.OrgId}/reactivate", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Active", (await ReadJsonAsync(response)).GetProperty("status").GetString());
        var profile = await ReadProfileAsync(supplier.OrgId);
        Assert.Equal(SupplierStatus.Active, profile.Status);
        Assert.Null(profile.SuspendedAt);
        Assert.Null(profile.SuspensionReason);
    }

    [PostgresFact]
    public async Task Reactivate_SupplierThatNeverAcceptedTheTerms_GoesBackToPendingAndNotToActive()
    {
        var supplier = await SeedSupplierAsync($"{NewTag()} Srl", SupplierStatus.Pending, tosAccepted: false);
        using var admin = factory.CreateAuthenticatedClient(NewAdminId(), Admin);
        Assert.Equal(HttpStatusCode.OK, (await Suspend(admin, supplier.OrgId, "motivo")).StatusCode);

        var response = await admin.PostAsync($"/api/admin/suppliers/{supplier.OrgId}/reactivate", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // A reactivation never skips the activation wizard.
        Assert.Equal(SupplierStatus.Pending, (await ReadProfileAsync(supplier.OrgId)).Status);
    }

    [PostgresFact]
    public async Task Reactivate_SupplierThatIsNotSuspended_Returns409()
    {
        var supplier = await SeedSupplierAsync($"{NewTag()} Srl");
        using var admin = factory.CreateAuthenticatedClient(NewAdminId(), Admin);

        var response = await admin.PostAsync($"/api/admin/suppliers/{supplier.OrgId}/reactivate", null);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, SupplierAdminErrorCodes.NotSuspended);
        Assert.Empty(await ReadAuditAsync(supplier.OrgId));
    }

    [PostgresFact]
    public async Task Reactivate_UnknownSupplier_Returns404()
    {
        using var admin = factory.CreateAuthenticatedClient(NewAdminId(), Admin);

        var response = await admin.PostAsync($"/api/admin/suppliers/{Guid.NewGuid()}/reactivate", null);

        await AssertProblemAsync(response, HttpStatusCode.NotFound, SupplierAdminErrorCodes.SupplierNotFound);
    }

    [PostgresFact]
    public async Task GetAudit_AfterSuspendAndReactivate_ListsBothNewestFirstWithTheAdminName()
    {
        var supplier = await SeedSupplierAsync($"{NewTag()} Srl");
        var adminId = await SeedAdminAsync("Anna", "Admin");
        using var admin = factory.CreateAuthenticatedClient(adminId, Admin);
        Assert.Equal(HttpStatusCode.OK, (await Suspend(admin, supplier.OrgId, "Documenti non validi")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/admin/suppliers/{supplier.OrgId}/reactivate", null)).StatusCode);

        var audit = await GetJsonAsync(admin, $"/api/admin/suppliers/{supplier.OrgId}/audit");

        var entries = audit.EnumerateArray().ToList();
        Assert.Equal(new[] { "Reactivated", "Suspended" }, entries.Select(e => e.GetProperty("action").GetString()));
        Assert.All(entries, e => Assert.Equal("Anna Admin", e.GetProperty("actorName").GetString()));
        Assert.Equal("Documenti non validi", entries[1].GetProperty("reason").GetString());
        Assert.Equal("Suspended", entries[0].GetProperty("previousStatus").GetString());
        Assert.Equal("Active", entries[0].GetProperty("newStatus").GetString());
    }

    [PostgresFact]
    public async Task GetAudit_UnknownSupplier_Returns404()
    {
        using var admin = factory.CreateAuthenticatedClient(NewAdminId(), Admin);

        var response = await admin.GetAsync($"/api/admin/suppliers/{Guid.NewGuid()}/audit");

        await AssertProblemAsync(response, HttpStatusCode.NotFound, SupplierAdminErrorCodes.SupplierNotFound);
    }

    // ─── A suspended supplier performs no action (A4-29) ───

    [PostgresTheory]
    [InlineData("take", ServiceRequestStatus.Richiesto)]
    [InlineData("reject", ServiceRequestStatus.Richiesto)]
    [InlineData("complete", ServiceRequestStatus.PresoInCarico)]
    public async Task SupplierAction_WhenSuspended_Returns422NotActiveChangesNothingAndNotifiesNobody(
        string action,
        ServiceRequestStatus current)
    {
        var supplier = await SeedSupplierAsync($"{NewTag()} Srl", SupplierStatus.Suspended);
        var host = await SeedHostAsync();
        var requestId = await SeedRequestAsync(host, supplier.OrgId, current);
        var emailsBefore = factory.Emails.Snapshot().Count;
        using var client = factory.CreateAuthenticatedClient(supplier.UserId, Supplier);

        var response = await client.PostAsJsonAsync($"/api/service-requests/{requestId}/{action}", new { reason = "Non disponibile" });

        var problem = await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, ServiceRequestErrorCodes.SupplierNotActive);
        Assert.StartsWith("Il tuo account fornitore non è attivo", problem.GetProperty("detail").GetString());
        var stored = await ReadRequestAsync(requestId);
        Assert.Equal(current, stored.Status);
        Assert.Null(stored.RejectionReason);
        Assert.Equal(emailsBefore, factory.Emails.Snapshot().Count);
    }

    [PostgresFact]
    public async Task SupplierAction_PendingSupplier_Returns422NotActive()
    {
        var supplier = await SeedSupplierAsync($"{NewTag()} Srl", SupplierStatus.Pending, tosAccepted: false);
        var host = await SeedHostAsync();
        var requestId = await SeedRequestAsync(host, supplier.OrgId, ServiceRequestStatus.Richiesto);
        using var client = factory.CreateAuthenticatedClient(supplier.UserId, Supplier);

        var response = await client.PostAsJsonAsync($"/api/service-requests/{requestId}/take", new { });

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, ServiceRequestErrorCodes.SupplierNotActive);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await ReadRequestAsync(requestId)).Status);
    }

    [PostgresFact]
    public async Task SupplierAction_RequestOfAnotherSupplier_Returns403BeforeTheActiveCheck()
    {
        var suspended = await SeedSupplierAsync($"{NewTag()} Srl", SupplierStatus.Suspended);
        var other = await SeedSupplierAsync($"{NewTag()} Srl");
        var host = await SeedHostAsync();
        var requestId = await SeedRequestAsync(host, other.OrgId, ServiceRequestStatus.Richiesto);
        using var client = factory.CreateAuthenticatedClient(suspended.UserId, Supplier);

        var response = await client.PostAsJsonAsync($"/api/service-requests/{requestId}/take", new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [PostgresFact]
    public async Task SupplierAction_AfterSuspendAndReactivate_WorksAgain()
    {
        var supplier = await SeedSupplierAsync($"{NewTag()} Srl");
        var host = await SeedHostAsync();
        var requestId = await SeedRequestAsync(host, supplier.OrgId, ServiceRequestStatus.Richiesto);
        using var admin = factory.CreateAuthenticatedClient(NewAdminId(), Admin);
        using var client = factory.CreateAuthenticatedClient(supplier.UserId, Supplier);
        Assert.Equal(HttpStatusCode.OK, (await Suspend(admin, supplier.OrgId, "motivo")).StatusCode);
        await AssertProblemAsync(
            await client.PostAsJsonAsync($"/api/service-requests/{requestId}/take", new { }),
            HttpStatusCode.UnprocessableEntity,
            ServiceRequestErrorCodes.SupplierNotActive);

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/admin/suppliers/{supplier.OrgId}/reactivate", null)).StatusCode);
        var response = await client.PostAsJsonAsync($"/api/service-requests/{requestId}/take", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ServiceRequestStatus.PresoInCarico, (await ReadRequestAsync(requestId)).Status);
    }

    [PostgresFact]
    public async Task MarkPaid_CompletedRequestOfASuspendedSupplier_StillWorksForTheHost()
    {
        // Paying is the host's action, not the supplier's: a suspension must not leave completed work unpaid.
        var supplier = await SeedSupplierAsync($"{NewTag()} Srl", SupplierStatus.Suspended);
        var host = await SeedHostAsync();
        var requestId = await SeedRequestAsync(host, supplier.OrgId, ServiceRequestStatus.Completato);
        using var client = factory.CreateAuthenticatedClient(host.OwnerId, Host);

        var response = await client.PostAsync($"/api/service-requests/{requestId}/mark-paid", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ServiceRequestStatus.Pagato, (await ReadRequestAsync(requestId)).Status);
    }

    [PostgresFact]
    public async Task CreateRequest_ForASuspendedSupplier_Returns422SupplierInactive()
    {
        var supplier = await SeedSupplierAsync($"{NewTag()} Srl", SupplierStatus.Suspended);
        var host = await SeedHostAsync();
        using var client = factory.CreateAuthenticatedClient(host.OwnerId, Host);

        var response = await client.PostAsJsonAsync("/api/service-requests", new
        {
            propertyId = host.PropertyId,
            bookingId = host.BookingId,
            supplierOrgId = supplier.OrgId,
            category = "cleaning",
        });

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, ServiceRequestErrorCodes.SupplierInactive);
    }

    [PostgresFact]
    public async Task HostSupplierSearch_SuspendedSupplier_IsNotListed()
    {
        var suspendedName = $"{NewTag()} Sospeso";
        var activeName = $"{NewTag()} Attivo";
        await SeedSupplierAsync(suspendedName, SupplierStatus.Suspended);
        await SeedSupplierAsync(activeName);
        var host = await SeedHostAsync();
        using var client = factory.CreateAuthenticatedClient(host.OwnerId, Host);

        var result = await GetJsonAsync(client, $"/api/suppliers?comune={Comune}&category=cleaning");

        var names = result.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("legalName").GetString()).ToList();
        Assert.Contains(activeName, names);
        Assert.DoesNotContain(suspendedName, names);
    }

    [PostgresFact]
    public async Task CompleteActivation_WhenSuspended_Returns422AndTheSupplierStaysSuspended()
    {
        var supplier = await SeedSupplierAsync($"{NewTag()} Srl", SupplierStatus.Suspended);
        using var client = factory.CreateAuthenticatedClient(supplier.UserId, Supplier);

        var response = await client.PostAsJsonAsync("/api/supplier/profile/activation/complete", new { tosAccepted = true });

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "supplier_suspended");
        Assert.Equal(SupplierStatus.Suspended, (await ReadProfileAsync(supplier.OrgId)).Status);
    }

    [PostgresFact]
    public async Task SupplierProfile_WhenSuspended_ExposesTheStatusSoTheConsoleCanShowTheBanner()
    {
        var supplier = await SeedSupplierAsync($"{NewTag()} Srl", SupplierStatus.Suspended);
        using var client = factory.CreateAuthenticatedClient(supplier.UserId, Supplier);

        var profile = await GetJsonAsync(client, "/api/supplier/profile");
        var dashboard = await GetJsonAsync(client, "/api/supplier/dashboard");

        Assert.Equal("Suspended", profile.GetProperty("status").GetString());
        Assert.Equal("Suspended", dashboard.GetProperty("status").GetString());
    }

    // ─── Invites ───

    [PostgresFact]
    public async Task ListInvites_FiltersByStateAndPaginatesInSql()
    {
        var tag = NewTag();
        var pending = await SeedInviteAsync($"{tag}-pending@test.com");
        var used = await SeedInviteAsync($"{tag}-used@test.com", isUsed: true);
        var expired = await SeedInviteAsync($"{tag}-expired@test.com", expiresAt: DateTime.UtcNow.AddDays(-1));
        var revoked = await SeedInviteAsync($"{tag}-revoked@test.com", revokedAt: DateTime.UtcNow.AddHours(-1));
        // Created before SU-01: no token hash, so its link can no longer be accepted: it counts as expired.
        var legacy = await SeedInviteAsync($"{tag}-legacy@test.com", withToken: false);
        using var admin = factory.CreateAuthenticatedClient(NewAdminId(), Admin);

        async Task<List<Guid>> IdsAsync(string state) =>
            (await GetJsonAsync(admin, $"/api/admin/suppliers/invites?search={tag}&state={state}"))
                .GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();

        Assert.Equal(new[] { pending.Id }, await IdsAsync("Pending"));
        Assert.Equal(new[] { used.Id }, await IdsAsync("Used"));
        Assert.Equal(new[] { revoked.Id }, await IdsAsync("Revoked"));
        Assert.Equal(new[] { expired.Id, legacy.Id }.Order(), (await IdsAsync("Expired")).Order());
        var all = await GetJsonAsync(admin, $"/api/admin/suppliers/invites?search={tag}&pageSize=2&page=3");
        Assert.Equal(5, all.GetProperty("totalCount").GetInt32());
        Assert.Single(all.GetProperty("items").EnumerateArray());
        var item = (await GetJsonAsync(admin, $"/api/admin/suppliers/invites?search={tag}-pending")).GetProperty("items")[0];
        Assert.Equal("Pending", item.GetProperty("state").GetString());
        // The link token is never part of the answer.
        Assert.False(item.TryGetProperty("token", out _));
        Assert.False(item.TryGetProperty("tokenHash", out _));
    }

    [PostgresFact]
    public async Task ResendInvite_PendingInvite_SendsANewLinkAndTheOldOneStopsWorking()
    {
        var email = $"{NewTag()}-resend@test.com";
        using var admin = factory.CreateAuthenticatedClient(NewAdminId(), Admin);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync(
            "/api/admin/suppliers/invite", new { email, comuneCode = Comune })).StatusCode);
        var oldToken = TokenFromInviteEmail(email, emailIndex: 0);
        var invite = await ReadInviteByEmailAsync(email);

        var response = await admin.PostAsync($"/api/admin/suppliers/invites/{invite.Id}/resend", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(invite.Id, body.GetProperty("inviteId").GetGuid());
        var newToken = TokenFromInviteEmail(email, emailIndex: 1);
        Assert.NotEqual(oldToken, newToken);
        using var anonymous = factory.CreateClient();
        await AssertProblemAsync(
            await anonymous.PostAsJsonAsync("/api/suppliers/invites/lookup", new { token = oldToken }),
            HttpStatusCode.UnprocessableEntity,
            "supplier_invite_invalid");
        var lookup = await anonymous.PostAsJsonAsync("/api/suppliers/invites/lookup", new { token = newToken });
        Assert.Equal(HttpStatusCode.OK, lookup.StatusCode);
        var stored = await ReadInviteByEmailAsync(email);
        Assert.Equal(SupplierInviteTokens.Hash(newToken), stored.TokenHash);
        Assert.True(stored.ExpiresAt > DateTime.UtcNow.AddDays(6));
        var entry = Assert.Single(await ReadInviteAuditAsync(invite.Id));
        Assert.Equal(SupplierAdminAuditAction.InviteResent, entry.Action);
    }

    [PostgresFact]
    public async Task ResendInvite_ExpiredInvite_WorksAgainWithANewExpiry()
    {
        var email = $"{NewTag()}-expired@test.com";
        var invite = await SeedInviteAsync(email, expiresAt: DateTime.UtcNow.AddDays(-2));
        using var admin = factory.CreateAuthenticatedClient(NewAdminId(), Admin);

        var response = await admin.PostAsync($"/api/admin/suppliers/invites/{invite.Id}/resend", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = TokenFromInviteEmail(email, emailIndex: 0);
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsJsonAsync("/api/suppliers/invites/lookup", new { token })).StatusCode);
        var listed = await GetJsonAsync(admin, $"/api/admin/suppliers/invites?search={email}");
        Assert.Equal("Pending", listed.GetProperty("items")[0].GetProperty("state").GetString());
    }

    [PostgresFact]
    public async Task ResendInvite_LegacyInviteWithoutTokenHash_IssuesAWorkingLink()
    {
        var email = $"{NewTag()}-legacy@test.com";
        var invite = await SeedInviteAsync(email, withToken: false);
        using var admin = factory.CreateAuthenticatedClient(NewAdminId(), Admin);

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/admin/suppliers/invites/{invite.Id}/resend", null)).StatusCode);

        var token = TokenFromInviteEmail(email, emailIndex: 0);
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsJsonAsync("/api/suppliers/invites/lookup", new { token })).StatusCode);
    }

    [PostgresTheory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ResendInvite_UsedOrRevokedInvite_Returns409NotResendableAndSendsNothing(bool used, bool revoked)
    {
        var email = $"{NewTag()}-closed@test.com";
        var invite = await SeedInviteAsync(email, isUsed: used, revokedAt: revoked ? DateTime.UtcNow.AddHours(-1) : null);
        var before = invite.TokenHash;
        using var admin = factory.CreateAuthenticatedClient(NewAdminId(), Admin);

        var response = await admin.PostAsync($"/api/admin/suppliers/invites/{invite.Id}/resend", null);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, SupplierAdminErrorCodes.InviteNotResendable);
        Assert.DoesNotContain(factory.Emails.Snapshot(), e => e.To == email);
        Assert.Equal(before, (await ReadInviteByEmailAsync(email)).TokenHash);
    }

    [PostgresFact]
    public async Task ResendInvite_AnotherPendingInviteForTheSameEmail_Returns409Duplicate()
    {
        var email = $"{NewTag()}-dup@test.com";
        var expired = await SeedInviteAsync(email, expiresAt: DateTime.UtcNow.AddDays(-3));
        await SeedInviteAsync(email);
        using var admin = factory.CreateAuthenticatedClient(NewAdminId(), Admin);

        var response = await admin.PostAsync($"/api/admin/suppliers/invites/{expired.Id}/resend", null);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, SupplierAdminErrorCodes.DuplicateInvite);
        Assert.DoesNotContain(factory.Emails.Snapshot(), e => e.To == email);
    }

    [PostgresFact]
    public async Task ResendInvite_EmailNowHasASupplierProfile_Returns409EmailTaken()
    {
        var supplier = await SeedSupplierAsync($"{NewTag()} Srl");
        var invite = await SeedInviteAsync(supplier.Email, expiresAt: DateTime.UtcNow.AddDays(-1));
        using var admin = factory.CreateAuthenticatedClient(NewAdminId(), Admin);

        var response = await admin.PostAsync($"/api/admin/suppliers/invites/{invite.Id}/resend", null);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "supplier_email_taken");
    }

    [PostgresFact]
    public async Task ResendInvite_UnknownInvite_Returns404()
    {
        using var admin = factory.CreateAuthenticatedClient(NewAdminId(), Admin);

        var response = await admin.PostAsync($"/api/admin/suppliers/invites/{Guid.NewGuid()}/resend", null);

        await AssertProblemAsync(response, HttpStatusCode.NotFound, SupplierAdminErrorCodes.InviteNotFound);
    }

    [PostgresFact]
    public async Task RevokeInvite_PendingInvite_Returns204AndNeitherLookupNorRegistrationAcceptItAnymore()
    {
        var email = $"{NewTag()}-revoke@test.com";
        using var admin = factory.CreateAuthenticatedClient(NewAdminId(), Admin);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync(
            "/api/admin/suppliers/invite", new { email, comuneCode = Comune })).StatusCode);
        var token = TokenFromInviteEmail(email, emailIndex: 0);
        var invite = await ReadInviteByEmailAsync(email);

        var response = await admin.DeleteAsync($"/api/admin/suppliers/invites/{invite.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var anonymous = factory.CreateClient();
        await AssertProblemAsync(
            await anonymous.PostAsJsonAsync("/api/suppliers/invites/lookup", new { token }),
            HttpStatusCode.UnprocessableEntity,
            "supplier_invite_revoked");
        using var invited = factory.CreateAuthenticatedClient($"auth0|su12-invited-{Guid.NewGuid():N}", email: email);
        await AssertProblemAsync(
            await invited.PostAsJsonAsync("/api/suppliers/register", new
            {
                email,
                legalName = "Pulizie Revocate Srl",
                phone = "+39 06 123456",
                comuneCode = Comune,
                inviteToken = token,
            }),
            HttpStatusCode.UnprocessableEntity,
            "supplier_invite_revoked");
        var stored = await ReadInviteByEmailAsync(email);
        Assert.NotNull(stored.RevokedAt);
        Assert.False(stored.IsUsed);
        var entry = Assert.Single(await ReadInviteAuditAsync(invite.Id));
        Assert.Equal(SupplierAdminAuditAction.InviteRevoked, entry.Action);
        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().SupplierProfiles.AnyAsync(p => p.Email == email));
    }

    [PostgresTheory]
    [InlineData("used")]
    [InlineData("expired")]
    [InlineData("revoked")]
    public async Task RevokeInvite_NotPendingInvite_Returns409(string kind)
    {
        var email = $"{NewTag()}-{kind}@test.com";
        var invite = await SeedInviteAsync(
            email,
            isUsed: kind == "used",
            expiresAt: kind == "expired" ? DateTime.UtcNow.AddDays(-1) : null,
            revokedAt: kind == "revoked" ? DateTime.UtcNow.AddHours(-1) : null);
        using var admin = factory.CreateAuthenticatedClient(NewAdminId(), Admin);

        var response = await admin.DeleteAsync($"/api/admin/suppliers/invites/{invite.Id}");

        await AssertProblemAsync(response, HttpStatusCode.Conflict, SupplierAdminErrorCodes.InviteNotPending);
        Assert.Empty(await ReadInviteAuditAsync(invite.Id));
    }

    [PostgresFact]
    public async Task RevokeInvite_UnknownInvite_Returns404()
    {
        using var admin = factory.CreateAuthenticatedClient(NewAdminId(), Admin);

        var response = await admin.DeleteAsync($"/api/admin/suppliers/invites/{Guid.NewGuid()}");

        await AssertProblemAsync(response, HttpStatusCode.NotFound, SupplierAdminErrorCodes.InviteNotFound);
    }

    [PostgresFact]
    public async Task InviteSupplier_AfterRevokingThePendingInvite_CreatesANewOneInsteadOfDuplicateInvite()
    {
        var email = $"{NewTag()}-again@test.com";
        using var admin = factory.CreateAuthenticatedClient(NewAdminId(), Admin);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync(
            "/api/admin/suppliers/invite", new { email, comuneCode = Comune })).StatusCode);
        var first = await ReadInviteByEmailAsync(email);
        // While it is pending, a second invite for the email is refused.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync(
            "/api/admin/suppliers/invite", new { email, comuneCode = Comune })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/suppliers/invites/{first.Id}")).StatusCode);

        var second = await admin.PostAsJsonAsync("/api/admin/suppliers/invite", new { email, comuneCode = Comune });

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var listed = await GetJsonAsync(admin, $"/api/admin/suppliers/invites?search={email}");
        Assert.Equal(
            new[] { "Pending", "Revoked" },
            listed.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("state").GetString()).Order());
    }

    // ─── helpers ───

    private sealed record SeededSupplier(Guid OrgId, string UserId, string Email);

    private sealed record SeededHost(string OwnerId, Guid OrgId, Guid PropertyId, Guid BookingId);

    private static string NewTag() => $"su12{Guid.NewGuid():N}"[..14];

    private static string NewAdminId() => $"auth0|su12-admin-{Guid.NewGuid():N}";

    private static DateTime DaysAgo(int days) => DateTime.UtcNow.AddDays(-days);

    private static Task<HttpResponseMessage> Suspend(HttpClient client, Guid orgId, string reason) =>
        client.PostAsJsonAsync($"/api/admin/suppliers/{orgId}/suspend", new { reason });

    private async Task<string> SeedAdminAsync(string firstName, string lastName)
    {
        var id = NewAdminId();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Users.Add(new User
        {
            Id = id,
            Email = $"{id[^8..]}@example.com",
            FirstName = firstName,
            LastName = lastName,
            Role = UserRole.Admin,
            IsActive = true,
        });
        await db.SaveChangesAsync();
        return id;
    }

    /// <summary>An own supplier org with its profile (comune H501, cleaning) and one member account.</summary>
    private async Task<SeededSupplier> SeedSupplierAsync(
        string legalName,
        SupplierStatus status = SupplierStatus.Active,
        bool tosAccepted = true,
        DateTime? createdAt = null)
    {
        var email = $"{NewTag()}@example.com";
        var userId = $"auth0|su12-supplier-{Guid.NewGuid():N}";
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = new OrgEntity
        {
            Name = legalName,
            Slug = $"su12-{Guid.NewGuid():N}"[..25],
            DisplayName = legalName,
            ContactEmail = email,
            OrgType = OrgType.Supplier,
            PlanTier = PlanTier.Starter,
        };
        db.AddRange(
            org,
            new User
            {
                Id = userId,
                Email = email,
                FirstName = "Sara",
                LastName = "Fornitore",
                OrgId = org.Id,
                SupplierOrgId = org.Id,
                IsActive = true,
            },
            new SupplierProfile
            {
                OrgId = org.Id,
                Email = email,
                LegalName = legalName,
                Phone = "+39 06 000000",
                Status = status,
                ComuniJson = $"[\"{Comune}\"]",
                CategoriesJson = "[\"cleaning\"]",
                TosAcceptedAt = tosAccepted ? DateTime.UtcNow : null,
                SuspendedAt = status == SupplierStatus.Suspended ? DateTime.UtcNow : null,
                SuspensionReason = status == SupplierStatus.Suspended ? "Sospeso nel test" : null,
                CreatedAt = createdAt ?? DateTime.UtcNow,
                UpdatedAt = createdAt ?? DateTime.UtcNow,
            });
        await db.SaveChangesAsync();
        return new SeededSupplier(org.Id, userId, email);
    }

    /// <summary>A host org with one property in comune H501 and one confirmed stay on it.</summary>
    private async Task<SeededHost> SeedHostAsync()
    {
        var ownerId = $"auth0|su12-owner-{Guid.NewGuid():N}";
        var org = await factory.SeedOrgForOwnerAsync(ownerId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var property = new Property
        {
            OwnerId = ownerId,
            OrgId = org.Id,
            Name = "Casa SU12",
            Address = $"Via SU12 {Guid.NewGuid():N}",
            City = Comune,
            PostalCode = "00100",
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 100m,
            CinCode = "IT058091C27G5FFZDZ",
            IsActive = true,
        };
        var guest = new Guest
        {
            OrgId = org.Id,
            FirstName = "Anna",
            LastName = "Ospite",
            Email = $"su12-{Guid.NewGuid():N}@example.com",
        };
        var stay = new Booking
        {
            OrgId = org.Id,
            PropertyId = property.Id,
            GuestId = guest.Id,
            CheckInDate = TimeProvider.System.TodayInRome().AddDays(5),
            CheckOutDate = TimeProvider.System.TodayInRome().AddDays(8),
            NumberOfGuests = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            BasePrice = 300m,
            TotalPrice = 300m,
        };
        db.AddRange(property, guest, stay);
        await db.SaveChangesAsync();
        return new SeededHost(ownerId, org.Id, property.Id, stay.Id);
    }

    private async Task<Guid> SeedRequestAsync(SeededHost host, Guid supplierOrgId, ServiceRequestStatus status)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var request = new ServiceRequest
        {
            OrgId = host.OrgId,
            BookingId = host.BookingId,
            PropertyId = host.PropertyId,
            SupplierOrgId = supplierOrgId,
            Category = "cleaning",
            Status = status,
            TakenAt = status >= ServiceRequestStatus.PresoInCarico && status != ServiceRequestStatus.Rifiutato ? DateTime.UtcNow : null,
            CompletedAt = status is ServiceRequestStatus.Completato or ServiceRequestStatus.Pagato ? DateTime.UtcNow : null,
        };
        db.ServiceRequests.Add(request);
        await db.SaveChangesAsync();
        return request.Id;
    }

    private async Task<SupplierInviteRecord> SeedInviteAsync(
        string email,
        bool isUsed = false,
        DateTime? expiresAt = null,
        DateTime? revokedAt = null,
        bool withToken = true)
    {
        var invite = new SupplierInviteRecord
        {
            Email = email,
            TokenHash = withToken ? SupplierInviteTokens.Hash(SupplierInviteTokens.Generate()) : null,
            ComuneCode = Comune,
            IsUsed = isUsed,
            RevokedAt = revokedAt,
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddDays(7),
        };
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.SupplierInviteRecords.Add(invite);
        await db.SaveChangesAsync();
        return invite;
    }

    private string TokenFromInviteEmail(string email, int emailIndex)
    {
        var emails = factory.Emails.Snapshot().Where(e => e.To == email).ToList();
        Assert.True(emails.Count > emailIndex, $"Expected at least {emailIndex + 1} invite emails to {email}, got {emails.Count}.");
        var match = Regex.Match(emails[emailIndex].Content.HtmlBody, "/register\\?inviteToken=([0-9a-f]{64})\"");
        Assert.True(match.Success, "The invite email has no web app registration link.");
        return match.Groups[1].Value;
    }

    private async Task<SupplierProfile> ReadProfileAsync(Guid orgId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .SupplierProfiles.AsNoTracking().SingleAsync(p => p.OrgId == orgId);
    }

    private async Task<ServiceRequest> ReadRequestAsync(Guid id)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .ServiceRequests.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == id);
    }

    private async Task<SupplierInviteRecord> ReadInviteByEmailAsync(string email)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .SupplierInviteRecords.AsNoTracking().SingleAsync(i => i.Email == email);
    }

    private async Task<List<SupplierAdminAuditEntry>> ReadAuditAsync(Guid supplierOrgId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .SupplierAdminAuditEntries.AsNoTracking().Where(e => e.SupplierOrgId == supplierOrgId)
            .OrderBy(e => e.OccurredAt).ToListAsync();
    }

    private async Task<List<SupplierAdminAuditEntry>> ReadInviteAuditAsync(Guid inviteId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .SupplierAdminAuditEntries.AsNoTracking().Where(e => e.InviteId == inviteId)
            .OrderBy(e => e.OccurredAt).ToListAsync();
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {url}: expected 200, got {(int)response.StatusCode}: {text}");
        return JsonSerializer.Deserialize<JsonElement>(text, JsonOptions);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(), JsonOptions);

    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"Expected {(int)status}, got {(int)response.StatusCode}: {text}");
        var body = JsonSerializer.Deserialize<JsonElement>(text, JsonOptions);
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("detail").GetString()));
        return body;
    }

    /// <summary>400 <c>validation_error</c> (FD-05); returns the field errors.</summary>
    private static async Task<JsonElement> AssertValidationErrorAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"Expected 400, got {(int)response.StatusCode}: {text}");
        var body = JsonSerializer.Deserialize<JsonElement>(text, JsonOptions);
        Assert.Equal("validation_error", body.GetProperty("code").GetString());
        return body.GetProperty("errors");
    }
}
