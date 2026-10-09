using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SP-10 and the admin repair <c>POST /api/admin/suppliers/fix-orphaned</c> (SU-14) on PostgreSQL: the public showcase of a
/// duplicate supplier profile goes to the keeper before the profile is deleted. Its requests (their <c>OrgId</c> is the supplier
/// org itself, which would stay behind and keep the org alive) and its private customers move; a customer the keeper already
/// has (the same e-mail) is merged into the keeper's, with its requests; the unverified holds of the duplicate are dropped; a dry
/// run reports the move and changes nothing. A class of its own, like <see cref="SupplierAgendaRepairPostgresTests"/>: the repair
/// spans every org of the database and each test drops the unique e-mail index to reproduce the data written before it.
/// </summary>
public class ShowcaseBookingRepairPostgresTests(CasazenWebApplicationFactory factory) : IClassFixture<CasazenWebApplicationFactory>
{
    private static readonly DateTime Older = new(2026, 1, 10, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Newer = new(2026, 3, 10, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime At = new(2026, 10, 12, 6, 45, 0, DateTimeKind.Utc);

    [PostgresFact]
    public async Task FixOrphaned_DuplicateWithAShowcase_MovesItsCustomersAndRequests_AndDropsItsHolds()
    {
        await DropEmailIndexAsync();
        var email = $"sp10-{Guid.NewGuid():N}@test.com";
        var keeper = await SeedProfileAsync(email, SupplierStatus.Active, Newer);
        var duplicate = await SeedProfileAsync(email, SupplierStatus.Pending, Older);
        await SeedShowcaseAsync(duplicate, "uno@example.com", "due@example.com");

        // The dry run reports the move and changes nothing.
        var dry = await FixOrphanedAsync(dryRun: true);
        Assert.Equal(HttpStatusCode.OK, dry.StatusCode);
        Assert.Equal(2 + 2, SingleMerge(await ReadAsync(dry), duplicate).GetProperty("showcaseRowsMoved").GetInt32());
        Assert.Equal((2, 2, 1), await CountsAsync(duplicate));
        Assert.Equal((0, 0, 0), await CountsAsync(keeper));

        var applied = await FixOrphanedAsync(dryRun: false);

        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        var merge = SingleMerge(await ReadAsync(applied), duplicate);
        Assert.Equal(keeper, merge.GetProperty("keeperOrgId").GetGuid());
        Assert.Equal(4, merge.GetProperty("showcaseRowsMoved").GetInt32());
        Assert.True(merge.GetProperty("duplicateOrgDeleted").GetBoolean());

        // Nothing is lost with the duplicate profile (the cascade would have taken what stayed): every customer and request is the
        // keeper's, the holds of the duplicate are gone, and each request still points at a customer of the keeper.
        Assert.False(await ProfileExistsAsync(duplicate));
        Assert.Equal((0, 0, 0), await CountsAsync(duplicate));
        Assert.Equal((2, 2, 0), await CountsAsync(keeper));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var requests = await db.ServiceRequests.IgnoreQueryFilters().AsNoTracking().Where(r => r.OrgId == keeper).ToListAsync();
        var customers = await db.ServiceCustomers.AsNoTracking().Where(c => c.OrgId == keeper).ToListAsync();
        Assert.All(requests, request =>
        {
            Assert.Equal(keeper, request.SupplierOrgId);
            Assert.Contains(customers, customer => customer.Id == request.CustomerId);
        });
        Assert.Contains(customers, c => c.Email == "uno@example.com" && c.FullName == "Mario Rossi");
    }

    [PostgresFact]
    public async Task FixOrphaned_ACustomerBothSidesHave_IsMergedIntoTheKeepersCustomer_WithItsRequests()
    {
        await DropEmailIndexAsync();
        var email = $"sp10-{Guid.NewGuid():N}@test.com";
        var keeper = await SeedProfileAsync(email, SupplierStatus.Active, Newer);
        var duplicate = await SeedProfileAsync(email, SupplierStatus.Pending, Older);
        // The same person booked both: "uno@example.com" is a customer of each. "due@example.com" is only the duplicate's.
        var keepersCustomer = (await SeedShowcaseAsync(keeper, "uno@example.com")).Customers.Single();
        await SeedShowcaseAsync(duplicate, "uno@example.com", "due@example.com");

        var applied = await FixOrphanedAsync(dryRun: false);

        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        // Moved: its 2 requests, the customer only it has, and the one that was merged (its row is gone).
        Assert.Equal(4, SingleMerge(await ReadAsync(applied), duplicate).GetProperty("showcaseRowsMoved").GetInt32());
        Assert.Equal((2, 3, 1), await CountsAsync(keeper)); // its own hold stays, the duplicate's was dropped
        Assert.Equal((0, 0, 0), await CountsAsync(duplicate));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var customers = await db.ServiceCustomers.AsNoTracking().Where(c => c.OrgId == keeper).ToListAsync();
        Assert.Equal(2, customers.Count);
        var requests = await db.ServiceRequests.IgnoreQueryFilters().AsNoTracking().Where(r => r.OrgId == keeper).ToListAsync();
        // The same address is one customer: two of the three requests are its.
        Assert.Equal(2, requests.Count(r => r.CustomerId == keepersCustomer.Id));
        Assert.All(requests, request => Assert.Contains(customers, customer => customer.Id == request.CustomerId));
    }

    [PostgresFact]
    public async Task FixOrphaned_DuplicateWithoutAShowcase_MergesAsBeforeAndReportsNone()
    {
        await DropEmailIndexAsync();
        var email = $"sp10-{Guid.NewGuid():N}@test.com";
        var keeper = await SeedProfileAsync(email, SupplierStatus.Active, Newer);
        var duplicate = await SeedProfileAsync(email, SupplierStatus.Pending, Older);
        await SeedShowcaseAsync(keeper, "uno@example.com");

        var applied = await FixOrphanedAsync(dryRun: false);

        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        var merge = SingleMerge(await ReadAsync(applied), duplicate);
        Assert.Equal(0, merge.GetProperty("showcaseRowsMoved").GetInt32());
        Assert.True(merge.GetProperty("duplicateOrgDeleted").GetBoolean());
        // The keeper's own showcase is untouched.
        Assert.Equal((1, 1, 1), await CountsAsync(keeper));
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private static JsonElement SingleMerge(JsonElement report, Guid duplicateOrgId) =>
        Assert.Single(
            report.GetProperty("merges").EnumerateArray(),
            m => m.GetProperty("duplicateOrgId").GetGuid() == duplicateOrgId);

    private async Task<HttpResponseMessage> FixOrphanedAsync(bool dryRun)
    {
        using var admin = factory.CreateAuthenticatedClient($"auth0|sp10-admin-{Guid.NewGuid():N}", roles: "Admin");
        return await admin.PostAsync($"/api/admin/suppliers/fix-orphaned?dryRun={(dryRun ? "true" : "false")}", null);
    }

    /// <summary>Data written before the migration SupplierProfileEmailUnique: without its unique index.</summary>
    private async Task DropEmailIndexAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync($"DROP INDEX IF EXISTS \"{SupplierProfileEmailIndex.Name}\"");
    }

    private async Task<bool> ProfileExistsAsync(Guid orgId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().SupplierProfiles.AnyAsync(sp => sp.OrgId == orgId);
    }

    /// <summary>The customers, the showcase requests (their <c>OrgId</c>) and the holds of a supplier org.</summary>
    private async Task<(int Customers, int Requests, int Holds)> CountsAsync(Guid orgId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (
            await db.ServiceCustomers.CountAsync(c => c.OrgId == orgId),
            await db.ServiceRequests.IgnoreQueryFilters().CountAsync(r => r.OrgId == orgId && r.RentalContext == ServiceRequestRentalContext.Showcase),
            await db.ShowcaseBookingHolds.CountAsync(h => h.OrgId == orgId));
    }

    private async Task<Guid> SeedProfileAsync(string email, SupplierStatus status, DateTime createdAt)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = new OrgEntity
        {
            Name = "Fornitore SP-10",
            Slug = $"sp10-{Guid.NewGuid():N}",
            DisplayName = "Fornitore SP-10",
            ContactEmail = email,
            OrgType = OrgType.Supplier,
            PlanTier = PlanTier.Starter,
        };
        db.Orgs.Add(org);
        db.SupplierProfiles.Add(new SupplierProfile
        {
            OrgId = org.Id,
            Email = email,
            LegalName = "Fornitore SP-10 Srl",
            Phone = "+39 06 101010",
            Status = status,
            CategoriesJson = "[]",
            ComuniJson = """["Monza"]""",
            TosAcceptedAt = status == SupplierStatus.Active ? createdAt : null,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        });
        await db.SaveChangesAsync();
        return org.Id;
    }

    private sealed record ShowcaseSeed(IReadOnlyList<ServiceCustomer> Customers, IReadOnlyList<ServiceRequest> Requests);

    /// <summary>A customer per address, a showcase request for each, and one hold, of <paramref name="orgId"/> (encrypted as in production).</summary>
    private async Task<ShowcaseSeed> SeedShowcaseAsync(Guid orgId, params string[] addresses)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var customers = addresses.Select(address => new ServiceCustomer
        {
            OrgId = orgId,
            // Any 64 hexadecimal characters stand for the HMAC of the address; the same address gives the same text.
            EmailHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(address))).ToLowerInvariant(),
            FullName = "Mario Rossi",
            Email = address,
            Phone = "+393331234567",
            Locale = "it",
            PrivacyNoticeVersion = "2026-11-test",
            PrivacyAcceptedAt = At,
            ConsentIp = "203.0.113.7",
            CreatedAt = At,
            UpdatedAt = At,
        }).ToList();
        var requests = customers.Select(customer => new ServiceRequest
        {
            OrgId = orgId,
            SupplierOrgId = orgId,
            RentalContext = ServiceRequestRentalContext.Showcase,
            Source = ServiceRequestSource.Showcase,
            Category = ServiceCategories.Cleaning,
            CustomerId = customer.Id,
            PublicCode = BookingCodes.New(),
            LocationCity = "Monza",
            LocationPostalCode = "20900",
            LocationAddress = "Via Segretissima 7",
            Status = ServiceRequestStatus.Richiesto,
            CreatedAt = At,
            UpdatedAt = At,
        }).ToList();
        db.ServiceCustomers.AddRange(customers);
        db.ServiceRequests.AddRange(requests);
        db.ShowcaseBookingHolds.Add(new ShowcaseBookingHold
        {
            OrgId = orgId,
            ClientRequestId = Guid.NewGuid(),
            StartUtc = At.AddDays(1),
            EndUtc = At.AddDays(1).AddHours(2),
            PublicCode = BookingCodes.New(),
            TokenHash = new string('a', 64),
            EmailHash = new string('b', 64),
            Payload = """{"fullName":"Mario Rossi"}""",
            ExpiresAt = At.AddMinutes(30),
            CreatedAt = At,
        });
        await db.SaveChangesAsync();
        return new ShowcaseSeed(customers, requests);
    }
}
