using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
// OrgEntity alias from Casazen.Tests.csproj

namespace Casazen.Tests.Integration;

/// <summary>
/// SU-03 (A4-05, A6-03): one taxonomy of service categories. The catalog endpoint serves the codes, every write
/// accepts only codes (422 <c>invalid_service_category</c> otherwise, FD-05) and the host search filters by code.
/// Runs on PostgreSQL when <c>TEST_POSTGRES_CONNECTION</c> is set.
/// </summary>
public class ServiceCategoriesIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string Comune = "H501";

    private readonly CasazenWebApplicationFactory _factory;

    public ServiceCategoriesIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Theory]
    [InlineData("PropertyOwner")]
    [InlineData("Supplier")]
    [InlineData("Admin")]
    public async Task GetAll_AsAuthenticatedUser_ReturnsEveryCategoryCodeInOrder(string role)
    {
        using var client = _factory.CreateAuthenticatedClient($"auth0|categories-{Guid.NewGuid():N}", role);

        var response = await client.GetAsync("/api/service-categories");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var codes = body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("code").GetString()!).ToList();
        Assert.Equal(ServiceCategories.All, codes);
    }

    [Fact]
    public async Task GetAll_WithoutAuth_Returns401()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/service-categories");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UpdateProfile_ItalianLabel_Returns422InvalidServiceCategoryAndKeepsStoredCategories()
    {
        var (supplierUserId, supplierOrgId) = await SeedSupplierAsync("""["cleaning"]""");
        using var client = _factory.CreateAuthenticatedClient(supplierUserId, "Supplier");

        var response = await client.PutAsJsonAsync("/api/supplier/profile", new
        {
            categories = new[] { "Pulizie" },
            bio = "Non deve essere salvata",
        });

        await AssertInvalidCategoryProblemAsync(response);
        var profile = await ReadProfileAsync(supplierOrgId);
        Assert.Equal("""["cleaning"]""", profile.CategoriesJson.Replace(" ", string.Empty));
        Assert.Null(profile.Bio);
    }

    [Fact]
    public async Task UpdateProfile_CodesWithSpacesCaseAndDuplicates_StoresNormalizedDistinctCodes()
    {
        var (supplierUserId, supplierOrgId) = await SeedSupplierAsync("[]");
        using var client = _factory.CreateAuthenticatedClient(supplierUserId, "Supplier");

        var response = await client.PutAsJsonAsync("/api/supplier/profile", new
        {
            categories = new[] { " Cleaning ", "check-in", "cleaning" },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var categories = body.GetProperty("categories").EnumerateArray().Select(c => c.GetString()).ToList();
        Assert.Equal(new[] { "cleaning", "check-in" }, categories);
        var stored = JsonSerializer.Deserialize<string[]>((await ReadProfileAsync(supplierOrgId)).CategoriesJson);
        Assert.Equal(new[] { "cleaning", "check-in" }, stored);
    }

    [Fact]
    public async Task GetSuppliers_FilterByCode_ReturnsOnlyActiveSuppliersThatDeclaredIt()
    {
        var (_, cleaningSupplier) = await SeedSupplierAsync("""["maintenance","cleaning"]""", active: true);
        var (_, maintenanceSupplier) = await SeedSupplierAsync("""["maintenance"]""", active: true);
        var (_, noCategorySupplier) = await SeedSupplierAsync("[]", active: true);
        var (_, pendingCleaningSupplier) = await SeedSupplierAsync("""["cleaning"]""", active: false);
        var (hostId, _, propertyId) = await SeedHostPropertyAsync();
        using var client = _factory.CreateAuthenticatedClient(hostId, "PropertyOwner");

        var response = await client.GetAsync($"/api/suppliers?propertyId={propertyId}&category=cleaning");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ids = await ReadSupplierIdsAsync(response);
        Assert.Contains(cleaningSupplier, ids);
        Assert.DoesNotContain(maintenanceSupplier, ids);
        Assert.DoesNotContain(noCategorySupplier, ids);
        Assert.DoesNotContain(pendingCleaningSupplier, ids);
    }

    [Theory]
    [InlineData("Pulizie")]
    [InlineData("idraulica")]
    public async Task GetSuppliers_UnknownCategory_Returns422InsteadOfEmptyList(string category)
    {
        var (hostId, _, propertyId) = await SeedHostPropertyAsync();
        using var client = _factory.CreateAuthenticatedClient(hostId, "PropertyOwner");

        var response = await client.GetAsync($"/api/suppliers?propertyId={propertyId}&category={Uri.EscapeDataString(category)}");

        await AssertInvalidCategoryProblemAsync(response);
    }

    [Fact]
    public async Task CreateServiceRequest_ItalianLabel_Returns422AndCreatesNothing()
    {
        var (_, supplierOrgId) = await SeedSupplierAsync("""["cleaning"]""", active: true);
        var (hostId, hostOrgId, propertyId) = await SeedHostPropertyAsync();
        using var client = _factory.CreateAuthenticatedClient(hostId, "PropertyOwner");

        var response = await client.PostAsJsonAsync("/api/service-requests", new
        {
            propertyId,
            supplierOrgId,
            category = "Pulizie",
        });

        await AssertInvalidCategoryProblemAsync(response);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.ServiceRequests.IgnoreQueryFilters().AnyAsync(sr => sr.OrgId == hostOrgId));
    }

    [Fact]
    public async Task CreateServiceRequest_CanonicalCode_Returns201WithCode()
    {
        var (_, supplierOrgId) = await SeedSupplierAsync("""["check-in"]""", active: true);
        var (hostId, hostOrgId, propertyId) = await SeedHostPropertyAsync();
        var bookingId = await SeedStayAsync(hostOrgId, propertyId);
        using var client = _factory.CreateAuthenticatedClient(hostId, "PropertyOwner");

        var response = await client.PostAsJsonAsync("/api/service-requests", new
        {
            propertyId,
            bookingId,
            supplierOrgId,
            category = "check-in",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("check-in", body.GetProperty("category").GetString());
    }

    [Fact]
    public async Task AdminInvite_ItalianLabel_Returns422AndCreatesNoInvite()
    {
        var email = $"invite-{Guid.NewGuid():N}@test.com";
        using var client = _factory.CreateAuthenticatedClient(roles: "Admin");

        var response = await client.PostAsJsonAsync("/api/admin/suppliers/invite", new
        {
            email,
            comuneCode = Comune,
            categories = new[] { "Pulizie" },
        });

        await AssertInvalidCategoryProblemAsync(response);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.SupplierInviteRecords.AnyAsync(i => i.Email == email));
    }

    [Fact]
    public async Task GetUnmappedCategories_AsAdmin_ListsStoredValuesThatAreNotCodes()
    {
        var (_, supplierOrgId) = await SeedSupplierAsync("""["cleaning","Idraulica speciale"]""");
        using var client = _factory.CreateAuthenticatedClient(roles: "Admin");

        var response = await client.GetAsync("/api/admin/suppliers/unmapped-categories");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var forSupplier = body.GetProperty("items").EnumerateArray()
            .Where(i => i.GetProperty("id").GetGuid() == supplierOrgId)
            .ToList();
        var item = Assert.Single(forSupplier);
        Assert.Equal("supplier_profile", item.GetProperty("source").GetString());
        Assert.Equal("Idraulica speciale", item.GetProperty("value").GetString());
    }

    [Fact]
    public async Task GetUnmappedCategories_AsHost_Returns403()
    {
        using var client = _factory.CreateAuthenticatedClient(roles: "PropertyOwner");

        var response = await client.GetAsync("/api/admin/suppliers/unmapped-categories");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static async Task AssertInvalidCategoryProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ServiceCategories.InvalidCategoryCode, problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
    }

    private static async Task<HashSet<Guid>> ReadSupplierIdsAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("orgId").GetGuid()).ToHashSet();
    }

    private async Task<SupplierProfile> ReadProfileAsync(Guid supplierOrgId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.SupplierProfiles.AsNoTracking().SingleAsync(sp => sp.OrgId == supplierOrgId);
    }

    private async Task<(string UserId, Guid OrgId)> SeedSupplierAsync(string categoriesJson, bool active = false)
    {
        var userId = $"auth0|su03-supplier-{Guid.NewGuid():N}";
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var org = new OrgEntity
        {
            Name = "SU-03 Supplier",
            Slug = $"su03-sup-{Guid.NewGuid():N}"[..28],
            DisplayName = "SU-03 Supplier",
            ContactEmail = $"{Guid.NewGuid():N}@test.com",
            OrgType = OrgType.Supplier,
            PlanTier = PlanTier.Starter,
        };
        db.Orgs.Add(org);
        db.Users.Add(new User
        {
            Id = userId,
            Email = org.ContactEmail,
            FirstName = "SU03",
            LastName = "Supplier",
            OrgId = org.Id,
            SupplierOrgId = org.Id,
            IsActive = true,
        });
        db.SupplierProfiles.Add(new SupplierProfile
        {
            OrgId = org.Id,
            Email = org.ContactEmail,
            LegalName = "SU-03 Supplier Srl",
            Phone = "+39 06 123123",
            Status = active ? SupplierStatus.Active : SupplierStatus.Pending,
            TosAcceptedAt = active ? DateTime.UtcNow : null,
            ComuniJson = $"[\"{Comune}\"]",
            CategoriesJson = categoriesJson,
        });

        await db.SaveChangesAsync();
        return (userId, org.Id);
    }

    private async Task<(string HostId, Guid HostOrgId, Guid PropertyId)> SeedHostPropertyAsync()
    {
        var hostId = $"auth0|su03-host-{Guid.NewGuid():N}";
        var hostOrg = await _factory.SeedOrgForOwnerAsync(hostId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var property = new Property
        {
            OwnerId = hostId,
            OrgId = hostOrg.Id,
            Name = "SU-03 Property",
            Address = $"Via Categorie {Guid.NewGuid():N}",
            City = Comune,
            PostalCode = "00100",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 90m,
            IsActive = true,
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return (hostId, hostOrg.Id, property.Id);
    }

    /// <summary>A confirmed stay on the property: short-rent requests are for a stay (SU-07, D2).</summary>
    private async Task<Guid> SeedStayAsync(Guid orgId, Guid propertyId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var guest = new Guest { OrgId = orgId, FirstName = "Anna", LastName = "Ospite", Email = $"su03-{Guid.NewGuid():N}@example.com" };
        var booking = new Booking
        {
            OrgId = orgId,
            PropertyId = propertyId,
            GuestId = guest.Id,
            CheckInDate = DateTime.UtcNow.Date.AddDays(3),
            CheckOutDate = DateTime.UtcNow.Date.AddDays(5),
            NumberOfGuests = 2,
            Status = BookingStatus.Confirmed,
        };
        db.AddRange(guest, booking);
        await db.SaveChangesAsync();
        return booking.Id;
    }
}
