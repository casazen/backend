using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SP-15a and the admin repair <c>POST /api/admin/suppliers/fix-orphaned</c> (SU-14) on PostgreSQL: the payments of the requests
/// of a duplicate supplier profile move to the keeper together with their requests. They name the supplier too (the supplier's
/// Stripe account is read from it) and their foreign key to the org is <c>Restrict</c>, so a payment left behind would keep the
/// duplicate org alive. A class of its own, as <see cref="SupplierAgendaRepairPostgresTests"/>: the repair spans every org of the
/// database and each test drops the unique email index to reproduce the data written before it.
/// </summary>
public class SupplierPaymentsRepairPostgresTests(CasazenWebApplicationFactory factory) : IClassFixture<CasazenWebApplicationFactory>
{
    private static readonly DateTime Older = new(2026, 1, 10, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Newer = new(2026, 3, 10, 9, 0, 0, DateTimeKind.Utc);

    [PostgresFact]
    public async Task FixOrphaned_DuplicateWithAPayment_MovesItWithItsRequest_AndTheDuplicateOrgGoes()
    {
        await DropEmailIndexAsync();
        var email = $"sp15-{Guid.NewGuid():N}@test.com";
        var keeper = await SeedProfileAsync(email, SupplierStatus.Active, Newer);
        var duplicate = await SeedProfileAsync(email, SupplierStatus.Pending, Older);
        var property = await factory.SeedPropertyAsync($"auth0|sp15-host-{Guid.NewGuid():N}");
        var (requestId, paymentId) = await SeedRequestWithPaymentAsync(property, duplicate);

        using var admin = factory.CreateAuthenticatedClient($"auth0|sp15-admin-{Guid.NewGuid():N}", roles: "Admin");
        var response = await admin.PostAsync("/api/admin/suppliers/fix-orphaned?dryRun=false", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<JsonElement>();
        var merge = Assert.Single(
            report.GetProperty("merges").EnumerateArray(),
            m => m.GetProperty("duplicateOrgId").GetGuid() == duplicate);
        Assert.Equal(keeper, merge.GetProperty("keeperOrgId").GetGuid());
        Assert.Equal(1, merge.GetProperty("serviceRequestsMoved").GetInt32());
        Assert.True(merge.GetProperty("duplicateOrgDeleted").GetBoolean());

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var payment = await db.ServiceRequestPayments.AsNoTracking().SingleAsync(p => p.Id == paymentId);
        var request = await db.ServiceRequests.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == requestId);
        Assert.Equal(keeper, request.SupplierOrgId);
        Assert.Equal(keeper, payment.SupplierOrgId);
        Assert.False(await db.Orgs.AnyAsync(o => o.Id == duplicate));
        Assert.Equal(6_000, payment.AmountCents); // the snapshot of the payment is not touched
    }

    // ─── helpers ───

    /// <summary>Data written before the migration SupplierProfileEmailUnique: without its unique index.</summary>
    private async Task DropEmailIndexAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync($"DROP INDEX IF EXISTS \"{SupplierProfileEmailIndex.Name}\"");
    }

    private async Task<Guid> SeedProfileAsync(string email, SupplierStatus status, DateTime createdAt)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = new OrgEntity
        {
            Name = "Fornitore SP-15",
            Slug = $"sp15-{Guid.NewGuid():N}",
            DisplayName = "Fornitore SP-15",
            ContactEmail = email,
            OrgType = OrgType.Supplier,
            PlanTier = PlanTier.Starter,
        };
        db.Orgs.Add(org);
        db.SupplierProfiles.Add(new SupplierProfile
        {
            OrgId = org.Id,
            Email = email,
            LegalName = "Fornitore SP-15 Srl",
            Phone = "+39 06 151515",
            Status = status,
            CategoriesJson = "[]",
            ComuniJson = """["H501"]""",
            TosAcceptedAt = status == SupplierStatus.Active ? createdAt : null,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        });
        await db.SaveChangesAsync();
        return org.Id;
    }

    private async Task<(Guid RequestId, Guid PaymentId)> SeedRequestWithPaymentAsync(Property property, Guid supplierOrgId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var request = new ServiceRequest
        {
            OrgId = property.OrgId,
            PropertyId = property.Id,
            SupplierOrgId = supplierOrgId,
            Category = "cleaning",
            Status = ServiceRequestStatus.Completato,
            // Long-rent: tied to the property only, no stay needed (D2).
            RentalContext = ServiceRequestRentalContext.LongRent,
            CompletedAt = DateTime.UtcNow,
            FinalAmountCents = 6_000,
            PaymentMode = ServiceRequestPaymentMode.Online,
        };
        db.ServiceRequests.Add(request);
        await db.SaveChangesAsync();

        var payment = new ServiceRequestPayment
        {
            ServiceRequestId = request.Id,
            SupplierOrgId = supplierOrgId,
            PayerKind = ServicePayerKind.Host,
            PayerOrgId = property.OrgId,
            AmountCents = 6_000,
            CommissionPercent = 10m,
            ApplicationFeeCents = 600,
            NetCents = 5_400,
            Status = ServicePaymentStatus.Requested,
        };
        db.ServiceRequestPayments.Add(payment);
        await db.SaveChangesAsync();
        return (request.Id, payment.Id);
    }
}
