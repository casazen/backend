using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// AM-03b over the real pipeline, on the two rilievi of the security review of AM-03 and on the crossing with the payments of the
/// suppliers. (1) The export of the org's fiscal data (<c>GET /api/gdpr/org/export</c>) is the holder's: the owner and the
/// administrators get the complete answer they always got, and a collaborator (limited to some properties or not), a property
/// manager and an accountant get 403. (2) Taking a property away from the person in charge of it puts nobody in charge, and the
/// person cannot be put back in charge without the access. (3) The actions that move money for an intervention are refused to a
/// collaborator "Solo alcuni" on a property it was not given (404, as if the request did not exist) and keep their permission
/// (403 without <c>property.write</c> on the property it was given). On PostgreSQL in CI; on the in-memory fallback locally (the
/// seed data of the model is written first).
/// </summary>
public class OrgScopeHardeningHttpIntegrationTests(OrgInvitationsFactory factory) : IClassFixture<OrgInvitationsFactory>
{
    private const string OrgFiscalCode = "RSSMRA80A01H501U";
    private const string TaxpayerOfGranted = "VRDLGU75B12F205X";
    private const string TaxpayerOfHidden = "BNCLRA85C45L219Z";

    private sealed record World(
        string OwnerId,
        Guid OrgId,
        string CollaboratorId,
        Guid CollaboratorMemberId,
        Guid GrantedId,
        Guid HiddenId,
        Guid GrantedRequestId,
        Guid HiddenRequestId);

    private static readonly SemaphoreSlim SeedLock = new(1, 1);

    private HttpClient Owner(World world) => factory.CreateAuthenticatedClient(world.OwnerId, roles: "PropertyOwner");

    /// <summary>A collaborator is a person of the database only: no role in the token, nothing it could widen its reach with.</summary>
    private HttpClient Collaborator(World world) => factory.CreateAuthenticatedClient(world.CollaboratorId);

    /// <summary>
    /// An org with its owner and a collaborator, two properties full of data, the fiscal data of the org and of both properties, and
    /// one completed intervention per property (its final amount waiting for the host's confirmation).
    /// </summary>
    private async Task<World> NewWorldAsync()
    {
        await EnsureSeedsAsync();
        var (ownerId, orgId) = await OrgTeamHttp.SeedOwnerWithTeamRowsAsync(factory);
        var collaboratorId = await OrgTeamHttp.AddMemberAsync(factory, orgId, OrgRole.Collaborator, ["short-rent"]);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (granted, hidden, supplierOrgId) = await HostScopeScenario.SeedPropertiesOfAsync(db, orgId, ownerId);
        var memberId = await db.OrgMembers.IgnoreQueryFilters().Where(m => m.UserId == collaboratorId).Select(m => m.Id).SingleAsync();

        var org = await db.Orgs.IgnoreQueryFilters().SingleAsync(o => o.Id == orgId);
        org.FiscalCode = OrgFiscalCode;
        org.HasPartitaIva = true;
        org.PartitaIvaNumber = "12345678901";
        foreach (var (id, taxpayer) in new[] { (granted.Id, TaxpayerOfGranted), (hidden.Id, TaxpayerOfHidden) })
        {
            var property = await db.Properties.IgnoreQueryFilters().SingleAsync(p => p.Id == id);
            property.TaxpayerFiscalCode = taxpayer;
            db.PropertyFiscalYears.Add(new PropertyFiscalYear
            {
                OrgId = orgId,
                PropertyId = id,
                TaxYear = 2026,
                Regime = StrFiscalRegime.CedolareSecca21,
                IsPrimaryForCedolare = true,
            });
        }

        var grantedRequest = await AddCompletedRequestAsync(db, granted, supplierOrgId);
        var hiddenRequest = await AddCompletedRequestAsync(db, hidden, supplierOrgId);
        await db.SaveChangesAsync();
        return new World(ownerId, orgId, collaboratorId, memberId, granted.Id, hidden.Id, grantedRequest, hiddenRequest);
    }

    private static async Task<Guid> AddCompletedRequestAsync(AppDbContext db, Property property, Guid supplierOrgId)
    {
        var bookingId = await db.Bookings.IgnoreQueryFilters().Where(b => b.PropertyId == property.Id).Select(b => b.Id).FirstAsync();
        var request = new ServiceRequest
        {
            OrgId = property.OrgId,
            PropertyId = property.Id,
            BookingId = bookingId,
            SupplierOrgId = supplierOrgId,
            RentalContext = ServiceRequestRentalContext.ShortRent,
            Category = "cleaning",
            Status = ServiceRequestStatus.Completato,
            TakenAt = HostScopeScenario.Now.UtcDateTime.AddDays(-2),
            CompletedAt = HostScopeScenario.Now.UtcDateTime.AddDays(-1),
            FinalAmountCents = 8_000,
            FinalAmountNeedsConfirmation = true,
        };
        db.ServiceRequests.Add(request);
        return request.Id;
    }

    private static object Selected(params Guid[] propertyIds) => new { propertyScope = "Selected", propertyIds };

    /// <summary>
    /// Gives the person a role of the short-rent context that holds <c>property.write</c> besides what a collaborator holds: the
    /// state the product owner's decision would leave. The membership is re-pointed in the database before the person's first
    /// request, so no cached copy of its permissions exists.
    /// </summary>
    private async Task GivePropertyWriteAsync(string userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var role = new Role
        {
            Id = Random.Shared.Next(100_000, int.MaxValue),
            ContextKey = "short-rent",
            RoleKey = $"am03b_collaborator_with_write_{Guid.NewGuid():N}",
        };
        foreach (var permission in new[] { "property.read", "property.write", "servicerequest.write", "booking.read", "guest.read", "guest.write" })
            role.Permissions.Add(new RolePermission { PermissionKey = permission });

        db.Roles.Add(role);
        var membership = await db.UserContextMemberships.SingleAsync(m => m.UserId == userId && m.ContextKey == "short-rent");
        membership.RoleId = role.Id;
        await db.SaveChangesAsync();
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.Equal(expected, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    // --- The export of the org --------------------------------------------------------------------------

    [Fact]
    public async Task Export_TheOwner_GetsTheCompleteAnswerOfAlways()
    {
        var world = await NewWorldAsync();
        using var owner = Owner(world);

        var export = await JsonAsync(await owner.GetAsync("/api/gdpr/org/export"));

        // The keys it always had; the owner also gets the activity log (AM-02b).
        Assert.Equal(
            ["activity", "exportedAt", "fiscalCode", "fiscalDataRetentionUntil", "hasPartitaIva", "members", "partitaIvaNumber", "propertyFiscalYears", "propertyTaxpayers"],
            export.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(OrgFiscalCode, export.GetProperty("fiscalCode").GetString());
        Assert.True(export.GetProperty("hasPartitaIva").GetBoolean());
        Assert.Equal(
            new[] { world.GrantedId, world.HiddenId }.Order(),
            export.GetProperty("propertyFiscalYears").EnumerateArray().Select(y => y.GetProperty("propertyId").GetGuid()).Order());
        Assert.Equal(
            new[] { TaxpayerOfGranted, TaxpayerOfHidden }.Order(StringComparer.Ordinal),
            export.GetProperty("propertyTaxpayers").EnumerateArray().Select(t => t.GetProperty("fiscalCode").GetString()!).Order(StringComparer.Ordinal));
        Assert.Contains(
            export.GetProperty("members").EnumerateArray(),
            member => member.GetProperty("userId").GetString() == world.CollaboratorId);
    }

    [Fact]
    public async Task Export_AnAdministrator_GetsTheSameAnswerAsTheOwner()
    {
        var world = await NewWorldAsync();
        var adminId = await OrgTeamHttp.AddMemberAsync(factory, world.OrgId, OrgRole.Admin, ["short-rent"]);
        using var admin = factory.CreateAuthenticatedClient(adminId);
        using var owner = Owner(world);

        var byAdmin = await JsonAsync(await admin.GetAsync("/api/gdpr/org/export"));
        var byOwner = await JsonAsync(await owner.GetAsync("/api/gdpr/org/export"));

        Assert.Equal(byOwner.GetProperty("fiscalCode").GetString(), byAdmin.GetProperty("fiscalCode").GetString());
        Assert.Equal(2, byAdmin.GetProperty("propertyTaxpayers").GetArrayLength());
        Assert.Equal(2, byAdmin.GetProperty("propertyFiscalYears").GetArrayLength());
    }

    [Fact]
    public async Task Export_ACollaboratorSoloAlcuni_IsRefused_AndSoIsOneWithEveryProperty()
    {
        var world = await NewWorldAsync();
        using var owner = Owner(world);
        using var collaborator = Collaborator(world);

        // It reaches every property now: the export is still not its to make.
        await OrgTeamHttp.AssertProblemAsync(await collaborator.GetAsync("/api/gdpr/org/export"), HttpStatusCode.Forbidden, "forbidden");

        await JsonAsync(await owner.PutAsJsonAsync($"/api/orgs/me/members/{world.CollaboratorMemberId}/properties", Selected(world.GrantedId)));
        var refused = await collaborator.GetAsync("/api/gdpr/org/export");

        await OrgTeamHttp.AssertProblemAsync(refused, HttpStatusCode.Forbidden, "forbidden");
        Assert.DoesNotContain(OrgFiscalCode, await refused.Content.ReadAsStringAsync());
        Assert.DoesNotContain(TaxpayerOfHidden, await refused.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(OrgRole.PropertyManager)]
    [InlineData(OrgRole.Accountant)]
    public async Task Export_APropertyManagerAndAnAccountant_AreRefused(OrgRole role)
    {
        var world = await NewWorldAsync();
        var memberId = await OrgTeamHttp.AddMemberAsync(factory, world.OrgId, role, ["short-rent"]);
        using var member = factory.CreateAuthenticatedClient(memberId);

        var response = await member.GetAsync("/api/gdpr/org/export");

        await OrgTeamHttp.AssertProblemAsync(response, HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task Export_WithoutAToken_IsUnauthorized()
    {
        using var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/gdpr/org/export")).StatusCode);
    }

    // --- The person in charge of a property -----------------------------------------------------------------

    [Fact]
    public async Task Responsible_WhenTheOwnerTakesThePropertyAway_NobodyIsInChargeAnyMore_AndTheAccessIsNeededToBePutBack()
    {
        var world = await NewWorldAsync();
        using var owner = Owner(world);
        var access = $"/api/orgs/me/members/{world.CollaboratorMemberId}/properties";
        await JsonAsync(await owner.PutAsJsonAsync(access, Selected(world.GrantedId, world.HiddenId)));
        foreach (var propertyId in new[] { world.GrantedId, world.HiddenId })
        {
            Assert.Equal(
                HttpStatusCode.NoContent,
                (await owner.PutAsJsonAsync($"/api/properties/{propertyId}/responsible", new { userId = world.CollaboratorId })).StatusCode);
        }

        // Only the second property stays with the collaborator.
        await JsonAsync(await owner.PutAsJsonAsync(access, Selected(world.HiddenId)));

        var first = await JsonAsync(await owner.GetAsync($"/api/properties/{world.GrantedId}"));
        var second = await JsonAsync(await owner.GetAsync($"/api/properties/{world.HiddenId}"));
        Assert.Equal(JsonValueKind.Null, first.GetProperty("responsibleUserId").ValueKind);
        Assert.Equal(world.CollaboratorId, second.GetProperty("responsibleUserId").GetString());

        // Without the access it cannot be put back in charge; with it, it can.
        await OrgTeamHttp.AssertProblemAsync(
            await owner.PutAsJsonAsync($"/api/properties/{world.GrantedId}/responsible", new { userId = world.CollaboratorId }),
            HttpStatusCode.UnprocessableEntity,
            "property_responsible_invalid");
        await JsonAsync(await owner.PutAsJsonAsync(access, Selected(world.GrantedId, world.HiddenId)));
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await owner.PutAsJsonAsync($"/api/properties/{world.GrantedId}/responsible", new { userId = world.CollaboratorId })).StatusCode);
    }

    [Fact]
    public async Task Responsible_WhenTheAccessGoesBackToEveryProperty_TheCollaboratorStaysInCharge()
    {
        var world = await NewWorldAsync();
        using var owner = Owner(world);
        var access = $"/api/orgs/me/members/{world.CollaboratorMemberId}/properties";
        await JsonAsync(await owner.PutAsJsonAsync(access, Selected(world.GrantedId)));
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await owner.PutAsJsonAsync($"/api/properties/{world.GrantedId}/responsible", new { userId = world.CollaboratorId })).StatusCode);

        await JsonAsync(await owner.PutAsJsonAsync(access, new { propertyScope = "All" }));

        var property = await JsonAsync(await owner.GetAsync($"/api/properties/{world.GrantedId}"));
        Assert.Equal(world.CollaboratorId, property.GetProperty("responsibleUserId").GetString());
    }

    // --- The payments of the interventions -------------------------------------------------------------------

    [Fact]
    public async Task Payments_ACollaboratorSoloAlcuni_CannotMarkPaidTheRequestOfAPropertyItWasNotGiven()
    {
        var world = await NewWorldAsync();
        using var owner = Owner(world);
        using var collaborator = Collaborator(world);
        await JsonAsync(await owner.PutAsJsonAsync($"/api/orgs/me/members/{world.CollaboratorMemberId}/properties", Selected(world.GrantedId)));

        // As if it did not exist: the same answer as for an id nobody knows.
        await OrgTeamHttp.AssertProblemAsync(
            await collaborator.PostAsync($"/api/service-requests/{world.HiddenRequestId}/mark-paid", content: null),
            HttpStatusCode.NotFound,
            "service_request_not_found");
        await OrgTeamHttp.AssertProblemAsync(
            await collaborator.PostAsync($"/api/service-requests/{Guid.NewGuid()}/mark-paid", content: null),
            HttpStatusCode.NotFound,
            "service_request_not_found");

        // Nothing happened to it: still completed and unpaid, seen by its owner.
        var hidden = await JsonAsync(await owner.GetAsync($"/api/service-requests/{world.HiddenRequestId}"));
        Assert.Equal("Completato", hidden.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Payments_EvenWithPropertyWrite_ACollaboratorSoloAlcuni_IsLimitedToThePropertiesItWasGiven()
    {
        // The permission is one gate and the scope another: a collaborator whose role also holds property.write (the product owner
        // may give it, to confirm a price or pay online) still acts only on the properties it was given.
        var world = await NewWorldAsync();
        await GivePropertyWriteAsync(world.CollaboratorId);
        using var owner = Owner(world);
        using var collaborator = Collaborator(world);
        await JsonAsync(await owner.PutAsJsonAsync($"/api/orgs/me/members/{world.CollaboratorMemberId}/properties", Selected(world.GrantedId)));

        foreach (var action in new[] { "final-amount/confirm", "payment-session", "mark-paid" })
        {
            await OrgTeamHttp.AssertProblemAsync(
                await collaborator.PostAsync($"/api/service-requests/{world.HiddenRequestId}/{action}", content: null),
                HttpStatusCode.NotFound,
                "service_request_not_found");

            // On the property it was given the request is authorized: whatever it answers is the rules of the payment, not a refusal.
            var granted = await collaborator.PostAsync($"/api/service-requests/{world.GrantedRequestId}/{action}", content: null);
            Assert.NotEqual(HttpStatusCode.Forbidden, granted.StatusCode);
            Assert.True((int)granted.StatusCode < 500, $"{action} answered {(int)granted.StatusCode}");
            if (granted.StatusCode == HttpStatusCode.NotFound)
            {
                var problem = await granted.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("service_payment_not_found", problem.GetProperty("code").GetString());
            }
        }

        var hidden = await JsonAsync(await owner.GetAsync($"/api/service-requests/{world.HiddenRequestId}"));
        Assert.Equal("Completato", hidden.GetProperty("status").GetString());
        Assert.True(hidden.GetProperty("price").GetProperty("needsCustomerConfirmation").GetBoolean());
    }

    [Fact]
    public async Task Payments_ACollaboratorSoloAlcuni_OnAPropertyItWasGiven_MarksPaid_ButDoesNotConfirmThePriceNorPayOnline()
    {
        var world = await NewWorldAsync();
        using var owner = Owner(world);
        using var collaborator = Collaborator(world);
        await JsonAsync(await owner.PutAsJsonAsync($"/api/orgs/me/members/{world.CollaboratorMemberId}/properties", Selected(world.GrantedId)));

        // property.write is not the collaborator's (the product owner decides whether it may confirm or pay): refused on every
        // request, the ones it was given and the others alike, before the scope is even looked at.
        foreach (var action in new[] { "final-amount/confirm", "payment-session" })
        {
            foreach (var requestId in new[] { world.GrantedRequestId, world.HiddenRequestId })
            {
                Assert.Equal(
                    HttpStatusCode.Forbidden,
                    (await collaborator.PostAsync($"/api/service-requests/{requestId}/{action}", content: null)).StatusCode);
            }
        }

        // servicerequest.write is: it records that the request was paid, on the property it was given.
        var paid = await JsonAsync(await collaborator.PostAsync($"/api/service-requests/{world.GrantedRequestId}/mark-paid", content: null));
        Assert.Equal("Pagato", paid.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Payments_TheOwner_ActsOnEveryProperty_TheControlOfTheRefusalsAbove()
    {
        var world = await NewWorldAsync();
        using var owner = Owner(world);

        var paid = await JsonAsync(await owner.PostAsync($"/api/service-requests/{world.HiddenRequestId}/mark-paid", content: null));

        Assert.Equal("Pagato", paid.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("final-amount/confirm")]
    [InlineData("payment-session")]
    [InlineData("mark-paid")]
    public async Task Payments_TheLongRentTwins_AreNotReachedByACollaboratorOfTheShortRentOnly(string action)
    {
        var world = await NewWorldAsync();
        using var collaborator = Collaborator(world);

        var response = await collaborator.PostAsync($"/api/long-rent/service-requests/{world.GrantedRequestId}/{action}", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // --- The seed data the in-memory fallback lacks ----------------------------------------------------

    /// <summary>
    /// On PostgreSQL the migrations seed the contexts, roles and permissions. The in-memory fallback of the host has none (and
    /// <c>EnsureCreated</c> seeds only a store nobody has touched, which the host's start-up already did): write the seed data
    /// of the model, once, so the same tests run locally. The same as in <c>OrgPropertyScopeHttpIntegrationTests</c>.
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
                    // Only the columns: a seed row also carries the navigation lists of the model (shared objects), and adding
                    // them would attach and change the seed of the model itself, which every other host of the process reads.
                    foreach (var (name, value) in row.Where(column => entityType.FindProperty(column.Key) is not null))
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
}
