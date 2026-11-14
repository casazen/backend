using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Features;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Fiscal;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using FiscalDocumentKind = Casazen.Core.Options.FiscalDocumentKind;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// FIS-02, NF-82, NF-112, E11/F01/US01/T01: CasaZen is the only issuer. The rules that cost if they fail: every booking of every source is CasaZen's while
/// the issuance is on, a booking never has two issuers (a legacy Octorate lock is kept), a retired mode is refused and changes nothing, and the setting of
/// one organization is never returned to another.
/// </summary>
public class DocumentIssuerProofTests
{
    private static readonly Guid OrgA = Guid.Parse("6d5c4b3a-2918-4f7e-8d6c-5b4a39281703");
    private static readonly Guid OrgB = Guid.Parse("7e6d5c4b-3a29-4f8e-9d7c-6c5b4a392804");
    private static readonly Guid PropertyOfColleague = Guid.Parse("3c4d5e6f-7a8b-4c9d-8e0f-1a2b3c4d5e14");

    /// <summary>
    /// PROOF TEST: data rule. A booking is locked to CasaZen once (the first document is kept); the rows are written with CasaZen only, so Octorate can no
    /// longer be locked; a booking a legacy row locked to Octorate keeps it: CasaZen cannot be locked over it (409 <c>document_issuer_conflict</c>) and
    /// the decision says so, so a booking Octorate issued for is never issued twice.
    /// </summary>
    [Fact]
    public async Task DocumentIssuer_NeverTwoIssuersPerBooking_Test()
    {
        await using var db = NewDb($"document-issuer-lock-{Guid.NewGuid()}", new FixedTenantContext(OrgA));
        db.OrgFiscalIssuerProfiles.Add(NewProfile(OrgA, DocumentIssuerMode.CasaZen));
        var ownBooking = NewBooking(OrgA, BookingSource.BookingCom);
        var legacyBooking = NewBooking(OrgA, BookingSource.Airbnb);
        db.Bookings.AddRange(ownBooking, legacyBooking);
        db.BookingDocumentIssuers.Add(new BookingDocumentIssuer
        {
            OrgId = OrgA,
            BookingId = legacyBooking.Id,
            Issuer = BookingDocumentIssuerKind.Octorate,
            LockedAt = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync();
        var sut = Service(db, issuanceOn: true);

        // CasaZen takes a booking of a portal; the same kind again changes nothing.
        var firstDocument = Guid.NewGuid();
        var locked = await sut.LockIssuerAsync(ownBooking.Id, BookingDocumentIssuerKind.CasaZen, firstDocument);
        Assert.Equal(BookingDocumentIssuerKind.CasaZen, locked.Issuer);
        Assert.Equal(firstDocument, locked.LockedByDocumentId);
        var again = await sut.LockIssuerAsync(ownBooking.Id, BookingDocumentIssuerKind.CasaZen, Guid.NewGuid());
        Assert.Equal(firstDocument, again.LockedByDocumentId);
        Assert.Equal(new IssuerDecision(IssuerDecisionKind.CasaZen, IssuerReasons.IssuerCasazen), await sut.ResolveIssuerAsync(ownBooking.Id));

        // The rows are written with CasaZen only: an imported document never locks a booking to Octorate.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => sut.LockIssuerAsync(ownBooking.Id, BookingDocumentIssuerKind.Octorate, Guid.NewGuid()));

        // A legacy lock to Octorate stays: Octorate issued that booking, CasaZen does not issue it again.
        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => sut.LockIssuerAsync(legacyBooking.Id, BookingDocumentIssuerKind.CasaZen, Guid.NewGuid()));
        Assert.Equal(DocumentIssuerErrorCodes.IssuerConflict, conflict.Code);
        Assert.Equal(
            new IssuerDecision(IssuerDecisionKind.Octorate, IssuerReasons.IssuerLockedOctorate),
            await sut.ResolveIssuerAsync(legacyBooking.Id));

        db.ChangeTracker.Clear();
        var rows = await db.BookingDocumentIssuers.AsNoTracking().ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(BookingDocumentIssuerKind.CasaZen, rows.Single(r => r.BookingId == ownBooking.Id).Issuer);
        Assert.Equal(BookingDocumentIssuerKind.Octorate, rows.Single(r => r.BookingId == legacyBooking.Id).Issuer);
    }

    /// <summary>
    /// PROOF TEST: data rule (replaces «an OTA booking is never CasaZen's»). Whatever mode a stored profile still says (the retired Octorate and accountant
    /// included), every booking of every source, the portals through Octorate included, is CasaZen's while the issuance is on, and can be locked to it.
    /// With the issuance off nobody issues and nothing can be locked.
    /// </summary>
    [Fact]
    public async Task DocumentIssuer_EveryBookingSource_IsCasazen_Test()
    {
        var sources = Enum.GetValues<BookingSource>();
        Assert.Contains(BookingSource.BookingCom, sources);
        Assert.Contains(BookingSource.OtherChannel, sources);

        foreach (var mode in Enum.GetValues<DocumentIssuerMode>())
        {
            await using var db = NewDb($"document-issuer-sources-{Guid.NewGuid()}", new FixedTenantContext(OrgA));
            db.OrgFiscalIssuerProfiles.Add(NewProfile(OrgA, mode));
            var bookings = sources.Select(source => NewBooking(OrgA, source)).ToList();
            db.Bookings.AddRange(bookings);
            await db.SaveChangesAsync();

            // Issuance off: nobody issues, and no booking can be locked to CasaZen.
            var off = Service(db, issuanceOn: false);
            Assert.Equal(new IssuerDecision(IssuerDecisionKind.None, IssuerReasons.IssuanceOff), await off.ResolveIssuerAsync(bookings[0].Id));
            var notAvailable = await Assert.ThrowsAsync<DomainRuleException>(
                () => off.LockIssuerAsync(bookings[0].Id, BookingDocumentIssuerKind.CasaZen, null));
            Assert.Equal(DocumentIssuerErrorCodes.IssuanceNotAvailable, notAvailable.Code);
            Assert.Empty(await db.BookingDocumentIssuers.AsNoTracking().ToListAsync());

            var sut = Service(db, issuanceOn: true);
            foreach (var booking in bookings)
            {
                Assert.Equal(new IssuerDecision(IssuerDecisionKind.CasaZen, IssuerReasons.IssuerCasazen), await sut.ResolveIssuerAsync(booking.Id));
                Assert.Equal(BookingDocumentIssuerKind.CasaZen, (await sut.LockIssuerAsync(booking.Id, BookingDocumentIssuerKind.CasaZen, Guid.NewGuid())).Issuer);
            }

            db.ChangeTracker.Clear();
            Assert.Equal(bookings.Count, await db.BookingDocumentIssuers.AsNoTracking().CountAsync(r => r.Issuer == BookingDocumentIssuerKind.CasaZen));
        }
    }

    /// <summary>
    /// PROOF TEST: permission rule. Two organizations each saved their issuer. The caller of org A reads its own setting only: the one of org B is not in
    /// the answer, and not found even when the service is asked for it by id; the booking of org B is a 404 of the preview, a booking the caller sees and
    /// does not reach a 403, and a caller who reaches only some of the properties cannot change the setting (403).
    /// </summary>
    [Fact]
    public async Task FiscalDocumentSettings_OtherOrg_NotReadable_Test()
    {
        var databaseName = $"document-issuer-settings-{Guid.NewGuid()}";
        var bookingOfOrgB = Guid.NewGuid();
        var bookingOfColleague = Guid.NewGuid();
        await using (var system = NewDb(databaseName, tenant: null))
        {
            system.Orgs.AddRange(NewOrg(OrgA, "casa-rossi"), NewOrg(OrgB, "casa-bianchi"));
            system.OrgFiscalIssuerProfiles.AddRange(
                NewProfile(OrgA, DocumentIssuerMode.CasaZen, "Casa Rossi S.r.l."),
                NewProfile(OrgB, DocumentIssuerMode.CasaZen, "Casa Bianchi S.r.l."));
            await system.SaveChangesAsync();
        }

        await using var db = NewDb(databaseName, new FixedTenantContext(OrgA));
        var sut = Service(db, issuanceOn: true);
        var resolver = new Mock<IOrgContextResolver>();
        resolver.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OrgA);
        var lookup = new Mock<IHostResourceLookup>();
        lookup.Setup(l => l.ForBookingAsync(bookingOfOrgB, It.IsAny<CancellationToken>())).ReturnsAsync((HostResource?)null);
        lookup.Setup(l => l.ForBookingAsync(bookingOfColleague, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostResource(OrgA, "auth0|colleague-a", PropertyOfColleague));

        // An account in no org team with no org-wide role: it reaches only the properties it created, so it cannot change the setting of the org.
        var staff = Controller(sut, resolver, lookup, HostAuthorizationTestHarness.User("auth0|staff-a", "Staff"));
        var read = Assert.IsType<OkObjectResult>((await staff.GetSettings(CancellationToken.None)).Result);
        var settings = Assert.IsType<FiscalDocumentSettingsDto>(read.Value);
        Assert.Equal("CasaZen", settings.Mode);
        Assert.Equal("Casa Rossi S.r.l.", settings.Issuer.LegalName);
        Assert.DoesNotContain("Bianchi", JsonSerializer.Serialize(settings, new JsonSerializerOptions(JsonSerializerDefaults.Web)), StringComparison.Ordinal);

        // By id, the service under the filter of org A finds nothing of org B: the empty setting of one that has saved nothing.
        Assert.Null((await sut.GetSettingsAsync(OrgB)).Mode);

        var refused = await staff.PutSettings(new SaveFiscalDocumentSettingsRequest { Mode = "CasaZen" }, CancellationToken.None);
        var problem = Assert.IsType<ObjectResult>(refused.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, problem.StatusCode);
        Assert.Equal(DocumentIssuerErrorCodes.NeedsWholeOrg, Assert.IsAssignableFrom<ProblemDetails>(problem.Value).Extensions["code"]);

        Assert.Equal(
            StatusCodes.Status404NotFound,
            Assert.IsType<ObjectResult>((await staff.IssuerPreview(bookingOfOrgB, CancellationToken.None)).Result).StatusCode);
        Assert.IsType<ForbidResult>((await staff.IssuerPreview(bookingOfColleague, CancellationToken.None)).Result);

        // The preview of the organization (no booking): who issues (the flag of this controller is off) and the tax rule in force today for the profile
        // of org A, with the rate of every line type (E11/F01/US01/T02).
        var preview = Assert.IsType<IssuerPreviewDto>(Assert.IsType<OkObjectResult>((await staff.IssuerPreview(null, CancellationToken.None)).Result).Value);
        Assert.Equal(("None", IssuerReasons.IssuanceOff), (preview.Issuer, preview.Reason));
        Assert.NotNull(preview.Rule);
        Assert.Equal("it-ordinary-2026.1", preview.Rule.Version);
        Assert.Equal(Enum.GetValues<FiscalLineType>().Length, preview.Rule.Rates.Count);
        Assert.Contains(preview.Rule.Rates, rate => rate.LineType == nameof(FiscalLineType.Accommodation) && rate.Percent == 10m);

        // A manager of the whole org changes the setting of its own org (a draft: the profile is not complete yet), and org B's stays as it was.
        var manager = Controller(sut, resolver, lookup, HostAuthorizationTestHarness.User("auth0|manager-a", "PropertyManager"));
        var saved = await manager.PutSettings(
            new SaveFiscalDocumentSettingsRequest { Regime = "VatOrdinary", Issuer = new SaveIssuerDataRequest { LegalName = "Casa Rossi Due S.r.l." } },
            CancellationToken.None);
        Assert.Equal("Casa Rossi Due S.r.l.", Assert.IsType<FiscalDocumentSettingsDto>(Assert.IsType<OkObjectResult>(saved.Result).Value).Issuer.LegalName);

        await using var system2 = NewDb(databaseName, tenant: null);
        var others = await system2.OrgFiscalIssuerProfiles.AsNoTracking().SingleAsync(p => p.OrgId == OrgB);
        Assert.Equal("Casa Bianchi S.r.l.", others.LegalName);
        Assert.Equal(DocumentIssuerMode.CasaZen, others.IssuerMode);
    }

    /// <summary>
    /// PROOF TEST: privacy rule. The IBAN and the QR-IBAN of the issuer are shown in full only to the caller who may change the setting (the whole
    /// organization and <c>property.write</c>): a collaborator who reaches only some of the properties and a role of the whole organization that cannot write
    /// read the setting with the accounts masked, and nothing of the full numbers is in their answer.
    /// </summary>
    [Fact]
    public async Task FiscalDocumentSettings_BankAccounts_AreInClearOnlyForWhoMayChangeTheSetting_Test()
    {
        const string iban = "CH9300762011623852957";
        const string qrIban = "CH4431999123000889012";
        var databaseName = $"document-issuer-bank-{Guid.NewGuid()}";
        await using (var system = NewDb(databaseName, tenant: null))
        {
            system.Orgs.Add(NewOrg(OrgA, "casa-rossi"));
            var profile = NewProfile(OrgA, DocumentIssuerMode.CasaZen, "Casa Rossi S.r.l.");
            profile.Iban = iban;
            profile.QrIban = qrIban;
            system.OrgFiscalIssuerProfiles.Add(profile);
            await system.SaveChangesAsync();
        }

        await using var db = NewDb(databaseName, new FixedTenantContext(OrgA));
        var sut = Service(db, issuanceOn: true);
        var resolver = new Mock<IOrgContextResolver>();
        resolver.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OrgA);
        var lookup = new Mock<IHostResourceLookup>();

        // A collaborator who reaches only some of the properties reads the setting, the accounts masked.
        var staff = Controller(sut, resolver, lookup, HostAuthorizationTestHarness.User("auth0|staff-a", "Staff"));
        var partial = Assert.IsType<FiscalDocumentSettingsDto>(Assert.IsType<OkObjectResult>((await staff.GetSettings(CancellationToken.None)).Result).Value);
        AssertMasked(partial);

        // A role of the whole organization that cannot write reads them masked too.
        var readOnly = Controller(
            sut, resolver, lookup, HostAuthorizationTestHarness.User("auth0|manager-a", "PropertyManager"), (_, permission) => permission != "property.write");
        AssertMasked(Assert.IsType<FiscalDocumentSettingsDto>(Assert.IsType<OkObjectResult>((await readOnly.GetSettings(CancellationToken.None)).Result).Value));

        // The manager of the whole organization, who may change the setting, reads them in full and gets them back when it saves.
        var manager = Controller(sut, resolver, lookup, HostAuthorizationTestHarness.User("auth0|manager-a", "PropertyManager"));
        var full = Assert.IsType<FiscalDocumentSettingsDto>(Assert.IsType<OkObjectResult>((await manager.GetSettings(CancellationToken.None)).Result).Value);
        Assert.Equal(iban, full.Issuer.Iban);
        Assert.Equal(qrIban, full.Issuer.QrIban);
        Assert.Equal("CH93…2957", full.Issuer.IbanMasked);
        var saved = await manager.PutSettings(
            new SaveFiscalDocumentSettingsRequest
            {
                Regime = "VatOrdinary",
                Issuer = new SaveIssuerDataRequest { LegalName = "Casa Rossi S.r.l.", Iban = iban, QrIban = qrIban },
            },
            CancellationToken.None);
        var answer = Assert.IsType<FiscalDocumentSettingsDto>(Assert.IsType<OkObjectResult>(saved.Result).Value);
        Assert.Equal(iban, answer.Issuer.Iban);
        Assert.Equal(qrIban, answer.Issuer.QrIban);

        void AssertMasked(FiscalDocumentSettingsDto settings)
        {
            Assert.Equal("Casa Rossi S.r.l.", settings.Issuer.LegalName);
            Assert.Null(settings.Issuer.Iban);
            Assert.Null(settings.Issuer.QrIban);
            Assert.Equal("CH93…2957", settings.Issuer.IbanMasked);
            Assert.Equal("CH44…9012", settings.Issuer.QrIbanMasked);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.DoesNotContain("00762011623852", json, StringComparison.Ordinal);
            Assert.DoesNotContain("31999123000889", json, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// PROOF TEST: data rule. The modes Octorate and accountant were retired on 2026-11-14: a PUT that asks for either answers 422
    /// <c>issuer_mode_retired</c> and changes nothing of the stored profile; the old name of the mode CasaZen is still read as CasaZen.
    /// </summary>
    [Fact]
    public async Task IssuerSettings_RetiredModeRefused_Test()
    {
        var databaseName = $"document-issuer-retired-{Guid.NewGuid()}";
        await using (var system = NewDb(databaseName, tenant: null))
        {
            system.Orgs.Add(NewOrg(OrgA, "casa-rossi"));
            system.OrgFiscalIssuerProfiles.Add(NewProfile(OrgA, DocumentIssuerMode.CasaZen, "Casa Rossi S.r.l."));
            await system.SaveChangesAsync();
        }

        await using var db = NewDb(databaseName, new FixedTenantContext(OrgA));
        var resolver = new Mock<IOrgContextResolver>();
        resolver.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OrgA);
        var manager = Controller(Service(db, issuanceOn: true), resolver, new Mock<IHostResourceLookup>(), HostAuthorizationTestHarness.User("auth0|manager-a", "PropertyManager"));

        foreach (var retired in new[] { "Octorate", "Accountant" })
        {
            var refusal = await Assert.ThrowsAsync<DomainRuleException>(() => manager.PutSettings(
                new SaveFiscalDocumentSettingsRequest { Mode = retired, Regime = "VatFlatRate", Issuer = new SaveIssuerDataRequest { LegalName = "Altro Nome S.r.l." } },
                CancellationToken.None));
            Assert.Equal(DocumentIssuerErrorCodes.ModeRetired, refusal.Code);
            Assert.Equal("IssuerModeRetired", refusal.MessageKey);
        }

        await using (var check = NewDb(databaseName, tenant: null))
        {
            var stored = await check.OrgFiscalIssuerProfiles.AsNoTracking().SingleAsync(p => p.OrgId == OrgA);
            Assert.Equal("Casa Rossi S.r.l.", stored.LegalName);
            Assert.Equal(DocumentRegime.VatOrdinary, stored.DocumentRegime);
            Assert.Equal(DocumentIssuerMode.CasaZen, stored.IssuerMode);
        }

        var legacyName = await manager.PutSettings(
            new SaveFiscalDocumentSettingsRequest { Mode = "CasaZenDirectOnly", Regime = "VatOrdinary", Issuer = new SaveIssuerDataRequest { LegalName = "Casa Rossi S.r.l." } },
            CancellationToken.None);
        Assert.Equal("CasaZen", Assert.IsType<FiscalDocumentSettingsDto>(Assert.IsType<OkObjectResult>(legacyName.Result).Value).Mode);
    }

    [Fact]
    public async Task DocumentIssuer_TheSettingIsSavedOnlyWhenItCanWork_AndARefusedSaveChangesNothing()
    {
        var databaseName = $"document-issuer-modes-{Guid.NewGuid()}";
        await using (var system = NewDb(databaseName, tenant: null))
        {
            system.Orgs.Add(NewOrg(OrgA, "casa-rossi"));
            await system.SaveChangesAsync();
        }

        await using var db = NewDb(databaseName, new FixedTenantContext(OrgA));
        var complete = new SaveIssuerSettingsCommand
        {
            Regime = DocumentRegime.VatOrdinary,
            LegalName = "Casa Rossi S.r.l.",
            AddressStreet = "Via Roma",
            AddressCivic = "1",
            PostalCode = "00100",
            City = "Roma",
            Province = "rm",
            FatturaPaRegimeCode = "rf01",
            Confirmed = true,
        };

        // The flag is off: nothing can be saved, whatever else is true.
        var off = Service(db, issuanceOn: false);
        var notAvailable = await Assert.ThrowsAsync<DomainRuleException>(() => off.SaveSettingsAsync(OrgA, complete));
        Assert.Equal(DocumentIssuerErrorCodes.IssuanceNotAvailable, notAvailable.Code);

        // The flag is on, the wizard is confirmed and the organization has no VAT number: the missing field is named.
        var on = Service(db, issuanceOn: true);
        var incomplete = await Assert.ThrowsAsync<DomainRuleException>(() => on.SaveSettingsAsync(OrgA, complete));
        Assert.Equal(DocumentIssuerErrorCodes.ProfileIncomplete, incomplete.Code);
        Assert.Equal(new[] { IssuerFields.VatNumber }, Assert.IsAssignableFrom<IReadOnlyList<string>>(incomplete.Extensions["fields"]).ToArray());

        // A Swiss regime in Italy, a value that is not valid, a VAT registration the regime contradicts: each names its field.
        var mismatch = await Assert.ThrowsAsync<DomainRuleException>(() => on.SaveSettingsAsync(OrgA, complete with { Regime = DocumentRegime.SwissVatRegistered }));
        Assert.Equal(DocumentIssuerErrorCodes.RegimeCountryMismatch, mismatch.Code);
        var invalid = await Assert.ThrowsAsync<DomainRuleException>(() => on.SaveSettingsAsync(OrgA, complete with { IssuerPec = "not an address" }));
        Assert.Equal(DocumentIssuerErrorCodes.FieldInvalid, invalid.Code);
        Assert.Equal(IssuerFields.IssuerPec, invalid.Extensions["field"]);
        var registration = await Assert.ThrowsAsync<DomainRuleException>(() => on.SaveSettingsAsync(OrgA, complete with { VatRegistered = false }));
        Assert.Equal(IssuerFields.VatRegistered, registration.Extensions["field"]);

        // Nothing was saved by the refusals; the same answers not confirmed are a draft, with what is missing.
        Assert.Empty(await db.OrgFiscalIssuerProfiles.IgnoreQueryFilters().AsNoTracking().ToListAsync());
        var saved = await on.SaveSettingsAsync(OrgA, complete with { Confirmed = false });
        Assert.Equal(DocumentIssuerMode.CasaZen, saved.Mode);
        Assert.Equal(DocumentRegime.VatOrdinary, saved.Regime);
        Assert.Equal("RF01", saved.EffectiveRegimeCode);
        Assert.Equal("RM", saved.Issuer.Province);
        Assert.True(saved.Issuer.VatRegistered);
        Assert.Null(saved.ConfirmedAt);
        Assert.Equal(new[] { IssuerFields.VatNumber }, saved.MissingFields.ToArray());

        // The setting shows the tax rule in force today for the profile (the catalog FiscalDocuments:TaxRules); with no rule for it, none is shown and
        // nothing would be issued (document_rule_not_configured) instead of guessing a rate.
        Assert.NotNull(saved.Rule);
        Assert.Equal(("it-ordinary-2026.1", FiscalDocumentKind.Invoice), (saved.Rule.Version, saved.Rule.DocumentKind));
        Assert.Null((await Service(db, issuanceOn: true, new FiscalTaxRulesOptions()).GetSettingsAsync(OrgA)).Rule);
    }

    /// <summary>
    /// A Swiss issuer (E11/F01/US01/T01): the UID is checked (format and check digit of eCH-0097) and stored as <c>CHE-123.456.789</c>, the Italian tax ids
    /// are not asked, the IBAN and the QR-IBAN are checked (mod 97, a QR-IBAN of a Swiss bank), and the settings return them with the treatments and the
    /// series. A UID with a wrong check digit or an IBAN in the place of the QR-IBAN is refused on its field.
    /// </summary>
    [Fact]
    public async Task IssuerSettings_SwissProfile_ChecksTheUidAndTheBankAccounts()
    {
        var databaseName = $"document-issuer-swiss-{Guid.NewGuid()}";
        await using (var system = NewDb(databaseName, tenant: null))
        {
            system.Orgs.Add(NewOrg(OrgA, "quokka-test"));
            await system.SaveChangesAsync();
        }

        await using var db = NewDb(databaseName, new FixedTenantContext(OrgA));
        var sut = Service(db, issuanceOn: true);
        var swiss = new SaveIssuerSettingsCommand
        {
            Regime = DocumentRegime.SwissVatRegistered,
            LegalName = "TEST Quokka Sagl",
            AddressStreet = "Via Nassa",
            AddressCivic = "5",
            PostalCode = "6900",
            City = "Lugano",
            Province = "ti",
            CountryCode = "ch",
            SwissVatNumber = "che 109 322 551 mwst",
            Iban = "CH93 0076 2011 6238 5295 7",
            QrIban = "CH44 3199 9123 0008 8901 2",
            CleaningFeeTreatment = CleaningFeeTreatment.Service,
            GuestDocumentSeries = "Q{year}",
            PlatformCollectedTouristTaxChannels = ["Airbnb", "airbnb"],
            Confirmed = true,
        };

        var wrongCheckDigit = await Assert.ThrowsAsync<DomainRuleException>(() => sut.SaveSettingsAsync(OrgA, swiss with { SwissVatNumber = "CHE-109.322.552" }));
        Assert.Equal(IssuerFields.SwissVatNumber, wrongCheckDigit.Extensions["field"]);
        var notQr = await Assert.ThrowsAsync<DomainRuleException>(() => sut.SaveSettingsAsync(OrgA, swiss with { QrIban = "CH93 0076 2011 6238 5295 7" }));
        Assert.Equal(IssuerFields.QrIban, notQr.Extensions["field"]);
        var badIban = await Assert.ThrowsAsync<DomainRuleException>(() => sut.SaveSettingsAsync(OrgA, swiss with { Iban = "CH93 0076 2011 6238 5295 8" }));
        Assert.Equal(IssuerFields.Iban, badIban.Extensions["field"]);
        var italianRegime = await Assert.ThrowsAsync<DomainRuleException>(() => sut.SaveSettingsAsync(OrgA, swiss with { Regime = DocumentRegime.VatOrdinary }));
        Assert.Equal(DocumentIssuerErrorCodes.RegimeCountryMismatch, italianRegime.Code);
        Assert.Empty(await db.OrgFiscalIssuerProfiles.IgnoreQueryFilters().AsNoTracking().ToListAsync());

        var saved = await sut.SaveSettingsAsync(OrgA, swiss);

        Assert.Empty(saved.MissingFields);
        Assert.NotNull(saved.ConfirmedAt);
        Assert.Equal("CH", saved.Issuer.CountryCode);
        Assert.Equal("CHE-109.322.551", saved.Issuer.SwissVatNumber);
        Assert.True(saved.Issuer.VatRegistered);
        Assert.Equal("CH9300762011623852957", saved.Issuer.Iban);
        Assert.Equal("CH4431999123000889012", saved.Issuer.QrIban);
        Assert.Equal(CleaningFeeTreatment.Service, saved.Issuer.CleaningFeeTreatment);
        Assert.Equal(StayVatTreatment.Standard, saved.Issuer.StayVatTreatment);
        Assert.Equal("Q{year}", saved.Issuer.GuestDocumentSeries);
        Assert.Equal([BookingSource.Airbnb], saved.Issuer.PlatformCollectedTouristTaxChannels);
        Assert.Null(saved.EffectiveRegimeCode);

        // The same issuer not registered: no UID is needed and the registration follows the regime.
        var notRegistered = await sut.SaveSettingsAsync(OrgA, swiss with { Regime = DocumentRegime.SwissNotRegistered, SwissVatNumber = null });
        Assert.Empty(notRegistered.MissingFields);
        Assert.False(notRegistered.Issuer.VatRegistered);
        Assert.Null(notRegistered.Issuer.SwissVatNumber);
    }

    private static DocumentIssuerService Service(AppDbContext db, bool issuanceOn, FiscalTaxRulesOptions? catalog = null)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Testing");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [$"Features:{FeatureFlags.FiscalDocumentIssuance}"] = issuanceOn ? "true" : "false" })
            .Build();
        return new DocumentIssuerService(
            db,
            new ConfigurationFeatureFlags(configuration),
            TaxRuleCatalogs.Resolver(catalog),
            environment.Object,
            TimeProvider.System,
            NullLogger<DocumentIssuerService>.Instance);
    }

    private static FiscalDocumentSettingsController Controller(
        DocumentIssuerService service,
        Mock<IOrgContextResolver> resolver,
        Mock<IHostResourceLookup> lookup,
        System.Security.Claims.ClaimsPrincipal user,
        Func<string, string, bool>? hasPermission = null)
    {
        var sdi = new Mock<ISdiEInvoiceProvider>();
        sdi.SetupGet(p => p.IsConfigured).Returns(false);
        sdi.SetupGet(p => p.Name).Returns("none");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        return new FiscalDocumentSettingsController(
            service,
            resolver.Object,
            HostAuthorizationTestHarness.ScopeResolver(),
            lookup.Object,
            HostAuthorizationTestHarness.Create(OrgA, hasPermission),
            new ConfigurationFeatureFlags(configuration),
            Microsoft.Extensions.Options.Options.Create(new FiscalDocumentRulesOptions()),
            sdi.Object,
            NullLogger<FiscalDocumentSettingsController>.Instance)
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
    }

    private static Booking NewBooking(Guid orgId, BookingSource source) => new()
    {
        OrgId = orgId,
        PropertyId = Guid.NewGuid(),
        GuestId = Guid.NewGuid(),
        CheckInDate = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc),
        CheckOutDate = new DateTime(2026, 12, 3, 0, 0, 0, DateTimeKind.Utc),
        NumberOfGuests = 2,
        Status = BookingStatus.Confirmed,
        Source = source,
    };

    private static OrgFiscalIssuerProfile NewProfile(Guid orgId, DocumentIssuerMode mode, string? legalName = null) => new()
    {
        OrgId = orgId,
        IssuerMode = mode,
        DocumentRegime = DocumentRegime.VatOrdinary,
        LegalName = legalName,
    };

    private static OrgEntity NewOrg(Guid id, string slug) => new()
    {
        Id = id,
        Name = slug,
        Slug = slug,
        DisplayName = slug,
        ContactEmail = $"{slug}@example.com",
        PlanTier = PlanTier.Starter,
    };

    private static AppDbContext NewDb(string databaseName, ITenantContext? tenant) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(databaseName).Options, tenant);

    private sealed class FixedTenantContext(Guid? orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }
}
