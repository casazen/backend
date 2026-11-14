using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E13/F01/US01/T01 (NF-110, NF-115): the versioned mandate of an owner, the standard mandate applied in one call, the encrypted billing address and the
/// commission rules of the channels with their defaults (⚖ R2), with the reach of the caller.
/// </summary>
public class OwnerMandateServiceTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly HostScope OrgWide = new(OrgId);

    /// <summary>Money/data: a new version ends the one in force the day before it starts; versions never overlap and the ledger picks the one of the day.</summary>
    [Fact]
    public async Task Mandate_NewVersionClosesPrevious_NoOverlap_Test()
    {
        var world = World.Create();
        var owner = await world.OwnerAsync(start: new DateOnly(2025, 1, 1));

        var first = await world.Service.CreateVersionAsync(OrgWide, owner, Command(new DateOnly(2026, 1, 1), 15m), "auth0|admin");
        var second = await world.Service.CreateVersionAsync(OrgWide, owner, Command(new DateOnly(2026, 6, 1), 18m, cleaning: 45m), "auth0|admin");

        var versions = await world.Service.ListAsync(OrgWide, owner);
        Assert.Equal([second.Id, first.Id], versions.Select(v => v.Id));
        var closed = versions.Single(v => v.Id == first.Id);
        Assert.Equal((new DateOnly(2026, 1, 1), new DateOnly(2026, 5, 31)), (closed.ValidFrom, closed.ValidTo!.Value));
        Assert.Null(versions.Single(v => v.Id == second.Id).ValidTo);
        Assert.All(versions, v => Assert.Null(v.PropertyOwnershipId));
        Assert.Equal((CommissionBase.GrossExclTouristTax, false), (second.CommissionBase, second.CommissionIncludesVat));

        // The day before the new version is the old one's; the first day is the new one's: one mandate a day, never two, never none in between.
        Assert.Equal(15m, OwnerMandateRules.InForce(versions, owner, null, new DateOnly(2026, 5, 31))!.CommissionPercent);
        Assert.Equal(18m, OwnerMandateRules.InForce(versions, owner, null, new DateOnly(2026, 6, 1))!.CommissionPercent);
        Assert.Null(OwnerMandateRules.InForce(versions, owner, null, new DateOnly(2025, 12, 31)));
        Assert.Null(OwnerMandateRules.InForce(versions, Guid.NewGuid(), null, new DateOnly(2026, 7, 1)));

        // A version that would start on or before the last one is refused: it would overlap.
        var overlap = await Assert.ThrowsAsync<DomainConflictException>(
            () => world.Service.CreateVersionAsync(OrgWide, owner, Command(new DateOnly(2026, 6, 1), 20m), null));
        Assert.Equal(OwnerMandateErrors.MandateOverlap, overlap.Code);

        // A percent outside 0-100 or with three decimals, and a negative flat fee, are refused with their field; nothing is written.
        foreach (var (command, field) in new[]
                 {
                     (Command(new DateOnly(2026, 9, 1), 100.01m), "commissionPercent"),
                     (Command(new DateOnly(2026, 9, 1), 12.345m), "commissionPercent"),
                     (Command(new DateOnly(2026, 9, 1), 10m, cleaning: -1m), "cleaningFlatRate"),
                     (Command(default, 10m), "validFrom"),
                 })
        {
            var invalid = await Assert.ThrowsAsync<DomainRuleException>(() => world.Service.CreateVersionAsync(OrgWide, owner, command, null));
            Assert.Equal(OwnerMandateErrors.MandateInvalid, invalid.Code);
            Assert.Equal(field, invalid.Extensions["field"]);
        }

        Assert.Equal(2, await world.Db.OwnerMandates.CountAsync());

        // An owner of another org is the 404 of an owner that does not exist.
        await Assert.ThrowsAsync<NotFoundException>(() => world.Service.ListAsync(new HostScope(Guid.NewGuid()), owner));
    }

    /// <summary>Data: the standard mandate goes only to the owners with an ownership not ended and no mandate; the others keep theirs and are counted skipped.</summary>
    [Fact]
    public async Task MandateTemplate_OnlyOwnersWithoutActiveMandate_Test()
    {
        var world = World.Create();
        var without = await world.OwnerAsync(start: new DateOnly(2025, 1, 1));
        var withMandate = await world.OwnerAsync(start: new DateOnly(2025, 1, 1));
        var sold = await world.OwnerAsync(start: new DateOnly(2020, 1, 1), end: new DateOnly(2025, 12, 31));
        var noProperty = await world.OwnerAsync(start: null);
        var existing = await world.Service.CreateVersionAsync(OrgWide, withMandate, Command(new DateOnly(2025, 1, 1), 22m), null);

        var template = Command(new DateOnly(2026, 1, 1), 15m, cleaning: 40m) with { PassthroughPaymentFees = true };
        var result = await world.Service.ApplyTemplateAsync(OrgWide, new OwnerMandateTemplateCommand { Template = template }, "auth0|admin");

        Assert.Equal((1, 3), (result.Created, result.Skipped));
        Assert.Equal([without], result.CreatedOwnerIds);
        Assert.Equal(new[] { withMandate, sold, noProperty }.Order(), result.SkippedOwnerIds.Order());
        var created = Assert.Single(await world.Db.OwnerMandates.AsNoTracking().Where(m => m.PropertyOwnerId == without).ToListAsync());
        Assert.Equal((15m, 40m, true, new DateOnly(2026, 1, 1), (DateOnly?)null), (created.CommissionPercent, created.CleaningFlatRate!.Value, created.PassthroughPaymentFees, created.ValidFrom, created.ValidTo));

        // The owner that had one keeps it as it was: the template never overlaps or changes an existing mandate.
        var kept = Assert.Single(await world.Db.OwnerMandates.AsNoTracking().Where(m => m.PropertyOwnerId == withMandate).ToListAsync());
        Assert.Equal((existing.Id, 22m, (DateOnly?)null), (kept.Id, kept.CommissionPercent, kept.ValidTo));

        // A second call finds every owner with a mandate: nothing more is created.
        var again = await world.Service.ApplyTemplateAsync(OrgWide, new OwnerMandateTemplateCommand { Template = template }, null);
        Assert.Equal((0, 4), (again.Created, again.Skipped));
        Assert.Equal(2, await world.Db.OwnerMandates.CountAsync());

        // A list restricts the owners, an empty list is never «everyone», an owner that is not of the org is a 404; nothing is written by a refusal.
        var other = await world.OwnerAsync(start: new DateOnly(2025, 1, 1));
        var empty = await Assert.ThrowsAsync<DomainRuleException>(
            () => world.Service.ApplyTemplateAsync(OrgWide, new OwnerMandateTemplateCommand { Template = template, OwnerIds = [] }, null));
        Assert.Equal(OwnerMandateErrors.TemplateOwnersEmpty, empty.Code);
        await Assert.ThrowsAsync<NotFoundException>(
            () => world.Service.ApplyTemplateAsync(OrgWide, new OwnerMandateTemplateCommand { Template = template, OwnerIds = [other, Guid.NewGuid()] }, null));
        Assert.False(await world.Db.OwnerMandates.AnyAsync(m => m.PropertyOwnerId == other));

        var listed = await world.Service.ApplyTemplateAsync(OrgWide, new OwnerMandateTemplateCommand { Template = template, OwnerIds = [other] }, null);
        Assert.Equal([other], listed.CreatedOwnerIds);
        Assert.Equal(0, listed.Skipped);
    }

    /// <summary>Permission: the channel rules are of the whole org; a caller restricted to some properties can neither save nor read them, even with the permission.</summary>
    [Fact]
    public async Task ChannelRules_PartialScopeCannotSave_Test()
    {
        foreach (var restricted in new[] { new HostScope(OrgId, GrantedToUserId: "auth0|solo-alcuni"), new HostScope(OrgId, OwnerId: "auth0|solo-alcuni") })
        {
            var mandates = new Mock<IOwnerMandateService>(MockBehavior.Strict);
            var controller = new ChannelCommissionRulesController(mandates.Object, Org(), Scope(restricted)) { ControllerContext = Context() };

            var save = await controller.Put([new ChannelCommissionRuleCommand { Channel = BookingSource.BookingCom, Percent = 1m, ValidFrom = new DateOnly(2026, 1, 1) }]);
            var read = await controller.Get();

            Assert.IsType<ForbidResult>(save.Result);
            Assert.IsType<ForbidResult>(read.Result);
            mandates.VerifyNoOtherCalls();
        }

        // The whole org is served.
        var served = new Mock<IOwnerMandateService>();
        served.Setup(m => m.GetChannelRulesAsync(OrgId, It.IsAny<CancellationToken>())).ReturnsAsync(new ChannelCommissionRuleSet([], []));
        var allowed = new ChannelCommissionRulesController(served.Object, Org(), Scope(OrgWide)) { ControllerContext = Context() };
        Assert.IsType<OkObjectResult>((await allowed.Get()).Result);
    }

    /// <summary>
    /// Money: without a rule the configured default applies and is marked; a rule in force replaces it; the rules of a channel never overlap; a rule saved
    /// again as it was keeps its id (the posted lines name it).
    /// </summary>
    [Fact]
    public async Task ChannelRules_DefaultsMarked_RuleReplacesIt_SameRuleKeepsItsId_Test()
    {
        var world = World.Create();

        var defaults = await world.Service.GetChannelRulesAsync(OrgId);
        Assert.Empty(defaults.Rules);
        var booking = defaults.Channels.Single(c => c.Channel == BookingSource.BookingCom);
        Assert.Equal((15m, true, CommissionBase.GrossExclTouristTax), (booking.Percent!.Value, booking.IsDefault, booking.Base!.Value));
        var airbnb = defaults.Channels.Single(c => c.Channel == BookingSource.Airbnb);
        Assert.Equal((15.5m, true), (airbnb.Percent!.Value, airbnb.IsDefault));
        var agoda = defaults.Channels.Single(c => c.Channel == BookingSource.Agoda);
        Assert.Equal(((decimal?)null, false), (agoda.Percent, agoda.IsDefault));
        Assert.DoesNotContain(defaults.Channels, c => c.Channel is BookingSource.Direct or BookingSource.Manual or BookingSource.Local);

        var saved = await world.Service.SaveChannelRulesAsync(
            OrgId,
            [
                new ChannelCommissionRuleCommand { Channel = BookingSource.BookingCom, Percent = 16m, ValidFrom = new DateOnly(2026, 1, 1) },
                new ChannelCommissionRuleCommand
                {
                    Channel = BookingSource.Agoda, Percent = 17.5m, Base = CommissionBase.GrossExclTouristTaxAndCleaning, ValidFrom = new DateOnly(2026, 1, 1),
                },
            ],
            "auth0|admin");
        var rule = saved.Rules.Single(r => r.Channel == nameof(BookingSource.BookingCom));
        var inForce = saved.Channels.Single(c => c.Channel == BookingSource.BookingCom);
        Assert.Equal((16m, false, rule.Id), (inForce.Percent!.Value, inForce.IsDefault, inForce.RuleId!.Value));
        Assert.Equal(17.5m, saved.Channels.Single(c => c.Channel == BookingSource.Agoda).Percent);

        // Saved again as it is, the Booking rule keeps its row; the Agoda one, left out, is removed (the default of nobody: nothing applies).
        var again = await world.Service.SaveChannelRulesAsync(
            OrgId, [new ChannelCommissionRuleCommand { Channel = BookingSource.BookingCom, Percent = 16m, ValidFrom = new DateOnly(2026, 1, 1) }], null);
        Assert.Equal(rule.Id, Assert.Single(again.Rules).Id);
        Assert.Null(again.Channels.Single(c => c.Channel == BookingSource.Agoda).Percent);

        // Two rules of one channel that overlap, a channel that is not a portal, a percent out of range: 422 with the row and the field, nothing written.
        foreach (var (rules, index, field) in new (ChannelCommissionRuleCommand[] Rules, int Index, string Field)[]
                 {
                     ([
                         new() { Channel = BookingSource.Vrbo, Percent = 8m, ValidFrom = new DateOnly(2026, 1, 1) },
                         new() { Channel = BookingSource.Vrbo, Percent = 9m, ValidFrom = new DateOnly(2026, 6, 1) },
                     ], 1, "validFrom"),
                     ([new() { Channel = BookingSource.Direct, Percent = 3m, ValidFrom = new DateOnly(2026, 1, 1) }], 0, "channel"),
                     ([new() { Channel = BookingSource.Expedia, Percent = 101m, ValidFrom = new DateOnly(2026, 1, 1) }], 0, "percent"),
                 })
        {
            var invalid = await Assert.ThrowsAsync<DomainRuleException>(() => world.Service.SaveChannelRulesAsync(OrgId, rules, null));
            Assert.Equal(OwnerMandateErrors.ChannelRulesInvalid, invalid.Code);
            Assert.Equal(index, invalid.Extensions["index"]);
            Assert.Equal(field, invalid.Extensions["field"]);
        }

        Assert.Equal(rule.Id, (await world.Db.OrgChannelCommissionRules.AsNoTracking().SingleAsync()).Id);
    }

    /// <summary>
    /// Permission/privacy: the address of an owner is saved only within the reach of the caller (an owner with a property outside it is a 404), Italy needs
    /// the province, and the address is stored encrypted.
    /// </summary>
    [Fact]
    public async Task OwnerAddress_OnlyAnOwnerWithinReach_IsSavedEncrypted_Test()
    {
        var world = World.Create();
        var inside = await world.OwnerAsync(start: new DateOnly(2025, 1, 1), propertyOwner: "auth0|solo-alcuni");
        var shared = await world.OwnerAsync(start: new DateOnly(2025, 1, 1), propertyOwner: "auth0|solo-alcuni");
        await world.PeriodAsync(shared, new DateOnly(2025, 1, 1), propertyOwner: "auth0|agency");
        var partial = new HostScope(OrgId, OwnerId: "auth0|solo-alcuni");
        var address = new OwnerAddressCommand { Street = "Via Nassa", BuildingNumber = "5", PostalCode = "6900", Town = "Lugano", Country = "ch" };

        var saved = await world.Service.SaveAddressAsync(partial, inside, address, "auth0|solo-alcuni");
        Assert.Equal(("Via Nassa", "5", "6900", "Lugano", "CH"), (saved.BillingStreet, saved.BillingCivic, saved.BillingPostalCode, saved.BillingCity, saved.BillingCountry));

        // The owner also holds a property the caller does not reach: the same 404 as an owner that does not exist, nothing written.
        await Assert.ThrowsAsync<NotFoundException>(() => world.Service.SaveAddressAsync(partial, shared, address, null));
        await Assert.ThrowsAsync<NotFoundException>(() => world.Service.GetAddressAsync(partial, shared));
        Assert.Null((await world.Db.PropertyOwners.AsNoTracking().SingleAsync(o => o.Id == shared)).BillingStreet);
        Assert.Equal("Via Nassa", (await world.Service.GetAddressAsync(OrgWide, inside)).BillingStreet);

        // Italy needs the province (as the readiness of the owner does); a country is two letters.
        var italian = await Assert.ThrowsAsync<DomainRuleException>(
            () => world.Service.SaveAddressAsync(OrgWide, shared, address with { Country = "IT", PostalCode = "20121", Town = "Milano" }, null));
        Assert.Equal(OwnerMandateErrors.AddressInvalid, italian.Code);
        Assert.Equal("province", italian.Extensions["field"]);
        var country = await Assert.ThrowsAsync<DomainRuleException>(() => world.Service.SaveAddressAsync(OrgWide, shared, address with { Country = "Svizzera" }, null));
        Assert.Equal("country", country.Extensions["field"]);

        // At rest the columns hold a Data Protection payload, never the street.
        Assert.True(Casazen.Infrastructure.Data.Encryption.EncryptedColumns.IsConfigured(world.Db));
        var column = world.Db.Model.FindEntityType(typeof(PropertyOwner))!.FindProperty(nameof(PropertyOwner.BillingStreet))!;
        var stored = (string)column.GetValueConverter()!.ConvertToProvider("Via Nassa")!;
        Assert.StartsWith(Casazen.Infrastructure.Data.Encryption.EncryptedColumns.ProtectedPayloadPrefix, stored);
    }

    /// <summary>
    /// Permission: the mandates are read, written and applied only within the reach of the caller: an owner with a property outside it, or with none, is the 404 of
    /// an owner that does not exist, and the standard mandate without a list reaches only the owners inside (the others are neither created nor counted).
    /// </summary>
    [Fact]
    public async Task Mandates_PartialScope_ListCreateAndTemplateStayWithinReach_Test()
    {
        var world = World.Create();
        var inside = await world.OwnerAsync(start: new DateOnly(2025, 1, 1), propertyOwner: "auth0|solo-alcuni");
        var alsoInside = await world.OwnerAsync(start: new DateOnly(2025, 1, 1), propertyOwner: "auth0|solo-alcuni");
        var shared = await world.OwnerAsync(start: new DateOnly(2025, 1, 1), propertyOwner: "auth0|solo-alcuni");
        await world.PeriodAsync(shared, new DateOnly(2025, 1, 1), propertyOwner: "auth0|agency");
        var outside = await world.OwnerAsync(start: new DateOnly(2025, 1, 1), propertyOwner: "auth0|agency");
        var noProperty = await world.OwnerAsync(start: null);
        var partial = new HostScope(OrgId, OwnerId: "auth0|solo-alcuni");

        // One owner, read and written: inside it works, outside it is a 404 and nothing is written.
        var created = await world.Service.CreateVersionAsync(partial, inside, Command(new DateOnly(2026, 1, 1), 15m), "auth0|solo-alcuni");
        Assert.Equal([created.Id], (await world.Service.ListAsync(partial, inside)).Select(v => v.Id));
        foreach (var hidden in new[] { shared, outside, noProperty })
        {
            await Assert.ThrowsAsync<NotFoundException>(() => world.Service.ListAsync(partial, hidden));
            await Assert.ThrowsAsync<NotFoundException>(() => world.Service.CreateVersionAsync(partial, hidden, Command(new DateOnly(2026, 1, 1), 15m), null));
        }

        Assert.Equal(1, await world.Db.OwnerMandates.CountAsync());

        // The standard mandate without a list: the owner inside that has none gets one, the one that has a mandate is skipped, those outside are not touched.
        var template = new OwnerMandateTemplateCommand { Template = Command(new DateOnly(2026, 1, 1), 12m) };
        var result = await world.Service.ApplyTemplateAsync(partial, template, null);
        Assert.Equal([alsoInside], result.CreatedOwnerIds);
        Assert.Equal([inside], result.SkippedOwnerIds);
        Assert.False(await world.Db.OwnerMandates.AnyAsync(m => m.PropertyOwnerId == shared || m.PropertyOwnerId == outside || m.PropertyOwnerId == noProperty));

        // A list names owners inside only: one outside is the 404 of the whole call and nothing is written.
        var before = await world.Db.OwnerMandates.CountAsync();
        var listed = new OwnerMandateTemplateCommand { Template = Command(new DateOnly(2026, 6, 1), 9m), OwnerIds = [alsoInside, shared] };
        await Assert.ThrowsAsync<NotFoundException>(() => world.Service.ApplyTemplateAsync(partial, listed, null));
        Assert.Equal(before, await world.Db.OwnerMandates.CountAsync());
    }

    /// <summary>
    /// Permission/privacy: the address is read with the finance read permission and written with the manage one (the owner view alone is not enough); the
    /// mandates are read with the owner view and written, applied and the channel rules replaced with the manage permission; the channel rules are read with the
    /// statement view.
    /// </summary>
    [Fact]
    public void Mandates_AddressAndChannelRules_NeedTheirOwnPermission_Test()
    {
        static string? PolicyOf(Type controller, string action) =>
            controller.GetMethod(action)!.GetCustomAttributes(typeof(AuthorizeAttribute), false).Cast<AuthorizeAttribute>().Select(a => a.Policy).SingleOrDefault();

        static string? ClassPolicyOf(Type controller) =>
            controller.GetCustomAttributes(typeof(AuthorizeAttribute), false).Cast<AuthorizeAttribute>().Select(a => a.Policy).SingleOrDefault();

        Assert.Equal(CasazenPolicies.AgencyOwnerView, ClassPolicyOf(typeof(OwnersController)));
        Assert.Equal(CasazenPolicies.AgencyOwnerFinancialRead, PolicyOf(typeof(OwnersController), nameof(OwnersController.GetAddress)));
        Assert.Equal(CasazenPolicies.AgencyOwnerFinancialManage, PolicyOf(typeof(OwnersController), nameof(OwnersController.SaveAddress)));

        Assert.Equal(CasazenPolicies.AgencyOwnerView, ClassPolicyOf(typeof(OwnerMandatesController)));
        Assert.Null(PolicyOf(typeof(OwnerMandatesController), nameof(OwnerMandatesController.List)));
        Assert.Equal(CasazenPolicies.AgencyOwnerFinancialManage, PolicyOf(typeof(OwnerMandatesController), nameof(OwnerMandatesController.Create)));
        Assert.Equal(CasazenPolicies.AgencyOwnerFinancialManage, PolicyOf(typeof(OwnerMandatesController), nameof(OwnerMandatesController.ApplyTemplate)));

        Assert.Equal(CasazenPolicies.AgencyStatementView, ClassPolicyOf(typeof(ChannelCommissionRulesController)));
        Assert.Null(PolicyOf(typeof(ChannelCommissionRulesController), nameof(ChannelCommissionRulesController.Get)));
        Assert.Equal(CasazenPolicies.AgencyOwnerFinancialManage, PolicyOf(typeof(ChannelCommissionRulesController), nameof(ChannelCommissionRulesController.Put)));
    }

    /// <summary>
    /// Money: the percent of a mandate and of a channel rule is required: a request without it (or with the field misspelt) is 422 and writes nothing, never a
    /// commission of 0% by omission; the 0% a request writes is a choice and is kept.
    /// </summary>
    [Fact]
    public async Task CommissionPercent_Omitted_IsRefused_NeverZero_Test()
    {
        var world = World.Create();
        var owner = await world.OwnerAsync(start: new DateOnly(2025, 1, 1));
        var withoutPercent = new OwnerMandateCommand { ValidFrom = new DateOnly(2026, 1, 1) };

        var version = await Assert.ThrowsAsync<DomainRuleException>(() => world.Service.CreateVersionAsync(OrgWide, owner, withoutPercent, null));
        Assert.Equal(OwnerMandateErrors.MandateInvalid, version.Code);
        Assert.Equal("commissionPercent", version.Extensions["field"]);

        var template = await Assert.ThrowsAsync<DomainRuleException>(
            () => world.Service.ApplyTemplateAsync(OrgWide, new OwnerMandateTemplateCommand { Template = withoutPercent }, null));
        Assert.Equal(OwnerMandateErrors.MandateInvalid, template.Code);
        Assert.Equal("commissionPercent", template.Extensions["field"]);

        var rules = new[] { new ChannelCommissionRuleCommand { Channel = BookingSource.BookingCom, ValidFrom = new DateOnly(2026, 1, 1) } };
        var rule = await Assert.ThrowsAsync<DomainRuleException>(() => world.Service.SaveChannelRulesAsync(OrgId, rules, null));
        Assert.Equal(OwnerMandateErrors.ChannelRulesInvalid, rule.Code);
        Assert.Equal(0, rule.Extensions["index"]);
        Assert.Equal("percent", rule.Extensions["field"]);

        Assert.False(await world.Db.OwnerMandates.AnyAsync());
        Assert.False(await world.Db.OrgChannelCommissionRules.AnyAsync());

        var zero = await world.Service.CreateVersionAsync(OrgWide, owner, withoutPercent with { CommissionPercent = 0m }, null);
        Assert.Equal(0m, zero.CommissionPercent);
    }

    private static OwnerMandateCommand Command(DateOnly from, decimal percent, decimal? cleaning = null) =>
        new() { ValidFrom = from, CommissionPercent = percent, CleaningFlatRate = cleaning };

    private static IOrgContextResolver Org()
    {
        var resolver = new Mock<IOrgContextResolver>();
        resolver.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OrgId);
        return resolver.Object;
    }

    private static IHostScopeResolver Scope(HostScope scope)
    {
        var resolver = new Mock<IHostScopeResolver>();
        resolver.Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>(), OrgId, It.IsAny<CancellationToken>())).ReturnsAsync(scope);
        return resolver.Object;
    }

    private static ControllerContext Context()
    {
        var identity = new ClaimsIdentity([new Claim("sub", "auth0|solo-alcuni"), new Claim(ClaimTypes.NameIdentifier, "auth0|solo-alcuni")], "test");
        return new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } };
    }

    /// <summary>An agency over one in-memory database, with encryption, the committed channel defaults and a clock on 14 November 2026.</summary>
    private sealed class World
    {
        private World(AppDbContext db)
        {
            Db = db;
            var options = new OwnerAccountingOptions();
            foreach (var (channel, percent) in new[] { ("BookingCom", 15m), ("Expedia", 18m), ("Airbnb", 15.5m), ("Vrbo", 8m) })
                options.ChannelCommissionDefaults[channel] = percent;
            Service = new OwnerMandateService(
                db,
                new FakeTimeProvider(new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero)),
                Microsoft.Extensions.Options.Options.Create(options),
                NullLogger<OwnerMandateService>.Instance);
        }

        public AppDbContext Db { get; }

        public OwnerMandateService Service { get; }

        public static World Create() => new(new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"OwnerMandates_{Guid.NewGuid()}").Options,
            new MandateTenant(),
            new EphemeralDataProtectionProvider()));

        /// <summary>An owner of the org and, from <paramref name="start"/>, the whole of a property of its own (none without a start).</summary>
        public async Task<Guid> OwnerAsync(DateOnly? start, DateOnly? end = null, string propertyOwner = "auth0|agency")
        {
            var owner = new PropertyOwner { OrgId = OrgId, FirstName = "TEST", LastName = "Proprietario", Email = $"{Guid.NewGuid():N}@example.com" };
            Db.PropertyOwners.Add(owner);
            await Db.SaveChangesAsync();
            if (start is { } first)
                await PeriodAsync(owner.Id, first, end, propertyOwner);
            return owner.Id;
        }

        /// <summary>A property of <paramref name="propertyOwner"/> held in full by the owner from <paramref name="start"/>.</summary>
        public async Task PeriodAsync(Guid owner, DateOnly start, DateOnly? end = null, string propertyOwner = "auth0|agency")
        {
            var property = new Property { OrgId = OrgId, OwnerId = propertyOwner, Name = "TEST Casa", Address = "Via 1", City = "Lugano", Currency = "CHF" };
            Db.Properties.Add(property);
            Db.PropertyOwnerships.Add(new PropertyOwnership
            {
                OrgId = OrgId,
                PropertyId = property.Id,
                PropertyOwnerId = owner,
                SharePercent = 100m,
                OwnershipStart = start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                OwnershipEnd = end?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            });
            await Db.SaveChangesAsync();
        }
    }
}

file sealed class MandateTenant : ITenantContext
{
    public Guid? OrgId => null;

    public bool FilterEnabled => false;
}
