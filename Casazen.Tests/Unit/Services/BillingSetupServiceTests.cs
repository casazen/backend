using System.Security.Claims;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Models;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Features;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Authorization;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Fiscal;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// NF-82, NF-104, NF-118, E11/F01/US01/T04: the billing setup, the whole invoicing setup of an organization in one place. The rules that cost if they fail: a
/// caller who reaches only some of the properties cannot change the setup of the organization, a partial save never clears what it did not carry, a
/// refused save changes nothing, nothing counts as done while an item is missing, and the setup of one organization is never the one of another.
/// </summary>
public class BillingSetupServiceTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly Guid OrgA = Guid.Parse("6d5c4b3a-2918-4f7e-8d6c-5b4a39281711");
    private static readonly Guid OrgB = Guid.Parse("7e6d5c4b-3a29-4f8e-9d7c-6c5b4a392812");

    /// <summary>
    /// PROOF TEST: permission rule. A caller who reaches only some of the properties (a collaborator «Solo alcuni», an account that owns a few) reads the setup
    /// of the organization but cannot change any section of it: 403 <c>fiscal_settings_need_whole_org</c> for every section, nothing is saved, and what is
    /// stored (of this org and of another) stays as it was. The manager of the whole organization changes its own setup, and only its own.
    /// </summary>
    [Fact]
    public async Task BillingSetup_PartialScopeCannotChangeOrgSetup_Test()
    {
        var databaseName = $"billing-setup-scope-{Guid.NewGuid()}";
        await SeedAsync(databaseName, profileOfA: Profile(OrgA, "TEST Quokka Sagl"), profileOfB: Profile(OrgB, "TEST Altro Sagl"));
        await using var db = NewDb(databaseName, new FixedTenantContext(OrgA));
        var service = BillingSetupTestServices.Create(Issuers(db));
        var resolver = new Mock<IOrgContextResolver>();
        resolver.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OrgA);

        // The collaborator reads the setup of its organization: and of no other.
        var staff = Controller(service, resolver, HostAuthorizationTestHarness.User("auth0|staff-a", "Staff"));
        var read = Assert.IsType<BillingSetupDto>(Assert.IsType<OkObjectResult>((await staff.Get(Ct)).Result).Value);
        Assert.Equal("TEST Quokka Sagl", read.Sections.Single(s => s.Section == BillingSetupSections.Issuer).Values[BillingSetupFields.LegalName]);

        // It cannot change any section: every one is refused with the same code, before anything is read or written.
        foreach (var section in BillingSetupSections.Writable)
        {
            var refused = await staff.PutSection(section, new SaveBillingSetupSectionRequest { LegalName = "Cambiato Sagl" }, Ct);
            var problem = Assert.IsType<ObjectResult>(refused.Result);
            Assert.Equal(StatusCodes.Status403Forbidden, problem.StatusCode);
            Assert.Equal(DocumentIssuerErrorCodes.NeedsWholeOrg, Assert.IsAssignableFrom<ProblemDetails>(problem.Value).Extensions["code"]);
        }

        await using (var check = NewDb(databaseName, tenant: null))
        {
            Assert.Equal("TEST Quokka Sagl", (await check.OrgFiscalIssuerProfiles.AsNoTracking().SingleAsync(p => p.OrgId == OrgA)).LegalName);
            Assert.Equal("TEST Altro Sagl", (await check.OrgFiscalIssuerProfiles.AsNoTracking().SingleAsync(p => p.OrgId == OrgB)).LegalName);
        }

        // The manager of the whole organization changes it.
        var manager = Controller(service, resolver, HostAuthorizationTestHarness.User("auth0|manager-a", "PropertyManager"));
        var saved = Assert.IsType<BillingSetupSectionDto>(
            Assert.IsType<OkObjectResult>((await manager.PutSection("issuer", new SaveBillingSetupSectionRequest { LegalName = "TEST Quokka Due Sagl" }, Ct)).Result).Value);
        Assert.Equal("TEST Quokka Due Sagl", saved.Values[BillingSetupFields.LegalName]);

        await using var after = NewDb(databaseName, tenant: null);
        Assert.Equal("TEST Quokka Due Sagl", (await after.OrgFiscalIssuerProfiles.AsNoTracking().SingleAsync(p => p.OrgId == OrgA)).LegalName);
        Assert.Equal("TEST Altro Sagl", (await after.OrgFiscalIssuerProfiles.AsNoTracking().SingleAsync(p => p.OrgId == OrgB)).LegalName);
    }

    /// <summary>
    /// The endpoint is the one of the brief: <c>api/billing-setup</c>, read with <c>payment.read</c>, saved with <c>property.write</c>, and the whole controller
    /// is behind <c>Features:FiscalDocumentIssuance</c> (404 while it is off).
    /// </summary>
    [Fact]
    public void BillingSetupController_IsBehindThePaymentAndPropertyPolicies_AndTheIssuanceFlag()
    {
        var type = typeof(BillingSetupController);
        Assert.Equal("api/billing-setup", type.GetCustomAttributes(typeof(RouteAttribute), inherit: false).Cast<RouteAttribute>().Single().Template);
        Assert.Contains(type.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false).Cast<AuthorizeAttribute>(), a => a.Policy == CasazenPolicies.PaymentRead);
        Assert.Equal(FeatureFlags.FiscalDocumentIssuance, type.GetCustomAttributes(typeof(FeatureGateAttribute), inherit: false).Cast<FeatureGateAttribute>().Single().Flag);

        var put = type.GetMethod(nameof(BillingSetupController.PutSection))!;
        Assert.Equal("{section}", put.GetCustomAttributes(typeof(HttpPutAttribute), inherit: false).Cast<HttpPutAttribute>().Single().Template);
        Assert.Contains(put.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false).Cast<AuthorizeAttribute>(), a => a.Policy == CasazenPolicies.PropertyWrite);
        Assert.Empty(type.GetMethod(nameof(BillingSetupController.Get))!.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false));
    }

    /// <summary>
    /// One read returns every section in the order of the wizard with its values and its missing items; an organization that saved nothing is missing the
    /// country, the name, the address and the regime, and every confirmation; the sections that do not apply (the Swiss tourist tax of an Italian issuer, the
    /// sections nobody provides yet) say so and are not counted. The setup of another organization is never in the answer.
    /// </summary>
    [Fact]
    public async Task GetAsync_ReturnsEverySectionInOrder_WithItsMissingItems()
    {
        var databaseName = $"billing-setup-get-{Guid.NewGuid()}";
        await SeedAsync(databaseName, profileOfA: null, profileOfB: Profile(OrgB, "TEST Altro Sagl"));
        await using var db = NewDb(databaseName, new FixedTenantContext(OrgA));
        var service = BillingSetupTestServices.Create(Issuers(db));

        var setup = await service.GetAsync(OrgA, Ct);

        Assert.Equal(
            [
                BillingSetupSections.Issuer, BillingSetupSections.Vat, BillingSetupSections.Numbering, BillingSetupSections.TouristTax,
                BillingSetupSections.Channels, BillingSetupSections.Mandates, BillingSetupSections.Automation, BillingSetupSections.Italy,
            ],
            setup.Sections.Select(s => s.Section));
        Assert.Equal(
            [true, true, true, false, false, false, true, true],
            setup.Sections.Select(s => s.Applies));
        Assert.Equal(
            [
                BillingSetupItemKeys.CountryMissing, BillingSetupItemKeys.LegalNameMissing, BillingSetupItemKeys.AddressMissing,
                BillingSetupItemKeys.RegimeMissing,
                BillingSetupItemKeys.GuestSeriesUnconfirmed, BillingSetupItemKeys.OwnerSeriesUnconfirmed,
                BillingSetupItemKeys.AutoIssueUnconfirmed,
                BillingSetupItemKeys.ConservationAgreementUnconfirmed, BillingSetupItemKeys.StampDutyUnconfirmed,
            ],
            setup.Missing.Select(i => i.Key));
        Assert.All(setup.Missing, item => Assert.Equal(setup.Sections.Single(s => s.Missing.Contains(item)).Section, item.Section));
        Assert.False(setup.IsComplete);
        Assert.Equal("IT", setup.Sections[0].Values[BillingSetupFields.CountryCode]);
        Assert.Equal(OrgFiscalIssuerProfile.DefaultOwnerInvoiceSeries, setup.Sections[2].Values["defaultOwnerInvoiceSeries"]);
        Assert.DoesNotContain("Altro", System.Text.Json.JsonSerializer.Serialize(BillingSetupDto.From(setup)), StringComparison.Ordinal);

        var progress = await service.GetProgressAsync(OrgA, null, Ct);
        Assert.Equal((0, 5), (progress.Done, progress.Total));
        Assert.Equal(BillingSetupItemKeys.CountryMissing, progress.FirstMissing!.Key);
        Assert.False(progress.IsComplete);
    }

    /// <summary>
    /// A save may carry only some fields: what it does not carry stays, the same answers saved twice leave the same setting, a text sent empty clears it, and a
    /// refusal (an unknown section, a section another endpoint saves, a field of another section) saves nothing.
    /// </summary>
    [Fact]
    public async Task PutSectionAsync_PartialPayload_KeepsTheRest_IsIdempotent_AndRefusesWhatIsNotOfTheSection()
    {
        var databaseName = $"billing-setup-partial-{Guid.NewGuid()}";
        await SeedAsync(databaseName, profileOfA: null, profileOfB: null);
        await using var db = NewDb(databaseName, new FixedTenantContext(OrgA));
        var service = BillingSetupTestServices.Create(Issuers(db));

        // A draft: only the country and the name.
        var first = await service.PutSectionAsync(OrgA, "issuer", new BillingSetupPayload { CountryCode = "ch", LegalName = "TEST Quokka Sagl" }, "auth0|manager-a", Ct);
        Assert.Equal("CH", first.Values[BillingSetupFields.CountryCode]);
        Assert.Equal([BillingSetupItemKeys.AddressMissing], first.Missing.Select(i => i.Key));

        // The next step carries the address only: the name stays. Saved twice, the answer is the same.
        var address = new BillingSetupPayload { AddressStreet = "Via Nassa", AddressCivic = "5", PostalCode = "6900", City = "Lugano" };
        var second = await service.PutSectionAsync(OrgA, "ISSUER", address, "auth0|manager-a", Ct);
        var again = await service.PutSectionAsync(OrgA, " issuer ", address, "auth0|manager-a", Ct);
        Assert.Equal("TEST Quokka Sagl", second.Values[BillingSetupFields.LegalName]);
        Assert.Equal(second.Values.OrderBy(v => v.Key), again.Values.OrderBy(v => v.Key));
        Assert.Empty(again.Missing);

        // A text sent empty clears it; a payload with no field saves nothing and answers the section as it is.
        var cleared = await service.PutSectionAsync(OrgA, "issuer", new BillingSetupPayload { AddressCivic = "" }, null, Ct);
        Assert.Null(cleared.Values[BillingSetupFields.AddressCivic]);
        var nothing = await service.PutSectionAsync(OrgA, "issuer", new BillingSetupPayload(), null, Ct);
        Assert.Equal("TEST Quokka Sagl", nothing.Values[BillingSetupFields.LegalName]);

        // The refusals: each names its code (and the field), and the stored setting stays.
        var stray = await Assert.ThrowsAsync<DomainRuleException>(() => service.PutSectionAsync(OrgA, "numbering", new BillingSetupPayload { LegalName = "Altro Sagl" }, null, Ct));
        Assert.Equal(BillingSetupErrorCodes.FieldNotInSection, stray.Code);
        Assert.Equal(BillingSetupFields.LegalName, stray.Extensions["field"]);
        var notWritable = await Assert.ThrowsAsync<DomainRuleException>(() => service.PutSectionAsync(OrgA, "channels", new BillingSetupPayload(), null, Ct));
        Assert.Equal(BillingSetupErrorCodes.SectionNotWritable, notWritable.Code);
        var unknown = await Assert.ThrowsAsync<NotFoundException>(() => service.PutSectionAsync(OrgA, "invoices", new BillingSetupPayload(), null, Ct));
        Assert.Equal(BillingSetupErrorCodes.SectionUnknown, unknown.Code);
        await using var check = NewDb(databaseName, tenant: null);
        Assert.Equal("TEST Quokka Sagl", (await check.OrgFiscalIssuerProfiles.AsNoTracking().SingleAsync(p => p.OrgId == OrgA)).LegalName);
    }

    /// <summary>
    /// A value that is not valid comes back with the code and the field of the issuer setting (every field of the failure in <c>errors</c>) and nothing is
    /// saved; a regime of the other country is refused; a name that is not a member of an enum is a failure on its field.
    /// </summary>
    [Fact]
    public async Task PutSectionAsync_InvalidData_ComesBackWithFieldCodes_AndSavesNothing()
    {
        var databaseName = $"billing-setup-invalid-{Guid.NewGuid()}";
        await SeedAsync(databaseName, profileOfA: Profile(OrgA, "TEST Quokka Sagl"), profileOfB: null);
        await using var db = NewDb(databaseName, new FixedTenantContext(OrgA));
        var service = BillingSetupTestServices.Create(Issuers(db));

        var invalid = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.PutSectionAsync(OrgA, "issuer", new BillingSetupPayload { PostalCode = "69X0", Province = "TICINO", LegalName = "Cambiato Sagl" }, null, Ct));
        Assert.Equal(DocumentIssuerErrorCodes.FieldInvalid, invalid.Code);
        Assert.Equal(BillingSetupFields.Province, invalid.Extensions["field"]);
        var errors = string.Join(' ', Assert.IsAssignableFrom<System.Collections.IEnumerable>(invalid.Extensions["errors"]).Cast<object>().Select(e => e.ToString()));
        Assert.Contains(BillingSetupFields.Province, errors, StringComparison.Ordinal);
        Assert.Contains(BillingSetupFields.PostalCode, errors, StringComparison.Ordinal);
        Assert.DoesNotContain(BillingSetupFields.LegalName, errors, StringComparison.Ordinal);

        var uid = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.PutSectionAsync(OrgA, "vat", new BillingSetupPayload { SwissVatNumber = "CHE-109.322.552" }, null, Ct));
        Assert.Equal(BillingSetupFields.SwissVatNumber, uid.Extensions["field"]);
        var mismatch = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.PutSectionAsync(OrgA, "vat", new BillingSetupPayload { Regime = DocumentRegime.VatOrdinary }, null, Ct));
        Assert.Equal(DocumentIssuerErrorCodes.RegimeCountryMismatch, mismatch.Code);
        var series = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.PutSectionAsync(OrgA, "numbering", new BillingSetupPayload { GuestDocumentSeries = "una serie troppo lunga" }, null, Ct));
        Assert.Equal(BillingSetupFields.GuestDocumentSeries, series.Extensions["field"]);

        var badName = Assert.Throws<DomainRuleException>(() => new SaveBillingSetupSectionRequest { Regime = "Quokka", StayVatTreatment = "7" }.ToPayload());
        Assert.Equal(DocumentIssuerErrorCodes.FieldInvalid, badName.Code);
        Assert.Equal(BillingSetupFields.Regime, badName.Extensions["field"]);
        Assert.Equal(DocumentRegime.SwissVatRegistered, new SaveBillingSetupSectionRequest { Regime = " swissvatregistered " }.ToPayload().Regime);

        await using var check = NewDb(databaseName, tenant: null);
        var stored = await check.OrgFiscalIssuerProfiles.AsNoTracking().SingleAsync(p => p.OrgId == OrgA);
        Assert.Equal("TEST Quokka Sagl", stored.LegalName);
        Assert.Equal("6900", stored.PostalCode);
        Assert.Null(stored.GuestDocumentSeries);
    }

    /// <summary>
    /// The step «IVA»: the VAT registration of a Swiss issuer tells its regime, a registered issuer is asked for its UID (the item of the section «issuer»), the UID
    /// is saved by the step «IVA» too, and changing the country drops a regime of the other country, so the step asks again.
    /// </summary>
    [Fact]
    public async Task Vat_TheRegistrationTellsTheSwissRegime_TheUidIsAsked_AndANewCountryDropsTheRegime()
    {
        var databaseName = $"billing-setup-vat-{Guid.NewGuid()}";
        await SeedAsync(databaseName, profileOfA: null, profileOfB: null);
        await using var db = NewDb(databaseName, new FixedTenantContext(OrgA));
        var service = BillingSetupTestServices.Create(Issuers(db));
        await service.PutSectionAsync(
            OrgA,
            "issuer",
            new BillingSetupPayload { CountryCode = "CH", LegalName = "TEST Quokka Sagl", AddressStreet = "Via Nassa", PostalCode = "6900", City = "Lugano" },
            null,
            Ct);

        var registered = await service.PutSectionAsync(OrgA, "vat", new BillingSetupPayload { VatRegistered = true }, null, Ct);
        Assert.Equal("SwissVatRegistered", registered.Values[BillingSetupFields.Regime]);
        Assert.Equal(true, registered.Values[BillingSetupFields.VatRegistered]);
        Assert.Empty(registered.Missing);
        Assert.Equal([BillingSetupItemKeys.SwissUidMissing], (await service.GetProgressAsync(OrgA, [BillingSetupSections.Issuer], Ct)).Missing.Select(i => i.Key));

        await service.PutSectionAsync(OrgA, "vat", new BillingSetupPayload { SwissVatNumber = "che 109 322 551 mwst" }, null, Ct);
        Assert.True((await service.GetProgressAsync(OrgA, [BillingSetupSections.Issuer, BillingSetupSections.Vat], Ct)).IsComplete);

        var notRegistered = await service.PutSectionAsync(OrgA, "vat", new BillingSetupPayload { VatRegistered = false }, null, Ct);
        Assert.Equal("SwissNotRegistered", notRegistered.Values[BillingSetupFields.Regime]);
        Assert.Equal(false, notRegistered.Values[BillingSetupFields.VatRegistered]);

        // Another country: the Swiss regime does not follow, the postal code of the old country is asked again (the street and the city stay), and the step «IVA» is asked again.
        var italian = await service.PutSectionAsync(OrgA, "issuer", new BillingSetupPayload { CountryCode = "IT" }, null, Ct);
        Assert.Equal("IT", italian.Values[BillingSetupFields.CountryCode]);
        Assert.Equal("Via Nassa", italian.Values[BillingSetupFields.AddressStreet]);
        Assert.Null(italian.Values[BillingSetupFields.PostalCode]);
        Assert.Equal([BillingSetupItemKeys.AddressMissing], italian.Missing.Select(i => i.Key));
        var vat = (await service.GetAsync(OrgA, Ct)).Sections.Single(s => s.Section == BillingSetupSections.Vat);
        Assert.Null(vat.Values[BillingSetupFields.Regime]);
        Assert.Equal([BillingSetupItemKeys.RegimeMissing], vat.Missing.Select(i => i.Key));

        // The same save may carry the new address: nothing is dropped then.
        await service.PutSectionAsync(OrgA, "issuer", new BillingSetupPayload { CountryCode = "CH" }, null, Ct);
        var swissAgain = await service.PutSectionAsync(OrgA, "issuer", new BillingSetupPayload { CountryCode = "IT", PostalCode = "00100", Province = "rm" }, null, Ct);
        Assert.Equal(("00100", "RM"), ((string?)swissAgain.Values[BillingSetupFields.PostalCode], (string?)swissAgain.Values[BillingSetupFields.Province]));
        Assert.Empty(swissAgain.Missing);
    }

    /// <summary>
    /// The Swiss tourist tax: the portals that collect it are confirmed at least once, even to say that none does. A confirmation of none is kept by the saves of
    /// the other sections, a choice never made is never turned into one by them, and an Italian issuer has no such section.
    /// </summary>
    [Fact]
    public async Task TouristTax_ConfirmedNoneIsKept_AndAnotherSaveNeverConfirmsIt()
    {
        var databaseName = $"billing-setup-tax-{Guid.NewGuid()}";
        await SeedAsync(databaseName, profileOfA: Profile(OrgA, "TEST Quokka Sagl"), profileOfB: null);
        await using var db = NewDb(databaseName, new FixedTenantContext(OrgA));
        var service = BillingSetupTestServices.Create(Issuers(db));

        Assert.Equal([BillingSetupItemKeys.TouristTaxChannelsUnconfirmed], (await Section(service, BillingSetupSections.TouristTax)).Missing.Select(i => i.Key));

        // Saving other sections (the whole setting is written back) does not confirm what was never chosen.
        await service.PutSectionAsync(OrgA, "numbering", new BillingSetupPayload { GuestDocumentSeries = "{year}" }, null, Ct);
        var untouched = await Section(service, BillingSetupSections.TouristTax);
        Assert.Equal(false, untouched.Values["confirmed"]);
        Assert.Single(untouched.Missing);
        Assert.Null((await StoredAsync(databaseName)).PlatformCollectedTouristTaxChannelsJson);

        // None collects it: confirmed with an empty list, and kept by the next saves.
        var none = await service.PutSectionAsync(OrgA, "touristTax", new BillingSetupPayload { PlatformCollectedTouristTaxChannels = [] }, null, Ct);
        Assert.Empty(none.Missing);
        Assert.Equal(true, none.Values["confirmed"]);
        await service.PutSectionAsync(OrgA, "numbering", new BillingSetupPayload { OwnerInvoiceSeries = "PM{year}" }, null, Ct);
        Assert.Equal("[]", (await StoredAsync(databaseName)).PlatformCollectedTouristTaxChannelsJson);
        Assert.Empty((await Section(service, BillingSetupSections.TouristTax)).Missing);

        // A portal does: the names are checked, and the saves of the other sections keep the list.
        await Assert.ThrowsAsync<DomainRuleException>(
            () => service.PutSectionAsync(OrgA, "touristTax", new BillingSetupPayload { PlatformCollectedTouristTaxChannels = ["Quokka"] }, null, Ct));
        await service.PutSectionAsync(OrgA, "touristTax", new BillingSetupPayload { PlatformCollectedTouristTaxChannels = ["Airbnb", "airbnb"] }, null, Ct);
        await service.PutSectionAsync(OrgA, "automation", new BillingSetupPayload { AutoIssueGuestDocuments = false }, null, Ct);
        Assert.Equal("""["Airbnb"]""", (await StoredAsync(databaseName)).PlatformCollectedTouristTaxChannelsJson);

        // An Italian issuer has no such section: it does not apply and has no item.
        await service.PutSectionAsync(OrgA, "issuer", new BillingSetupPayload { CountryCode = "IT" }, null, Ct);
        var italian = await Section(service, BillingSetupSections.TouristTax);
        Assert.False(italian.Applies);
        Assert.Empty(italian.Missing);
    }

    /// <summary>
    /// The step «Emissione»: the choice counts as made when the step was saved while the profile could issue; saved before, it is a draft, and a profile that
    /// stops being complete asks for the confirmation again.
    /// </summary>
    [Fact]
    public async Task Automation_IsConfirmedOnlyByASaveWhileTheProfileCanIssue()
    {
        var databaseName = $"billing-setup-automation-{Guid.NewGuid()}";
        await SeedAsync(databaseName, profileOfA: null, profileOfB: null);
        await using var db = NewDb(databaseName, new FixedTenantContext(OrgA));
        var time = new Casazen.Tests.Unit.FakeTimeProvider(HostScopeScenario.Now);
        var service = BillingSetupTestServices.Create(Issuers(db, time: time));

        // Nothing to issue with yet: the answers are kept as a draft, the choice is not confirmed.
        var draft = await service.PutSectionAsync(OrgA, "automation", new BillingSetupPayload { AutoIssueGuestDocuments = true, EmailGuestDocuments = false }, null, Ct);
        Assert.Equal(true, draft.Values[BillingSetupFields.AutoIssueGuestDocuments]);
        Assert.Equal([BillingSetupItemKeys.AutoIssueUnconfirmed], draft.Missing.Select(i => i.Key));

        await service.PutSectionAsync(
            OrgA,
            "issuer",
            new BillingSetupPayload { CountryCode = "CH", LegalName = "TEST Quokka Sagl", AddressStreet = "Via Nassa", PostalCode = "6900", City = "Lugano" },
            null,
            Ct);
        await service.PutSectionAsync(OrgA, "vat", new BillingSetupPayload { Regime = DocumentRegime.SwissNotRegistered }, null, Ct);
        Assert.Equal([BillingSetupItemKeys.AutoIssueUnconfirmed], (await Section(service, BillingSetupSections.Automation)).Missing.Select(i => i.Key));

        // Saved again with a profile that can issue: confirmed, with the answers it had.
        var confirmed = await service.PutSectionAsync(OrgA, "automation", new BillingSetupPayload { AutoIssueGuestDocuments = true, EmailGuestDocuments = false }, null, Ct);
        Assert.Empty(confirmed.Missing);
        Assert.NotNull(confirmed.Values["confirmedAt"]);
        Assert.Equal(true, confirmed.Values[BillingSetupFields.AutoIssueGuestDocuments]);

        // The same answers saved again change nothing: the time of the confirmation stays.
        time.Advance(TimeSpan.FromHours(1));
        var again = await service.PutSectionAsync(OrgA, "automation", new BillingSetupPayload { AutoIssueGuestDocuments = true, EmailGuestDocuments = false }, null, Ct);
        Assert.Equal(confirmed.Values["confirmedAt"], again.Values["confirmedAt"]);
        Assert.Empty(again.Missing);

        // The profile stops being complete: the confirmation goes, and comes back only with the step.
        await service.PutSectionAsync(OrgA, "issuer", new BillingSetupPayload { LegalName = "" }, null, Ct);
        Assert.Equal([BillingSetupItemKeys.AutoIssueUnconfirmed], (await Section(service, BillingSetupSections.Automation)).Missing.Select(i => i.Key));
        await service.PutSectionAsync(OrgA, "issuer", new BillingSetupPayload { LegalName = "TEST Quokka Sagl" }, null, Ct);
        Assert.NotEmpty((await Section(service, BillingSetupSections.Automation)).Missing);
        await service.PutSectionAsync(OrgA, "automation", new BillingSetupPayload { EmailGuestDocuments = true }, null, Ct);
        Assert.Empty((await Section(service, BillingSetupSections.Automation)).Missing);
    }

    /// <summary>
    /// The series are asked until they are saved (the default is shown, not yet confirmed), and the Italian reminders are asked of an Italian issuer only: the
    /// conservation agreement, and the reference of the stamp or the confirmation of the virtual stamp. The Italian VAT number of the organization is read, never
    /// copied: without it a VAT regime is missing it, with it the section «issuer» is complete.
    /// </summary>
    [Fact]
    public async Task NumberingAndItaly_ItemsFollowTheStoredAnswers_AndTheItalianTaxIdsAreReadFromTheOrg()
    {
        var databaseName = $"billing-setup-italy-{Guid.NewGuid()}";
        await SeedAsync(databaseName, profileOfA: null, profileOfB: null, italianTaxIdsOfA: false);
        await using var db = NewDb(databaseName, new FixedTenantContext(OrgA));
        var service = BillingSetupTestServices.Create(Issuers(db));
        await service.PutSectionAsync(
            OrgA,
            "issuer",
            new BillingSetupPayload { LegalName = "Casa Rossi S.r.l.", AddressStreet = "Via Roma", PostalCode = "00100", City = "Roma", Province = "rm" },
            null,
            Ct);
        await service.PutSectionAsync(OrgA, "vat", new BillingSetupPayload { Regime = DocumentRegime.VatOrdinary }, null, Ct);

        // The organization has no VAT number on its tax profile: the regime asks for it, and the step cannot save it (it is not a field of the section).
        Assert.Equal([BillingSetupItemKeys.VatNumberMissing], (await Section(service, BillingSetupSections.Issuer)).Missing.Select(i => i.Key));
        await Assert.ThrowsAsync<DomainRuleException>(() => service.PutSectionAsync(OrgA, "vat", new BillingSetupPayload { LegalName = "Altro" }, null, Ct));
        await using (var system = NewDb(databaseName, tenant: null))
        {
            var org = await system.Orgs.SingleAsync(o => o.Id == OrgA);
            org.HasPartitaIva = true;
            org.PartitaIvaNumber = "01234567897";
            await system.SaveChangesAsync();
        }

        var issuer = await Section(service, BillingSetupSections.Issuer);
        Assert.Empty(issuer.Missing);
        Assert.Equal("01234567897", issuer.Values["vatNumber"]);

        // The series: shown with their defaults, asked until saved.
        var numbering = await Section(service, BillingSetupSections.Numbering);
        Assert.Equal(["guest_series_unconfirmed", "owner_series_unconfirmed"], numbering.Missing.Select(i => i.Key));
        Assert.Equal("{year}", numbering.Values["defaultGuestDocumentSeries"]);
        var saved = await service.PutSectionAsync(OrgA, "numbering", new BillingSetupPayload { GuestDocumentSeries = "Q{year}", OwnerInvoiceSeries = "PM{year}" }, null, Ct);
        Assert.Empty(saved.Missing);
        Assert.Equal("Q{year}", saved.Values[BillingSetupFields.GuestDocumentSeries]);

        // Italy: the agreement, and the stamp by its reference or by the confirmation of the virtual stamp.
        var italy = await Section(service, BillingSetupSections.Italy);
        Assert.True(italy.Applies);
        Assert.Equal([BillingSetupItemKeys.ConservationAgreementUnconfirmed, BillingSetupItemKeys.StampDutyUnconfirmed], italy.Missing.Select(i => i.Key));
        var agreement = await service.PutSectionAsync(OrgA, "italy", new BillingSetupPayload { ConservationAgreementConfirmed = true }, null, Ct);
        Assert.Equal([BillingSetupItemKeys.StampDutyUnconfirmed], agreement.Missing.Select(i => i.Key));
        var stamp = await service.PutSectionAsync(OrgA, "italy", new BillingSetupPayload { StampDutyReference = "AUT-2026-001" }, null, Ct);
        Assert.Empty(stamp.Missing);
        var virtualStamp = await service.PutSectionAsync(OrgA, "italy", new BillingSetupPayload { StampDutyReference = "", VirtualStampConfirmed = true }, null, Ct);
        Assert.Empty(virtualStamp.Missing);
        var withoutStamp = await service.PutSectionAsync(OrgA, "italy", new BillingSetupPayload { VirtualStampConfirmed = false }, null, Ct);
        Assert.Equal([BillingSetupItemKeys.StampDutyUnconfirmed], withoutStamp.Missing.Select(i => i.Key));
    }

    /// <summary>
    /// Another epic adds its items through <see cref="IBillingSetupContributor"/> without changing the service: its section applies and is counted, its items
    /// come after the ones of the sections before it (and after ours in the same section), an item is attributed to the section of its contributor, the
    /// first missing item is the first of the first section that has one, and a progress asked for some sections runs only their contributors.
    /// </summary>
    [Fact]
    public async Task Contributors_OtherEpicsAddTheirItems_WithoutTouchingTheService()
    {
        var databaseName = $"billing-setup-contributors-{Guid.NewGuid()}";
        await SeedAsync(databaseName, profileOfA: Profile(OrgA, "TEST Quokka Sagl"), profileOfB: null);
        await using var db = NewDb(databaseName, new FixedTenantContext(OrgA));
        var channels = new FakeContributor(BillingSetupSections.Channels, new BillingSetupItem("channel_commissions_unconfirmed", "wrong", BillingSetupReasons.Unconfirmed));
        var mandates = new FakeContributor(BillingSetupSections.Mandates, new BillingSetupItem("owners_without_mandate", BillingSetupSections.Mandates, BillingSetupReasons.Required, 3));
        var owners = new FakeContributor(BillingSetupSections.Italy, new BillingSetupItem("owner_issuer_missing", BillingSetupSections.Italy, BillingSetupReasons.Required, 2));
        var service = BillingSetupTestServices.Create(Issuers(db), channels, mandates, owners);

        var setup = await service.GetAsync(OrgA, Ct);

        var sections = setup.Sections.ToDictionary(s => s.Section);
        Assert.True(sections[BillingSetupSections.Channels].Applies);
        Assert.Equal("channel_commissions_unconfirmed", Assert.Single(sections[BillingSetupSections.Channels].Missing).Key);
        Assert.Equal(BillingSetupSections.Channels, sections[BillingSetupSections.Channels].Missing[0].Section);
        Assert.Equal(3, Assert.Single(sections[BillingSetupSections.Mandates].Missing).Count);

        // The Italian section of a Swiss issuer applies through the contributor of the Italian owners, and their item follows ours (there is none for a Swiss issuer).
        Assert.True(sections[BillingSetupSections.Italy].Applies);
        Assert.Equal(["owner_issuer_missing"], sections[BillingSetupSections.Italy].Missing.Select(i => i.Key));

        // Counted with the others: the Swiss tourist tax, the channels, the mandates and the Italian section join issuer, vat, numbering and automation.
        var progress = await service.GetProgressAsync(OrgA, null, Ct);
        Assert.Equal((2, 8), (progress.Done, progress.Total));
        Assert.Equal(BillingSetupItemKeys.GuestSeriesUnconfirmed, progress.FirstMissing!.Key);
        Assert.Contains(progress.Missing, i => i.Key == "owners_without_mandate" && i.Count == 3);

        // The flow «Documents» asks for the issuer and the VAT only: no other contributor runs.
        channels.Calls = 0;
        mandates.Calls = 0;
        owners.Calls = 0;
        var issuing = await service.GetProgressAsync(OrgA, [BillingSetupSections.Issuer, BillingSetupSections.Vat], Ct);
        Assert.True(issuing.IsComplete);
        Assert.Equal(0, channels.Calls + mandates.Calls + owners.Calls);
    }

    private static async Task<BillingSetupSection> Section(BillingSetupService service, string section) =>
        (await service.GetAsync(OrgA, Ct)).Sections.Single(s => s.Section == section);

    private static async Task<OrgFiscalIssuerProfile> StoredAsync(string databaseName)
    {
        await using var check = NewDb(databaseName, tenant: null);
        return await check.OrgFiscalIssuerProfiles.AsNoTracking().SingleAsync(p => p.OrgId == OrgA);
    }

    private static async Task SeedAsync(
        string databaseName,
        OrgFiscalIssuerProfile? profileOfA,
        OrgFiscalIssuerProfile? profileOfB,
        bool italianTaxIdsOfA = true)
    {
        await using var system = NewDb(databaseName, tenant: null);
        system.Orgs.AddRange(NewOrg(OrgA, "quokka-test", italianTaxIdsOfA), NewOrg(OrgB, "altro-test", italianTaxIds: false));
        if (profileOfA is not null)
            system.OrgFiscalIssuerProfiles.Add(profileOfA);
        if (profileOfB is not null)
            system.OrgFiscalIssuerProfiles.Add(profileOfB);
        await system.SaveChangesAsync();
    }

    /// <summary>A Swiss issuer that is not registered for the VAT: complete to issue (it needs only its name and its address), nothing else confirmed.</summary>
    private static OrgFiscalIssuerProfile Profile(Guid orgId, string legalName) => new()
    {
        OrgId = orgId,
        IssuerMode = DocumentIssuerMode.CasaZen,
        CountryCode = "CH",
        DocumentRegime = DocumentRegime.SwissNotRegistered,
        LegalName = legalName,
        AddressStreet = "Via Nassa",
        PostalCode = "6900",
        City = "Lugano",
    };

    private static OrgEntity NewOrg(Guid id, string slug, bool italianTaxIds) => new()
    {
        Id = id,
        Name = slug,
        Slug = slug,
        DisplayName = slug,
        ContactEmail = $"{slug}@example.com",
        PlanTier = PlanTier.Starter,
        HasPartitaIva = italianTaxIds,
        PartitaIvaNumber = italianTaxIds ? "01234567897" : null,
    };

    private static AppDbContext NewDb(string databaseName, ITenantContext? tenant) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(databaseName).Options, tenant);

    private static DocumentIssuerService Issuers(AppDbContext db, bool issuanceOn = true, Casazen.Tests.Unit.FakeTimeProvider? time = null)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Testing");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [$"Features:{FeatureFlags.FiscalDocumentIssuance}"] = issuanceOn ? "true" : "false" })
            .Build();
        return new DocumentIssuerService(
            db,
            new ConfigurationFeatureFlags(configuration),
            TaxRuleCatalogs.Resolver(),
            environment.Object,
            time ?? new Casazen.Tests.Unit.FakeTimeProvider(HostScopeScenario.Now),
            NullLogger<DocumentIssuerService>.Instance);
    }

    private static BillingSetupController Controller(BillingSetupService service, Mock<IOrgContextResolver> resolver, ClaimsPrincipal user) =>
        new(service, resolver.Object, HostAuthorizationTestHarness.ScopeResolver(), NullLogger<BillingSetupController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = user,
                    RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
                },
            },
        };

    private sealed class FixedTenantContext(Guid? orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }

    /// <summary>The contributor of another epic: the items it says, and how many times it was asked.</summary>
    private sealed class FakeContributor(string section, params BillingSetupItem[] items) : IBillingSetupContributor
    {
        public string Section { get; } = section;

        public int Calls { get; set; }

        public Task<IReadOnlyList<BillingSetupItem>> EvaluateAsync(Guid orgId, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<BillingSetupItem>>(items);
        }
    }
}

/// <summary>The real billing setup over a real issuer service: the six contributors of the setup and the ones another epic adds.</summary>
internal static class BillingSetupTestServices
{
    public static BillingSetupService Create(IDocumentIssuerService issuers, params IBillingSetupContributor[] more) =>
        new(
            issuers,
            [
                new IssuerSectionContributor(issuers),
                new VatSectionContributor(issuers),
                new NumberingSectionContributor(issuers),
                new TouristTaxSectionContributor(issuers),
                new AutomationSectionContributor(issuers),
                new ItalySectionContributor(issuers),
                .. more,
            ],
            NullLogger<BillingSetupService>.Instance);
}
