using System.Reflection;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E18/F01/US03/T01 (NF-117, ⚖ I6, I7, I8): the limit of units per owner (the third apartment let short makes a private person a business), the company tenant in the
/// property of a private owner (a «Da fare», a decision recorded and followed by the withholding facts) and the co-owner reporting field of the mandate. On EF InMemory, a
/// clock on 15 November 2026.
/// </summary>
public class OwnerTaxChecksTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly HostScope OrgWide = new(OrgId);
    private static readonly DateOnly Today = new(2026, 11, 15);

    [Fact]
    public async Task OwnerTaxChecks_ThirdUnitFlagsBusiness_Test()
    {
        var world = World.Create();
        var owner = await world.OwnerAsync();
        var units = await world.UnitsAsync(owner, 2);
        var stay = await world.BookingAsync(units[0], new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 13));

        var under = (await world.Checks.CheckUnitsAsync(OrgId, [owner], Today))[owner];
        Assert.Equal((true, 2, 0, 2, 2, false), (under.Applies, under.ManagedUnits, under.ExternalUnits, under.TotalUnits, under.MaxUnits, under.PresumedBusiness));
        Assert.True((await world.TreatmentAsync(owner, stay)).QualifiesAsShortLet);

        var declared = await world.Checks.DeclareExternalUnitsAsync(OrgWide, owner, 1, "auth0|admin");
        Assert.Equal((2, 1, 3, true), (declared.ManagedUnits, declared.ExternalUnits, declared.TotalUnits, declared.PresumedBusiness));
        var business = await world.TreatmentAsync(owner, stay);
        Assert.Equal((false, ShortLetTreatmentReasons.OwnerPresumedBusiness), (business.QualifiesAsShortLet, business.Reason));

        var back = await world.Checks.DeclareExternalUnitsAsync(OrgWide, owner, 0, "auth0|admin");
        Assert.False(back.PresumedBusiness);
        Assert.True((await world.TreatmentAsync(owner, stay)).QualifiesAsShortLet);

        await world.UnitsAsync(owner, 1);
        var three = (await world.Checks.CheckUnitsAsync(OrgId, [owner], Today))[owner];
        Assert.Equal((3, 0, true), (three.ManagedUnits, three.ExternalUnits, three.PresumedBusiness));
        Assert.Equal(ShortLetTreatmentReasons.OwnerPresumedBusiness, (await world.TreatmentAsync(owner, stay)).Reason);
    }

    /// <summary>
    /// Money: the facts the withholding reads are NOT behind <c>Features:TaxReportingExports</c>, which is off by default in production: with the flag off an owner past the limit is
    /// still a business and a decision already recorded is still followed (the flag silences only the warnings of the UI: the readiness, the statement version, the «Da fare»).
    /// The codes of the contract that the slices reading the checks rely on never change.
    /// </summary>
    [Fact]
    public async Task Facts_AreNotBehindTheFlag_TheCodesOfTheContractNeverChange_Test()
    {
        var world = World.Create(flagOn: false);
        Assert.False(world.Checks.WarningsEnabled);

        var owner = await world.OwnerAsync();
        var units = await world.UnitsAsync(owner, 3);
        var stay = await world.BookingAsync(units[0], new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 13));
        var limit = (await world.Checks.CheckUnitsAsync(OrgId, [owner], Today))[owner];
        Assert.Equal((true, 3), (limit.PresumedBusiness, limit.TotalUnits));
        var business = await world.TreatmentAsync(owner, stay);
        Assert.Equal((false, ShortLetTreatmentReasons.OwnerPresumedBusiness), (business.QualifiesAsShortLet, business.Reason));

        var other = await world.OwnerAsync();
        var company = await world.CompanyStayAsync(await world.OwnedPropertyAsync(other));
        var waiting = await world.TreatmentAsync(other, company);
        Assert.Equal((true, ShortLetTreatmentReasons.ShortLet, true, true), (waiting.QualifiesAsShortLet, waiting.Reason, waiting.CompanyTenant, waiting.DecisionPending));
        world.Db.ShortLetTaxDecisions.Add(new ShortLetTaxDecision
        {
            OrgId = OrgId,
            BookingId = company,
            Regime = ShortLetTaxRegime.NotShortLet,
            Reason = "TEST",
            DecidedAt = World.Now.UtcDateTime,
            DecidedByUserId = "auth0|admin",
        });
        await world.Db.SaveChangesAsync();
        var decided = await world.TreatmentAsync(other, company);
        Assert.Equal((false, ShortLetTreatmentReasons.CompanyTenantNotShortLet), (decided.QualifiesAsShortLet, decided.Reason));

        Assert.Equal(
            ("short_let", "owner_presumed_business", "company_tenant_short_let", "company_tenant_not_short_let"),
            (ShortLetTreatmentReasons.ShortLet, ShortLetTreatmentReasons.OwnerPresumedBusiness, ShortLetTreatmentReasons.CompanyTenantShortLet, ShortLetTreatmentReasons.CompanyTenantNotShortLet));
        Assert.Equal(
            ("owner_tax_decision_invalid", "owner_tax_decision_not_applicable", "owner_external_units_invalid", "booking_not_found"),
            (OwnerTaxErrors.DecisionInvalid, OwnerTaxErrors.DecisionNotApplicable, OwnerTaxErrors.ExternalUnitsInvalid, OwnerTaxErrors.BookingNotFound));
        Assert.Equal(
            ("short_let_business_presumed", "short_let_business_presumed", "issuer_profile", "TaxReportingExports"),
            (OwnerReadinessWarnings.ShortLetBusinessPresumed, OwnerReadinessCodes.ShortLetBusinessPresumed, OwnerReadinessSuggestions.IssuerProfile, FeatureFlags.TaxReportingExports));
    }

    [Fact]
    public async Task Units_OnlyItalianShortRentApartmentsInForce_CountOnceForEachCoOwner_Test()
    {
        var world = World.Create();
        var owner = await world.OwnerAsync();
        var coOwner = await world.OwnerAsync();
        var start = new DateOnly(2025, 1, 1);

        await world.OwnsAsync(owner, await world.PropertyAsync(), start);
        await world.OwnsAsync(owner, await world.PropertyAsync(mode: RentalMode.Long), start);
        await world.OwnsAsync(owner, await world.PropertyAsync(country: "CH"), start);
        await world.OwnsAsync(owner, await world.PropertyAsync(deleted: true), start);
        await world.OwnsAsync(owner, await world.PropertyAsync(), start, end: new DateOnly(2026, 6, 30));
        await world.OwnsAsync(owner, await world.PropertyAsync(), new DateOnly(2027, 1, 1));

        var shared = await world.PropertyAsync();
        await world.OwnsAsync(owner, shared, start, share: 60m);
        await world.OwnsAsync(coOwner, shared, start, share: 40m);

        var twoPeriods = await world.PropertyAsync();
        await world.OwnsAsync(owner, twoPeriods, start, end: new DateOnly(2026, 3, 31));
        await world.OwnsAsync(owner, twoPeriods, new DateOnly(2026, 4, 1));

        var checks = await world.Checks.CheckUnitsAsync(OrgId, [owner, coOwner, Guid.NewGuid()], Today);

        Assert.Equal(2, checks.Count);
        Assert.Equal(3, checks[owner].ManagedUnits);
        Assert.True(checks[owner].PresumedBusiness);
        Assert.Equal(1, checks[coOwner].ManagedUnits);
        Assert.False(checks[coOwner].PresumedBusiness);

        Assert.Equal(0, (await world.Checks.CheckUnitsAsync(OrgId, [owner], new DateOnly(2024, 12, 31)))[owner].ManagedUnits);
        Assert.Equal(4, (await world.Checks.CheckUnitsAsync(OrgId, [owner], new DateOnly(2026, 6, 30)))[owner].ManagedUnits);
        Assert.Equal(3, (await world.Checks.CheckUnitsAsync(OrgId, [owner], new DateOnly(2026, 7, 1)))[owner].ManagedUnits);
    }

    /// <summary>
    /// Money: the limit is the configuration of the fiscal rules, never a number of the code (a higher one lets the third unit pass), is about natural persons (a legal entity or
    /// a VAT holder is a business already: nothing to presume), and starts with its tax year (a day of an earlier year presumes nothing).
    /// </summary>
    [Fact]
    public void CheckUnits_TheLimitIsConfiguration_AboutPrivatePersons_FromItsTaxYear_Test()
    {
        var id = Guid.NewGuid();
        var rules = new ShortStayFiscalOptions();

        Assert.True(OwnerTaxRules.CheckUnits(id, OwnerEntityKind.NaturalPerson, OwnerTaxRegime.Private, 2, 1, Today, rules).PresumedBusiness);
        Assert.True(OwnerTaxRules.CheckUnits(id, null, OwnerTaxRegime.Private, 3, 0, Today, rules).PresumedBusiness);
        Assert.False(OwnerTaxRules.CheckUnits(id, OwnerEntityKind.NaturalPerson, OwnerTaxRegime.Private, 2, 0, Today, rules).PresumedBusiness);
        Assert.False(OwnerTaxRules.CheckUnits(id, OwnerEntityKind.NaturalPerson, OwnerTaxRegime.Private, 3, 0, Today, new ShortStayFiscalOptions { MaxApartmentsPerTaxpayer = 4 }).PresumedBusiness);

        foreach (var (kind, regime) in new[] { (OwnerEntityKind.LegalEntity, OwnerTaxRegime.Private), (OwnerEntityKind.NaturalPerson, OwnerTaxRegime.VatHolder) })
        {
            var business = OwnerTaxRules.CheckUnits(id, kind, regime, 5, 5, Today, rules);
            Assert.Equal((false, 10, false), (business.Applies, business.TotalUnits, business.PresumedBusiness));
        }

        Assert.False(OwnerTaxRules.CheckUnits(id, OwnerEntityKind.NaturalPerson, OwnerTaxRegime.Private, 9, 0, new DateOnly(2025, 12, 31), rules).PresumedBusiness);
        Assert.True(OwnerTaxRules.CheckUnits(id, OwnerEntityKind.NaturalPerson, OwnerTaxRegime.Private, 3, 0, new DateOnly(2026, 1, 1), rules).PresumedBusiness);

        Assert.Equal(1, OwnerTaxRules.CheckUnits(id, null, OwnerTaxRegime.Private, 1, 0, Today, new ShortStayFiscalOptions { MaxApartmentsPerTaxpayer = 0 }).MaxUnits);
    }

    [Fact]
    public async Task DeclareExternalUnits_ARequiredCount_NothingIsWrittenByARefusal_Test()
    {
        var world = World.Create();
        var owner = await world.OwnerAsync(external: 1);

        foreach (var invalid in new int?[] { null, -1, PropertyOwner.ExternalShortLetUnitsMax + 1 })
        {
            var refused = await Assert.ThrowsAsync<DomainRuleException>(() => world.Checks.DeclareExternalUnitsAsync(OrgWide, owner, invalid, null));
            Assert.Equal(OwnerTaxErrors.ExternalUnitsInvalid, refused.Code);
            Assert.Equal("units", refused.Extensions["field"]);
        }

        Assert.Equal(1, (await world.Db.PropertyOwners.AsNoTracking().SingleAsync(o => o.Id == owner)).ExternalShortLetUnits);
        var zero = await world.Checks.DeclareExternalUnitsAsync(OrgWide, owner, 0, null);
        Assert.Equal(0, zero.ExternalUnits);
        Assert.Equal(0, (await world.Db.PropertyOwners.AsNoTracking().SingleAsync(o => o.Id == owner)).ExternalShortLetUnits);
        Assert.Equal(PropertyOwner.ExternalShortLetUnitsMax, (await world.Checks.DeclareExternalUnitsAsync(OrgWide, owner, PropertyOwner.ExternalShortLetUnitsMax, null)).ExternalUnits);
    }

    [Fact]
    public async Task OwnerLimit_PartialReach_AnOwnerWithAPropertyOutsideIsNotFound_TheCountIsTheWholePortfolio_Test()
    {
        var world = World.Create();
        var inside = await world.OwnerAsync();
        await world.UnitsAsync(inside, 3, createdBy: "auth0|solo-alcuni");
        var shared = await world.OwnerAsync();
        await world.UnitsAsync(shared, 1, createdBy: "auth0|solo-alcuni");
        await world.UnitsAsync(shared, 1, createdBy: "auth0|agency");
        var outside = await world.OwnerAsync();
        await world.UnitsAsync(outside, 1, createdBy: "auth0|agency");
        var noProperty = await world.OwnerAsync();
        var partial = new HostScope(OrgId, OwnerId: "auth0|solo-alcuni");

        Assert.True((await world.Checks.GetUnitsCheckAsync(partial, inside)).PresumedBusiness);
        Assert.Equal(1, (await world.Checks.DeclareExternalUnitsAsync(partial, inside, 1, "auth0|solo-alcuni")).ExternalUnits);
        foreach (var hidden in new[] { shared, outside, noProperty, Guid.NewGuid() })
        {
            var read = await Assert.ThrowsAsync<NotFoundException>(() => world.Checks.GetUnitsCheckAsync(partial, hidden));
            Assert.Equal(OwnerFiscalErrors.OwnerNotFound, read.Code);
            await Assert.ThrowsAsync<NotFoundException>(() => world.Checks.DeclareExternalUnitsAsync(partial, hidden, 2, null));
        }

        Assert.Equal(0, (await world.Db.PropertyOwners.AsNoTracking().SingleAsync(o => o.Id == shared)).ExternalShortLetUnits);

        Assert.Equal(2, (await world.Checks.GetUnitsCheckAsync(OrgWide, shared)).ManagedUnits);
        await Assert.ThrowsAsync<NotFoundException>(() => world.Checks.GetUnitsCheckAsync(new HostScope(Guid.NewGuid()), inside));
        Assert.Empty(await world.Checks.CheckUnitsAsync(Guid.NewGuid(), [inside, shared], Today));
        Assert.Empty(await world.Checks.CheckUnitsAsync(OrgId, [], Today));
    }

    /// <summary>
    /// Money: the stay of a company tenant in the Italian property of a private owner stays a short let until a person decides (no automatic change of regime, the decision is
    /// pending), then the withholding facts follow the decision, the latest one when it changes; every decision stays as history, the same one again is not written twice, and the
    /// «Da fare» closes with the first.
    /// </summary>
    [Fact]
    public async Task CompanyTenantDecision_DrivesWithholdingFacts_Test()
    {
        var world = World.Create();
        var owner = await world.OwnerAsync();
        var property = await world.PropertyAsync();
        await world.OwnsAsync(owner, property, new DateOnly(2025, 1, 1));
        var booking = await world.BookingAsync(property, new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 13));
        await world.InvoiceAsync(booking, InvoiceRecipientKind.Company);
        Assert.True(await world.Checks.RaiseCompanyTenantDecisionAsync(OrgId, booking));

        var pending = await world.TreatmentAsync(owner, booking);
        Assert.Equal((true, ShortLetTreatmentReasons.ShortLet, true, true), (pending.QualifiesAsShortLet, pending.Reason, pending.CompanyTenant, pending.DecisionPending));
        Assert.Null(pending.Decision);

        var notShortLet = await world.Checks.DecideAsync(OrgWide, Decision(booking, ShortLetTaxRegime.NotShortLet, "Confermato dal commercialista"), "auth0|admin");
        var against = await world.TreatmentAsync(owner, booking);
        Assert.Equal((false, ShortLetTreatmentReasons.CompanyTenantNotShortLet, false), (against.QualifiesAsShortLet, against.Reason, against.DecisionPending));
        Assert.Equal(ShortLetTaxRegime.NotShortLet, against.Decision);
        Assert.Equal(notShortLet.DecidedAt, against.DecidedAt);
        Assert.Equal(("auth0|admin", OrgId, booking), (notShortLet.DecidedByUserId, notShortLet.OrgId, notShortLet.BookingId));
        Assert.All(await world.TodosAsync(booking), t => Assert.NotNull(t.CompletedAt));

        var again = await world.Checks.DecideAsync(OrgWide, Decision(booking, ShortLetTaxRegime.NotShortLet, "Confermato dal commercialista"), "auth0|admin");
        Assert.Equal(notShortLet.Id, again.Id);
        Assert.Equal(1, await world.Db.ShortLetTaxDecisions.CountAsync());

        world.Clock.Advance(TimeSpan.FromHours(1));
        await world.Checks.DecideAsync(OrgWide, Decision(booking, ShortLetTaxRegime.ShortLet, "Segue la Cassazione"), "auth0|admin");
        var followed = await world.TreatmentAsync(owner, booking);
        Assert.Equal((true, ShortLetTreatmentReasons.CompanyTenantShortLet), (followed.QualifiesAsShortLet, followed.Reason));
        Assert.Equal(2, await world.Db.ShortLetTaxDecisions.CountAsync());

        world.Clock.Advance(TimeSpan.FromHours(1));
        await world.Checks.DecideAsync(OrgWide, Decision(booking, ShortLetTaxRegime.NotShortLet, "Di nuovo"), "auth0|admin");
        await world.SetRecipientAsync(booking, InvoiceRecipientKind.Guest);
        var stale = await world.TreatmentAsync(owner, booking);
        Assert.Equal((true, ShortLetTreatmentReasons.ShortLet, false, false), (stale.QualifiesAsShortLet, stale.Reason, stale.CompanyTenant, stale.DecisionPending));
    }

    /// <summary>
    /// Money: only the stay that is one of a company tenant takes a decision (a regime is never recorded for a stay nobody questioned, which would switch the withholding off): the
    /// guest of a private person, a company owner, a cancelled, no-show or still pending stay, a stay of more than 30 nights and a property outside Italy are all 422
    /// <c>owner_tax_decision_not_applicable</c>, and an owner past the limit treats every stay as a business whatever was decided.
    /// </summary>
    [Fact]
    public async Task CompanyTenantDecision_OnlyForAStayThatNeedsOne_Test()
    {
        var world = World.Create();
        var owner = await world.OwnerAsync();
        var company = await world.OwnerAsync(kind: OwnerEntityKind.LegalEntity);
        var vat = await world.OwnerAsync(regime: OwnerTaxRegime.VatHolder);
        var property = await world.PropertyAsync();
        await world.OwnsAsync(owner, property, new DateOnly(2025, 1, 1));
        var swiss = await world.PropertyAsync(country: "CH");
        await world.OwnsAsync(owner, swiss, new DateOnly(2025, 1, 1));

        var qualifies = await world.CompanyStayAsync(property);
        var privateGuest = await world.CompanyStayAsync(property, recipient: InvoiceRecipientKind.Guest);
        var cancelled = await world.CompanyStayAsync(property, status: BookingStatus.Cancelled);
        var pendingStatus = await world.CompanyStayAsync(property, status: BookingStatus.Pending);
        var noShow = await world.CompanyStayAsync(property, noShowAt: new DateTime(2026, 11, 11, 0, 0, 0, DateTimeKind.Utc));
        var tooLong = await world.CompanyStayAsync(property, checkIn: new DateOnly(2026, 9, 1), checkOut: new DateOnly(2026, 10, 2));
        var abroad = await world.CompanyStayAsync(swiss);
        var ofACompany = await world.CompanyStayAsync(await world.OwnedPropertyAsync(company));
        var ofAVatHolder = await world.CompanyStayAsync(await world.OwnedPropertyAsync(vat));
        var noOwner = await world.CompanyStayAsync(await world.PropertyAsync());

        foreach (var stay in new[] { privateGuest, cancelled, pendingStatus, noShow, tooLong, abroad, ofACompany, ofAVatHolder, noOwner })
        {
            var refused = await Assert.ThrowsAsync<DomainRuleException>(() => world.Checks.DecideAsync(OrgWide, Decision(stay, ShortLetTaxRegime.NotShortLet, "x"), "auth0|admin"));
            Assert.Equal(OwnerTaxErrors.DecisionNotApplicable, refused.Code);
            Assert.False((await world.TreatmentAsync(owner, stay)).CompanyTenant);
        }

        Assert.Equal(0, await world.Db.ShortLetTaxDecisions.CountAsync());
        Assert.False(await world.Checks.RaiseCompanyTenantDecisionAsync(OrgId, noShow));
        Assert.Equal(ShortLetTaxRegime.NotShortLet, (await world.Checks.DecideAsync(OrgWide, Decision(qualifies, ShortLetTaxRegime.NotShortLet, "ok"), "auth0|admin")).Regime);

        await world.UnitsAsync(owner, 2);
        await world.Checks.DeclareExternalUnitsAsync(OrgWide, owner, 1, null);
        await world.Checks.DecideAsync(OrgWide, Decision(qualifies, ShortLetTaxRegime.ShortLet, "il conduttore"), "auth0|admin");
        var business = await world.TreatmentAsync(owner, qualifies);
        Assert.Equal((false, ShortLetTreatmentReasons.OwnerPresumedBusiness, false), (business.QualifiesAsShortLet, business.Reason, business.DecisionPending));
    }

    [Fact]
    public async Task Decide_ARequiredRegimeAndReason_AreRefusedWithTheirField_Test()
    {
        var world = World.Create();
        var booking = await world.CompanyStayOfAPrivateOwnerAsync();

        foreach (var (command, field) in new (DecideShortLetTaxCommand Command, string Field)[]
                 {
                     (new DecideShortLetTaxCommand { BookingId = Guid.Empty, Regime = ShortLetTaxRegime.ShortLet, Reason = "x" }, "bookingId"),
                     (new DecideShortLetTaxCommand { BookingId = booking, Regime = null, Reason = "x" }, "regime"),
                     (new DecideShortLetTaxCommand { BookingId = booking, Regime = (ShortLetTaxRegime)99, Reason = "x" }, "regime"),
                     (new DecideShortLetTaxCommand { BookingId = booking, Regime = ShortLetTaxRegime.ShortLet, Reason = null }, "reason"),
                     (new DecideShortLetTaxCommand { BookingId = booking, Regime = ShortLetTaxRegime.ShortLet, Reason = "   " }, "reason"),
                     (new DecideShortLetTaxCommand { BookingId = booking, Regime = ShortLetTaxRegime.ShortLet, Reason = new string('x', ShortLetTaxDecision.ReasonMaxLength + 1) }, "reason"),
                     (new DecideShortLetTaxCommand { BookingId = booking, Regime = ShortLetTaxRegime.ShortLet, Reason = "riga\nacapo" }, "reason"),
                 })
        {
            var refused = await Assert.ThrowsAsync<DomainRuleException>(() => world.Checks.DecideAsync(OrgWide, command, "auth0|admin"));
            Assert.Equal(OwnerTaxErrors.DecisionInvalid, refused.Code);
            Assert.Equal(field, refused.Extensions["field"]);
        }

        Assert.Equal(0, await world.Db.ShortLetTaxDecisions.CountAsync());
        var saved = await world.Checks.DecideAsync(OrgWide, Decision(booking, ShortLetTaxRegime.ShortLet, "  motivo  "), "auth0|admin");
        Assert.Equal("motivo", saved.Reason);
        Assert.Equal(new string('x', ShortLetTaxDecision.ReasonMaxLength), (await world.Checks.DecideAsync(OrgWide, Decision(booking, ShortLetTaxRegime.NotShortLet, new string('x', ShortLetTaxDecision.ReasonMaxLength)), "auth0|admin")).Reason);
    }

    [Fact]
    public async Task CompanyTenantStays_PartialReach_AreListedAndDecidedWithinIt_Test()
    {
        var world = World.Create();
        var owner = await world.OwnerAsync();
        var mine = await world.OwnedPropertyAsync(owner, createdBy: "auth0|solo-alcuni");
        var others = await world.OwnedPropertyAsync(owner, createdBy: "auth0|agency");
        var inside = await world.CompanyStayAsync(mine);
        var outside = await world.CompanyStayAsync(others);
        var partial = new HostScope(OrgId, OwnerId: "auth0|solo-alcuni");

        Assert.Equal([inside], (await world.Checks.ListCompanyTenantCasesAsync(partial, null)).Select(c => c.BookingId));
        Assert.Equal(new[] { inside, outside }.Order(), (await world.Checks.ListCompanyTenantCasesAsync(OrgWide, null)).Select(c => c.BookingId).Order());
        var refused = await Assert.ThrowsAsync<NotFoundException>(() => world.Checks.DecideAsync(partial, Decision(outside, ShortLetTaxRegime.NotShortLet, "x"), "auth0|solo-alcuni"));
        Assert.Equal(OwnerTaxErrors.BookingNotFound, refused.Code);
        await Assert.ThrowsAsync<NotFoundException>(() => world.Checks.DecideAsync(new HostScope(Guid.NewGuid()), Decision(inside, ShortLetTaxRegime.NotShortLet, "x"), "auth0|admin"));
        Assert.Equal(0, await world.Db.ShortLetTaxDecisions.CountAsync());

        await world.Checks.DecideAsync(partial, Decision(inside, ShortLetTaxRegime.NotShortLet, "x"), "auth0|solo-alcuni");
        Assert.Equal([inside], (await world.Checks.ListCompanyTenantCasesAsync(OrgWide, CompanyTenantCaseState.Decided)).Select(c => c.BookingId));
        Assert.Equal([outside], (await world.Checks.ListCompanyTenantCasesAsync(OrgWide, CompanyTenantCaseState.Pending)).Select(c => c.BookingId));
        Assert.Empty(await world.Checks.TreatmentOfStaysAsync(Guid.NewGuid(), owner, [inside, outside], Today));
        Assert.Empty(await world.Checks.TreatmentOfStaysAsync(OrgId, owner, [], Today));
    }

    [Fact]
    public async Task CompanyTenant_NothingOfTheTenantInTheCaseTheTodoOrTheLogs_Test()
    {
        var world = World.Create();
        var owner = await world.OwnerAsync();
        var property = await world.OwnedPropertyAsync(owner);
        var booking = await world.CompanyStayAsync(property, companyName: "TEST Impresa Segreta Srl");
        await world.Checks.RaiseCompanyTenantDecisionAsync(OrgId, booking);
        await world.Checks.DecideAsync(OrgWide, Decision(booking, ShortLetTaxRegime.NotShortLet, "nota riservata dell'agenzia"), "auth0|admin");

        var listed = Assert.Single(await world.Checks.ListCompanyTenantCasesAsync(OrgWide, null));
        Assert.Equal(
            [
                nameof(CompanyTenantCase.BookingCode), nameof(CompanyTenantCase.BookingId), nameof(CompanyTenantCase.CheckIn), nameof(CompanyTenantCase.CheckOut),
                nameof(CompanyTenantCase.DecidedAt), nameof(CompanyTenantCase.DecidedByUserId), nameof(CompanyTenantCase.Decision), nameof(CompanyTenantCase.PropertyId),
                nameof(CompanyTenantCase.PropertyName),
            ],
            typeof(CompanyTenantCase).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).Order(StringComparer.Ordinal).ToList());
        Assert.Equal("TEST Casa", listed.PropertyName);
        Assert.Equal(ShortLetTaxRegime.NotShortLet, listed.Decision);

        var todo = Assert.Single(await world.TodosAsync(booking));
        var said = string.Join('|', todo.Reason, todo.EntityType, todo.EntityId, todo.StaffUserId, todo.Notes, todo.Action);
        Assert.DoesNotContain("Segreta", said, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("riservata", said, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(EmailTexts.Get(OwnerTaxChecks.CompanyTenantReasonKey, EmailTemplates.DefaultCulture), todo.Reason);
        foreach (var culture in new[] { EmailTemplates.DefaultCulture, System.Globalization.CultureInfo.GetCultureInfo("en") })
        {
            var text = EmailTexts.Get(OwnerTaxChecks.CompanyTenantReasonKey, culture);
            Assert.DoesNotContain("{", text, StringComparison.Ordinal);

            Assert.DoesNotContain("%", text, StringComparison.Ordinal);
            Assert.True(text.Length <= TodoLimits.ReasonMaxLength);
            Assert.Contains("12395/2024", text, StringComparison.Ordinal);
            Assert.Contains("24/E", text, StringComparison.Ordinal);
        }

        Assert.NotEmpty(world.Log.Entries);
        Assert.DoesNotContain("riservata", world.Log.AllOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Segreta", world.Log.AllOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompanyTenantTodo_RaisedOnce_OnlyWithTheFlag_ClosedByTheCancellation_Test()
    {
        var world = World.Create();
        var booking = await world.CompanyStayOfAPrivateOwnerAsync();
        var handler = new CompanyTenantDecisionHandler(world.Checks);
        var saved = new DomainEvent(Guid.NewGuid(), OrgId, DomainEventType.InvoiceDataSaved, booking, World.Now.UtcDateTime, "{}");

        await handler.HandleAsync(saved, CancellationToken.None);
        await handler.HandleAsync(saved with { Id = Guid.NewGuid() }, CancellationToken.None);

        var item = Assert.Single(await world.TodosAsync(booking));
        Assert.Equal(nameof(ActivityType.CompanyTenantDecision), item.ActivityType);
        Assert.Equal(ActivityAction.ReviewBooking, item.Action);
        Assert.Equal(InternalActivity.BookingEntityType, item.EntityType);
        Assert.Equal(ActivityUrgency.Medium, item.Urgency);
        Assert.Equal(world.PropertyOf(booking), item.PropertyId);
        Assert.Equal(OrgId, item.OrgId);
        Assert.Null(item.CompletedAt);

        await world.SetStatusAsync(booking, BookingStatus.Cancelled);
        var close = new ActivityAutoCloseHandler(world.Db, world.Activities, NullLogger<ActivityAutoCloseHandler>.Instance);
        await close.HandleAsync(new DomainEvent(Guid.NewGuid(), OrgId, DomainEventType.BookingCancelled, booking, World.Now.UtcDateTime, "{}"), CancellationToken.None);
        Assert.Equal(InternalActivity.StatusCompleted, Assert.Single(await world.TodosAsync(booking)).Status);
        Assert.Contains(ActivityType.CompanyTenantDecision, ActivityAutoCloseHandler.Rules[DomainEventType.BookingCancelled]);

        var noShow = await world.CompanyStayOfAPrivateOwnerAsync();
        await handler.HandleAsync(saved with { Id = Guid.NewGuid(), EntityId = noShow }, CancellationToken.None);
        Assert.Equal(InternalActivity.StatusPending, Assert.Single(await world.TodosAsync(noShow)).Status);
        await close.HandleAsync(new DomainEvent(Guid.NewGuid(), OrgId, DomainEventType.BookingNoShowMarked, noShow, World.Now.UtcDateTime, "{}"), CancellationToken.None);
        Assert.Equal(InternalActivity.StatusCompleted, Assert.Single(await world.TodosAsync(noShow)).Status);

        await handler.HandleAsync(saved with { Id = Guid.NewGuid() }, CancellationToken.None);
        Assert.Single(await world.TodosAsync(booking));

        var off = World.Create(flagOn: false);
        var other = await off.CompanyStayOfAPrivateOwnerAsync();
        await new CompanyTenantDecisionHandler(off.Checks).HandleAsync(saved with { EntityId = other }, CancellationToken.None);
        Assert.False(await off.Checks.RaiseCompanyTenantDecisionAsync(OrgId, other));
        Assert.Empty(await off.TodosAsync(other));
        Assert.False(off.Checks.WarningsEnabled);
    }

    [Fact]
    public async Task CompanyTenantDecisionHandler_ReactsToTheInvoiceDataAndTheConfirmationOnly_InTheOrgOfTheEvent_Test()
    {
        var checks = new Mock<IOwnerTaxChecks>(MockBehavior.Strict);
        var org = Guid.NewGuid();
        var booking = Guid.NewGuid();
        checks.Setup(c => c.RaiseCompanyTenantDecisionAsync(org, booking, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var handler = new CompanyTenantDecisionHandler(checks.Object);
        DomainEventType[] heard = [DomainEventType.InvoiceDataSaved, DomainEventType.BookingConfirmed];

        foreach (var type in Enum.GetValues<DomainEventType>().Where(t => !heard.Contains(t)))
            await handler.HandleAsync(new DomainEvent(Guid.NewGuid(), org, type, booking, World.Now.UtcDateTime, "{}"), CancellationToken.None);
        foreach (var type in heard)
            await handler.HandleAsync(new DomainEvent(Guid.NewGuid(), org, type, Guid.Empty, World.Now.UtcDateTime, "{}"), CancellationToken.None);
        checks.VerifyNoOtherCalls();

        foreach (var type in heard)
            await handler.HandleAsync(new DomainEvent(Guid.NewGuid(), org, type, booking, World.Now.UtcDateTime, "{}"), CancellationToken.None);
        checks.Verify(c => c.RaiseCompanyTenantDecisionAsync(org, booking, It.IsAny<CancellationToken>()), Times.Exactly(heard.Length));
        checks.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CompanyTenantTodo_EitherOrderOfTheInvoiceDataAndTheConfirmation_RaisesItOnce_Test()
    {
        var world = World.Create();
        var handler = new CompanyTenantDecisionHandler(world.Checks);

        var property = await world.OwnedPropertyAsync(await world.OwnerAsync());
        var dataFirst = await world.CompanyStayAsync(property, status: BookingStatus.Pending);
        await handler.HandleAsync(new DomainEvent(Guid.NewGuid(), OrgId, DomainEventType.InvoiceDataSaved, dataFirst, World.Now.UtcDateTime, "{}"), CancellationToken.None);
        Assert.Empty(await world.TodosAsync(dataFirst));

        world.Db.ChangeTracker.Clear();
        (await world.Db.Bookings.SingleAsync(b => b.Id == dataFirst)).Status = BookingStatus.Confirmed;
        await world.Db.SaveChangesAsync();
        var confirmed = new DomainEvent(Guid.NewGuid(), OrgId, DomainEventType.BookingConfirmed, dataFirst, World.Now.UtcDateTime, "{}");
        await handler.HandleAsync(confirmed, CancellationToken.None);
        await handler.HandleAsync(confirmed with { Id = Guid.NewGuid() }, CancellationToken.None);
        Assert.Single(await world.TodosAsync(dataFirst));

        var confirmationFirst = await world.BookingAsync(property, new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 4));
        await handler.HandleAsync(confirmed with { Id = Guid.NewGuid(), EntityId = confirmationFirst }, CancellationToken.None);
        Assert.Empty(await world.TodosAsync(confirmationFirst));

        await world.InvoiceAsync(confirmationFirst, InvoiceRecipientKind.Company);
        await handler.HandleAsync(new DomainEvent(Guid.NewGuid(), OrgId, DomainEventType.InvoiceDataSaved, confirmationFirst, World.Now.UtcDateTime, "{}"), CancellationToken.None);
        Assert.Single(await world.TodosAsync(confirmationFirst));
    }

    [Fact]
    public async Task CompanyTenantTodo_ClosedWhenTheStayIsNoCaseAnyMore_AskedAgainIfItIsOneAgain_Test()
    {
        var world = World.Create();
        var booking = await world.CompanyStayOfAPrivateOwnerAsync();
        var handler = new CompanyTenantDecisionHandler(world.Checks);
        var saved = new DomainEvent(Guid.NewGuid(), OrgId, DomainEventType.InvoiceDataSaved, booking, World.Now.UtcDateTime, "{}");
        await handler.HandleAsync(saved, CancellationToken.None);
        Assert.Equal(InternalActivity.StatusPending, Assert.Single(await world.TodosAsync(booking)).Status);

        await world.SetRecipientAsync(booking, InvoiceRecipientKind.Guest);
        await handler.HandleAsync(saved with { Id = Guid.NewGuid() }, CancellationToken.None);
        var closed = Assert.Single(await world.TodosAsync(booking));
        Assert.Equal((InternalActivity.StatusCompleted, InternalActivity.ClosedBySystem), (closed.Status, closed.ClosedBy));
        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => world.Checks.DecideAsync(OrgWide, Decision(booking, ShortLetTaxRegime.NotShortLet, "x"), "auth0|admin"));
        Assert.Equal(OwnerTaxErrors.DecisionNotApplicable, refused.Code);

        await world.SetRecipientAsync(booking, InvoiceRecipientKind.Company);
        await handler.HandleAsync(saved with { Id = Guid.NewGuid() }, CancellationToken.None);
        await handler.HandleAsync(saved with { Id = Guid.NewGuid() }, CancellationToken.None);
        var items = await world.TodosAsync(booking);
        Assert.Equal(2, items.Count);
        var open = Assert.Single(items, t => t.CompletedAt is null);

        await world.Activities.CloseAsync(open.Id, "auth0|admin", CancellationToken.None);
        await world.SetRecipientAsync(booking, InvoiceRecipientKind.Guest);
        await handler.HandleAsync(saved with { Id = Guid.NewGuid() }, CancellationToken.None);
        await world.SetRecipientAsync(booking, InvoiceRecipientKind.Company);
        await handler.HandleAsync(saved with { Id = Guid.NewGuid() }, CancellationToken.None);
        var after = await world.TodosAsync(booking);
        Assert.Equal(2, after.Count);
        Assert.DoesNotContain(after, t => t.CompletedAt is null);
    }

    /// <summary>
    /// The readiness warning and the statement flag (behind the flag): an owner past the limit carries the warning (the counts and the suggestion of an issuer profile as a business)
    /// and its statement version the non-blocking issue, neither of which changes what the owner is ready for or lets the version be flagged; without the flag, or an owner under the
    /// limit, there is none.
    /// </summary>
    [Fact]
    public async Task Warnings_TheReadinessAndTheStatementVersion_AreNeverBlocking_AndBehindTheFlag_Test()
    {
        foreach (var flagOn in new[] { true, false })
        {
            var world = World.Create(flagOn);
            var presumed = await world.OwnerAsync(external: 1);
            await world.UnitsAsync(presumed, 2);
            var under = await world.OwnerAsync();
            await world.UnitsAsync(under, 2);
            var onboarding = new OwnerOnboardingService(world.Db, new ActivityLog(world.Db, world.Clock), world.Clock, NullLogger<OwnerOnboardingService>.Instance, world.Checks);

            var report = await onboarding.GetReadinessAsync(OrgWide, CancellationToken.None);

            var row = report.Owners.Single(o => o.OwnerId == presumed);
            Assert.Empty(report.Owners.Single(o => o.OwnerId == under).Warnings);
            if (flagOn)
            {
                var warning = Assert.Single(row.Warnings);
                Assert.Equal((OwnerReadinessWarnings.ShortLetBusinessPresumed, 2, 1, 2, OwnerReadinessSuggestions.IssuerProfile), (warning.Code, warning.ManagedUnits, warning.ExternalUnits, warning.MaxUnits, warning.Suggestion));
            }
            else
            {
                Assert.Empty(row.Warnings);
            }

            // A warning is not a reason: what the owner is ready for is the same with and without it.
            Assert.DoesNotContain(row.Reasons, r => r.Code == OwnerReadinessWarnings.ShortLetBusinessPresumed);
            var plain = await new OwnerOnboardingService(world.Db, new ActivityLog(world.Db, world.Clock), world.Clock, NullLogger<OwnerOnboardingService>.Instance)
                .GetReadinessAsync(OrgWide, CancellationToken.None);
            Assert.Equal(plain.Owners.Single(o => o.OwnerId == presumed).AccountingReady, row.AccountingReady);
            Assert.Equal(plain.Summary, report.Summary);

            var accounting = new OwnerAccountingService(
                world.Db, Mock.Of<IOwnerFiscalProfileService>(), world.Clock, NullLogger<OwnerAccountingService>.Instance, null, null, world.Checks);
            var version = await accounting.GenerateVersionAsync(OrgId, presumed, 2026, 10, "auth0|admin");
            var clean = await accounting.GenerateVersionAsync(OrgId, under, 2026, 10, "auth0|admin");

            Assert.Equal(OwnerStatementVersionStatus.Draft, version.Status);
            Assert.Equal(flagOn, version.IssuesJson?.Contains(OwnerReadinessCodes.ShortLetBusinessPresumed, StringComparison.Ordinal) ?? false);
            Assert.True(version.IssuesJson is null || version.IssuesJson.Contains("\"blocking\":false", StringComparison.Ordinal));
            Assert.Null(clean.IssuesJson);
        }
    }

    /// <summary>
    /// Permission: a caller with a partial reach gets the readiness warning only for an owner whose every ownership it reaches (the line of the check of an owner, of the import and of
    /// the invitations): for an owner with a property outside its reach the counts would tell it about properties it does not reach, so there is no warning; the whole org sees both.
    /// </summary>
    [Fact]
    public async Task Warnings_PartialReach_OnlyForAnOwnerEntirelyInsideTheReach_Test()
    {
        var world = World.Create();
        var inside = await world.OwnerAsync();
        await world.UnitsAsync(inside, 3, createdBy: "auth0|solo-alcuni");
        var shared = await world.OwnerAsync();
        await world.UnitsAsync(shared, 2, createdBy: "auth0|solo-alcuni");
        await world.UnitsAsync(shared, 1, createdBy: "auth0|agency");
        var onboarding = new OwnerOnboardingService(world.Db, new ActivityLog(world.Db, world.Clock), world.Clock, NullLogger<OwnerOnboardingService>.Instance, world.Checks);

        var partial = await onboarding.GetReadinessAsync(new HostScope(OrgId, OwnerId: "auth0|solo-alcuni"), CancellationToken.None);
        var whole = await onboarding.GetReadinessAsync(OrgWide, CancellationToken.None);

        // Inside the reach: the warning, with the units of the owner. The owner with a property outside it is in the list (one of its properties is reached) but with no warning.
        Assert.Equal(3, Assert.Single(partial.Owners.Single(o => o.OwnerId == inside).Warnings).ManagedUnits);
        Assert.Empty(partial.Owners.Single(o => o.OwnerId == shared).Warnings);

        Assert.Equal(3, Assert.Single(whole.Owners.Single(o => o.OwnerId == inside).Warnings).ManagedUnits);
        Assert.Equal(3, Assert.Single(whole.Owners.Single(o => o.OwnerId == shared).Warnings).ManagedUnits);
    }

    [Fact]
    public async Task Mandate_CoOwnerTaxReporting_DefaultsToTheSignatory_AndIsReturned_Test()
    {
        var world = World.Create();
        var first = await world.OwnerAsync();
        await world.UnitsAsync(first, 1);
        var second = await world.OwnerAsync();
        await world.UnitsAsync(second, 1);
        var mandates = new OwnerMandateService(world.Db, world.Clock, Microsoft.Extensions.Options.Options.Create(new OwnerAccountingOptions()), NullLogger<OwnerMandateService>.Instance);
        var terms = new OwnerMandateCommand { ValidFrom = new DateOnly(2026, 1, 1), CommissionPercent = 15m };

        Assert.Equal(CoOwnerTaxReporting.Signatory, new OwnerMandate().CoOwnerTaxReporting);
        var byDefault = await mandates.CreateVersionAsync(OrgWide, first, terms, "auth0|admin");
        Assert.Equal(CoOwnerTaxReporting.Signatory, byDefault.CoOwnerTaxReporting);
        var perShare = await mandates.CreateVersionAsync(OrgWide, first, terms with { ValidFrom = new DateOnly(2026, 6, 1), CoOwnerTaxReporting = CoOwnerTaxReporting.PerShare }, "auth0|admin");
        Assert.Equal(CoOwnerTaxReporting.PerShare, perShare.CoOwnerTaxReporting);
        Assert.Equal(CoOwnerTaxReporting.PerShare, (await mandates.ListAsync(OrgWide, first)).First().CoOwnerTaxReporting);
        Assert.Equal(CoOwnerTaxReporting.PerShare, Casazen.Web.DTOs.Owners.OwnerMandateDto.From(perShare).CoOwnerTaxReporting);

        var template = await mandates.ApplyTemplateAsync(OrgWide, new OwnerMandateTemplateCommand { Template = terms with { CoOwnerTaxReporting = CoOwnerTaxReporting.PerShare }, OwnerIds = [second] }, null);
        Assert.Equal([second], template.CreatedOwnerIds);
        Assert.Equal(CoOwnerTaxReporting.PerShare, (await world.Db.OwnerMandates.AsNoTracking().SingleAsync(m => m.PropertyOwnerId == second)).CoOwnerTaxReporting);

        var before = await world.Db.OwnerMandates.CountAsync();
        var invalid = await Assert.ThrowsAsync<DomainRuleException>(
            () => mandates.CreateVersionAsync(OrgWide, first, terms with { ValidFrom = new DateOnly(2027, 1, 1), CoOwnerTaxReporting = (CoOwnerTaxReporting)7 }, null));
        Assert.Equal(OwnerMandateErrors.MandateInvalid, invalid.Code);
        Assert.Equal("coOwnerTaxReporting", invalid.Extensions["field"]);
        Assert.Equal(before, await world.Db.OwnerMandates.CountAsync());
    }

    [Fact]
    public void Controller_BehindTheFlag_EachActionNeedsItsOwnPermission_Test()
    {
        var controller = typeof(OwnerTaxDecisionsController);
        Assert.Equal([FeatureFlags.TaxReportingExports], controller.GetCustomAttributes<FeatureGateAttribute>(inherit: true).Select(g => g.Flag));
        Assert.Empty(controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true));

        static string Policy(string action) =>
            Assert.Single(typeof(OwnerTaxDecisionsController).GetMethod(action)!.GetCustomAttributes<AuthorizeAttribute>()).Policy!;

        Assert.Equal(CasazenPolicies.AgencyOwnerFinancialRead, Policy(nameof(OwnerTaxDecisionsController.List)));
        Assert.Equal(CasazenPolicies.AgencyOwnerFinancialManage, Policy(nameof(OwnerTaxDecisionsController.Decide)));
        Assert.Equal(CasazenPolicies.AgencyOwnerView, Policy(nameof(OwnerTaxDecisionsController.GetUnitsCheck)));
        Assert.Equal(CasazenPolicies.AgencyOwnerFinancialManage, Policy(nameof(OwnerTaxDecisionsController.DeclareExternalUnits)));

        Assert.Equal("api/owner-tax-decisions", controller.GetMethod(nameof(OwnerTaxDecisionsController.List))!.GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Equal("api/owner-tax-decisions", controller.GetMethod(nameof(OwnerTaxDecisionsController.Decide))!.GetCustomAttribute<HttpPostAttribute>()!.Template);
        Assert.Equal("api/owners/{ownerId:guid}/tax-checks", controller.GetMethod(nameof(OwnerTaxDecisionsController.GetUnitsCheck))!.GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Equal("api/owners/{ownerId:guid}/external-short-let-units", controller.GetMethod(nameof(OwnerTaxDecisionsController.DeclareExternalUnits))!.GetCustomAttribute<HttpPutAttribute>()!.Template);
        Assert.Contains(FeatureFlags.TaxReportingExports, FeatureFlags.All);
    }

    [Fact]
    public void Rules_TheLatestDecisionIsInForce_TheNightsAreTheDaysBetween()
    {
        var first = new ShortLetTaxDecision { Regime = ShortLetTaxRegime.NotShortLet, DecidedAt = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc), Id = Guid.Parse("00000000-0000-0000-0000-000000000009") };
        var second = new ShortLetTaxDecision { Regime = ShortLetTaxRegime.ShortLet, DecidedAt = new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc), Id = Guid.Parse("00000000-0000-0000-0000-000000000001") };
        var tie = new ShortLetTaxDecision { Regime = ShortLetTaxRegime.NotShortLet, DecidedAt = second.DecidedAt, Id = Guid.Parse("00000000-0000-0000-0000-000000000002") };

        Assert.Same(second, OwnerTaxRules.CurrentDecision([first, second]));
        Assert.Same(tie, OwnerTaxRules.CurrentDecision([second, first, tie]));
        Assert.Null(OwnerTaxRules.CurrentDecision([]));
        Assert.Equal(3, OwnerTaxRules.Nights(new DateTime(2026, 11, 10, 15, 0, 0), new DateTime(2026, 11, 13, 10, 0, 0)));
        Assert.True(OwnerTaxRules.IsNaturalPrivate(null, OwnerTaxRegime.Private));
        Assert.False(OwnerTaxRules.IsNaturalPrivate(OwnerEntityKind.LegalEntity, OwnerTaxRegime.Private));
        Assert.False(OwnerTaxRules.IsNaturalPrivate(OwnerEntityKind.NaturalPerson, OwnerTaxRegime.VatHolder));
        Assert.Equal(ShortLetTaxRegime.ShortLet, (ShortLetTaxRegime)1);
        Assert.Equal(ShortLetTaxRegime.NotShortLet, (ShortLetTaxRegime)2);
        Assert.Equal(CoOwnerTaxReporting.Signatory, (CoOwnerTaxReporting)0);
        Assert.Equal(CoOwnerTaxReporting.PerShare, (CoOwnerTaxReporting)1);
    }

    private static DecideShortLetTaxCommand Decision(Guid booking, ShortLetTaxRegime regime, string reason) =>
        new() { BookingId = booking, Regime = regime, Reason = reason };

    private sealed class World
    {
        public static readonly DateTimeOffset Now = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

        private World(AppDbContext db, FakeTimeProvider clock, bool flagOn, ShortStayFiscalOptions rules)
        {
            Db = db;
            Clock = clock;
            Activities = new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, clock);
            Checks = new OwnerTaxChecks(
                db,
                Activities,
                flagOn ? new TestFeatureFlags(FeatureFlags.TaxReportingExports) : new TestFeatureFlags(),
                Microsoft.Extensions.Options.Options.Create(rules),
                clock,
                Log);
        }

        public AppDbContext Db { get; }

        public FakeTimeProvider Clock { get; }

        public InternalActivityService Activities { get; }

        public CapturingLogger<OwnerTaxChecks> Log { get; } = new();

        public OwnerTaxChecks Checks { get; }

        public static World Create(bool flagOn = true, ShortStayFiscalOptions? rules = null) => new(
            new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"OwnerTaxChecks_{Guid.NewGuid()}").Options,
                null,
                TestDataProtection.Shared),
            new FakeTimeProvider(Now),
            flagOn,
            rules ?? new ShortStayFiscalOptions());

        public async Task<Guid> OwnerAsync(OwnerEntityKind? kind = OwnerEntityKind.NaturalPerson, OwnerTaxRegime regime = OwnerTaxRegime.Private, int external = 0)
        {
            var owner = new PropertyOwner
            {
                OrgId = OrgId,
                FirstName = "TEST",
                LastName = "Proprietario",
                Email = $"{Guid.NewGuid():N}@example.com",
                EntityKind = kind,
                TaxRegime = regime,
                ExternalShortLetUnits = external,
            };
            Db.PropertyOwners.Add(owner);
            await Db.SaveChangesAsync();
            return owner.Id;
        }

        public async Task<Guid> PropertyAsync(string country = "IT", RentalMode mode = RentalMode.Short, bool deleted = false, string createdBy = "auth0|agency")
        {
            var property = new Property
            {
                OrgId = OrgId,
                OwnerId = createdBy,
                Name = "TEST Casa",
                Address = "Via 1",
                City = "Como",
                Country = country,
                RentalMode = mode,
                IsDeleted = deleted,
            };
            Db.Properties.Add(property);
            await Db.SaveChangesAsync();
            return property.Id;
        }

        public async Task OwnsAsync(Guid owner, Guid property, DateOnly start, DateOnly? end = null, decimal share = 100m)
        {
            Db.PropertyOwnerships.Add(new PropertyOwnership
            {
                OrgId = OrgId,
                PropertyId = property,
                PropertyOwnerId = owner,
                SharePercent = share,
                OwnershipStart = start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                OwnershipEnd = end?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            });
            await Db.SaveChangesAsync();
        }

        public async Task<Guid> OwnedPropertyAsync(Guid owner, string createdBy = "auth0|agency")
        {
            var property = await PropertyAsync(createdBy: createdBy);
            await OwnsAsync(owner, property, new DateOnly(2025, 1, 1));
            return property;
        }

        public async Task<List<Guid>> UnitsAsync(Guid owner, int count, string createdBy = "auth0|agency")
        {
            var properties = new List<Guid>(count);
            for (var i = 0; i < count; i++)
                properties.Add(await OwnedPropertyAsync(owner, createdBy));

            return properties;
        }

        public async Task<Guid> BookingAsync(
            Guid property,
            DateOnly checkIn,
            DateOnly checkOut,
            BookingStatus status = BookingStatus.Confirmed,
            DateTime? noShowAt = null)
        {
            var booking = new Booking
            {
                OrgId = OrgId,
                PropertyId = property,
                GuestId = Guid.NewGuid(),
                CheckInDate = checkIn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                CheckOutDate = checkOut.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                NumberOfGuests = 2,
                Status = status,
                NoShowAt = noShowAt,
                BasePrice = 300m,
                TotalPrice = 300m,
            };
            Db.Bookings.Add(booking);
            await Db.SaveChangesAsync();
            return booking.Id;
        }

        public async Task InvoiceAsync(Guid booking, InvoiceRecipientKind kind, string? name = null)
        {
            Db.BookingInvoiceData.Add(new BookingInvoiceData
            {
                OrgId = OrgId,
                BookingId = booking,
                RecipientKind = kind,
                Name = name,
                InvoiceRequested = true,
                Source = InvoiceDataSource.Host,
            });
            await Db.SaveChangesAsync();
        }

        public async Task SetRecipientAsync(Guid booking, InvoiceRecipientKind kind)
        {
            Db.ChangeTracker.Clear();
            (await Db.BookingInvoiceData.SingleAsync(d => d.BookingId == booking)).RecipientKind = kind;
            await Db.SaveChangesAsync();
        }

        public async Task SetStatusAsync(Guid booking, BookingStatus status)
        {
            Db.ChangeTracker.Clear();
            (await Db.Bookings.SingleAsync(b => b.Id == booking)).Status = status;
            await Db.SaveChangesAsync();
        }

        public async Task<Guid> CompanyStayAsync(
            Guid property,
            InvoiceRecipientKind recipient = InvoiceRecipientKind.Company,
            BookingStatus status = BookingStatus.Confirmed,
            DateTime? noShowAt = null,
            DateOnly? checkIn = null,
            DateOnly? checkOut = null,
            string? companyName = null)
        {
            var booking = await BookingAsync(property, checkIn ?? new DateOnly(2026, 11, 10), checkOut ?? new DateOnly(2026, 11, 13), status, noShowAt);
            await InvoiceAsync(booking, recipient, companyName);
            return booking;
        }

        public async Task<Guid> CompanyStayOfAPrivateOwnerAsync()
        {
            var owner = await OwnerAsync();
            return await CompanyStayAsync(await OwnedPropertyAsync(owner));
        }

        public Guid PropertyOf(Guid booking)
        {
            Db.ChangeTracker.Clear();
            return Db.Bookings.AsNoTracking().Single(b => b.Id == booking).PropertyId;
        }

        public async Task<StayShortLetTreatment> TreatmentAsync(Guid owner, Guid booking) =>
            Assert.Single(await Checks.TreatmentOfStaysAsync(OrgId, owner, [booking], Today));

        public async Task<List<InternalActivity>> TodosAsync(Guid booking)
        {
            Db.ChangeTracker.Clear();
            return await Db.InternalActivities.AsNoTracking().Where(a => a.Action != null && a.EntityId == booking).ToListAsync();
        }
    }
}
