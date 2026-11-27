using System.Security.Claims;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Owners;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// FIX/RES-OWNERS (S016): the list of the owners of an agency (<c>GET api/owners</c>) carries the number of properties, the kind and the settlement currency of the
/// fiscal profile in force and the mandate in force today, read with the list; it is the owners of the caller's org inside the caller's reach and nothing else, and the
/// mandate it shows is the one <see cref="OwnerMandateRules.InForce"/> chooses.
/// </summary>
public class OwnerDirectoryServiceTests
{
    /// <summary>11:00 in Rome, 15 November 2026.</summary>
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly Today = new(2026, 11, 15);

    /// <summary>
    /// PROOF TEST: permission and tenant rule. The list is the owners of the org of the caller inside the caller's reach: the whole org for an org-wide scope (the owners with
    /// no property too), for a collaborator «Solo alcuni» and an account outside the org team only the owners that hold at least one period and every period on a property
    /// they reach (an owner with no declaration is never in a partial reach), nobody for a member who was given nothing; the owners of another org are never listed, by
    /// the filter of the tenant or without it.
    /// </summary>
    [Fact]
    public async Task OwnersList_IsScopedToTheOrgAndTheCallersReach_Test()
    {
        var world = await DirectoryWorld.CreateAsync();

        // The whole org: every owner of the org, in order, the ones with no period and with periods on both properties included; Franco of the other org never.
        var wholeOrg = await world.ListAsync(new HostScope(world.OrgId));
        Assert.Equal(["Bianchi", "Conti", "Gialli", "Neri", "Rossi", "Verdi", "Zanetti"], wholeOrg.Select(o => o.LastName));
        Assert.DoesNotContain(wholeOrg, o => o.Id == world.Franco.Id);

        // The collaborator was given the lake house only: Bianchi, Conti and Gialli hold nothing else. Rossi also holds the mountain house, Verdi only that, Neri a period on
        // the mountain house and one on a deleted property, and Zanetti none: none of them is in its reach (never «no declaration = in scope»).
        Assert.Equal(["Bianchi", "Conti", "Gialli"], (await world.ListAsync(new HostScope(world.OrgId, GrantedToUserId: DirectoryWorld.Collaborator))).Select(o => o.LastName));

        // An account outside the org team reaches the properties it created: the lake house (and the deleted one, which nobody reaches).
        Assert.Equal(["Bianchi", "Conti", "Gialli"], (await world.ListAsync(new HostScope(world.OrgId, OwnerId: DirectoryWorld.Creator))).Select(o => o.LastName));

        // A member who was given nothing reaches nothing, and is never widened to the whole org.
        Assert.Empty(await world.ListAsync(new HostScope(world.OrgId, GrantedToUserId: "auth0|nessun-immobile")));

        // The tenant filter on (the caller of the request) and off (a job) give the same owners: the org is asked of every row, not only filtered underneath.
        await using (var asTheOrg = world.ContextOf(world.OrgId))
        {
            var service = new OwnerDirectoryService(asTheOrg, new FakeTimeProvider(Now), NullLogger<OwnerDirectoryService>.Instance);
            Assert.Equal(wholeOrg.Select(o => o.Id), (await service.ListAsync(new HostScope(world.OrgId))).Select(o => o.Id));
        }

        // A scope of the other org reads that org's owner and none of this one's.
        var other = await world.ListAsync(new HostScope(world.OtherOrgId));
        Assert.Equal([world.Franco.Id], other.Select(o => o.Id));
    }

    /// <summary>
    /// PROOF TEST: data rule. What the row says is read from the rows of the owner as the other readers read them: the properties held today or to come (an ownership that
    /// ended before today, a property that is deleted and the same property twice do not add), the fiscal profile in force today (the newest when none is, none without
    /// one), and the mandate of the owner in force today, never the one of a single ownership of before the mandates of the owner nor one that has not begun or has ended.
    /// </summary>
    [Fact]
    public async Task OwnersList_CarriesThePropertyCountTheKindTheCurrencyAndTheMandateInForce_Test()
    {
        var world = await DirectoryWorld.CreateAsync();

        var rows = (await world.ListAsync(new HostScope(world.OrgId))).ToDictionary(o => o.LastName);
        static (int Properties, OwnerEntityKind? Kind, string? Currency, bool HasMandate) FactsOf(OwnerDirectoryRow row) =>
            (row.PropertyCount, row.EntityKind, row.SettlementCurrency, row.MandateInForce is not null);

        // Bianchi: one ownership of the lake house; the profile in force (a company in francs, not the older one of a person in euros); of the mandates the one that began on
        // the latest day among those in force (never the first version, that ended, nor the 99% of the old ownership mandate).
        var bianchi = rows["Bianchi"];
        Assert.Equal((1, OwnerEntityKind.LegalEntity, "CHF", true), FactsOf(bianchi));
        var mandate = Assert.IsType<OwnerDirectoryMandate>(bianchi.MandateInForce);
        Assert.Equal((world.BianchiMandateInForce, new DateOnly(2026, 7, 1), (DateOnly?)null, 15.00m, CommissionBase.GrossExclTouristTaxAndCleaning, true),
            (mandate.Id, mandate.ValidFrom, mandate.ValidTo, mandate.CommissionPercent, mandate.CommissionBase, mandate.CommissionIncludesVat));

        // Conti: two periods on the lake house, one of them long over: one property. No profile, no mandate: nothing is invented.
        Assert.Equal((1, (OwnerEntityKind?)null, (string?)null, false), FactsOf(rows["Conti"]));

        // Gialli: a mandate that ends today is in force today, and one that starts tomorrow is not; a profile without a currency says none.
        var gialli = rows["Gialli"];
        Assert.Equal((1, (OwnerEntityKind?)OwnerEntityKind.NaturalPerson, (string?)null, true), FactsOf(gialli));
        Assert.Equal((new DateOnly(2026, 11, 1), (DateOnly?)Today, 8.00m), (gialli.MandateInForce!.ValidFrom, gialli.MandateInForce.ValidTo, gialli.MandateInForce.CommissionPercent));

        // Neri: a period over yesterday and one to come on the same property, one on the mountain house that ends today (still held today), one on a deleted property.
        Assert.Equal(2, rows["Neri"].PropertyCount);

        // Rossi: no profile in force today: the newest one (a person in euros from next year); a mandate that has not begun is not in force.
        Assert.Equal((2, (OwnerEntityKind?)OwnerEntityKind.NaturalPerson, (string?)"EUR", false), FactsOf(rows["Rossi"]));

        // Verdi: one property, no profile, a mandate that ended yesterday is not in force.
        Assert.Equal((1, (OwnerEntityKind?)null, (string?)null, false), FactsOf(rows["Verdi"]));

        // Zanetti: no property at all.
        Assert.Equal((0, (OwnerEntityKind?)null, (string?)null, false), FactsOf(rows["Zanetti"]));
    }

    /// <summary>
    /// The mandate the list shows is the one <see cref="OwnerMandateRules.InForce"/> chooses for the owner on the same day, whichever the day: before the first version,
    /// on the first and the last day of each, between two versions, when two cover the day (the latest start wins), after the last, and with the mandate of one ownership
    /// that must never be taken for the owner's.
    /// </summary>
    [Fact]
    public async Task OwnersList_TheMandateInForce_IsTheOneOfTheRuleOfTheMandates_OnEveryDay_Test()
    {
        var world = await DirectoryWorld.CreateAsync();
        var ownerId = world.Bianchi.Id;
        var all = await world.Db.OwnerMandates.AsNoTracking().Where(m => m.PropertyOwnerId == ownerId).ToListAsync();
        Assert.NotEmpty(all);

        var clock = new FakeTimeProvider(Now);
        var service = new OwnerDirectoryService(world.Db, clock, NullLogger<OwnerDirectoryService>.Instance);
        var checkedDays = 0;
        for (var day = new DateOnly(2025, 12, 28); day <= new DateOnly(2027, 1, 4); day = day.AddDays(1))
        {
            clock.SetUtcNow(new DateTimeOffset(day.ToDateTime(new TimeOnly(10, 0)), TimeSpan.Zero));

            var row = (await service.ListAsync(new HostScope(world.OrgId))).Single(o => o.Id == ownerId);

            Assert.Equal(OwnerMandateRules.InForce(all, ownerId, null, day)?.Id, row.MandateInForce?.Id);
            checkedDays++;
        }

        Assert.True(checkedDays > 360);
    }

    /// <summary>
    /// What the list is allowed to carry of the owner: the IBAN is masked to its last four characters and the clear value is nowhere in the answer, the identifiers of the
    /// fiscal profile (its fiscal code, VAT number, address and bank account) are not in it, and the facts of the row reach the answer as the service gave them.
    /// </summary>
    [Fact]
    public async Task OwnersList_Answer_MasksTheIban_AndCarriesTheFactsOfTheRow_Test()
    {
        var scope = new HostScope(Guid.NewGuid());
        var mandateId = Guid.NewGuid();
        var directory = new Mock<IOwnerDirectoryService>(MockBehavior.Strict);
        directory.Setup(d => d.ListAsync(scope, It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new OwnerDirectoryRow(
                Guid.NewGuid(), "TEST", "Proprietario", "proprietario@example.com", "+41000000", OwnerTaxRegime.Private, "IT60X0542811101000000123456", Now.UtcDateTime,
                PropertyCount: 3, OwnerEntityKind.LegalEntity, "CHF",
                new OwnerDirectoryMandate(mandateId, new DateOnly(2026, 1, 1), null, 12.5m, CommissionBase.GrossExclTouristTax, false)),
        ]);
        var controller = ControllerFor(scope.OrgId);

        var answer = await controller.GetAll(directory.Object, ScopeResolver(scope, scope.OrgId));

        var rows = Assert.IsType<List<PropertyOwnerSummaryDto>>(Assert.IsType<OkObjectResult>(answer.Result).Value);
        var row = Assert.Single(rows);
        Assert.Equal(("****3456", 3, OwnerEntityKind.LegalEntity, "CHF"), (row.IbanMasked, row.PropertyCount, row.EntityKind, row.SettlementCurrency));
        Assert.Equal((mandateId, 12.5m, CommissionBase.GrossExclTouristTax, false), (row.MandateInForce!.Id, row.MandateInForce.CommissionPercent, row.MandateInForce.CommissionBase, row.MandateInForce.CommissionIncludesVat));

        var json = JsonSerializer.Serialize(rows);
        Assert.DoesNotContain("IT60X0542811101000000123456", json, StringComparison.Ordinal);
        Assert.DoesNotContain("fiscalCode", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vatNumber", json, StringComparison.OrdinalIgnoreCase);
        directory.Verify(d => d.ListAsync(scope, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// PROOF TEST: permission rule. The list is asked with the scope the caller's membership gives (never a wider one), and a caller with no reach at all (its scope cannot be
    /// resolved: inactive, a member of another org) is refused with a 403 before anything is read.
    /// </summary>
    [Fact]
    public async Task OwnersList_CallerWithoutAReach_IsForbidden_AndAPartialScopeIsPassedAsItIs_Test()
    {
        var orgId = Guid.NewGuid();
        var directory = new Mock<IOwnerDirectoryService>(MockBehavior.Strict);

        var none = await ControllerFor(orgId).GetAll(directory.Object, ScopeResolver(null, orgId));
        Assert.IsType<ForbidResult>(none.Result);
        directory.VerifyNoOtherCalls();

        var partial = new HostScope(orgId, GrantedToUserId: "auth0|collaboratore");
        directory.Setup(d => d.ListAsync(partial, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var served = await ControllerFor(orgId).GetAll(directory.Object, ScopeResolver(partial, orgId));
        Assert.IsType<OkObjectResult>(served.Result);
        directory.Verify(d => d.ListAsync(partial, It.IsAny<CancellationToken>()), Times.Once);
        directory.VerifyNoOtherCalls();
    }

    private static OwnersController ControllerFor(Guid orgId)
    {
        var resolver = new Mock<IOrgContextResolver>();
        resolver.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(orgId);
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"OwnersController_{Guid.NewGuid()}").Options, NullTenantContext.Instance);
        var identity = new ClaimsIdentity([new Claim("sub", "auth0|agenzia"), new Claim(ClaimTypes.NameIdentifier, "auth0|agenzia")], "test");
        return new OwnersController(db, resolver.Object, NullLogger<OwnersController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } },
        };
    }

    private static IHostScopeResolver ScopeResolver(HostScope? scope, Guid orgId)
    {
        var resolver = new Mock<IHostScopeResolver>();
        resolver.Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>(), orgId, It.IsAny<CancellationToken>())).ReturnsAsync(scope);
        return resolver.Object;
    }

    /// <summary>
    /// An agency with two owned properties (the lake house and the mountain house) and a deleted one, and the owners of every case the list has to tell apart, over one
    /// in-memory database that a context with the tenant filter and one without it both read; and another org with an owner of its own.
    /// </summary>
    private sealed class DirectoryWorld
    {
        public const string Collaborator = "auth0|collaboratore";
        public const string Creator = "auth0|titolare";

        private readonly DbContextOptions<AppDbContext> _options;
        private readonly IDataProtectionProvider _protection = new EphemeralDataProtectionProvider();

        private DirectoryWorld(DbContextOptions<AppDbContext> options)
        {
            _options = options;
            Db = new AppDbContext(options, NullTenantContext.Instance, _protection);
        }

        public AppDbContext Db { get; }

        public Guid OrgId { get; } = Guid.NewGuid();

        public Guid OtherOrgId { get; } = Guid.NewGuid();

        public Guid BianchiMandateInForce { get; } = Guid.NewGuid();

        public PropertyOwner Bianchi { get; private set; } = null!;

        public PropertyOwner Franco { get; private set; } = null!;

        public AppDbContext ContextOf(Guid orgId) => new(_options, new OrgTenant(orgId), _protection);

        public async Task<IReadOnlyList<OwnerDirectoryRow>> ListAsync(HostScope scope) =>
            await new OwnerDirectoryService(Db, new FakeTimeProvider(Now), NullLogger<OwnerDirectoryService>.Instance).ListAsync(scope);

        public static async Task<DirectoryWorld> CreateAsync()
        {
            var world = new DirectoryWorld(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"OwnerDirectory_{Guid.NewGuid()}").Options);
            await world.SeedAsync();
            return world;
        }

        private async Task SeedAsync()
        {
            var lake = NewProperty(OrgId, "TEST Casa Lago", Creator);
            var mountain = NewProperty(OrgId, "TEST Casa Monte", "auth0|altro-creatore");
            var gone = NewProperty(OrgId, "TEST Casa Venduta", Creator);
            gone.IsDeleted = true;
            var foreign = NewProperty(OtherOrgId, "TEST Villa Altrove", "auth0|altrove");
            Db.Properties.AddRange(lake, mountain, gone, foreign);
            Db.PropertyMemberAccesses.Add(new PropertyMemberAccess { OrgId = OrgId, UserId = Collaborator, PropertyId = lake.Id });

            Bianchi = NewOwner(OrgId, "Anna", "Bianchi");
            var conti = NewOwner(OrgId, "Fabio", "Conti");
            var gialli = NewOwner(OrgId, "Giulia", "Gialli");
            var neri = NewOwner(OrgId, "Elena", "Neri");
            var rossi = NewOwner(OrgId, "Bruno", "Rossi");
            var verdi = NewOwner(OrgId, "Carla", "Verdi");
            var zanetti = NewOwner(OrgId, "Dario", "Zanetti");
            Franco = NewOwner(OtherOrgId, "Franco", "Altrove");
            Bianchi.Iban = "IT60X0542811101000000123456";
            Db.PropertyOwners.AddRange(Bianchi, conti, gialli, neri, rossi, verdi, zanetti, Franco);

            // The last instant of a day is what an ownership that ends that day holds: over yesterday, over today (still held through it), to come.
            var endedYesterday = RomeCalendar.StartOfDayUtc(Today).AddTicks(-10);
            var endsToday = RomeCalendar.StartOfDayUtc(Today.AddDays(1)).AddTicks(-10);
            var longAgo = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var longOver = new DateTime(2025, 6, 30, 22, 0, 0, DateTimeKind.Utc);
            var comingSoon = RomeCalendar.StartOfDayUtc(Today.AddDays(16));

            var bianchiPeriod = NewPeriod(lake, Bianchi, longAgo, null);
            Db.PropertyOwnerships.AddRange(
                bianchiPeriod,
                NewPeriod(lake, conti, longAgo, longOver), NewPeriod(lake, conti, longOver.AddDays(1), null),
                NewPeriod(lake, gialli, longAgo, null),
                NewPeriod(lake, neri, longAgo, endedYesterday), NewPeriod(lake, neri, comingSoon, null), NewPeriod(mountain, neri, longAgo, endsToday), NewPeriod(gone, neri, longAgo, null),
                NewPeriod(lake, rossi, longAgo, null), NewPeriod(mountain, rossi, longAgo, null),
                NewPeriod(mountain, verdi, longAgo, null),
                NewPeriod(foreign, Franco, longAgo, null));

            // Bianchi: a company in francs from the start of the year after a person in euros; two versions of the mandate, one more that overlaps the second in September (the
            // rules of the writes never make two, an old row could: the latest start wins), and a mandate of the ownership of before the mandates of the owner (99%, in force
            // from the start of the year) that is never the owner's.
            Db.OwnerFiscalProfiles.AddRange(
                NewProfile(Bianchi, new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31), OwnerEntityKind.NaturalPerson, "EUR"),
                NewProfile(Bianchi, new DateOnly(2026, 1, 1), null, OwnerEntityKind.LegalEntity, "CHF"));
            Db.OwnerMandates.AddRange(
                NewMandate(Bianchi, new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 30), 10.00m),
                NewMandate(Bianchi, new DateOnly(2026, 7, 1), null, 15.00m, includesVat: true, commissionBase: CommissionBase.GrossExclTouristTaxAndCleaning, id: BianchiMandateInForce),
                NewMandate(Bianchi, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), 5.00m),
                NewMandate(Bianchi, new DateOnly(2026, 1, 1), null, 99.00m, ownershipId: bianchiPeriod.Id));

            // Gialli: a profile without a currency; a mandate that ends today and one that starts tomorrow.
            Db.OwnerFiscalProfiles.Add(NewProfile(gialli, new DateOnly(2026, 1, 1), null, OwnerEntityKind.NaturalPerson, null));
            Db.OwnerMandates.AddRange(
                NewMandate(gialli, new DateOnly(2026, 11, 1), Today, 8.00m),
                NewMandate(gialli, Today.AddDays(1), null, 9.00m));

            // Rossi: a profile that ended and one that has not begun (none in force today: the newest); a mandate that has not begun.
            Db.OwnerFiscalProfiles.AddRange(
                NewProfile(rossi, new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31), OwnerEntityKind.LegalEntity, "CHF"),
                NewProfile(rossi, new DateOnly(2027, 1, 1), null, OwnerEntityKind.NaturalPerson, "EUR"));
            Db.OwnerMandates.Add(NewMandate(rossi, Today.AddDays(30), null, 11.00m));

            // Verdi: a mandate that ended yesterday.
            Db.OwnerMandates.Add(NewMandate(verdi, new DateOnly(2026, 1, 1), Today.AddDays(-1), 12.00m));

            // The owner of the other org has everything too, and none of it may reach this org's list.
            Db.OwnerFiscalProfiles.Add(NewProfile(Franco, new DateOnly(2026, 1, 1), null, OwnerEntityKind.LegalEntity, "CHF"));
            Db.OwnerMandates.Add(NewMandate(Franco, new DateOnly(2026, 1, 1), null, 20.00m));

            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        private static Property NewProperty(Guid orgId, string name, string createdBy) => new()
        {
            OrgId = orgId,
            OwnerId = createdBy,
            Name = name,
            Address = $"Via {name}",
            City = "Lugano",
            Currency = "CHF",
        };

        private static PropertyOwner NewOwner(Guid orgId, string first, string last) => new()
        {
            OrgId = orgId,
            FirstName = first,
            LastName = last,
            Email = $"{last.ToLowerInvariant()}@example.com",
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        private PropertyOwnership NewPeriod(Property property, PropertyOwner owner, DateTime start, DateTime? end) => new()
        {
            OrgId = property.OrgId,
            PropertyId = property.Id,
            PropertyOwnerId = owner.Id,
            SharePercent = 100m,
            OwnershipStart = start,
            OwnershipEnd = end,
        };

        private static OwnerFiscalProfile NewProfile(PropertyOwner owner, DateOnly from, DateOnly? to, OwnerEntityKind kind, string? currency) => new()
        {
            OrgId = owner.OrgId,
            PropertyOwnerId = owner.Id,
            ValidFrom = from,
            ValidTo = to,
            EntityKind = kind,
            SettlementCurrency = currency,
        };

        private static OwnerMandate NewMandate(
            PropertyOwner owner,
            DateOnly from,
            DateOnly? to,
            decimal percent,
            bool includesVat = false,
            CommissionBase commissionBase = CommissionBase.GrossExclTouristTax,
            Guid? ownershipId = null,
            Guid? id = null) => new()
            {
                Id = id ?? Guid.NewGuid(),
                OrgId = owner.OrgId,
                PropertyOwnerId = owner.Id,
                PropertyOwnershipId = ownershipId,
                ValidFrom = from,
                ValidTo = to,
                CommissionPercent = percent,
                CommissionBase = commissionBase,
                CommissionIncludesVat = includesVat,
                MandatePdfUrl = "mandati/test.pdf",
            };
    }

    private sealed class OrgTenant(Guid orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }
}
