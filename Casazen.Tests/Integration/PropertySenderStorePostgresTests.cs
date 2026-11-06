using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// The data rule of the sender store (E03/F01/US05/T02, ADR-029-R1, ADR-029-R5, NF-49), on a real PostgreSQL database where the unique
/// index, the check constraint and the tenant filter act together: a property never has two senders. <c>SetAsync</c> writes the
/// Alloggiati and the ISTAT sender as one value; two first writes at the same moment leave one row (the loser re-reads it); a property of
/// another org, or outside Italy, is never given a sender.
/// </summary>
public class PropertySenderStorePostgresTests
{
    private const string UserA = "auth0|sender-admin-a";
    private const string UserB = "auth0|sender-admin-b";

    [PostgresFact]
    public async Task PropertySenderStore_NeverTwoSenders_Test()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("sendstore");
        var orgA = await SeedOrgAsync(database);
        var orgB = await SeedOrgAsync(database);
        var first = await SeedPropertyAsync(database, orgA, "IT", 1);
        var raced = await SeedPropertyAsync(database, orgA, "IT", 2);
        var swiss = await SeedPropertyAsync(database, orgA, "CH", 3);
        var foreign = await SeedPropertyAsync(database, orgB, "IT", 4);

        // One value for both senders, from the default (manual: no confirmed listing) to the one chosen, with its line.
        await using (var db = TenantContext(database, orgA))
        {
            var set = await Store(db).SetAsync(first, ComplianceSenderType.CasaZen, UserA, CancellationToken.None);
            Assert.Equal((ComplianceSenderType.CasaZen, ComplianceSenderType.CasaZen, SenderSource.Stored), (set.AlloggiatiSender, set.IstatSender, set.Source));

            // The same sender again: nothing more is written.
            await Store(db).SetAsync(first, ComplianceSenderType.CasaZen, UserA, CancellationToken.None);
        }

        await using (var verify = database.CreateContext())
        {
            var row = await verify.PropertyComplianceSenders.AsNoTracking().SingleAsync(s => s.PropertyId == first);
            Assert.Equal((ComplianceSenderType.CasaZen, ComplianceSenderType.CasaZen), (row.AlloggiatiSender, row.IstatSender));
            var line = await verify.OrgActivityEntries.AsNoTracking().SingleAsync(e => e.SubjectId == first.ToString());
            Assert.Equal(OrgActivityType.PropertySenderChanged, line.Type);
            Assert.Equal(
                new Dictionary<string, string> { [OrgActivityDetailKeys.FromSender] = "Manual", [OrgActivityDetailKeys.ToSender] = "CasaZen" },
                OrgActivityDetails.Parse(line.DetailsJson));
        }

        // Two first writes at the same moment, with two senders that both change the default (manual): both read no row, both insert with
        // their line, the unique index keeps one. The loser re-reads the row, which holds the other sender, and is refused: one row, one
        // line, one coherent pair.
        var race = new SenderSaveRendezvous();
        var outcomes = await Task.WhenAll(
            OutcomeAsync(database, orgA, race, raced, ComplianceSenderType.ChannelManager, UserA),
            OutcomeAsync(database, orgA, race, raced, ComplianceSenderType.CasaZen, UserB));
        var winner = Assert.Single(outcomes.OfType<PropertySenderInfo>());
        var loser = Assert.Single(outcomes.OfType<DomainConflictException>());
        Assert.Equal("sender_changed_concurrently", loser.Code);
        await using (var verify = database.CreateContext())
        {
            var row = await verify.PropertyComplianceSenders.AsNoTracking().SingleAsync(s => s.PropertyId == raced);
            Assert.Equal((winner.AlloggiatiSender, winner.AlloggiatiSender), (row.AlloggiatiSender, row.IstatSender));
            Assert.Equal(1, await verify.OrgActivityEntries.CountAsync(e => e.SubjectId == raced.ToString()));
        }

        // A property of another org is not found, read or written; a property outside Italy has no sender to choose.
        await using (var db = TenantContext(database, orgA))
        {
            var store = Store(db);
            var notFound = await Assert.ThrowsAsync<NotFoundException>(
                () => store.SetAsync(foreign, ComplianceSenderType.CasaZen, UserA, CancellationToken.None));
            Assert.Equal("property_not_found", notFound.Code);
            await Assert.ThrowsAsync<NotFoundException>(() => store.GetAsync(foreign, CancellationToken.None));
            Assert.Empty(await store.GetManyAsync([foreign], CancellationToken.None));

            var notApplicable = await Assert.ThrowsAsync<DomainRuleException>(
                () => store.SetAsync(swiss, ComplianceSenderType.ChannelManager, UserA, CancellationToken.None));
            Assert.Equal("sender_not_applicable", notApplicable.Code);
            var swissSender = await store.GetAsync(swiss, CancellationToken.None);
            Assert.Equal((ComplianceSenderType.Manual, SenderSource.NotApplicable, false), (swissSender.AlloggiatiSender, swissSender.Source, swissSender.Applies));
        }

        await using (var verify = database.CreateContext())
        {
            Assert.False(await verify.PropertyComplianceSenders.AnyAsync(s => s.PropertyId == foreign || s.PropertyId == swiss));
            Assert.Equal(2, await verify.PropertyComplianceSenders.CountAsync());
        }
    }

    /// <summary>
    /// Holds every save of a sender until two have arrived, so that both writes read the same state (no row) before either inserts: the
    /// race of two people choosing the first sender of a property, made deterministic. One instance is shared by the two contexts.
    /// </summary>
    private sealed class SenderSaveRendezvous : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _bothArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var savesASender = eventData.Context?.ChangeTracker.Entries<PropertyComplianceSender>()
                .Any(entry => entry.State is EntityState.Added or EntityState.Modified) == true;
            if (!savesASender)
                return result;

            if (Interlocked.Increment(ref _arrived) >= 2)
                _bothArrived.TrySetResult();

            await _bothArrived.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            return result;
        }
    }

    /// <summary>The answer of one write of the race, or its conflict.</summary>
    private static async Task<object> OutcomeAsync(
        PostgresTestDatabase database,
        Guid orgId,
        SenderSaveRendezvous race,
        Guid propertyId,
        ComplianceSenderType sender,
        string userId)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>(database.CreateOptions()).AddInterceptors(race).Options;
        await using var db = new AppDbContext(options, new FixedTenantContext(orgId));
        try
        {
            return await Store(db).SetAsync(propertyId, sender, userId, CancellationToken.None);
        }
        catch (DomainConflictException conflict)
        {
            return conflict;
        }
    }

    private static PropertySenderStore Store(AppDbContext db)
    {
        var host = new Mock<IHostEnvironment>();
        host.SetupGet(h => h.EnvironmentName).Returns("Staging");
        return new PropertySenderStore(db, new ActivityLog(db), host.Object);
    }

    /// <summary>A context of a request of <paramref name="orgId"/>: the tenant filter on.</summary>
    private static AppDbContext TenantContext(PostgresTestDatabase database, Guid orgId) =>
        new(database.CreateOptions(), new FixedTenantContext(orgId));

    private static async Task<Guid> SeedOrgAsync(PostgresTestDatabase database)
    {
        await using var db = database.CreateContext();
        var org = new OrgEntity
        {
            Name = "Org TEST sender",
            Slug = $"sendstore-{Guid.NewGuid():N}",
            DisplayName = "Org TEST sender",
            ContactEmail = "sendstore@example.com",
            PlanTier = PlanTier.Starter,
            IsActive = true,
        };
        db.Orgs.Add(org);
        await db.SaveChangesAsync();
        return org.Id;
    }

    /// <summary>
    /// A property of <paramref name="orgId"/>. The address is unique per org (<c>UIX_Properties_OrgId_AddressKey</c>), so every property
    /// of one org gets its own <paramref name="streetNumber"/>.
    /// </summary>
    private static async Task<Guid> SeedPropertyAsync(PostgresTestDatabase database, Guid orgId, string country, int streetNumber)
    {
        await using var db = database.CreateContext();
        var property = new Property
        {
            OwnerId = UserA,
            OrgId = orgId,
            Name = "Casa TEST",
            Description = "Casa",
            Address = $"Via Roma {streetNumber}",
            City = country == "CH" ? "Lugano" : "Firenze",
            PostalCode = country == "CH" ? "6900" : "50100",
            Country = country,
            Currency = country == "CH" ? "CHF" : "EUR",
            MaxGuests = 4,
            NightlyRate = 100m,
            IsActive = true,
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property.Id;
    }

    private sealed class FixedTenantContext(Guid orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }
}
