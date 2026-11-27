using System.Xml.Linq;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E24/F01/US01/T01 (NF-120): the default cleaner of a property, with the price and the time agreed, single and in bulk. A supplier is checked as a service request checks
/// it (active, covers the comune), a member of the staff as the person in charge of a property is (active, reaches it), the price is in the currency of the property, and a
/// bulk save answers for each property and never says «all saved» when one was not. On EF InMemory, on the world of <see cref="ServiceRequestScenario"/> (one host, one
/// property in comune H501, one active supplier that covers it) and on the one of <see cref="HostScopeScenario"/> (a collaborator «Solo alcuni», a second org).
/// </summary>
public class PropertyCleaningSetupServiceTests
{
    private const string Actor = "auth0|actor";

    private static PropertyCleaningSetupService Service(ServiceRequestScenario scenario) =>
        new(scenario.Db, scenario.Kit.Matcher, NullLogger<PropertyCleaningSetupService>.Instance, scenario.Clock);

    private static PropertyCleaningSetupService Service(AppDbContext db) =>
        new(db, ComuneTestServices.Matcher(db), NullLogger<PropertyCleaningSetupService>.Instance, new FakeTimeProvider(HostScopeScenario.Now));

    private static CleaningSetupInput Supplier(Guid supplierOrgId, int? price = 7500, string? currency = "EUR", bool active = true) =>
        new(PropertyCleanerKind.Supplier, supplierOrgId, null, price, currency, null, null, active);

    private static CleaningSetupInput Staff(string userId, int? price = null, string? currency = null, bool active = true) =>
        new(PropertyCleanerKind.Staff, null, userId, price, currency, null, null, active);

    private static async Task<Guid> AddSupplierAsync(AppDbContext db, string comune, SupplierStatus status = SupplierStatus.Active)
    {
        var org = new OrgEntity
        {
            Name = "Altro fornitore",
            Slug = $"altro-{Guid.NewGuid():N}"[..20],
            DisplayName = "Altro fornitore",
            ContactEmail = "altro@test.com",
            OrgType = OrgType.Supplier,
        };
        db.Orgs.Add(org);
        db.SupplierProfiles.Add(new SupplierProfile
        {
            GuestPortalMarketplaceEnabled = true,
            OrgId = org.Id,
            Email = "altro@test.com",
            LegalName = "Altro Srl",
            Phone = "+39 06 000000",
            Status = status,
            ComuniJson = $"[\"{comune}\"]",
            CategoriesJson = "[\"cleaning\"]",
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return org.Id;
    }

    /// <summary>The supplier org of the world of <see cref="HostScopeScenario"/>, with an active profile that covers Ostuni, where its properties are.</summary>
    private static async Task AddProfileToWorldSupplierAsync(AppDbContext db, HostScopeWorld world, SupplierStatus status = SupplierStatus.Active)
    {
        db.SupplierProfiles.Add(new SupplierProfile
        {
            GuestPortalMarketplaceEnabled = true,
            OrgId = world.SupplierOrgId,
            Email = "fornitore@test.com",
            LegalName = "Fornitore Srl",
            Phone = "+39 080 000000",
            Status = status,
            ComuniJson = "[\"Ostuni\"]",
            CategoriesJson = "[\"cleaning\"]",
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task ChangeAsync<T>(AppDbContext db, Guid id, Action<T> change)
        where T : class
    {
        db.ChangeTracker.Clear();
        var row = await db.Set<T>().FindAsync(id) ?? throw new InvalidOperationException("No such row.");
        change(row);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    // ─── Proofs: the rules of the brief that cost data or permission if wrong ──────────────────────────────────────────────────────

    /// <summary>
    /// Proof (data): a supplier that does not cover the comune of the property is refused with its own code, as a service request would refuse it, and nothing is saved; the
    /// supplier that covers it is saved with the same body.
    /// </summary>
    [Fact]
    public async Task CleaningSetup_SupplierOutsideComuneRefused_Test()
    {
        using var scenario = await ServiceRequestScenario.CreateAsync();
        var service = Service(scenario);
        var outside = await AddSupplierAsync(scenario.Db, "F205");

        var error = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.SaveAsync(scenario.HostOrgId, scenario.PropertyId, Supplier(outside), Actor));

        Assert.Equal(PropertyCleaningSetupRules.SupplierOutsideComune, error.Code);
        Assert.Empty(await scenario.Db.PropertyCleaningSetups.ToListAsync());

        var saved = await service.SaveAsync(scenario.HostOrgId, scenario.PropertyId, Supplier(scenario.SupplierOrgId), Actor);

        Assert.True(saved.Configured);
        Assert.Equal(scenario.SupplierOrgId, saved.SupplierOrgId);
        Assert.Equal("Supplier Srl", saved.SupplierName);
        var row = await scenario.Db.PropertyCleaningSetups.SingleAsync();
        Assert.Equal((scenario.HostOrgId, scenario.PropertyId, PropertyCleanerKind.Supplier, 7500, true), (row.OrgId, row.PropertyId, row.CleanerKind, row.PriceCents, row.Active));
        Assert.Equal(Actor, row.UpdatedByUserId);
    }

    /// <summary>
    /// Proof (permission): the property of another org is not found, whatever is asked of it: a read, a save and a bulk save answer as for a property that does not exist, and
    /// nothing is written for it.
    /// </summary>
    [Fact]
    public async Task CleaningSetup_OtherOrganizationProperty404_Test()
    {
        using var scenario = await ServiceRequestScenario.CreateAsync();
        var service = Service(scenario);
        var otherOrg = scenario.SupplierOrgId;

        var read = await Assert.ThrowsAsync<NotFoundException>(() => service.GetAsync(otherOrg, scenario.PropertyId));
        var write = await Assert.ThrowsAsync<NotFoundException>(
            () => service.SaveAsync(otherOrg, scenario.PropertyId, Staff(ServiceRequestScenario.HostUserId), Actor));
        var bulk = Assert.Single(await service.SaveBulkAsync(
            otherOrg, new HostScope(otherOrg), [scenario.PropertyId], Staff(ServiceRequestScenario.HostUserId), Actor));

        Assert.Equal("property_not_found", read.Code);
        Assert.Equal("property_not_found", write.Code);
        Assert.Equal(("property_not_found", false), (bulk.Code, bulk.Saved));
        Assert.Empty(await scenario.Db.PropertyCleaningSetups.ToListAsync());
        // A scope of another org than the one the save is for is a mistake of the caller, never a widening.
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.SaveBulkAsync(scenario.HostOrgId, new HostScope(otherOrg), [scenario.PropertyId], Staff(ServiceRequestScenario.HostUserId), Actor));
    }

    /// <summary>
    /// Proof (data): a bulk save answers for each property, in the order received. One that cannot be saved comes back with its code and does not stop the others, one of
    /// another org or unknown is «not found», a repeated id counts once, and the counts never say «all saved» when one was not.
    /// </summary>
    [Fact]
    public async Task CleaningSetup_BulkReportsEachResult_Test()
    {
        await using var db = HostScopeScenario.NewInMemoryDb();
        var world = await HostScopeScenario.SeedAsync(db);
        await AddProfileToWorldSupplierAsync(db, world);
        // The second property is in another comune than the one the supplier covers.
        await ChangeAsync<Property>(db, world.Hidden.Id, p => p.City = "Milano");
        var unknown = Guid.NewGuid();

        var results = await Service(db).SaveBulkAsync(
            world.OrgId,
            world.OrgWide,
            [world.Granted.Id, world.Hidden.Id, world.OtherOrgProperty.Id, unknown, world.Granted.Id],
            Supplier(world.SupplierOrgId),
            Actor);

        Assert.Equal([world.Granted.Id, world.Hidden.Id, world.OtherOrgProperty.Id, unknown], results.Select(r => r.PropertyId));
        Assert.Equal([true, false, false, false], results.Select(r => r.Saved));
        Assert.Equal(
            [null, PropertyCleaningSetupRules.SupplierOutsideComune, "property_not_found", "property_not_found"],
            results.Select(r => r.Code));
        Assert.Equal(1, results.Count(r => r.Saved));
        Assert.Equal(world.Granted.Id, (await db.PropertyCleaningSetups.AsNoTracking().SingleAsync()).PropertyId);
    }

    // ─── The reach of the caller ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A collaborator «Solo alcuni» saves only the properties it was given: the others of the org come back as «not found» in the result, and are not written.</summary>
    [Fact]
    public async Task Bulk_ACollaboratorWithSomeProperties_SavesOnlyThoseItWasGiven()
    {
        await using var db = HostScopeScenario.NewInMemoryDb();
        var world = await HostScopeScenario.SeedAsync(db);
        await AddProfileToWorldSupplierAsync(db, world);

        var results = await Service(db).SaveBulkAsync(
            world.OrgId, world.Restricted, [world.Granted.Id, world.Hidden.Id], Supplier(world.SupplierOrgId), Actor);

        Assert.Equal([true, false], results.Select(r => r.Saved));
        Assert.Equal("property_not_found", results[1].Code);
        Assert.Equal([world.Granted.Id], await db.PropertyCleaningSetups.AsNoTracking().Select(s => s.PropertyId).ToListAsync());
    }

    /// <summary>None is none: an empty list is 422, never «every property», and so is a list over the cap; a body that is wrong for every property is refused before anything is saved.</summary>
    [Fact]
    public async Task Bulk_AnEmptyList_ATooLongOne_AndAWrongBody_AreRefusedBeforeAnythingIsSaved()
    {
        await using var db = HostScopeScenario.NewInMemoryDb();
        var world = await HostScopeScenario.SeedAsync(db);
        await AddProfileToWorldSupplierAsync(db, world);
        var service = Service(db);

        var empty = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.SaveBulkAsync(world.OrgId, world.OrgWide, [], Supplier(world.SupplierOrgId), Actor));
        var tooMany = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.SaveBulkAsync(
                world.OrgId,
                world.OrgWide,
                [.. Enumerable.Range(0, PropertyCleaningSetupRules.MaxBulkProperties + 1).Select(_ => Guid.NewGuid())],
                Supplier(world.SupplierOrgId),
                Actor));
        var wrongBody = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.SaveBulkAsync(world.OrgId, world.OrgWide, [world.Granted.Id], Supplier(world.SupplierOrgId, price: null), Actor));

        Assert.Equal(PropertyCleaningSetupRules.BulkEmpty, empty.Code);
        Assert.Equal(PropertyCleaningSetupRules.BulkTooMany, tooMany.Code);
        Assert.Equal(PropertyCleaningSetupRules.PriceInvalid, wrongBody.Code);
        Assert.Empty(await db.PropertyCleaningSetups.ToListAsync());
    }

    // ─── The price ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The price is in the currency of the property and says so: another currency, and a price with none, are refused; a Swiss property takes francs and nothing else.</summary>
    [Fact]
    public async Task Price_ComesWithTheCurrencyOfTheProperty_NeverAnother()
    {
        using var scenario = await ServiceRequestScenario.CreateAsync();
        var service = Service(scenario);

        var other = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.SaveAsync(scenario.HostOrgId, scenario.PropertyId, Supplier(scenario.SupplierOrgId, currency: "CHF"), Actor));
        var none = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.SaveAsync(scenario.HostOrgId, scenario.PropertyId, Supplier(scenario.SupplierOrgId, currency: null), Actor));
        var lower = await service.SaveAsync(scenario.HostOrgId, scenario.PropertyId, Supplier(scenario.SupplierOrgId, currency: " eur "), Actor);

        Assert.Equal(PropertyCleaningSetupRules.CurrencyMismatch, other.Code);
        Assert.Equal("EUR", Assert.Single(other.MessageArgs));
        Assert.Equal(PropertyCleaningSetupRules.CurrencyRequired, none.Code);
        Assert.Equal(("EUR", 7500), (lower.Currency, lower.PriceCents));

        await ChangeAsync<Property>(scenario.Db, scenario.PropertyId, p => p.Currency = "CHF");
        var swiss = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.SaveAsync(scenario.HostOrgId, scenario.PropertyId, Supplier(scenario.SupplierOrgId, currency: "EUR"), Actor));
        Assert.Equal(PropertyCleaningSetupRules.CurrencyMismatch, swiss.Code);
        Assert.Equal("CHF", Assert.Single(swiss.MessageArgs));
    }

    /// <summary>
    /// Proof (money): the requests to the suppliers of the marketplace, their prices and their payments are in euro and nothing is converted, so a property in another currency takes no
    /// supplier. An active supplier setup of a Swiss house is refused (422, with the currency in the message) and nothing is saved; kept switched off a supplier setup is only kept; back
    /// in euro the supplier is accepted.
    /// </summary>
    [Fact]
    public async Task CleaningSetup_SupplierForAPropertyInAnotherCurrencyRefused_Test()
    {
        using var scenario = await ServiceRequestScenario.CreateAsync();
        var service = Service(scenario);
        await ChangeAsync<Property>(scenario.Db, scenario.PropertyId, p => p.Currency = "CHF");

        var refused = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.SaveAsync(scenario.HostOrgId, scenario.PropertyId, Supplier(scenario.SupplierOrgId, currency: "CHF"), Actor));

        Assert.Equal(PropertyCleaningSetupRules.SupplierCurrencyUnsupported, refused.Code);
        Assert.Equal("CHF", Assert.Single(refused.MessageArgs));
        Assert.Empty(await scenario.Db.PropertyCleaningSetups.ToListAsync());

        var kept = await service.SaveAsync(scenario.HostOrgId, scenario.PropertyId, Supplier(scenario.SupplierOrgId, currency: "CHF", active: false), Actor);
        Assert.False(kept.Active);

        await ChangeAsync<Property>(scenario.Db, scenario.PropertyId, p => p.Currency = "EUR");
        var saved = await service.SaveAsync(scenario.HostOrgId, scenario.PropertyId, Supplier(scenario.SupplierOrgId), Actor);
        Assert.True(saved.Active);
    }

    /// <summary>In a bulk save a house in another currency comes back with its own code and does not stop the others.</summary>
    [Fact]
    public async Task Bulk_AHouseInAnotherCurrency_TakesNoSupplier_AndTheOthersAreSaved()
    {
        await using var db = HostScopeScenario.NewInMemoryDb();
        var world = await HostScopeScenario.SeedAsync(db);
        await AddProfileToWorldSupplierAsync(db, world);
        await ChangeAsync<Property>(db, world.Hidden.Id, p => p.Currency = "CHF");
        var service = Service(db);

        var inFrancs = await service.SaveBulkAsync(world.OrgId, world.OrgWide, [world.Hidden.Id], Supplier(world.SupplierOrgId, currency: "CHF"), Actor);
        var mixed = await service.SaveBulkAsync(world.OrgId, world.OrgWide, [world.Granted.Id, world.Hidden.Id], Supplier(world.SupplierOrgId), Actor);

        var refused = Assert.Single(inFrancs);
        Assert.Equal((false, PropertyCleaningSetupRules.SupplierCurrencyUnsupported), (refused.Saved, refused.Code));
        Assert.Equal("CHF", Assert.Single(refused.MessageArgs));
        Assert.Equal([true, false], mixed.Select(r => r.Saved));
        Assert.Equal([null, PropertyCleaningSetupRules.CurrencyMismatch], mixed.Select(r => r.Code));
        Assert.Equal([world.Granted.Id], await db.PropertyCleaningSetups.AsNoTracking().Select(s => s.PropertyId).ToListAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(10_000_001)]
    public async Task Price_OfASupplier_IsRequiredFromOneCentToTheBoundOfARequest(int? price)
    {
        using var scenario = await ServiceRequestScenario.CreateAsync();

        var error = await Assert.ThrowsAsync<DomainRuleException>(
            () => Service(scenario).SaveAsync(scenario.HostOrgId, scenario.PropertyId, Supplier(scenario.SupplierOrgId, price), Actor));

        Assert.Equal(PropertyCleaningSetupRules.PriceInvalid, error.Code);
    }

    // ─── The cleaner ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cleaner_IsOneOfTheTwoKinds_AndOnlyTheColumnsOfItsKind()
    {
        using var scenario = await ServiceRequestScenario.CreateAsync();
        var service = Service(scenario);

        var none = await Assert.ThrowsAsync<DomainRuleException>(() => service.SaveAsync(
            scenario.HostOrgId, scenario.PropertyId, new CleaningSetupInput(null, null, null, 100, "EUR", null, null), Actor));
        var both = await Assert.ThrowsAsync<DomainRuleException>(() => service.SaveAsync(
            scenario.HostOrgId, scenario.PropertyId, Supplier(scenario.SupplierOrgId) with { StaffUserId = "auth0|someone" }, Actor));
        var supplierWithoutOrg = await Assert.ThrowsAsync<DomainRuleException>(() => service.SaveAsync(
            scenario.HostOrgId, scenario.PropertyId, Supplier(Guid.Empty), Actor));
        var staffWithSupplier = await Assert.ThrowsAsync<DomainRuleException>(() => service.SaveAsync(
            scenario.HostOrgId, scenario.PropertyId, Staff(ServiceRequestScenario.HostUserId) with { SupplierOrgId = scenario.SupplierOrgId }, Actor));
        var unknownSupplier = await Assert.ThrowsAsync<DomainRuleException>(() => service.SaveAsync(
            scenario.HostOrgId, scenario.PropertyId, Supplier(Guid.NewGuid()), Actor));

        Assert.Equal(PropertyCleaningSetupRules.CleanerRequired, none.Code);
        Assert.Equal(PropertyCleaningSetupRules.CleanerMismatch, both.Code);
        Assert.Equal(PropertyCleaningSetupRules.CleanerMismatch, supplierWithoutOrg.Code);
        Assert.Equal(PropertyCleaningSetupRules.CleanerMismatch, staffWithSupplier.Code);
        Assert.Equal(PropertyCleaningSetupRules.SupplierNotFound, unknownSupplier.Code);
        Assert.Empty(await scenario.Db.PropertyCleaningSetups.ToListAsync());
    }

    [Theory]
    [InlineData(SupplierStatus.Pending)]
    [InlineData(SupplierStatus.Suspended)]
    public async Task Supplier_ThatIsNotActive_IsRefused_ButAnInactiveSetupIsOnlyKept(SupplierStatus status)
    {
        using var scenario = await ServiceRequestScenario.CreateAsync();
        var service = Service(scenario);
        await scenario.SetSupplierStatusAsync(status);

        var error = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.SaveAsync(scenario.HostOrgId, scenario.PropertyId, Supplier(scenario.SupplierOrgId), Actor));
        var kept = await service.SaveAsync(scenario.HostOrgId, scenario.PropertyId, Supplier(scenario.SupplierOrgId, active: false), Actor);

        Assert.Equal(PropertyCleaningSetupRules.SupplierInactive, error.Code);
        Assert.False(kept.Active);
        Assert.True(kept.Configured);
    }

    /// <summary>
    /// A member of the staff is an active member of the org who reaches the property: the owner reaches all, the collaborator «Solo alcuni» only the ones it was given, a member who
    /// was deactivated or a person of another org none.
    /// </summary>
    [Fact]
    public async Task Staff_MustBeAnActiveMemberWhoReachesTheProperty()
    {
        await using var db = HostScopeScenario.NewInMemoryDb();
        var world = await HostScopeScenario.SeedAsync(db);
        var service = Service(db);

        var owner = await service.SaveAsync(world.OrgId, world.Hidden.Id, Staff(world.OwnerUserId, 4000, "EUR"), Actor);
        var given = await service.SaveAsync(world.OrgId, world.Granted.Id, Staff(world.CollaboratorId), Actor);
        var notGiven = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.SaveAsync(world.OrgId, world.Hidden.Id, Staff(world.CollaboratorId), Actor));
        var stranger = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.SaveAsync(world.OrgId, world.Granted.Id, Staff("auth0|nessuno"), Actor));
        await ChangeAsync<OrgMember>(
            db,
            (await db.OrgMembers.AsNoTracking().SingleAsync(m => m.UserId == world.CollaboratorId)).Id,
            m => m.Status = OrgMemberStatus.Deactivated);
        var deactivated = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.SaveAsync(world.OrgId, world.Granted.Id, Staff(world.CollaboratorId), Actor));

        Assert.Equal((PropertyCleanerKind.Staff, world.OwnerUserId, 4000), (owner.CleanerKind, owner.StaffUserId, owner.PriceCents));
        Assert.Null(given.PriceCents);
        Assert.All([notGiven, stranger, deactivated], error => Assert.Equal(PropertyCleaningSetupRules.StaffInvalid, error.Code));
    }

    [Fact]
    public async Task APropertyInLongRentMode_HasNoStaysToClean()
    {
        await using var db = HostScopeScenario.NewInMemoryDb();
        var world = await HostScopeScenario.SeedAsync(db);
        await ChangeAsync<Property>(db, world.Hidden.Id, p => p.RentalMode = RentalMode.Long);

        var error = await Assert.ThrowsAsync<DomainRuleException>(
            () => Service(db).SaveAsync(world.OrgId, world.Hidden.Id, Staff(world.OwnerUserId), Actor));

        Assert.Equal("property_not_bookable_in_long_mode", error.Code);
    }

    // ─── The time, the replacement and the answer ────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(4)]
    [InlineData(1441)]
    public async Task Duration_IsFiveMinutesToADay(int minutes)
    {
        using var scenario = await ServiceRequestScenario.CreateAsync();

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => Service(scenario).SaveAsync(
            scenario.HostOrgId, scenario.PropertyId, Supplier(scenario.SupplierOrgId) with { DurationMinutes = minutes }, Actor));

        Assert.Equal(PropertyCleaningSetupRules.DurationInvalid, error.Code);
    }

    [Fact]
    public async Task StartTime_IsInWholeMinutes()
    {
        using var scenario = await ServiceRequestScenario.CreateAsync();

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => Service(scenario).SaveAsync(
            scenario.HostOrgId, scenario.PropertyId, Supplier(scenario.SupplierOrgId) with { StartTime = new TimeOnly(11, 0, 30) }, Actor));

        Assert.Equal(PropertyCleaningSetupRules.StartTimeInvalid, error.Code);
    }

    /// <summary>A save replaces what the property had (the supplier of before is gone when the staff takes over), and the answer says what really applies: the defaults of the time and the length.</summary>
    [Fact]
    public async Task Save_ReplacesTheSetup_AndTheAnswerSaysTheTimeAndTheLengthThatApply()
    {
        using var scenario = await ServiceRequestScenario.CreateAsync();
        var service = Service(scenario);
        await ChangeAsync<Property>(scenario.Db, scenario.PropertyId, p => p.CheckOutBy = new TimeOnly(11, 0));
        // The host is the owner of its org: a member who reaches every property.
        scenario.Db.OrgMembers.Add(new OrgMember { OrgId = scenario.HostOrgId, UserId = ServiceRequestScenario.HostUserId, Role = OrgRole.Owner });
        await scenario.Db.SaveChangesAsync();

        var defaults = await service.GetAsync(scenario.HostOrgId, scenario.PropertyId);
        var first = await service.SaveAsync(scenario.HostOrgId, scenario.PropertyId, Supplier(scenario.SupplierOrgId), Actor);
        var timed = await service.SaveAsync(
            scenario.HostOrgId,
            scenario.PropertyId,
            Supplier(scenario.SupplierOrgId) with { StartTime = new TimeOnly(12, 30), DurationMinutes = 90 },
            Actor);
        var staff = await service.SaveAsync(scenario.HostOrgId, scenario.PropertyId, Staff(ServiceRequestScenario.HostUserId), Actor);

        Assert.False(defaults.Configured);
        Assert.Equal((new TimeOnly(11, 0), CleaningSchedule.DefaultDurationMinutes, "EUR"), (defaults.EffectiveStartTime, defaults.EffectiveDurationMinutes, defaults.Currency));
        Assert.Null(first.StartTime);
        Assert.Null(first.DurationMinutes);
        Assert.Equal((new TimeOnly(11, 0), 120), (first.EffectiveStartTime, first.EffectiveDurationMinutes));
        Assert.Equal((new TimeOnly(12, 30), new TimeOnly(12, 30), 90, 90), (timed.StartTime, timed.EffectiveStartTime, timed.DurationMinutes, timed.EffectiveDurationMinutes));
        Assert.Equal((PropertyCleanerKind.Staff, ServiceRequestScenario.HostUserId), (staff.CleanerKind, staff.StaffUserId));
        Assert.Null(staff.SupplierOrgId);
        var row = await scenario.Db.PropertyCleaningSetups.AsNoTracking().SingleAsync();
        Assert.Null(row.SupplierOrgId);
        Assert.Null(row.StartTime);
    }

    /// <summary>The supplier of a setup is «trusted» when the org lists it as one for this property (a list with no property is every property); it is a preference, never a rule.</summary>
    [Fact]
    public async Task View_SaysWhetherTheSupplierIsOneOfTheTrustedOnes()
    {
        using var scenario = await ServiceRequestScenario.CreateAsync();
        var service = Service(scenario);

        var plain = await service.SaveAsync(scenario.HostOrgId, scenario.PropertyId, Supplier(scenario.SupplierOrgId), Actor);
        scenario.Db.TrustedSuppliers.Add(new TrustedSupplier
        {
            OrgId = scenario.HostOrgId,
            SupplierOrgId = scenario.SupplierOrgId,
            AssignedPropertyIds = [Guid.NewGuid()],
            CreatedAt = scenario.Clock.GetUtcNow().UtcDateTime,
        });
        await scenario.Db.SaveChangesAsync();
        var elsewhere = await service.GetAsync(scenario.HostOrgId, scenario.PropertyId);
        var trusted = await scenario.Db.TrustedSuppliers.SingleAsync();
        trusted.AssignedPropertyIds = [];
        await scenario.Db.SaveChangesAsync();
        var everywhere = await service.GetAsync(scenario.HostOrgId, scenario.PropertyId);

        Assert.False(plain.SupplierIsTrusted);
        Assert.False(elsewhere.SupplierIsTrusted);
        Assert.True(everywhere.SupplierIsTrusted);
    }

    // ─── The texts ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every message of the setup exists in Italian and in English (the API answers a localized text for each code).</summary>
    [Fact]
    public void Messages_OfTheSetupAndTheAutomation_ExistInItalianAndEnglish()
    {
        var root = RepositoryRoot();
        var italian = Keys(Path.Combine(root, "Casazen.Web", "Resources", "SharedResources.resx"));
        var english = Keys(Path.Combine(root, "Casazen.Web", "Resources", "SharedResources.en.resx"));
        string[] keys =
        [
            .. PropertyCleaningSetupRules.MessageKeys,
            "ServiceRequestCannotReschedule",
            "CleaningAutomationSetupInvalid",
            "CleaningAutomationStaffUnavailable",
            "CleaningAutomationSupplierCurrencyUnsupported",
            InternalActivityErrors.CancelledMessageKey,
        ];

        Assert.All(keys, key =>
        {
            Assert.True(italian.TryGetValue(key, out var it) && !string.IsNullOrWhiteSpace(it), $"{key}: missing in Italian");
            Assert.True(english.TryGetValue(key, out var en) && !string.IsNullOrWhiteSpace(en), $"{key}: missing in English");
            Assert.NotEqual(it, en);
        });
    }

    private static Dictionary<string, string> Keys(string path) =>
        XDocument.Load(path).Root!
            .Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!, e => (string?)e.Element("value") ?? string.Empty, StringComparer.Ordinal);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
