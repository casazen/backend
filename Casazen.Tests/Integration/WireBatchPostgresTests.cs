using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using static Casazen.Tests.Unit.Services.WireTestData;

namespace Casazen.Tests.Integration;

/// <summary>
/// E16/F01/US01/T01 on a real PostgreSQL database, where the rule that matters for the money is enforced: a payout is paid by one batch only, ever (the unique index
/// on <c>WireBatchPayouts.OwnerPayoutId</c> and the lock of the owner accounting of the org), whatever two requests do at the same moment. The unit tests run on the
/// in-memory database, which applies no unique index and takes no advisory lock, so they cannot show it. Same style as
/// <see cref="BookingInvoiceDataEncryptionPostgresTests"/>.
/// </summary>
public class WireBatchPostgresTests
{
    /// <summary>
    /// PROOF TEST: money rule. Two requests for the same payouts at the same moment: the batch is made under the lock of the owner accounting of the org, so one
    /// request makes the batch and the other, which waits for it, finds its payouts in it and is refused (422 <c>payouts_already_in_batch</c>). There is one batch,
    /// with one row for each payout, and no payout is paid twice.
    /// </summary>
    [PostgresFact]
    public async Task WireBatch_TwoRequestsForTheSamePayouts_MakeOneBatch_Postgres_Test()
    {
        await using var database = await PostgresTestDatabase.CreateMigratedAsync("wirebatch");
        var provider = new EphemeralDataProtectionProvider();
        var orgId = Guid.NewGuid();
        await AddOrgAsync(database, provider, orgId);
        await using var db = NewDb(orgId, options: database.CreateOptions(), provider: provider);
        await AddIssuerAsync(db, orgId);
        var payouts = await AddTwoPayoutsAsync(db, orgId);

        await using var first = NewDb(orgId, options: database.CreateOptions(), provider: provider);
        await using var second = NewDb(orgId, options: database.CreateOptions(), provider: provider);
        var outcomes = await Task.WhenAll(
            AttemptAsync(NewService(first), orgId, payouts),
            AttemptAsync(NewService(second), orgId, payouts));

        Assert.Single(outcomes, outcome => outcome is null);
        var refused = Assert.Single(outcomes, outcome => outcome is not null);
        Assert.Equal(WireTransferErrors.PayoutsAlreadyInBatch, refused!.Code);

        var batch = Assert.Single(await db.WireBatches.IgnoreQueryFilters().AsNoTracking().ToListAsync());
        var links = await db.WireBatchPayouts.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        Assert.Equal(2, batch.TransferCount);
        Assert.Equal(payouts.Order(), links.Select(link => link.OwnerPayoutId).Order());
        Assert.All(links, link => Assert.Equal(batch.Id, link.WireBatchId));
    }

    /// <summary>
    /// PROOF TEST: money rule. The unique index on the payout is the backstop of the lock: another batch takes the same payouts after the checks of a request and
    /// before its save (the competing batch is committed from another connection, in the middle of the save of the first). The database refuses the second row of the
    /// payout, the request is 422 <c>payouts_already_in_batch</c> (not a 500) and nothing of it is left behind: the one batch is the competing one, with one row for
    /// each payout.
    /// </summary>
    [PostgresFact]
    public async Task WireBatch_PayoutTakenByAnotherBatchMeanwhile_IsRefusedByTheUniqueIndex_Postgres_Test()
    {
        await using var database = await PostgresTestDatabase.CreateMigratedAsync("wirebatch");
        var provider = new EphemeralDataProtectionProvider();
        var orgId = Guid.NewGuid();
        await AddOrgAsync(database, provider, orgId);
        await using var db = NewDb(orgId, options: database.CreateOptions(), provider: provider);
        await AddIssuerAsync(db, orgId);
        var payouts = await AddTwoPayoutsAsync(db, orgId);

        var competing = new CompetingBatchInterceptor(database.CreateOptions(), provider);
        var intercepted = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(database.ConnectionString, npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"))
            .AddInterceptors(competing)
            .Options;
        await using var requestDb = NewDb(orgId, options: intercepted, provider: provider);

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => NewService(requestDb).CreateBatchAsync(orgId, payouts, ExecutionDate));

        Assert.Equal(WireTransferErrors.PayoutsAlreadyInBatch, refused.Code);
        Assert.NotEqual(Guid.Empty, competing.CompetingBatchId);
        var batch = Assert.Single(await db.WireBatches.IgnoreQueryFilters().AsNoTracking().ToListAsync());
        Assert.Equal(competing.CompetingBatchId, batch.Id);
        var links = await db.WireBatchPayouts.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        Assert.Equal(payouts.Order(), links.Select(link => link.OwnerPayoutId).Order());
        Assert.All(links, link => Assert.Equal(batch.Id, link.WireBatchId));
    }

    private static async Task<DomainRuleException?> AttemptAsync(WireTransferService service, Guid orgId, IReadOnlyList<Guid> payouts)
    {
        try
        {
            await service.CreateBatchAsync(orgId, payouts, ExecutionDate);
            return null;
        }
        catch (DomainRuleException refused)
        {
            return refused;
        }
    }

    /// <summary>The org the rows belong to: the tables hold its id as a foreign key, which PostgreSQL enforces.</summary>
    private static async Task AddOrgAsync(PostgresTestDatabase database, IDataProtectionProvider provider, Guid orgId)
    {
        await using var db = new AppDbContext(database.CreateOptions(), tenantContext: null, provider);
        db.Orgs.Add(new OrgEntity
        {
            Id = orgId,
            Name = "Casa Bonifici",
            Slug = $"wire-{orgId:N}"[..28],
            DisplayName = "Casa Bonifici",
            ContactEmail = "wire@example.com",
            OrgType = OrgType.Host,
            PlanTier = PlanTier.Agency,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Another request that wins the race: when the request under test is about to save a batch, it commits, from another connection, a batch that pays the same
    /// payouts (the rows the request is about to add, copied), once. The save of the request then meets the unique index of the payout.
    /// </summary>
    private sealed class CompetingBatchInterceptor(DbContextOptions<AppDbContext> otherOptions, IDataProtectionProvider provider) : SaveChangesInterceptor
    {
        private int fired;

        public Guid CompetingBatchId { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var added = eventData.Context!.ChangeTracker.Entries<WireBatchPayout>()
                .Where(entry => entry.State == EntityState.Added)
                .Select(entry => entry.Entity)
                .ToList();
            if (added.Count == 0 || Interlocked.Exchange(ref fired, 1) == 1)
            {
                return result;
            }

            var batch = new WireBatch
            {
                OrgId = added[0].OrgId,
                Currency = "CHF",
                DebitAccount = DebitIban,
                TotalAmount = added.Sum(link => link.Amount),
                TransferCount = added.Count,
                Status = WireBatchStatus.Generated,
                CreatedAt = Now.UtcDateTime,
                XmlContent = "<Document/>",
            };
            await using var other = new AppDbContext(otherOptions, tenantContext: null, provider);
            other.WireBatches.Add(batch);
            foreach (var link in added)
            {
                other.WireBatchPayouts.Add(new WireBatchPayout
                {
                    OrgId = link.OrgId,
                    WireBatchId = batch.Id,
                    OwnerPayoutId = link.OwnerPayoutId,
                    PropertyOwnerId = link.PropertyOwnerId,
                    Amount = link.Amount,
                    EndToEndId = link.EndToEndId,
                    CreatedAt = Now.UtcDateTime,
                });
            }

            await other.SaveChangesAsync(cancellationToken);
            CompetingBatchId = batch.Id;
            return result;
        }
    }
}
