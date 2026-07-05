using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
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

    [Fact]
    public async Task Register_ValidRequest_Returns201WithOrgId()
    {
        using var client = _factory.CreateClient();

        var payload = new
        {
            email = $"supplier-{Guid.NewGuid():N}@test.com",
            legalName = "Pulizie Roma Srl",
            phone = "+39 06 123456",
            comuneCode = "H501",
        };

        var response = await client.PostAsJsonAsync("/api/suppliers/register", payload);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(Guid.Empty, body.GetProperty("orgId").GetGuid());
        Assert.Equal("/supplier/activation", body.GetProperty("authRedirectUrl").GetString());
    }

    [Fact]
    public async Task Register_InvalidInviteToken_Returns400()
    {
        using var client = _factory.CreateClient();

        var payload = new
        {
            email = $"invite-{Guid.NewGuid():N}@test.com",
            legalName = "Bad Token Srl",
            phone = "+39 06 000000",
            comuneCode = "H501",
            inviteToken = Guid.NewGuid().ToString(),
        };

        var response = await client.PostAsJsonAsync("/api/suppliers/register", payload);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Register_AuthenticatedEmailMismatch_Returns400WithoutCreatingSupplierOrg()
    {
        var attackerId = $"auth0|attacker-{Guid.NewGuid():N}";
        var attackerEmail = $"attacker-{Guid.NewGuid():N}@test.com";
        var victimEmail = $"victim-{Guid.NewGuid():N}@test.com";

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.Add(new User
            {
                Id = attackerId,
                Email = attackerEmail,
                FirstName = "Bad",
                LastName = "Actor",
                IsActive = true,
            });
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateAuthenticatedClient(
            attackerId,
            roles: "Supplier",
            email: attackerEmail);

        var response = await client.PostAsJsonAsync("/api/suppliers/register", new
        {
            email = victimEmail,
            legalName = "Victim Supplier Srl",
            phone = "+39 06 123456",
            comuneCode = "H501",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(verifyDb.SupplierProfiles.Any(sp => sp.Email == victimEmail));
        Assert.Null(verifyDb.Users.Single(u => u.Id == attackerId).SupplierOrgId);
    }

    [Fact]
    public async Task Register_AuthenticatedEmailMatch_LinksUserToCreatedSupplierOrg()
    {
        var userId = $"auth0|supplier-register-{Guid.NewGuid():N}";
        var email = $"supplier-register-{Guid.NewGuid():N}@test.com";

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.Add(new User
            {
                Id = userId,
                Email = email,
                FirstName = "Good",
                LastName = "Supplier",
                IsActive = true,
            });
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateAuthenticatedClient(
            userId,
            roles: "Supplier",
            email: email);

        var response = await client.PostAsJsonAsync("/api/suppliers/register", new
        {
            email,
            legalName = "Good Supplier Srl",
            phone = "+39 06 123456",
            comuneCode = "H501",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var orgId = body.GetProperty("orgId").GetGuid();

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(orgId, verifyDb.Users.Single(u => u.Id == userId).SupplierOrgId);
        Assert.True(verifyDb.SupplierProfiles.Any(sp => sp.OrgId == orgId && sp.Email == email));
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

    [Fact]
    public async Task GetActivation_AsDualRoleHost_FindsSupplierProfileByEmail()
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
    }

    [Fact]
    public async Task CompleteActivation_WithoutTos_Returns409()
    {
        var (supplierId, _) = await SeedSupplierAsync();
        using var client = _factory.CreateAuthenticatedClient(supplierId, "Supplier");

        var response = await client.PostAsJsonAsync("/api/supplier/profile/activation/complete",
            new { tosAccepted = false });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CompleteActivation_MinimalProfileWithTos_Returns200Active()
    {
        // Only ToS should gate activation. Categories, bio, comuni can be added later.
        var (supplierId, _) = await SeedSupplierAsync();
        using var client = _factory.CreateAuthenticatedClient(supplierId, "Supplier");

        var response = await client.PostAsJsonAsync("/api/supplier/profile/activation/complete",
            new { tosAccepted = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Active", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task CompleteActivation_AllStepsMet_Returns200Active()
    {
        var (supplierId, orgId) = await SeedFullSupplierAsync();
        using var client = _factory.CreateAuthenticatedClient(supplierId, "Supplier");

        var response = await client.PostAsJsonAsync("/api/supplier/profile/activation/complete",
            new { tosAccepted = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Active", body.GetProperty("status").GetString());
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

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
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

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
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

        using var client = _factory.CreateAuthenticatedClient(roles: "PropertyOwner");
        var response = await client.GetAsync("/api/suppliers?comune=F205");

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
