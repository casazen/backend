using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// TN-3 (A3-38 and the A9 endpoint inventory) over the real pipeline: context policies decide who may call a host
/// endpoint, the host resource handler decides on the row (org, permission, ownership).
/// </summary>
/// <remarks>
/// Runs with <c>Features:AiSupplierDiscovery</c> on (FD-21): <c>match-supplier</c> is 404 while the flag is off, and
/// its authorization must stay covered for when it is turned on.
/// </remarks>
public class HostAuthorizationIntegrationTests : IClassFixture<AiSupplierDiscoveryEnabledFactory>
{
    private readonly AiSupplierDiscoveryEnabledFactory _factory;

    public HostAuthorizationIntegrationTests(AiSupplierDiscoveryEnabledFactory factory) => _factory = factory;

    /// <summary>Host endpoints: an authenticated supplier (no host context) gets 403 on every one of them.</summary>
    public static TheoryData<string, string> HostEndpoints() => new()
    {
        { "GET", "/api/payments" },
        { "GET", "/api/payments/{payment}" },
        { "POST", "/api/payments/{payment}/refund" },
        { "GET", "/api/payments/{payment}/refunds" },
        { "GET", "/api/bookings/{booking}/cancellation" },
        { "POST", "/api/bookings/{booking}/cancel" },
        { "GET", "/api/guests" },
        { "GET", "/api/guests/{guest}" },
        { "GET", "/api/gdpr/guests/{guest}/export" },
        { "GET", "/api/pricing-adapter/config/{property}" },
        { "POST", "/api/pricing-adapter/sync/{property}" },
        { "GET", "/api/service-requests" },
        { "POST", "/api/service-requests/match-supplier" },
        { "POST", "/api/service-requests" },
        { "POST", "/api/service-requests/{request}/mark-paid" },
        { "GET", "/api/suppliers?comune=H501" },
        { "GET", "/api/properties" },
        { "GET", "/api/bookings" },
        { "GET", "/api/alloggiati/summary" },
        { "GET", "/api/compliance/summary" },
        { "GET", "/api/fiscal/regime" },
        { "GET", "/api/leases" },
    };

    [Theory]
    [MemberData(nameof(HostEndpoints))]
    public async Task HostEndpoint_AsAuthenticatedSupplier_Returns403(string method, string route)
    {
        var scenario = await SeedScenarioAsync();
        using var supplier = _factory.CreateAuthenticatedClient(scenario.SupplierUserId, "Supplier");

        var response = await SendAsync(supplier, method, scenario.Resolve(route), scenario);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("refund")]
    public async Task PaymentWrite_AsCollaboratorWithoutPaymentWrite_Returns403AndLeavesPaymentUnchanged(string operation)
    {
        var scenario = await SeedScenarioAsync();
        var collaboratorId = await SeedCollaboratorAsync(scenario.HostOrgId, "payment.read", "property.read", "booking.read");
        using var collaborator = _factory.CreateAuthenticatedClient(collaboratorId, "PropertyManager");

        // Same org, org-wide reach and payment.read: the payment is visible...
        var read = await collaborator.GetAsync($"/api/payments/{scenario.PaymentId}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        // ...but without payment.write it cannot be processed or refunded.
        var write = await collaborator.PostAsync($"/api/payments/{scenario.PaymentId}/{operation}", null);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var payment = await db.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == scenario.PaymentId);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Equal(0m, payment.RefundedAmount);
    }

    [Theory]
    [InlineData("GET", "/api/payments/{payment}")]
    [InlineData("GET", "/api/payments?propertyId={property}")]
    [InlineData("POST", "/api/payments/{payment}/refund")]
    [InlineData("GET", "/api/payments/{payment}/refunds")]
    [InlineData("GET", "/api/bookings/{booking}/cancellation")]
    [InlineData("POST", "/api/bookings/{booking}/cancel")]
    [InlineData("GET", "/api/pricing-adapter/config/{property}")]
    [InlineData("POST", "/api/pricing-adapter/config/{property}")]
    [InlineData("GET", "/api/service-requests/{request}")]
    [InlineData("POST", "/api/service-requests/{request}/mark-paid")]
    [InlineData("POST", "/api/service-requests/match-supplier")]
    [InlineData("POST", "/api/service-requests")]
    [InlineData("GET", "/api/suppliers?propertyId={property}")]
    [InlineData("GET", "/api/guests/{guest}")]
    [InlineData("GET", "/api/gdpr/guests/{guest}/export")]
    public async Task HostResource_AsHostOfAnotherOrg_Returns404Or403(string method, string route)
    {
        var scenario = await SeedScenarioAsync();
        var otherHost = $"auth0|tn3-other-{Guid.NewGuid():N}";
        await _factory.SeedOrgForOwnerAsync(otherHost);
        using var client = _factory.CreateAuthenticatedClient(otherHost, "PropertyOwner");

        var response = await SendAsync(client, method, scenario.Resolve(route), scenario);

        Assert.True(
            response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden,
            $"{method} {route} answered {(int)response.StatusCode} to another org's host.");
        await AssertScenarioUnchangedAsync(scenario);
    }

    [Fact]
    public async Task PaymentsList_AsHostOfAnotherOrg_DoesNotContainOtherOrgPayments()
    {
        var scenario = await SeedScenarioAsync();
        var otherHost = $"auth0|tn3-other-{Guid.NewGuid():N}";
        await _factory.SeedOrgForOwnerAsync(otherHost);
        using var client = _factory.CreateAuthenticatedClient(otherHost, "PropertyOwner");

        var response = await client.GetAsync("/api/payments");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ids = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .EnumerateArray().Select(p => p.GetProperty("id").GetGuid());
        Assert.DoesNotContain(scenario.PaymentId, ids);
    }

    [Fact]
    public async Task PaymentsList_AsOwner_ReturnsOwnPayments()
    {
        var scenario = await SeedScenarioAsync();
        using var owner = _factory.CreateAuthenticatedClient(scenario.HostId, "PropertyOwner");

        var response = await owner.GetAsync("/api/payments");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ids = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToList();
        Assert.Equal([scenario.PaymentId], ids);
    }

    [Fact]
    public async Task ServiceRequestWrites_AsSameOrgHostNotOwningTheProperty_AreDenied()
    {
        var scenario = await SeedScenarioAsync();
        var colleague = $"auth0|tn3-colleague-{Guid.NewGuid():N}";
        await SeedUserInOrgAsync(colleague, scenario.HostOrgId);
        using var client = _factory.CreateAuthenticatedClient(colleague, "PropertyOwner");

        var create = await client.PostAsJsonAsync("/api/service-requests", CreateRequestBody(scenario));
        var match = await client.PostAsJsonAsync("/api/service-requests/match-supplier", MatchBody(scenario));
        var markPaid = await client.PostAsync($"/api/service-requests/{scenario.ServiceRequestId}/mark-paid", null);

        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, match.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, markPaid.StatusCode);
        await AssertScenarioUnchangedAsync(scenario);
    }

    [Fact]
    public async Task MarkPaid_AsPropertyManagerOfTheOrg_Succeeds()
    {
        var scenario = await SeedScenarioAsync();
        var manager = $"auth0|tn3-manager-{Guid.NewGuid():N}";
        await SeedUserInOrgAsync(manager, scenario.HostOrgId);
        using var client = _factory.CreateAuthenticatedClient(manager, "PropertyOwner,PropertyManager");

        var response = await client.PostAsync($"/api/service-requests/{scenario.ServiceRequestId}/mark-paid", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Pagato", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task SupplierInbox_AsHostWithoutSupplierRole_Returns403()
    {
        var scenario = await SeedScenarioAsync();
        using var host = _factory.CreateAuthenticatedClient(scenario.HostId, "PropertyOwner");

        var response = await host.GetAsync("/api/service-requests?view=supplier");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ServiceRequestDetail_AsAssignedSupplier_Returns200()
    {
        var scenario = await SeedScenarioAsync();
        using var supplier = _factory.CreateAuthenticatedClient(scenario.SupplierUserId, "Supplier");

        var response = await supplier.GetAsync($"/api/service-requests/{scenario.ServiceRequestId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static object CreateRequestBody(Scenario scenario) =>
        new { propertyId = scenario.PropertyId, bookingId = scenario.BookingId, supplierOrgId = scenario.SupplierOrgId, category = "cleaning" };

    private static object MatchBody(Scenario scenario) =>
        new { propertyId = scenario.PropertyId, category = "cleaning" };

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string url, Scenario scenario)
    {
        if (method == "GET")
            return client.GetAsync(url);

        object body = url switch
        {
            _ when url.EndsWith("/match-supplier", StringComparison.Ordinal) => MatchBody(scenario),
            "/api/service-requests" => CreateRequestBody(scenario),
            _ when url.StartsWith("/api/pricing-adapter/config/", StringComparison.Ordinal) =>
                new { isEnabled = true, adaptationFrequency = "daily", includeSeasonality = true, includePublicHolidays = true },
            _ => new { },
        };

        return client.PostAsJsonAsync(url, body);
    }

    private async Task AssertScenarioUnchangedAsync(Scenario scenario)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var payment = await db.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == scenario.PaymentId);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.False(await db.PaymentRefunds.IgnoreQueryFilters().AnyAsync(r => r.PaymentId == scenario.PaymentId));

        var booking = await db.Bookings.IgnoreQueryFilters().AsNoTracking().SingleAsync(b => b.Id == scenario.BookingId);
        Assert.Equal(BookingStatus.Confirmed, booking.Status);

        var request = await db.ServiceRequests.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == scenario.ServiceRequestId);
        Assert.Equal(ServiceRequestStatus.Completato, request.Status);
        Assert.Equal(1, await db.ServiceRequests.IgnoreQueryFilters().CountAsync(r => r.PropertyId == scenario.PropertyId));

        Assert.False(await db.PricingAdapterConfigs.IgnoreQueryFilters().AnyAsync(c => c.PropertyId == scenario.PropertyId));
    }

    private sealed record Scenario(
        string HostId,
        Guid HostOrgId,
        Guid PropertyId,
        Guid GuestId,
        Guid PaymentId,
        Guid BookingId,
        Guid ServiceRequestId,
        Guid SupplierOrgId,
        string SupplierUserId)
    {
        public string Resolve(string route) => route
            .Replace("{property}", PropertyId.ToString())
            .Replace("{guest}", GuestId.ToString())
            .Replace("{payment}", PaymentId.ToString())
            .Replace("{booking}", BookingId.ToString())
            .Replace("{request}", ServiceRequestId.ToString());
    }

    /// <summary>
    /// Host org with one property (comune H501), a guest, a booking with a pending payment, a completed service
    /// request, and an active supplier (its own org) operating in H501.
    /// </summary>
    private async Task<Scenario> SeedScenarioAsync()
    {
        const string comune = "H501";
        var hostId = $"auth0|tn3-host-{Guid.NewGuid():N}";
        var hostOrg = await _factory.SeedOrgForOwnerAsync(hostId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var property = new Property
        {
            OwnerId = hostId,
            OrgId = hostOrg.Id,
            Name = "TN-3 Property",
            Address = $"Via TN3 {Guid.NewGuid():N}",
            City = comune,
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
            OrgId = hostOrg.Id,
            FirstName = "Anna",
            LastName = "Verdi",
            Email = $"tn3-{Guid.NewGuid():N}@example.com",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var booking = new Booking
        {
            PropertyId = property.Id,
            OrgId = hostOrg.Id,
            GuestId = guest.Id,
            CheckInDate = DateTime.UtcNow.Date.AddDays(10),
            CheckOutDate = DateTime.UtcNow.Date.AddDays(12),
            NumberOfGuests = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            BasePrice = 200m,
            TotalPrice = 200m,
        };
        var payment = new Payment
        {
            BookingId = booking.Id,
            OrgId = hostOrg.Id,
            Amount = 200m,
            Status = PaymentStatus.Pending,
            Method = PaymentMethod.BankTransfer,
        };

        var supplierOrg = new OrgEntity
        {
            Name = "TN-3 Supplier",
            Slug = $"tn3-sup-{Guid.NewGuid():N}"[..25],
            DisplayName = "TN-3 Supplier",
            ContactEmail = $"tn3-supplier-{Guid.NewGuid():N}@example.com",
            OrgType = OrgType.Supplier,
            PlanTier = PlanTier.Starter,
        };
        var supplierUserId = $"auth0|tn3-supplier-{Guid.NewGuid():N}";
        var supplierUser = new User
        {
            Id = supplierUserId,
            Email = supplierOrg.ContactEmail,
            FirstName = "Sara",
            LastName = "Fornitrice",
            OrgId = supplierOrg.Id,
            SupplierOrgId = supplierOrg.Id,
            IsActive = true,
        };
        var supplierProfile = new SupplierProfile
        {
            OrgId = supplierOrg.Id,
            Email = supplierOrg.ContactEmail,
            LegalName = "TN-3 Pulizie Srl",
            Phone = "+39 06 000000",
            Status = SupplierStatus.Active,
            ComuniJson = $"[\"{comune}\"]",
            CategoriesJson = "[\"cleaning\"]",
            TosAcceptedAt = DateTime.UtcNow,
        };
        var serviceRequest = new ServiceRequest
        {
            OrgId = hostOrg.Id,
            PropertyId = property.Id,
            SupplierOrgId = supplierOrg.Id,
            Category = "cleaning",
            Status = ServiceRequestStatus.Completato,
            CompletedAt = DateTime.UtcNow,
        };

        db.AddRange(property, guest, booking, payment, supplierOrg, supplierUser, supplierProfile, serviceRequest);
        await db.SaveChangesAsync();

        return new Scenario(
            hostId, hostOrg.Id, property.Id, guest.Id, payment.Id, booking.Id, serviceRequest.Id, supplierOrg.Id, supplierUserId);
    }

    private async Task SeedUserInOrgAsync(string userId, Guid orgId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User
        {
            Id = userId,
            Email = $"{Guid.NewGuid():N}@example.com",
            FirstName = "Collega",
            LastName = "Host",
            OrgId = orgId,
            IsActive = true,
        };
        db.Users.Add(user);
        // A colleague who completed the onboarding for the org (PL-02): the checks below are about permissions and ownership.
        await HostOnboardingSeed.MarkOnboardedAsync(db, user, orgId, scope.ServiceProvider.GetRequiredService<ILegalDocumentService>());
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// A collaborator of the org: a DB membership in short-rent whose role has only the given permissions (a custom
    /// role, as an org admin would configure it), no host role in the JWT context fallback.
    /// </summary>
    private async Task<string> SeedCollaboratorAsync(Guid orgId, params string[] permissions)
    {
        var userId = $"auth0|tn3-collaborator-{Guid.NewGuid():N}";
        await SeedUserInOrgAsync(userId, orgId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var role = new Role
        {
            Id = Random.Shared.Next(100_000, int.MaxValue),
            ContextKey = "short-rent",
            RoleKey = $"tn3_viewer_{Guid.NewGuid():N}",
        };
        foreach (var permission in permissions)
            role.Permissions.Add(new RolePermission { PermissionKey = permission });

        db.Roles.Add(role);
        db.UserContextMemberships.Add(new UserContextMembership { UserId = userId, ContextKey = "short-rent", RoleId = role.Id });
        await db.SaveChangesAsync();
        return userId;
    }
}
