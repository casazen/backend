using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Models;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E13/F01/US02/T03 (NF-118): the two items the owner ledger adds to the billing setup, for an agency only: the commission rules of the channels saved once
/// (<c>channel_commissions_unconfirmed</c>) and the owners without an active mandate (<c>owners_without_mandate</c>, with their count).
/// </summary>
public class OwnerBillingSetupContributorsTests
{
    private static readonly Guid AgencyId = Guid.NewGuid();
    private static readonly Guid OtherAgencyId = Guid.NewGuid();

    /// <summary>Today in Rome for every test: 14 November 2026.</summary>
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Data: the count is the owners with an ownership not ended and no mandate in force today; an owner with a mandate in force, with only ended ownerships, or
    /// anonymized is not counted, a mandate that ended is no mandate, a share that no mandate covers makes its owner count, and another agency's owners never do.
    /// </summary>
    [Fact]
    public async Task BillingSetupMandates_CountsOwnersWithoutActiveMandate_Test()
    {
        var world = World.Create();
        var without = await world.OwnerAsync(AgencyId, ownershipStart: new DateOnly(2025, 1, 1));
        var covered = await world.OwnerAsync(AgencyId, ownershipStart: new DateOnly(2025, 1, 1));
        await world.OwnerAsync(AgencyId, ownershipStart: new DateOnly(2020, 1, 1), ownershipEnd: new DateOnly(2025, 12, 31));
        var lapsed = await world.OwnerAsync(AgencyId, ownershipStart: new DateOnly(2025, 1, 1));
        await world.OwnerAsync(AgencyId, ownershipStart: new DateOnly(2025, 1, 1), anonymized: true);
        await world.OwnerAsync(AgencyId, ownershipStart: null);
        await world.OwnerAsync(OtherAgencyId, ownershipStart: new DateOnly(2025, 1, 1));
        world.Mandate(AgencyId, covered, new DateOnly(2025, 1, 1), null);
        world.Mandate(AgencyId, lapsed, new DateOnly(2025, 1, 1), new DateOnly(2026, 6, 30));
        await world.Db.SaveChangesAsync();

        var item = Assert.Single(await world.Mandates.EvaluateAsync(AgencyId, CancellationToken.None));

        // without + lapsed: two owners, counted once each however many ownerships they hold.
        Assert.Equal((OwnerBillingSetupItemKeys.OwnersWithoutMandate, BillingSetupSections.Mandates, BillingSetupReasons.Required, (int?)2), (item.Key, item.Section, item.Reason, item.Count));

        // A mandate from today on covers the owner that had none; the lapsed one is renewed too: nothing is missing any more.
        world.Mandate(AgencyId, without, new DateOnly(2026, 11, 14), null);
        world.Mandate(AgencyId, lapsed, new DateOnly(2026, 7, 1), null);
        await world.Db.SaveChangesAsync();
        Assert.Empty(await world.Mandates.EvaluateAsync(AgencyId, CancellationToken.None));
    }

    /// <summary>Data: a mandate of the owner covers every ownership of it; one of a single ownership (the shape of before E13) covers that one only, so the other share still has none.</summary>
    [Fact]
    public async Task BillingSetupMandates_AShareNoMandateCovers_CountsItsOwner_Test()
    {
        var world = World.Create();
        var owner = await world.OwnerAsync(AgencyId, ownershipStart: new DateOnly(2025, 1, 1));
        var first = world.Db.PropertyOwnerships.Single(o => o.PropertyOwnerId == owner);
        await world.OwnershipAsync(AgencyId, owner, new DateOnly(2025, 1, 1), null);
        world.Mandate(AgencyId, owner, new DateOnly(2025, 1, 1), null, ownershipId: first.Id);
        await world.Db.SaveChangesAsync();

        var item = Assert.Single(await world.Mandates.EvaluateAsync(AgencyId, CancellationToken.None));
        Assert.Equal(1, item.Count);

        // The owner's own mandate covers the second share too.
        world.Mandate(AgencyId, owner, new DateOnly(2025, 1, 1), null);
        await world.Db.SaveChangesAsync();
        Assert.Empty(await world.Mandates.EvaluateAsync(AgencyId, CancellationToken.None));
    }

    /// <summary>Data: the step is missing until the agency saved its commission rules once; the defaults alone, and the rules of another agency, do not count.</summary>
    [Fact]
    public async Task BillingSetupChannels_MissingUntilTheAgencySavedItsRules_Test()
    {
        var world = World.Create();
        world.Db.OrgChannelCommissionRules.Add(Rule(OtherAgencyId));
        await world.Db.SaveChangesAsync();

        var missing = Assert.Single(await world.Channels.EvaluateAsync(AgencyId, CancellationToken.None));
        Assert.Equal(
            (OwnerBillingSetupItemKeys.ChannelCommissionsUnconfirmed, BillingSetupSections.Channels, BillingSetupReasons.Unconfirmed, (int?)null),
            (missing.Key, missing.Section, missing.Reason, missing.Count));

        world.Db.OrgChannelCommissionRules.Add(Rule(AgencyId));
        await world.Db.SaveChangesAsync();
        Assert.Empty(await world.Channels.EvaluateAsync(AgencyId, CancellationToken.None));
    }

    /// <summary>Data: an organization that is not an agency gets no item from either contributor, whatever its owners and rules say.</summary>
    [Theory]
    [InlineData(PlanTier.Starter)]
    [InlineData(PlanTier.Pro)]
    [InlineData(PlanTier.Scale)]
    public async Task BillingSetup_NonAgencyOrganization_GetsNoItem_Test(PlanTier tier)
    {
        var world = World.Create(tier);
        await world.OwnerAsync(AgencyId, ownershipStart: new DateOnly(2025, 1, 1));

        Assert.Empty(await world.Channels.EvaluateAsync(AgencyId, CancellationToken.None));
        Assert.Empty(await world.Mandates.EvaluateAsync(AgencyId, CancellationToken.None));
        Assert.Empty(await world.Mandates.EvaluateAsync(Guid.NewGuid(), CancellationToken.None));
    }

    /// <summary>
    /// Acceptance: the part «Fatturazione» of an agency stays open while the channel rules are unconfirmed or some owner has no mandate, through the service that
    /// gathers the contributors; it is done when both are settled.
    /// </summary>
    [Fact]
    public async Task BillingSetup_PartStaysOpenWhileChannelRulesOrMandatesAreMissing_Test()
    {
        var world = World.Create();
        var owner = await world.OwnerAsync(AgencyId, ownershipStart: new DateOnly(2025, 1, 1));
        var service = new BillingSetupService(
            Mock.Of<IDocumentIssuerService>(MockBehavior.Strict),
            [world.Channels, world.Mandates],
            NullLogger<BillingSetupService>.Instance);
        string[] sections = [BillingSetupSections.Channels, BillingSetupSections.Mandates];

        var open = await service.GetProgressAsync(AgencyId, sections);
        Assert.False(open.IsComplete);
        Assert.Equal(
            [OwnerBillingSetupItemKeys.ChannelCommissionsUnconfirmed, OwnerBillingSetupItemKeys.OwnersWithoutMandate],
            open.Missing.Select(i => i.Key));
        Assert.Equal(1, open.Missing.Single(i => i.Key == OwnerBillingSetupItemKeys.OwnersWithoutMandate).Count);

        world.Db.OrgChannelCommissionRules.Add(Rule(AgencyId));
        await world.Db.SaveChangesAsync();
        Assert.Equal([OwnerBillingSetupItemKeys.OwnersWithoutMandate], (await service.GetProgressAsync(AgencyId, sections)).Missing.Select(i => i.Key));

        world.Mandate(AgencyId, owner, new DateOnly(2025, 1, 1), null);
        await world.Db.SaveChangesAsync();
        var done = await service.GetProgressAsync(AgencyId, sections);
        Assert.True(done.IsComplete);
        Assert.Equal((2, 2), (done.Done, done.Total));
    }

    private static OrgChannelCommissionRule Rule(Guid orgId) => new()
    {
        OrgId = orgId,
        Channel = nameof(BookingSource.BookingCom),
        Percent = 15m,
        Base = CommissionBase.GrossExclTouristTax,
        ValidFrom = new DateOnly(2026, 1, 1),
        CreatedAt = Now.UtcDateTime,
    };

    private sealed class World
    {
        private World(AppDbContext db)
        {
            Db = db;
            Channels = new ChannelCommissionsSectionContributor(db);
            Mandates = new OwnerMandatesSectionContributor(db, new FakeTimeProvider(Now));
        }

        public AppDbContext Db { get; }

        public ChannelCommissionsSectionContributor Channels { get; }

        public OwnerMandatesSectionContributor Mandates { get; }

        public static World Create(PlanTier tier = PlanTier.Agency)
        {
            var db = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"OwnerBillingSetup_{Guid.NewGuid()}").Options,
                new SetupTenant(),
                new EphemeralDataProtectionProvider());
            db.Orgs.Add(new OrgEntity { Id = AgencyId, Name = "TEST Agenzia", DisplayName = "TEST Agenzia", Slug = "test-agenzia", PlanTier = tier });
            db.Orgs.Add(new OrgEntity { Id = OtherAgencyId, Name = "TEST Altra", DisplayName = "TEST Altra", Slug = "test-altra", PlanTier = PlanTier.Agency });
            db.SaveChanges();
            return new World(db);
        }

        /// <summary>An owner of the org and, from <paramref name="ownershipStart"/>, the whole of a property of its own (none without a start).</summary>
        public async Task<Guid> OwnerAsync(Guid orgId, DateOnly? ownershipStart, DateOnly? ownershipEnd = null, bool anonymized = false)
        {
            var owner = new PropertyOwner
            {
                OrgId = orgId,
                FirstName = "TEST",
                LastName = "Proprietario",
                Email = $"{Guid.NewGuid():N}@example.com",
                AnonymizedAt = anonymized ? Now.UtcDateTime : null,
            };
            Db.PropertyOwners.Add(owner);
            await Db.SaveChangesAsync();
            if (ownershipStart is { } start)
                await OwnershipAsync(orgId, owner.Id, start, ownershipEnd);
            return owner.Id;
        }

        public async Task<Guid> OwnershipAsync(Guid orgId, Guid ownerId, DateOnly start, DateOnly? end)
        {
            var property = new Property { OrgId = orgId, OwnerId = "auth0|agency", Name = "TEST Casa", Address = "Via 1", City = "Lugano", Currency = "CHF" };
            var ownership = new PropertyOwnership
            {
                OrgId = orgId,
                PropertyId = property.Id,
                PropertyOwnerId = ownerId,
                SharePercent = 100m,
                OwnershipStart = start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                OwnershipEnd = end?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            };
            Db.Properties.Add(property);
            Db.PropertyOwnerships.Add(ownership);
            await Db.SaveChangesAsync();
            return ownership.Id;
        }

        public void Mandate(Guid orgId, Guid ownerId, DateOnly from, DateOnly? to, Guid? ownershipId = null) =>
            Db.OwnerMandates.Add(new OwnerMandate
            {
                OrgId = orgId,
                PropertyOwnerId = ownerId,
                PropertyOwnershipId = ownershipId,
                ValidFrom = from,
                ValidTo = to,
                CommissionPercent = 15m,
                MandatePdfUrl = string.Empty,
                CreatedAt = Now.UtcDateTime,
            });
    }
}

file sealed class SetupTenant : ITenantContext
{
    public Guid? OrgId => null;

    public bool FilterEnabled => false;
}
