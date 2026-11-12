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

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// FIS-02, NF-82, E06/F02/US02/T02: who issues the documents of a booking. The rules that cost if they fail: a booking never has two issuers, CasaZen
/// never issues for an OTA booking whatever the setting says, and the setting of one organization is never returned to another.
/// </summary>
public class DocumentIssuerProofTests
{
    private static readonly Guid OrgA = Guid.Parse("6d5c4b3a-2918-4f7e-8d6c-5b4a39281703");
    private static readonly Guid OrgB = Guid.Parse("7e6d5c4b-3a29-4f8e-9d7c-6c5b4a392804");
    private static readonly Guid PropertyOfColleague = Guid.Parse("3c4d5e6f-7a8b-4c9d-8e0f-1a2b3c4d5e14");

    /// <summary>
    /// PROOF TEST: data rule. A booking locked to CasaZen cannot be locked to Octorate, and a booking locked to Octorate cannot be locked to CasaZen:
    /// 409 <c>document_issuer_conflict</c>, the row of the first issuer untouched and no second row. The same kind again is a success that keeps the
    /// first document; the decision of the booking follows the lock.
    /// </summary>
    [Fact]
    public async Task DocumentIssuer_NeverTwoIssuersPerBooking_Test()
    {
        var databaseName = $"document-issuer-lock-{Guid.NewGuid()}";
        await using var db = NewDb(databaseName, new FixedTenantContext(OrgA));
        db.OrgFiscalIssuerProfiles.Add(NewProfile(OrgA, DocumentIssuerMode.CasaZenDirectOnly));
        var ownBooking = NewBooking(OrgA, BookingSource.Direct);
        var channelBooking = NewBooking(OrgA, BookingSource.Manual);
        db.Bookings.AddRange(ownBooking, channelBooking);
        await db.SaveChangesAsync();
        var sut = Service(db, issuanceOn: true);

        // CasaZen takes a booking: Octorate cannot take it afterwards.
        var firstDocument = Guid.NewGuid();
        var locked = await sut.LockIssuerAsync(ownBooking.Id, BookingDocumentIssuerKind.CasaZen, firstDocument);
        Assert.Equal(BookingDocumentIssuerKind.CasaZen, locked.Issuer);
        Assert.Equal(firstDocument, locked.LockedByDocumentId);
        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => sut.LockIssuerAsync(ownBooking.Id, BookingDocumentIssuerKind.Octorate, Guid.NewGuid()));
        Assert.Equal(DocumentIssuerErrorCodes.IssuerConflict, conflict.Code);

        // The same kind again changes nothing: the first document stays the one that locked it.
        var again = await sut.LockIssuerAsync(ownBooking.Id, BookingDocumentIssuerKind.CasaZen, Guid.NewGuid());
        Assert.Equal(BookingDocumentIssuerKind.CasaZen, again.Issuer);
        Assert.Equal(firstDocument, again.LockedByDocumentId);
        Assert.Equal(new IssuerDecision(IssuerDecisionKind.CasaZen, IssuerReasons.IssuerCasazen), await sut.ResolveIssuerAsync(ownBooking.Id));

        // The reverse: Octorate issued (even by hand, in its panel), CasaZen cannot issue for the booking.
        await sut.LockIssuerAsync(channelBooking.Id, BookingDocumentIssuerKind.Octorate, null);
        var reverse = await Assert.ThrowsAsync<DomainConflictException>(
            () => sut.LockIssuerAsync(channelBooking.Id, BookingDocumentIssuerKind.CasaZen, Guid.NewGuid()));
        Assert.Equal(DocumentIssuerErrorCodes.IssuerConflict, reverse.Code);
        Assert.Equal(
            new IssuerDecision(IssuerDecisionKind.Octorate, IssuerReasons.IssuerLockedOctorate),
            await sut.ResolveIssuerAsync(channelBooking.Id));

        db.ChangeTracker.Clear();
        var rows = await db.BookingDocumentIssuers.AsNoTracking().ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(BookingDocumentIssuerKind.CasaZen, rows.Single(r => r.BookingId == ownBooking.Id).Issuer);
        Assert.Equal(BookingDocumentIssuerKind.Octorate, rows.Single(r => r.BookingId == channelBooking.Id).Issuer);
    }

    /// <summary>
    /// PROOF TEST: data rule. Whatever the organization chose (Octorate, CasaZen for its direct bookings, the accountant), a booking that comes from
    /// an OTA is never CasaZen's: the decision is never CasaZen, and locking CasaZen as its issuer is 422 <c>document_booking_not_direct</c> with no
    /// row written. A direct booking and one entered by the host are CasaZen's only in the mode CasaZen, and only while issuance is on.
    /// </summary>
    [Fact]
    public async Task DocumentIssuer_OtaBooking_NeverCasazen_Test()
    {
        var otaSources = Enum.GetValues<BookingSource>().Where(FiscalCopy.IsOtaBookingSource).ToList();
        Assert.Contains(BookingSource.OtherChannel, otaSources);

        foreach (var mode in Enum.GetValues<DocumentIssuerMode>())
        {
            await using var db = NewDb($"document-issuer-ota-{Guid.NewGuid()}", new FixedTenantContext(OrgA));
            db.OrgFiscalIssuerProfiles.Add(NewProfile(OrgA, mode));
            var ota = otaSources.Select(source => NewBooking(OrgA, source)).ToList();
            var own = new[] { NewBooking(OrgA, BookingSource.Direct), NewBooking(OrgA, BookingSource.Manual) };
            db.Bookings.AddRange(ota);
            db.Bookings.AddRange(own);
            await db.SaveChangesAsync();
            var sut = Service(db, issuanceOn: true);

            foreach (var booking in ota)
            {
                var decision = await sut.ResolveIssuerAsync(booking.Id);
                Assert.NotEqual(IssuerDecisionKind.CasaZen, decision.Kind);
                Assert.Equal(
                    mode switch
                    {
                        DocumentIssuerMode.Octorate => IssuerReasons.IssuerOctorate,
                        DocumentIssuerMode.CasaZenDirectOnly => IssuerReasons.BookingNotDirect,
                        _ => IssuerReasons.IssuerNobody,
                    },
                    decision.Reason);

                var refusal = await Assert.ThrowsAsync<DomainRuleException>(
                    () => sut.LockIssuerAsync(booking.Id, BookingDocumentIssuerKind.CasaZen, Guid.NewGuid()));
                Assert.Equal(DocumentIssuerErrorCodes.BookingNotDirect, refusal.Code);
            }

            // Nothing was written for an OTA booking; the direct ones are CasaZen's only when the organization chose it.
            db.ChangeTracker.Clear();
            Assert.Empty(await db.BookingDocumentIssuers.AsNoTracking().ToListAsync());
            foreach (var booking in own)
            {
                var decision = await sut.ResolveIssuerAsync(booking.Id);
                Assert.Equal(mode == DocumentIssuerMode.CasaZenDirectOnly, decision.Kind == IssuerDecisionKind.CasaZen);
            }

            // Issuance switched off: even the mode CasaZen decides nobody, and no booking can be locked to CasaZen.
            var off = Service(db, issuanceOn: false);
            if (mode == DocumentIssuerMode.CasaZenDirectOnly)
            {
                Assert.Equal(IssuerReasons.IssuanceOff, (await off.ResolveIssuerAsync(own[0].Id)).Reason);
                var notAvailable = await Assert.ThrowsAsync<DomainRuleException>(
                    () => off.LockIssuerAsync(own[0].Id, BookingDocumentIssuerKind.CasaZen, null));
                Assert.Equal(DocumentIssuerErrorCodes.IssuanceNotAvailable, notAvailable.Code);
            }
        }
    }

    /// <summary>
    /// PROOF TEST: permission rule. Two organizations each chose how their documents are issued. The caller of org A reads its own setting only: the
    /// one of org B is not in the answer, and not found even when the service is asked for it by id; the booking of org B is a 404 of the preview, a
    /// booking the caller sees and does not reach a 403, and a caller who reaches only some of the properties cannot change the setting (403).
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
                NewProfile(OrgA, DocumentIssuerMode.Accountant, "Casa Rossi S.r.l."),
                NewProfile(OrgB, DocumentIssuerMode.CasaZenDirectOnly, "Casa Bianchi S.r.l."));
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
        Assert.Equal("Accountant", settings.Mode);
        Assert.Equal("Casa Rossi S.r.l.", settings.Issuer.LegalName);
        Assert.DoesNotContain("Bianchi", JsonSerializer.Serialize(settings, new JsonSerializerOptions(JsonSerializerDefaults.Web)), StringComparison.Ordinal);

        // By id, the service under the filter of org A finds nothing of org B: the empty setting of one that has not chosen.
        Assert.Null((await sut.GetSettingsAsync(OrgB)).Mode);

        var refused = await staff.PutSettings(new SaveFiscalDocumentSettingsRequest { Mode = "Octorate" }, CancellationToken.None);
        var problem = Assert.IsType<ObjectResult>(refused.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, problem.StatusCode);
        Assert.Equal(DocumentIssuerErrorCodes.NeedsWholeOrg, Assert.IsAssignableFrom<ProblemDetails>(problem.Value).Extensions["code"]);

        Assert.Equal(
            StatusCodes.Status404NotFound,
            Assert.IsType<ObjectResult>((await staff.IssuerPreview(bookingOfOrgB, CancellationToken.None)).Result).StatusCode);
        Assert.IsType<ForbidResult>((await staff.IssuerPreview(bookingOfColleague, CancellationToken.None)).Result);

        // A manager of the whole org changes the setting of its own org, and org B's stays as it was.
        var manager = Controller(sut, resolver, lookup, HostAuthorizationTestHarness.User("auth0|manager-a", "PropertyManager"));
        var saved = await manager.PutSettings(
            new SaveFiscalDocumentSettingsRequest { Mode = "Accountant", Regime = "VatOrdinary", Issuer = new SaveIssuerDataRequest { LegalName = "Casa Rossi Due S.r.l." } },
            CancellationToken.None);
        Assert.Equal("Casa Rossi Due S.r.l.", Assert.IsType<FiscalDocumentSettingsDto>(Assert.IsType<OkObjectResult>(saved.Result).Value).Issuer.LegalName);

        await using var system2 = NewDb(databaseName, tenant: null);
        var others = await system2.OrgFiscalIssuerProfiles.AsNoTracking().SingleAsync(p => p.OrgId == OrgB);
        Assert.Equal("Casa Bianchi S.r.l.", others.LegalName);
        Assert.Equal(DocumentIssuerMode.CasaZenDirectOnly, others.IssuerMode);
    }

    [Fact]
    public async Task DocumentIssuer_TheModeIsAcceptedOnlyWhenItCanWork_AndARefusedSaveChangesNothing()
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
            Mode = DocumentIssuerMode.CasaZenDirectOnly,
            Regime = DocumentRegime.VatOrdinary,
            LegalName = "Casa Rossi S.r.l.",
            AddressStreet = "Via Roma",
            AddressCivic = "1",
            PostalCode = "00100",
            City = "Roma",
            Province = "rm",
            FatturaPaRegimeCode = "rf01",
        };

        // The flag is off: CasaZen is not an option, whatever else is true.
        var off = Service(db, issuanceOn: false);
        var notAvailable = await Assert.ThrowsAsync<DomainRuleException>(() => off.SaveSettingsAsync(OrgA, complete));
        Assert.Equal(DocumentIssuerErrorCodes.IssuanceNotAvailable, notAvailable.Code);

        // The flag is on, the profile has no VAT number on the organization: the missing field is named.
        var on = Service(db, issuanceOn: true);
        var incomplete = await Assert.ThrowsAsync<DomainRuleException>(() => on.SaveSettingsAsync(OrgA, complete));
        Assert.Equal(DocumentIssuerErrorCodes.ProfileIncomplete, incomplete.Code);
        Assert.Equal(new[] { IssuerFields.VatNumber }, Assert.IsAssignableFrom<IReadOnlyList<string>>(incomplete.Extensions["fields"]).ToArray());

        // Octorate needs an active connection.
        var notConnected = await Assert.ThrowsAsync<DomainRuleException>(
            () => on.SaveSettingsAsync(OrgA, complete with { Mode = DocumentIssuerMode.Octorate }));
        Assert.Equal(DocumentIssuerErrorCodes.OctorateNotConnected, notConnected.Code);

        // A value that is not valid names its field.
        var invalid = await Assert.ThrowsAsync<DomainRuleException>(
            () => on.SaveSettingsAsync(OrgA, complete with { Mode = DocumentIssuerMode.Accountant, IssuerPec = "not an address" }));
        Assert.Equal(DocumentIssuerErrorCodes.FieldInvalid, invalid.Code);
        Assert.Equal(IssuerFields.IssuerPec, invalid.Extensions["field"]);

        // Nothing was saved by the four refusals; the accountant needs nothing, and saves.
        Assert.Empty(await db.OrgFiscalIssuerProfiles.IgnoreQueryFilters().AsNoTracking().ToListAsync());
        var saved = await on.SaveSettingsAsync(OrgA, complete with { Mode = DocumentIssuerMode.Accountant, Confirmed = true });
        Assert.Equal(DocumentIssuerMode.Accountant, saved.Mode);
        Assert.Equal(DocumentRegime.VatOrdinary, saved.Regime);
        Assert.Equal("RF01", saved.EffectiveRegimeCode);
        Assert.Equal("RM", saved.Issuer.Province);
        Assert.NotNull(saved.ConfirmedAt);

        // The rule is not configured until the accountant approves it: issuance refuses instead of guessing a rate.
        var refusal = await Assert.ThrowsAsync<DomainRuleException>(() => on.RequireRuleAsync(OrgA));
        Assert.Equal(DocumentIssuerErrorCodes.RuleNotConfigured, refusal.Code);
        var approved = new FiscalDocumentRulesOptions
        {
            Rules =
            {
                [DocumentRegime.VatOrdinary] = new FiscalDocumentRule
                {
                    DocumentKind = FiscalDocumentKind.Invoice,
                    VatRatePercent = 0m,
                    VatNatureCode = "N2.2",
                },
            },
        };
        Assert.Equal(FiscalDocumentKind.Invoice, (await Service(db, issuanceOn: true, approved).RequireRuleAsync(OrgA)).DocumentKind);
    }

    private static DocumentIssuerService Service(AppDbContext db, bool issuanceOn, FiscalDocumentRulesOptions? rules = null)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Testing");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [$"Features:{FeatureFlags.FiscalDocumentIssuance}"] = issuanceOn ? "true" : "false" })
            .Build();
        return new DocumentIssuerService(
            db,
            new ConfigurationFeatureFlags(configuration),
            Microsoft.Extensions.Options.Options.Create(rules ?? new FiscalDocumentRulesOptions()),
            environment.Object,
            TimeProvider.System,
            NullLogger<DocumentIssuerService>.Instance);
    }

    private static FiscalDocumentSettingsController Controller(
        DocumentIssuerService service,
        Mock<IOrgContextResolver> resolver,
        Mock<IHostResourceLookup> lookup,
        System.Security.Claims.ClaimsPrincipal user)
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
            HostAuthorizationTestHarness.Create(OrgA),
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
