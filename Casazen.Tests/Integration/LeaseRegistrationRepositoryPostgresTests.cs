using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Repositories;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// RLI registration reservation semantics on real PostgreSQL (unique index + conditional claim).
/// Runs on a throw-away, fully migrated database from <see cref="PostgresTestServer"/>
/// (<c>TEST_POSTGRES_CONNECTION</c> or Testcontainers); skipped with a reason only when no
/// PostgreSQL is available on a local run.
/// </summary>
public class LeaseRegistrationRepositoryPostgresTests : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    private AppDbContext NewContext() => _database!.CreateContext();

    [PostgresFact]
    public async Task TryReserveSubmissionAsync_WhenLeaseAlreadyHasRegistration_ReturnsFalse()
    {
        var leaseId = await SeedSignedLeaseAsync();

        await using (var first = NewContext())
        {
            var reserved = await new LeaseRegistrationRepository(first).TryReserveSubmissionAsync(
                new LeaseRegistration { LeaseContractId = leaseId, Status = RegistrationStatus.Pending });
            Assert.True(reserved);
        }

        await using var second = NewContext();
        var duplicate = await new LeaseRegistrationRepository(second).TryReserveSubmissionAsync(
            new LeaseRegistration { LeaseContractId = leaseId, Status = RegistrationStatus.Pending });

        Assert.False(duplicate);
        Assert.Equal(1, await second.LeaseRegistrations.CountAsync(r => r.LeaseContractId == leaseId));
    }

    [PostgresFact]
    public async Task TryReserveRetryAsync_WhenTwoRequestsRetrySameFailedRegistration_OnlyOneClaims()
    {
        var leaseId = await SeedSignedLeaseAsync();
        Guid registrationId;
        await using (var seed = NewContext())
        {
            var failed = new LeaseRegistration
            {
                LeaseContractId = leaseId,
                Status = RegistrationStatus.Failed,
                ExternalRegistrationId = "RLI-OLD",
            };
            seed.LeaseRegistrations.Add(failed);
            await seed.SaveChangesAsync();
            registrationId = failed.Id;
        }

        await using var contextA = NewContext();
        await using var contextB = NewContext();
        var rowA = await contextA.LeaseRegistrations.SingleAsync(r => r.Id == registrationId);
        var rowB = await contextB.LeaseRegistrations.SingleAsync(r => r.Id == registrationId);

        var claimedA = await new LeaseRegistrationRepository(contextA).TryReserveRetryAsync(rowA);
        var claimedB = await new LeaseRegistrationRepository(contextB).TryReserveRetryAsync(rowB);

        Assert.True(claimedA);
        Assert.False(claimedB);
        Assert.Equal(RegistrationStatus.Pending, rowA.Status);

        await using var verify = NewContext();
        var stored = await verify.LeaseRegistrations.SingleAsync(r => r.Id == registrationId);
        Assert.Equal(RegistrationStatus.Pending, stored.Status);
    }

    private async Task<Guid> SeedSignedLeaseAsync()
    {
        await using var db = NewContext();
        var org = new Casazen.Core.Entities.Org
        {
            Name = "Org LTR",
            Slug = $"org-ltr-{Guid.NewGuid():N}",
            DisplayName = "Org LTR",
        };
        var property = new Property
        {
            OwnerId = "auth0|ltr-owner",
            OrgId = org.Id,
            Name = "Casa",
            Address = "Via Roma 1",
            City = "Seveso",
        };
        var lease = new LeaseContract
        {
            PropertyId = property.Id,
            OrgId = org.Id,
            Status = LeaseStatus.Signed,
            FiscalRegime = FiscalRegime.CedolareSecca,
            StartDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2030, 8, 31, 0, 0, 0, DateTimeKind.Utc),
            MonthlyRent = 800m,
            RegistrationDeadline = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            DataRetentionUntil = new DateTime(2036, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            SignedPdfStoragePath = "/signed/lease.pdf",
        };
        db.Orgs.Add(org);
        db.Properties.Add(property);
        db.LeaseContracts.Add(lease);
        await db.SaveChangesAsync();
        return lease.Id;
    }
}
