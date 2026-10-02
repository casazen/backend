using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
// OrgEntity is a global alias defined in Casazen.Tests.csproj: OrgEntity = global::Casazen.Core.Entities.Org

namespace Casazen.Tests.Integration;

/// <summary>
/// Integration tests for the Supplier Console feature (US-022 / #292).
/// Covers AC1–AC8: registration, activation, profile, inbox, availability, admin invite.
/// </summary>
public class SupplierConsoleIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;

    public SupplierConsoleIntegrationTests(CasazenWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ─── AC3: POST /api/suppliers/register (public) ───────────────────────────
    // Invite, pilot comuni, email binding and rate limit: SupplierRegistrationIntegrationTests (SU-01).

    [Fact]
    public async Task Register_SelfServeWithoutPilotComuniConfigured_Returns422SelfServeUnavailable()
    {
        // This factory configures no Suppliers:PilotComuni: self-serve is off until the product owner sets them.
        using var client = _factory.CreateClient();
        var email = $"supplier-{Guid.NewGuid():N}@test.com";

        var response = await client.PostAsJsonAsync("/api/suppliers/register", new
        {
            email,
            legalName = "Pulizie Roma Srl",
            phone = "+39 06 123456",
            comuneCode = "H501",
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("supplier_self_serve_unavailable", problem.GetProperty("code").GetString());

        var options = await (await client.GetAsync("/api/suppliers/registration-options")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(options.GetProperty("selfServeEnabled").GetBoolean());
        Assert.Empty(options.GetProperty("pilotComuni").EnumerateArray());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(db.SupplierProfiles.Any(sp => sp.Email == email));
    }

    // ─── AC5: GET/POST activation ─────────────────────────────────────────────

    [Fact]
    public async Task GetActivation_AsSupplier_Returns200WithSteps()
    {
        var (supplierId, orgId) = await SeedSupplierAsync();
        using var client = _factory.CreateAuthenticatedClient(supplierId, "Supplier");

        var response = await client.GetAsync("/api/supplier/profile/activation");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Pending", body.GetProperty("status").GetString());
        var steps = body.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(5, steps.Count);
    }

    [Fact]
    public async Task GetActivation_AsSupplier_AutoProvisionsWhenNoProfile()
    {
        var userId = $"auth0|supplier-new-{Guid.NewGuid():N}";
        using var client = _factory.CreateAuthenticatedClient(userId, "Supplier");

        var response = await client.GetAsync("/api/supplier/profile/activation");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Pending", body.GetProperty("status").GetString());
        Assert.Equal(5, body.GetProperty("steps").GetArrayLength());
    }

    // SU-02 (A4-23): the dual-role host reaches the supplier profile through its explicit link (User.SupplierOrgId),
    // not through the email; linking by email alone is covered in SupplierClaimIntegrationTests.
    [Fact]
    public async Task GetActivation_AsDualRoleHostLinkedBySupplierOrgId_FindsItsSupplierProfile()
    {
        var userId = $"auth0|dual-{Guid.NewGuid():N}";
        var email = $"dual-{Guid.NewGuid():N}@test.com";

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var hostOrg = new OrgEntity
            {
                Name = "Host Org",
                Slug = $"host-{Guid.NewGuid():N}"[..20],
                DisplayName = "Host Org",
                ContactEmail = email,
                OrgType = OrgType.Host,
                PlanTier = PlanTier.Starter,
            };
            db.Orgs.Add(hostOrg);

            var supplierOrg = new OrgEntity
            {
                Name = "Supplier Org",
                Slug = $"sup-{Guid.NewGuid():N}"[..20],
                DisplayName = "Supplier Org",
                ContactEmail = email,
                OrgType = OrgType.Supplier,
                PlanTier = PlanTier.Starter,
            };
            db.Orgs.Add(supplierOrg);

            db.Users.Add(new User
            {
                Id = userId,
                Email = email,
                FirstName = "Dual",
                LastName = "Role",
                OrgId = hostOrg.Id,
                SupplierOrgId = supplierOrg.Id,
                IsActive = true,
            });

            db.SupplierProfiles.Add(new SupplierProfile
            {
                OrgId = supplierOrg.Id,
                Email = email,
                LegalName = "Supplier Org",
                Phone = "+39 06 111111",
                ComuniJson = "[\"H501\"]",
            });

            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateAuthenticatedClient(userId, "PropertyOwner,Supplier");
        var response = await client.GetAsync("/api/supplier/profile/activation");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var verify = _factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        // No second supplier org was provisioned and the host org is kept.
        Assert.Equal(1, await verifyDb.SupplierProfiles.CountAsync(sp => sp.Email == email));
        var user = await verifyDb.Users.SingleAsync(u => u.Id == userId);
        Assert.NotEqual(user.OrgId, user.SupplierOrgId);
    }

    [Fact]
    public async Task CompleteActivation_WithoutTos_Returns409WithTheTosBlocker()
    {
        var (supplierId, _) = await SeedFullSupplierAsync();
        using var client = _factory.CreateAuthenticatedClient(supplierId, "Supplier");

        var response = await client.PostAsJsonAsync("/api/supplier/profile/activation/complete",
            new { tosAccepted = false, tosVersion = await CurrentTosVersionAsync(client) });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("supplier_activation_blocked", problem.GetProperty("code").GetString());
        Assert.Equal(["tos_not_accepted"], Blockers(problem));
    }

    [Fact]
    public async Task CompleteActivation_NoCategoriesNoBio_Returns409AndStaysPending()
    {
        // A4-09: before SU-05 the Terms alone activated a profile with no category and no description.
        var (supplierId, orgId) = await SeedSupplierAsync();
        using var client = _factory.CreateAuthenticatedClient(supplierId, "Supplier");

        var response = await client.PostAsJsonAsync("/api/supplier/profile/activation/complete",
            new { tosAccepted = true, tosVersion = await CurrentTosVersionAsync(client) });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["categories_missing", "bio_missing"], Blockers(problem));
        Assert.Equal(SupplierStatus.Pending, (await LoadProfileAsync(orgId)).Status);
    }

    [Fact]
    public async Task CompleteActivation_NoComuni_Returns409WithComuniMissing()
    {
        var (supplierId, orgId) = await SeedFullSupplierAsync();
        await UpdateProfileAsync(orgId, p => { p.ComuniJson = "[]"; p.ComuneIstatCodesJson = "[]"; });
        using var client = _factory.CreateAuthenticatedClient(supplierId, "Supplier");

        var response = await client.PostAsJsonAsync("/api/supplier/profile/activation/complete",
            new { tosAccepted = true, tosVersion = await CurrentTosVersionAsync(client) });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(["comuni_missing"], Blockers(await response.Content.ReadFromJsonAsync<JsonElement>()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a phone")]
    [InlineData("123")]
    public async Task CompleteActivation_PhoneNotPlausible_Returns409WithPhoneInvalid(string phone)
    {
        var (supplierId, orgId) = await SeedFullSupplierAsync();
        await UpdateProfileAsync(orgId, p => p.Phone = phone);
        using var client = _factory.CreateAuthenticatedClient(supplierId, "Supplier");

        var response = await client.PostAsJsonAsync("/api/supplier/profile/activation/complete",
            new { tosAccepted = true, tosVersion = await CurrentTosVersionAsync(client) });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(["phone_invalid"], Blockers(await response.Content.ReadFromJsonAsync<JsonElement>()));
    }

    [Fact]
    public async Task CompleteActivation_AllRequirementsMet_ActivatesAndRecordsTheTermsVersion()
    {
        var (supplierId, orgId) = await SeedFullSupplierAsync();
        using var client = _factory.CreateAuthenticatedClient(supplierId, "Supplier");
        var version = await CurrentTosVersionAsync(client);

        var response = await client.PostAsJsonAsync("/api/supplier/profile/activation/complete",
            new { tosAccepted = true, tosVersion = version });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Active", body.GetProperty("status").GetString());

        var profile = await LoadProfileAsync(orgId);
        Assert.Equal(version, profile.TosVersion);
        Assert.NotNull(profile.TosAcceptedAt);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var consent = await db.ConsentRecords.IgnoreQueryFilters() // proof of the acceptance of a supplier org, read across tenants by the test
            .SingleAsync(c => c.OrgId == orgId && c.Type == ConsentType.Tos);
        Assert.Equal(version, consent.Version);
        Assert.Equal(supplierId, consent.UserId);
    }

    [Fact]
    public async Task CompleteActivation_StaleTermsVersion_Returns409AndStaysPending()
    {
        var (supplierId, orgId) = await SeedFullSupplierAsync();
        using var client = _factory.CreateAuthenticatedClient(supplierId, "Supplier");

        var response = await client.PostAsJsonAsync("/api/supplier/profile/activation/complete",
            new { tosAccepted = true, tosVersion = "1999-01-v1" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("supplier_tos_version_stale", problem.GetProperty("code").GetString());
        var profile = await LoadProfileAsync(orgId);
        Assert.Equal(SupplierStatus.Pending, profile.Status);
        Assert.Null(profile.TosVersion);
    }

    [Fact]
    public async Task CompleteActivation_WithoutTermsVersion_Returns400()
    {
        var (supplierId, _) = await SeedFullSupplierAsync();
        using var client = _factory.CreateAuthenticatedClient(supplierId, "Supplier");

        var response = await client.PostAsJsonAsync("/api/supplier/profile/activation/complete", new { tosAccepted = true });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetActivation_ProfileMissingRequirements_ReportsEachStepWithItsBlocker()
    {
        var (supplierId, _) = await SeedSupplierAsync();
        using var client = _factory.CreateAuthenticatedClient(supplierId, "Supplier");

        var body = await (await client.GetAsync("/api/supplier/profile/activation")).Content.ReadFromJsonAsync<JsonElement>();

        var steps = body.GetProperty("steps").EnumerateArray().ToDictionary(s => s.GetProperty("id").GetString()!);
        Assert.Equal(["identity", "services", "showcase", "profile", "terms"], steps.Keys);
        Assert.Equal("completed", steps["identity"].GetProperty("status").GetString());
        Assert.Equal("categories_missing", steps["services"].GetProperty("blocker").GetString());
        Assert.False(steps["showcase"].GetProperty("required").GetBoolean());
        Assert.Equal("bio_missing", steps["profile"].GetProperty("blocker").GetString());
        Assert.Equal("tos_not_accepted", steps["terms"].GetProperty("blocker").GetString());
        // Nothing saved yet: the wizard opens at the first incomplete required step.
        Assert.Equal(2, body.GetProperty("currentStep").GetInt32());
    }

    [Fact]
    public async Task SetActivationStep_SavesTheStepAndTheWizardResumesThere()
    {
        var (supplierId, _) = await SeedSupplierAsync();
        using var client = _factory.CreateAuthenticatedClient(supplierId, "Supplier");

        var put = await client.PutAsJsonAsync("/api/supplier/profile/activation/step", new { step = 4 });

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        // Another device / session of the same supplier.
        using var other = _factory.CreateAuthenticatedClient(supplierId, "Supplier");
        var body = await (await other.GetAsync("/api/supplier/profile/activation")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(4, body.GetProperty("currentStep").GetInt32());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public async Task SetActivationStep_OutsideTheFiveSteps_Returns400(int step)
    {
        var (supplierId, _) = await SeedSupplierAsync();
        using var client = _factory.CreateAuthenticatedClient(supplierId, "Supplier");

        var put = await client.PutAsJsonAsync("/api/supplier/profile/activation/step", new { step });

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task GetActivation_ActiveSupplierThatAcceptedAnOlderVersion_AsksToAcceptAgainAndBlocksActions()
    {
        var (supplierId, orgId) = await SeedFullSupplierAsync(autoActivate: true);
        await UpdateProfileAsync(orgId, p => p.TosVersion = "2025-01-v1");
        using var client = _factory.CreateAuthenticatedClient(supplierId, "Supplier");

        var tos = (await (await client.GetAsync("/api/supplier/profile/activation")).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("tos");

        Assert.True(tos.GetProperty("reacceptanceRequired").GetBoolean());
        Assert.True(tos.GetProperty("blocksActions").GetBoolean());
        Assert.Equal("2025-01-v1", tos.GetProperty("acceptedVersion").GetString());
    }

    [Fact]
    public async Task GetActivation_ActiveSupplierThatAcceptedBeforeVersionsWereRecorded_AsksToAcceptWithoutBlocking()
    {
        var (supplierId, _) = await SeedFullSupplierAsync(autoActivate: true);
        using var client = _factory.CreateAuthenticatedClient(supplierId, "Supplier");

        var tos = (await (await client.GetAsync("/api/supplier/profile/activation")).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("tos");

        Assert.True(tos.GetProperty("reacceptanceRequired").GetBoolean());
        Assert.False(tos.GetProperty("blocksActions").GetBoolean());
    }

    [Fact]
    public async Task AcceptTos_CurrentVersion_ClearsTheReacceptanceAndKeepsTheStatus()
    {
        var (supplierId, orgId) = await SeedFullSupplierAsync(autoActivate: true);
        await UpdateProfileAsync(orgId, p => p.TosVersion = "2025-01-v1");
        using var client = _factory.CreateAuthenticatedClient(supplierId, "Supplier");
        var version = await CurrentTosVersionAsync(client);

        var response = await client.PostAsJsonAsync("/api/supplier/profile/tos/accept", new { tosVersion = version });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var tos = (await (await client.GetAsync("/api/supplier/profile/activation")).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("tos");
        Assert.False(tos.GetProperty("reacceptanceRequired").GetBoolean());
        Assert.False(tos.GetProperty("blocksActions").GetBoolean());
        Assert.Equal(SupplierStatus.Active, (await LoadProfileAsync(orgId)).Status);
    }

    [Fact]
    public async Task AcceptTos_StaleVersion_Returns409()
    {
        var (supplierId, _) = await SeedFullSupplierAsync(autoActivate: true);
        using var client = _factory.CreateAuthenticatedClient(supplierId, "Supplier");

        var response = await client.PostAsJsonAsync("/api/supplier/profile/tos/accept", new { tosVersion = "1999-01-v1" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task UpdateProfile_ActiveSupplierEmptiesTheCategories_Returns422AndKeepsThem()
    {
        var (supplierId, orgId) = await SeedFullSupplierAsync(autoActivate: true);
        using var client = _factory.CreateAuthenticatedClient(supplierId, "Supplier");

        var response = await client.PutAsJsonAsync("/api/supplier/profile", new { categories = Array.Empty<string>() });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("supplier_profile_requirements", problem.GetProperty("code").GetString());
        Assert.Equal("[\"cleaning\"]", (await LoadProfileAsync(orgId)).CategoriesJson);
    }

    [Fact]
    public async Task UpdateProfile_ActiveSupplierActivatedBeforeSu05WithoutBio_CanStillEditOtherFields()
    {
        // A profile activated when the Terms alone sufficed has no description: editing something else (or uploading a
        // photo) must keep working; the edit just cannot take one more requirement away.
        var (supplierId, orgId) = await SeedFullSupplierAsync(autoActivate: true);
        await UpdateProfileAsync(orgId, p => p.Bio = null);
        using var client = _factory.CreateAuthenticatedClient(supplierId, "Supplier");

        var response = await client.PutAsJsonAsync("/api/supplier/profile", new { phone = "+39 06 7654321" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("+39 06 7654321", (await LoadProfileAsync(orgId)).Phone);
    }

    [Fact]
    public async Task UpdateProfile_PendingSupplierEmptiesTheCategories_IsSaved()
    {
        var (supplierId, orgId) = await SeedFullSupplierAsync();
        using var client = _factory.CreateAuthenticatedClient(supplierId, "Supplier");

        var response = await client.PutAsJsonAsync("/api/supplier/profile", new { categories = Array.Empty<string>() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", (await LoadProfileAsync(orgId)).CategoriesJson);
    }

    private static async Task<string> CurrentTosVersionAsync(HttpClient client)
    {
        var body = await (await client.GetAsync("/api/supplier/profile/activation")).Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("tos").GetProperty("currentVersion").GetString()!;
    }

    private static string[] Blockers(JsonElement problem) =>
        problem.GetProperty("blockers").EnumerateArray().Select(b => b.GetString()!).ToArray();

    private async Task<SupplierProfile> LoadProfileAsync(Guid orgId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.SupplierProfiles.AsNoTracking().SingleAsync(p => p.OrgId == orgId);
    }

    private async Task UpdateProfileAsync(Guid orgId, Action<SupplierProfile> change)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var profile = await db.SupplierProfiles.SingleAsync(p => p.OrgId == orgId);
        change(profile);
        await db.SaveChangesAsync();
    }

    // ─── AC4/Profile ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetProfile_AsSupplier_Returns200Profile()
    {
        var (supplierId, _) = await SeedSupplierAsync();
        using var client = _factory.CreateAuthenticatedClient(supplierId, "Supplier");

        var response = await client.GetAsync("/api/supplier/profile");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Pending", body.GetProperty("status").GetString());
        Assert.NotEmpty(body.GetProperty("legalName").GetString()!);
    }

    [Fact]
    public async Task UpdateProfile_AsSupplier_Returns200UpdatedProfile()
    {
        var (supplierId, _) = await SeedSupplierAsync();
        using var client = _factory.CreateAuthenticatedClient(supplierId, "Supplier");

        var response = await client.PutAsJsonAsync("/api/supplier/profile", new
        {
            bio = "Azienda di pulizie professionale con 10 anni di esperienza.",
            categories = new[] { "cleaning", "laundry" },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Azienda di pulizie professionale con 10 anni di esperienza.", body.GetProperty("bio").GetString());
    }

    // ─── AC7: Inbox (empty until #293) ───────────────────────────────────────

    [Fact]
    public async Task GetInbox_AsSupplier_Returns200EmptyList()
    {
        var (supplierId, _) = await SeedSupplierAsync();
        using var client = _factory.CreateAuthenticatedClient(supplierId, "Supplier");

        var response = await client.GetAsync("/api/supplier/inbox");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("total").GetInt32());
    }

    // ─── AC8: Availability ───────────────────────────────────────────────────

    [Fact]
    public async Task GetAvailability_AsSupplier_Returns200SavedDates()
    {
        var (supplierId, _) = await SeedSupplierAsync();
        using var client = _factory.CreateAuthenticatedClient(supplierId, "Supplier");

        var today = TimeProvider.System.TodayInRomeAsDateOnly();
        var putResponse = await client.PutAsJsonAsync("/api/supplier/availability", new
        {
            dates = new[]
            {
                new { date = today.ToString("yyyy-MM-dd"), available = false },
                new { date = today.AddDays(1).ToString("yyyy-MM-dd"), available = true },
            },
        });
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        var getResponse = await client.GetAsync(
            $"/api/supplier/availability?from={today:yyyy-MM-dd}&to={today.AddDays(1):yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        var body = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        var dates = body.GetProperty("dates").EnumerateArray().ToList();
        Assert.Equal(2, dates.Count);
        Assert.False(dates[0].GetProperty("available").GetBoolean());
        Assert.True(dates[1].GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task UpdateAvailability_AsSupplier_Returns200Updated()
    {
        var (supplierId, _) = await SeedSupplierAsync();
        using var client = _factory.CreateAuthenticatedClient(supplierId, "Supplier");

        var today = TimeProvider.System.TodayInRomeAsDateOnly();
        var response = await client.PutAsJsonAsync("/api/supplier/availability", new
        {
            dates = new[]
            {
                new { date = today.ToString("yyyy-MM-dd"), available = false },
                new { date = today.AddDays(1).ToString("yyyy-MM-dd"), available = true },
            },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("updated").GetInt32());
    }

    // ─── AC3: Admin invite ───────────────────────────────────────────────────

    [Fact]
    public async Task AdminInvite_AsAdmin_Returns201WithInviteId()
    {
        using var client = _factory.CreateAuthenticatedClient(roles: "Admin");

        var response = await client.PostAsJsonAsync("/api/admin/suppliers/invite", new
        {
            email = $"new-supplier-{Guid.NewGuid():N}@test.com",
            comuneCode = "H501",
            categories = new[] { "cleaning" },
            message = "Benvenuto nella piattaforma!",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(Guid.Empty, body.GetProperty("inviteId").GetGuid());
        Assert.True(body.GetProperty("expiresAt").GetDateTime() > DateTime.UtcNow);
    }

    [Fact]
    public async Task AdminInvite_AsNonAdmin_Returns403()
    {
        using var client = _factory.CreateAuthenticatedClient(roles: "PropertyOwner");

        var response = await client.PostAsJsonAsync("/api/admin/suppliers/invite", new
        {
            email = "blocked@test.com",
            comuneCode = "H501",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ─── AC6: GET /api/suppliers only Active ─────────────────────────────────

    [Fact]
    public async Task GetSuppliers_ReturnsOnlyActiveForComune()
    {
        await SeedFullSupplierAsync(comuneCode: "F205", autoActivate: true);
        await _factory.SeedOrgForOwnerAsync();

        using var client = _factory.CreateAuthenticatedClient(roles: "PropertyOwner");
        var response = await client.GetAsync("/api/suppliers?comune=F205");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.True(items.Count >= 1);
    }

    [Fact]
    public async Task GetSuppliers_WithoutComuneOrPropertyId_Returns400WithTheLocalizedProblem()
    {
        await _factory.SeedOrgForOwnerAsync();
        using var client = _factory.CreateAuthenticatedClient(roles: "PropertyOwner");

        var response = await client.GetAsync("/api/suppliers");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation_error", problem.GetProperty("code").GetString());
        Assert.Equal("Indica il comune oppure l'immobile per cercare i fornitori.", problem.GetProperty("detail").GetString());

        var english = await GetAsync(client, "/api/suppliers", "en");
        Assert.Equal(
            "Provide the municipality or the property to search for suppliers.",
            (await english.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
    }

    // ─── A4-27: API messages come from the localized resources (no inline text) ──────────────────────────

    [Fact]
    public async Task GetAvailability_ToBeforeFrom_Returns400LocalizedInItalianAndEnglish()
    {
        var (supplierId, _) = await SeedSupplierAsync();
        using var client = _factory.CreateAuthenticatedClient(supplierId, "Supplier");
        const string url = "/api/supplier/availability?from=2026-10-10&to=2026-10-01";

        var italian = await GetAsync(client, url, null);
        var english = await GetAsync(client, url, "en");

        Assert.Equal(HttpStatusCode.BadRequest, italian.StatusCode);
        var it = await italian.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation_error", it.GetProperty("code").GetString());
        Assert.Equal("La data finale deve essere uguale o successiva alla data iniziale.", it.GetProperty("detail").GetString());
        Assert.Equal(
            "The end date must be on or after the start date.",
            (await english.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task GetAvailability_RangeOverTheLimit_Returns400NamingTheLimit()
    {
        var (supplierId, _) = await SeedSupplierAsync();
        using var client = _factory.CreateAuthenticatedClient(supplierId, "Supplier");

        var response = await GetAsync(client, "/api/supplier/availability?from=2026-10-01&to=2027-03-01", "en");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "The maximum range is 90 days.",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task GetPublicShowcase_UnknownSlug_Returns404LocalizedProblem()
    {
        using var client = _factory.CreateClient();

        var italian = await GetAsync(client, "/api/public/suppliers/non-esiste", null);
        var english = await GetAsync(client, "/api/public/suppliers/non-esiste", "en");

        Assert.Equal(HttpStatusCode.NotFound, italian.StatusCode);
        var it = await italian.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("not_found", it.GetProperty("code").GetString());
        Assert.Equal("Questa vetrina fornitore non esiste o non è più disponibile.", it.GetProperty("detail").GetString());
        Assert.Equal(
            "This supplier showcase does not exist or is no longer available.",
            (await english.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task AdminInvite_SecondPendingInviteForTheSameEmail_Returns409LocalizedDuplicate()
    {
        var email = $"invite-{Guid.NewGuid():N}@test.com";
        using var admin = _factory.CreateAuthenticatedClient(roles: "Admin");
        var first = await admin.PostAsJsonAsync("/api/admin/suppliers/invite", new { email, comuneCode = "H501" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await admin.PostAsJsonAsync("/api/admin/suppliers/invite", new { email, comuneCode = "H501" });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var problem = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("duplicate_invite", problem.GetProperty("code").GetString());
        Assert.Equal(
            "Esiste già un altro invito in attesa per questa email: revocalo prima di reinviare questo.",
            problem.GetProperty("detail").GetString());
        Assert.DoesNotContain(email, problem.GetProperty("detail").GetString()!);
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string url, string? language)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (language is not null)
            request.Headers.Add("Accept-Language", language);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task GetSuppliers_ByPropertyId_ReturnsActiveSuppliersForPropertyComune()
    {
        const string comune = "H501";
        await SeedFullSupplierAsync(comuneCode: comune, autoActivate: true);

        var hostId = $"auth0|host-{Guid.NewGuid():N}";
        var hostOrg = await _factory.SeedOrgForOwnerAsync(hostId);

        Guid propertyId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var property = new Property
            {
                OwnerId = hostId,
                OrgId = hostOrg.Id,
                Name = "Supplier Picker Property",
                Address = $"Via Picker {Guid.NewGuid():N}",
                City = comune,
                PostalCode = "00100",
                Bedrooms = 2,
                Bathrooms = 1,
                MaxGuests = 4,
                NightlyRate = 100m,
                CinCode = "IT-ABC123-DEF456",
                IsActive = true,
            };
            db.Properties.Add(property);
            await db.SaveChangesAsync();
            propertyId = property.Id;
        }

        using var client = _factory.CreateAuthenticatedClient(hostId, "PropertyOwner");
        var response = await client.GetAsync($"/api/suppliers?propertyId={propertyId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.True(items.Count >= 1);
    }

    [Fact]
    public async Task GetSuppliers_ByPropertyId_MatchesSupplierWithLegacyComuneCode()
    {
        await SeedFullSupplierAsync(comuneCode: "H501", autoActivate: true);

        var hostId = $"auth0|host-{Guid.NewGuid():N}";
        var hostOrg = await _factory.SeedOrgForOwnerAsync(hostId);

        Guid propertyId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var property = new Property
            {
                OwnerId = hostId,
                OrgId = hostOrg.Id,
                Name = "Roma Property",
                Address = $"Via Roma {Guid.NewGuid():N}",
                City = "Roma",
                PostalCode = "00100",
                Bedrooms = 2,
                Bathrooms = 1,
                MaxGuests = 4,
                NightlyRate = 100m,
                CinCode = "IT-ABC123-DEF456",
                IsActive = true,
            };
            db.Properties.Add(property);
            await db.SaveChangesAsync();
            propertyId = property.Id;
        }

        using var client = _factory.CreateAuthenticatedClient(hostId, "PropertyOwner");
        var response = await client.GetAsync($"/api/suppliers?propertyId={propertyId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.True(items.Count >= 1);
    }

    // ─── Auth guards ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SupplierProfile_WithoutAuth_Returns401()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/supplier/profile");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SupplierProfile_WithPropertyOwnerRole_Returns403()
    {
        using var client = _factory.CreateAuthenticatedClient(roles: "PropertyOwner");
        var response = await client.GetAsync("/api/supplier/profile");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private async Task<(string UserId, Guid OrgId)> SeedSupplierAsync(string comuneCode = "H501")
    {
        var userId = $"auth0|supplier-{Guid.NewGuid():N}";

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var org = new OrgEntity
        {
            Name = "Test Supplier Srl",
            Slug = $"test-supplier-{Guid.NewGuid():N}"[..30],
            DisplayName = "Test Supplier Srl",
            ContactEmail = $"{Guid.NewGuid():N}@test.com",
            OrgType = OrgType.Supplier,
            PlanTier = PlanTier.Starter,
        };
        db.Orgs.Add(org);

        var user = new User
        {
            Id = userId,
            Email = org.ContactEmail,
            FirstName = "Test",
            LastName = "Supplier",
            OrgId = org.Id,
            IsActive = true,
        };
        db.Users.Add(user);

        var profile = new SupplierProfile
        {
            OrgId = org.Id,
            Email = org.ContactEmail,
            LegalName = "Test Supplier Srl",
            Phone = "+39 06 999999",
            ComuniJson = $"[\"{comuneCode}\"]",
        };
        db.SupplierProfiles.Add(profile);

        await db.SaveChangesAsync();
        return (userId, org.Id);
    }

    private async Task<(string UserId, Guid OrgId)> SeedFullSupplierAsync(string comuneCode = "H501", bool autoActivate = false)
    {
        var (userId, orgId) = await SeedSupplierAsync(comuneCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var profile = await db.SupplierProfiles.FindAsync(orgId);
        if (profile is not null)
        {
            profile.CategoriesJson = "[\"cleaning\"]";
            profile.Bio = "Azienda di pulizie professionale.";

            if (autoActivate)
            {
                profile.TosAcceptedAt = DateTime.UtcNow;
                profile.Status = SupplierStatus.Active;
            }

            await db.SaveChangesAsync();
        }

        return (userId, orgId);
    }
}
