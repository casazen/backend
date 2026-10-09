using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Encryption;
using Casazen.Tests.Integration.Postgres;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SP-10 on a real PostgreSQL database: the personal data of a customer of a supplier's showcase are stored encrypted — the
/// database never holds the name, the e-mail, the phone, the street address, the floor or the access notes in clear, nor the
/// payload of the booking that waits for the e-mail check — and read back through EF; values written in clear (a deploy that
/// stopped half way, a restore) are encrypted by the startup step like every other encrypted column. What the supplier sees
/// before it takes the request (the comune and the postal code) and what finds a customer (the HMAC of the address) are not
/// encrypted. Same style as <see cref="FieldEncryptionPostgresTests"/>.
/// </summary>
public class ShowcaseBookingEncryptionPostgresTests
{
    private const string FullName = "Mario Rossi";
    private const string Email = "mario.rossi@example.com";
    private const string Phone = "+393331234567";
    private const string Address = "Via Segretissima 7";
    private const string Floor = "Piano 3, interno 7";
    private const string AccessNotes = "Citofono Rossi, chiavi nella cassetta";
    private const string Payload = """{"fullName":"Mario Rossi","email":"mario.rossi@example.com","address":"Via Segretissima 7"}""";

    private static readonly string[] Secrets = [FullName, Email, Phone, Address, Floor, AccessNotes, "Segretissima", "Citofono"];

    [PostgresFact]
    public async Task SaveChanges_TheCustomerTheHoldAndThePlace_TheDatabaseNeverHoldsTheClearValues()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("sp10enc");
        var provider = new EphemeralDataProtectionProvider();
        var seed = await SeedAsync(database, provider, encrypted: true);

        // A context without Data Protection reads the stored text.
        await using var stored = database.CreateContext();
        var customer = await stored.ServiceCustomers.AsNoTracking().SingleAsync(c => c.Id == seed.CustomerId);
        var hold = await stored.ShowcaseBookingHolds.AsNoTracking().SingleAsync(h => h.Id == seed.HoldId);
        var request = await stored.ServiceRequests.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == seed.RequestId);

        var columns = new[] { customer.FullName, customer.Email, customer.Phone!, hold.Payload!, request.LocationAddress!, request.LocationFloor!, request.LocationAccessNotes! };
        Assert.All(columns, column =>
        {
            Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, column, StringComparison.Ordinal);
            foreach (var secret in Secrets)
                Assert.DoesNotContain(secret, column, StringComparison.OrdinalIgnoreCase);
        });

        // Not encrypted: what the supplier sees before the take, and the index the customer is found by.
        Assert.Equal("Monza", request.LocationCity);
        Assert.Equal("20900", request.LocationPostalCode);
        Assert.Matches("^[0-9a-f]{64}$", customer.EmailHash);

        // And in the table itself, whatever EF would have made of it.
        var names = await stored.Database.SqlQueryRaw<string>("SELECT \"FullName\" AS \"Value\" FROM \"ServiceCustomers\"").ToListAsync();
        var payloads = await stored.Database.SqlQueryRaw<string>("SELECT \"PayloadEncrypted\" AS \"Value\" FROM \"ShowcaseBookingHolds\"").ToListAsync();
        var streets = await stored.Database.SqlQueryRaw<string>("SELECT \"LocationAddress\" AS \"Value\" FROM \"ServiceRequests\" WHERE \"LocationAddress\" IS NOT NULL").ToListAsync();
        Assert.All(names.Concat(payloads).Concat(streets), value => Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, value, StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task Read_TheEncryptedColumns_RoundTripThroughEf()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("sp10enc");
        var provider = new EphemeralDataProtectionProvider();
        var seed = await SeedAsync(database, provider, encrypted: true);

        await using var db = new AppDbContext(database.CreateOptions(), tenantContext: null, provider);
        var customer = await db.ServiceCustomers.AsNoTracking().SingleAsync(c => c.Id == seed.CustomerId);
        var hold = await db.ShowcaseBookingHolds.AsNoTracking().SingleAsync(h => h.Id == seed.HoldId);
        var request = await db.ServiceRequests.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == seed.RequestId);

        Assert.Equal((FullName, Email, Phone), (customer.FullName, customer.Email, customer.Phone));
        Assert.Equal(Payload, hold.Payload);
        Assert.Equal((Address, Floor, AccessNotes), (request.LocationAddress, request.LocationFloor, request.LocationAccessNotes));
    }

    [PostgresFact]
    public async Task Read_AnEmptyOrMissingValue_StaysEmptyOrMissing()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("sp10enc");
        var provider = new EphemeralDataProtectionProvider();
        var seed = await SeedAsync(database, provider, encrypted: true, withoutOptionals: true);

        await using var db = new AppDbContext(database.CreateOptions(), tenantContext: null, provider);
        var customer = await db.ServiceCustomers.AsNoTracking().SingleAsync(c => c.Id == seed.CustomerId);
        var hold = await db.ShowcaseBookingHolds.AsNoTracking().SingleAsync(h => h.Id == seed.HoldId);
        var request = await db.ServiceRequests.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == seed.RequestId);

        Assert.Null(customer.Phone);
        Assert.Null(hold.Payload);
        Assert.Null(request.LocationFloor);
        Assert.Null(request.LocationAccessNotes);
        Assert.Equal(Address, request.LocationAddress);
    }

    [PostgresFact]
    public async Task Startup_ValuesStoredInClear_AreEncryptedOnceByTheStartupStep()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("sp10enc");
        var provider = new EphemeralDataProtectionProvider();
        var seed = await SeedAsync(database, provider, encrypted: false);

        await using (var stored = database.CreateContext())
        {
            Assert.Equal(FullName, (await stored.ServiceCustomers.AsNoTracking().SingleAsync(c => c.Id == seed.CustomerId)).FullName);
            Assert.Equal(Address, (await stored.ServiceRequests.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == seed.RequestId)).LocationAddress);
        }

        // Before the step the clear values are readable as they are (the legacy rule), so nothing breaks while it has not run.
        await using (var reader = new AppDbContext(database.CreateOptions(), tenantContext: null, provider))
            Assert.Equal(Email, (await reader.ServiceCustomers.AsNoTracking().SingleAsync(c => c.Id == seed.CustomerId)).Email);

        int rewritten;
        await using (var db = new AppDbContext(database.CreateOptions(), tenantContext: null, provider))
            rewritten = await EncryptedColumns.EncryptLegacyPlaintextAsync(db, NullLogger.Instance);

        Assert.True(rewritten >= 3, $"Rewritten {rewritten}: the customer, the hold and the request are expected");
        await using var after = database.CreateContext();
        var customer = await after.ServiceCustomers.AsNoTracking().SingleAsync(c => c.Id == seed.CustomerId);
        var hold = await after.ShowcaseBookingHolds.AsNoTracking().SingleAsync(h => h.Id == seed.HoldId);
        var request = await after.ServiceRequests.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == seed.RequestId);
        Assert.All(
            new[] { customer.FullName, customer.Email, customer.Phone!, hold.Payload!, request.LocationAddress!, request.LocationFloor!, request.LocationAccessNotes! },
            column => Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, column, StringComparison.Ordinal));

        // Idempotent: a second run has nothing left to do.
        await using var again = new AppDbContext(database.CreateOptions(), tenantContext: null, provider);
        Assert.Equal(0, await EncryptedColumns.EncryptLegacyPlaintextAsync(again, NullLogger.Instance));
    }

    // ─── helpers ───

    private sealed record Seed(Guid OrgId, Guid CustomerId, Guid RequestId, Guid HoldId);

    /// <summary>A supplier with a customer, a showcase request and a hold, written with or without the encryption.</summary>
    private static async Task<Seed> SeedAsync(
        PostgresTestDatabase database,
        IDataProtectionProvider provider,
        bool encrypted,
        bool withoutOptionals = false)
    {
        await using var db = encrypted
            ? new AppDbContext(database.CreateOptions(), tenantContext: null, provider)
            : database.CreateContext();

        var org = new OrgEntity
        {
            Name = "Fornitore SP-10",
            Slug = $"sp10-enc-{Guid.NewGuid():N}"[..28],
            DisplayName = "Fornitore SP-10",
            ContactEmail = "enc-sp10@example.com",
            OrgType = OrgType.Supplier,
            PlanTier = PlanTier.Starter,
        };
        db.Orgs.Add(org);
        db.SupplierProfiles.Add(new SupplierProfile
        {
            OrgId = org.Id,
            Email = $"sp10-enc-{Guid.NewGuid():N}@example.com",
            LegalName = "Fornitore SP-10 Srl",
            Phone = "+39 06 101010",
            Status = SupplierStatus.Active,
            ComuniJson = "[\"Monza\"]",
            CategoriesJson = "[\"cleaning\"]",
        });

        var at = new DateTime(2026, 10, 12, 6, 45, 0, DateTimeKind.Utc);
        var customer = new ServiceCustomer
        {
            OrgId = org.Id,
            EmailHash = new string('c', 64),
            FullName = FullName,
            Email = Email,
            Phone = withoutOptionals ? null : Phone,
            Locale = "it",
            PrivacyNoticeVersion = "2026-11-test",
            PrivacyAcceptedAt = at,
            ConsentIp = "203.0.113.7",
            CreatedAt = at,
            UpdatedAt = at,
        };
        var request = new ServiceRequest
        {
            OrgId = org.Id,
            SupplierOrgId = org.Id,
            RentalContext = ServiceRequestRentalContext.Showcase,
            Source = ServiceRequestSource.Showcase,
            Category = ServiceCategories.Cleaning,
            CustomerId = customer.Id,
            PublicCode = "ABCDEFGHJK",
            LocationCity = "Monza",
            LocationPostalCode = "20900",
            LocationAddress = Address,
            LocationFloor = withoutOptionals ? null : Floor,
            LocationAccessNotes = withoutOptionals ? null : AccessNotes,
            CreatedAt = at,
            UpdatedAt = at,
        };
        var hold = new ShowcaseBookingHold
        {
            OrgId = org.Id,
            ClientRequestId = Guid.NewGuid(),
            StartUtc = at.AddDays(1),
            EndUtc = at.AddDays(1).AddHours(2),
            PublicCode = "KJHGFEDCBA",
            TokenHash = new string('a', 64),
            EmailHash = new string('c', 64),
            Payload = withoutOptionals ? null : Payload,
            ExpiresAt = at.AddMinutes(30),
            CreatedAt = at,
        };
        db.AddRange(customer, request, hold);
        await db.SaveChangesAsync();
        return new Seed(org.Id, customer.Id, request.Id, hold.Id);
    }
}
