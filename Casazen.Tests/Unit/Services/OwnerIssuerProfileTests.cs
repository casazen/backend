using System.Security.Claims;
using System.Security.Cryptography;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Models;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Encryption;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Fiscal;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E17/F01/US01/T01 (NF-116, D12): the issuer profile of an owner who issues the documents of its Italian stays: its own data and sezionale, its personal data
/// encrypted at rest, saved only by the whole organization with <c>agency.owner.financial.manage</c>, read within the reach of the caller; the profile of the
/// organization stays the one every reader of «the org's profile» gets; the billing setup counts the Italian owners still without a profile that can issue.
/// </summary>
public class OwnerIssuerProfileTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly HostScope OrgWide = new(OrgId);
    private static readonly IDataProtectionProvider Provider = new EphemeralDataProtectionProvider();
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The shortest value searched for inside a Data Protection payload. A payload is random text, so a short value turns up in it by chance (the building number "12" in about one payload in a hundred); from this
    /// many characters on the chance is below one in a trillion.
    /// </summary>
    private const int MinLengthSearchedInPayload = 8;

    /// <summary>
    /// PROOF TEST: privacy rule. The personal data of an owner who issues (the legal name, the fiscal code, the VAT number, the address and the PEC) are stored as
    /// Data Protection payloads of the purpose of the issuer profiles, never in clear, and read back in clear through the model only; the answer of a caller who
    /// may not change the profile masks them.
    /// </summary>
    [Fact]
    public async Task OwnerIssuerProfile_PersonalDataEncrypted_Test()
    {
        var world = World.Create();
        var owner = await world.OwnerAsync("IT");
        var saved = await world.Service.SaveAsync(OrgId, owner, PrivateOwner(), "auth0|admin");
        Assert.True(saved.CanIssue);

        var entity = world.Db.Model.FindEntityType(typeof(OrgFiscalIssuerProfile))!;
        var otherPurpose = Provider.CreateProtector(EncryptedColumns.OwnerFiscalProfilePurpose);
        var stored = await world.Db.OrgFiscalIssuerProfiles.AsNoTracking().SingleAsync(p => p.PropertyOwnerId == owner);
        foreach (var (property, value) in new[]
                 {
                     (nameof(OrgFiscalIssuerProfile.LegalName), stored.LegalName),
                     (nameof(OrgFiscalIssuerProfile.FiscalCode), stored.FiscalCode),
                     (nameof(OrgFiscalIssuerProfile.AddressStreet), stored.AddressStreet),
                     (nameof(OrgFiscalIssuerProfile.AddressCivic), stored.AddressCivic),
                     (nameof(OrgFiscalIssuerProfile.PostalCode), stored.PostalCode),
                     (nameof(OrgFiscalIssuerProfile.City), stored.City),
                     (nameof(OrgFiscalIssuerProfile.IssuerPec), stored.IssuerPec),
                 })
        {
            Assert.Contains(EncryptedColumns.All, c => c.EntityType == typeof(OrgFiscalIssuerProfile) && c.Property == property && c.Purpose == EncryptedColumns.OrgFiscalIssuerPurpose);
            var column = entity.FindProperty(property)!;
            var converter = Assert.IsType<EncryptedStringConverter>(column.GetValueConverter());
            Assert.Null(column.GetMaxLength());

            var payload = (string)converter.ConvertToProvider(value!)!;
            Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, payload, StringComparison.Ordinal);
            Assert.NotEqual(value, payload);

            // The payload is random text, so a short value (the building number, the postal code, the town) turns up in it by chance: only a value too long to be met by chance is searched for in it.
            // A short one is proved by what holds every time: what is stored is a payload and not the value, and the converter of the column gives the value back.
            if (value!.Length >= MinLengthSearchedInPayload)
                Assert.DoesNotContain(value, payload, StringComparison.Ordinal);

            Assert.Equal(value, converter.ConvertFromProvider(payload));
            Assert.ThrowsAny<CryptographicException>(() => otherPurpose.Unprotect(payload));
        }

        Assert.Contains(EncryptedColumns.All, c => c.EntityType == typeof(OrgFiscalIssuerProfile) && c.Property == nameof(OrgFiscalIssuerProfile.VatNumber));
        Assert.Equal(("TEST Mario Rossi", "RSSMRA80A01H501U", "Via del Sole"), (stored.LegalName, stored.FiscalCode, stored.AddressStreet));

        // A reader who may not change the profile gets the identifiers masked: never the whole fiscal code or street.
        var masked = OwnerIssuerProfileDto.From(saved, full: false);
        Assert.True(masked.Masked);
        Assert.Equal("••••••••••••501U", masked.FiscalCode);
        Assert.DoesNotContain("Via del Sole", masked.AddressStreet ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("TEST Mario Rossi", OwnerIssuerProfileDto.From(saved, full: true).LegalName);
    }

    /// <summary>
    /// PROOF TEST: permission rule. A collaborator «Solo alcuni», even with <c>agency.owner.financial.manage</c>, cannot save the profile of an owner (403
    /// <c>fiscal_settings_need_whole_org</c>, the service is never called); it reads only the owners of its reach (an owner with a property outside it is the 404
    /// of an owner that does not exist) and gets their data masked.
    /// </summary>
    [Fact]
    public async Task OwnerIssuerProfile_PartialScopeCannotEdit_Test()
    {
        foreach (var restricted in new[] { new HostScope(OrgId, GrantedToUserId: "auth0|solo-alcuni"), new HostScope(OrgId, OwnerId: "auth0|solo-alcuni") })
        {
            var profiles = new Mock<IOwnerIssuerProfileService>(MockBehavior.Strict);
            var controller = Controller(profiles.Object, restricted, financialManage: true);

            var save = await controller.Save(Guid.NewGuid(), new SaveOwnerIssuerProfileRequest { Regime = "PrivatePerson", LegalName = "TEST Nessuno" });

            var refused = Assert.IsType<ObjectResult>(save.Result);
            Assert.Equal(StatusCodes.Status403Forbidden, refused.StatusCode);
            Assert.Equal(DocumentIssuerErrorCodes.NeedsWholeOrg, Assert.IsAssignableFrom<ProblemDetails>(refused.Value).Extensions["code"]);
            profiles.VerifyNoOtherCalls();
        }

        // The reads of a partial scope stay in its reach.
        var world = World.Create();
        var inside = await world.OwnerAsync("IT", propertyOwner: "auth0|solo-alcuni");
        var shared = await world.OwnerAsync("IT", propertyOwner: "auth0|solo-alcuni");
        await world.PeriodAsync(shared, "IT", propertyOwner: "auth0|agenzia");
        await world.Service.SaveAsync(OrgId, inside, PrivateOwner(), "auth0|admin");
        var partial = new HostScope(OrgId, OwnerId: "auth0|solo-alcuni");

        var read = await Controller(world.Service, partial, financialManage: true).Get(inside);
        var dto = Assert.IsType<OwnerIssuerProfileDto>(Assert.IsType<OkObjectResult>(read.Result).Value);
        Assert.True(dto.Masked);
        await Assert.ThrowsAsync<NotFoundException>(() => world.Service.GetAsync(partial, shared));
        await Assert.ThrowsAsync<NotFoundException>(() => world.Service.GetAsync(new HostScope(Guid.NewGuid()), inside));

        // The whole organization with the permission saves and reads in full.
        var wholeOrg = Controller(world.Service, OrgWide, financialManage: true);
        var savedByOrg = await wholeOrg.Save(shared, new SaveOwnerIssuerProfileRequest
        {
            Regime = "VatOrdinary",
            LegalName = "TEST Immobiliare Lago Srl",
            VatNumber = "IT01234567897",
            FiscalCode = "01234567897",
            AddressStreet = "Via del Lago",
            AddressCivic = "3",
            PostalCode = "22100",
            City = "Como",
            Province = "co",
            GuestDocumentSeries = "SRL{year}",
        });
        var company = Assert.IsType<OwnerIssuerProfileDto>(Assert.IsType<OkObjectResult>(savedByOrg.Result).Value);
        Assert.False(company.Masked);
        Assert.True(company.CanIssue);
        Assert.Equal(("01234567897", "CO", "SRL{year}", "RF01"), (company.VatNumber, company.Province, company.GuestDocumentSeries, company.EffectiveRegimeCode));
    }

    /// <summary>
    /// Data: an owner profile keeps what belongs to its country and regime (the VAT number of a private person and the Italian codes of a Swiss owner are dropped),
    /// refuses an invalid fiscal code, a regime of the other country and a save while the issuance is off, and says what it still misses.
    /// </summary>
    [Fact]
    public async Task OwnerIssuerProfile_ValidatesItsData_AndSaysWhatIsMissing()
    {
        var world = World.Create();
        var owner = await world.OwnerAsync("IT");

        var draft = await world.Service.SaveAsync(OrgId, owner, new SaveOwnerIssuerProfileCommand { LegalName = "TEST Bozza" }, null);
        Assert.False(draft.CanIssue);
        Assert.Contains(IssuerFields.Regime, draft.MissingFields);
        Assert.Contains(IssuerFields.AddressStreet, draft.MissingFields);

        var noFiscalCode = await world.Service.SaveAsync(OrgId, owner, PrivateOwner() with { FiscalCode = null, VatNumber = "01234567897" }, null);
        Assert.Equal([IssuerFields.FiscalCode], noFiscalCode.MissingFields);
        Assert.Null(noFiscalCode.VatNumber);

        var invalid = await Assert.ThrowsAsync<DomainRuleException>(() => world.Service.SaveAsync(OrgId, owner, PrivateOwner() with { FiscalCode = "RSSMRA80A01H501X" }, null));
        Assert.Equal((DocumentIssuerErrorCodes.FieldInvalid, IssuerFields.FiscalCode), (invalid.Code, invalid.Extensions["field"]));

        var mismatch = await Assert.ThrowsAsync<DomainRuleException>(
            () => world.Service.SaveAsync(OrgId, owner, PrivateOwner() with { CountryCode = "CH", PostalCode = "6900", Province = null }, null));
        Assert.Equal(DocumentIssuerErrorCodes.RegimeCountryMismatch, mismatch.Code);

        var swiss = await world.Service.SaveAsync(
            OrgId,
            owner,
            new SaveOwnerIssuerProfileCommand
            {
                Regime = DocumentRegime.SwissNotRegistered,
                LegalName = "TEST Hans Muster",
                FiscalCode = "RSSMRA80A01H501U",
                AddressStreet = "Via Nassa",
                PostalCode = "6900",
                City = "Lugano",
                CountryCode = "CH",
                StayIssuedByOwner = true,
            },
            null);
        Assert.True(swiss.CanIssue);
        Assert.Null(swiss.FiscalCode);
        Assert.True(swiss.StayIssuedByOwner);

        world.Flags.Set(FeatureFlags.FiscalDocumentIssuance, false);
        var off = await Assert.ThrowsAsync<DomainRuleException>(() => world.Service.SaveAsync(OrgId, owner, PrivateOwner(), null));
        Assert.Equal(DocumentIssuerErrorCodes.IssuanceNotAvailable, off.Code);

        // One profile for the owner, whatever was saved, and an owner of another org (or anonymized) is not found.
        Assert.Equal(1, await world.Db.OrgFiscalIssuerProfiles.CountAsync(p => p.PropertyOwnerId == owner));
        world.Flags.Set(FeatureFlags.FiscalDocumentIssuance, true);
        await Assert.ThrowsAsync<NotFoundException>(() => world.Service.SaveAsync(Guid.NewGuid(), owner, PrivateOwner(), null));
    }

    /// <summary>
    /// Data: an owner's profile in the org never stands for the profile of the organization: the setting of the issuer, the readers of the documents and the demo all
    /// read the organization's own (<see cref="OrgFiscalIssuerProfileQueries.OfOrganization"/>), even when the owner's was saved first.
    /// </summary>
    [Fact]
    public async Task OwnerProfile_IsNeverTakenForTheProfileOfTheOrganization()
    {
        var world = World.Create();
        var owner = await world.OwnerAsync("IT");
        await world.Service.SaveAsync(OrgId, owner, PrivateOwner(), null);

        world.Db.Orgs.Add(new OrgEntity
        {
            Id = OrgId,
            Name = "TEST Agenzia",
            Slug = $"owner-issuers-{Guid.NewGuid():N}"[..28],
            DisplayName = "TEST Agenzia",
            ContactEmail = "owner-issuers@example.com",
            OrgType = OrgType.Host,
            PlanTier = PlanTier.Agency,
            HasPartitaIva = true,
            PartitaIvaNumber = "01234567897",
        });
        await world.Db.SaveChangesAsync();
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Testing");
        var issuers = new DocumentIssuerService(world.Db, world.Flags, TaxRuleCatalogs.Resolver(), environment.Object, new FakeTimeProvider(Now), NullLogger<DocumentIssuerService>.Instance);
        Assert.Null((await issuers.GetSettingsAsync(OrgId)).Mode);

        await issuers.SaveSettingsAsync(OrgId, new SaveIssuerSettingsCommand { Regime = DocumentRegime.VatOrdinary, LegalName = "TEST Agenzia Srl" });
        Assert.Equal("TEST Agenzia Srl", (await issuers.GetSettingsAsync(OrgId)).Issuer.LegalName);
        Assert.Equal(2, await world.Db.OrgFiscalIssuerProfiles.CountAsync());
        Assert.Equal("TEST Agenzia Srl", (await world.Db.OrgFiscalIssuerProfiles.OfOrganization(OrgId).SingleAsync()).LegalName);

        // The accountant package and the agency summary (E15) fall back on the issuer of the organization, never on the owner's saved first.
        var fallback = await AccountantPackageFacts.CurrentIssuerAsync(world.Db, OrgId, CancellationToken.None);
        Assert.Equal(("TEST Agenzia Srl", "01234567897"), (fallback.DisplayName, fallback.VatNumber));
    }

    /// <summary>
    /// The billing setup (section «Italia»): the owners with an ownership of an Italian property that has not ended and no profile that can issue are counted
    /// (<c>owner_issuer_missing</c>); a complete profile, a Swiss property, an ended ownership and an anonymized owner are not; nothing is missing without them.
    /// </summary>
    [Fact]
    public async Task OwnerIssuerSetup_CountsTheItalianOwnersWithoutAProfileThatCanIssue()
    {
        var world = World.Create();
        var contributor = new OwnerIssuerSetupContributor(world.Db, TaxRuleCatalogs.Resolver(), new FakeTimeProvider(Now));
        Assert.Empty(await contributor.EvaluateAsync(OrgId, CancellationToken.None));

        var ready = await world.OwnerAsync("IT");
        await world.Service.SaveAsync(OrgId, ready, PrivateOwner(), null);
        var incomplete = await world.OwnerAsync("IT");
        await world.Service.SaveAsync(OrgId, incomplete, new SaveOwnerIssuerProfileCommand { LegalName = "TEST A metà" }, null);
        await world.OwnerAsync("IT");
        await world.OwnerAsync("CH");
        await world.OwnerAsync("IT", end: new DateOnly(2026, 1, 31));
        var anonymized = await world.OwnerAsync("IT");
        (await world.Db.PropertyOwners.SingleAsync(o => o.Id == anonymized)).AnonymizedAt = Now.UtcDateTime;
        await world.Db.SaveChangesAsync();

        // A complete Swiss profile (made for ⚖ C1) does not issue the stays of an Italian property: that owner is missing too.
        var swissProfile = await world.OwnerAsync("IT");
        var swiss = await world.Service.SaveAsync(
            OrgId,
            swissProfile,
            new SaveOwnerIssuerProfileCommand
            {
                Regime = DocumentRegime.SwissNotRegistered,
                LegalName = "TEST Hans Muster",
                AddressStreet = "Via Nassa",
                PostalCode = "6900",
                City = "Lugano",
                CountryCode = "CH",
            },
            null);
        Assert.True(swiss.CanIssue);

        var item = Assert.Single(await contributor.EvaluateAsync(OrgId, CancellationToken.None));
        Assert.Equal((BillingSetupItemKeys.OwnerIssuerMissing, BillingSetupSections.Italy, BillingSetupReasons.Required, 3), (item.Key, item.Section, item.Reason, item.Count));
        Assert.Empty(await contributor.EvaluateAsync(Guid.NewGuid(), CancellationToken.None));
    }

    private static SaveOwnerIssuerProfileCommand PrivateOwner() => new()
    {
        Regime = DocumentRegime.PrivatePerson,
        LegalName = "TEST Mario Rossi",
        FiscalCode = "rssmra80a01h501u",
        AddressStreet = "Via del Sole",
        AddressCivic = "12",
        PostalCode = "20121",
        City = "Milano",
        Province = "MI",
        IssuerPec = "mario.rossi@pec.example.com",
        StampDutyReference = "TEST-BOLLO-2026-1",
    };

    private static OwnerIssuerProfilesController Controller(IOwnerIssuerProfileService profiles, HostScope scope, bool financialManage)
    {
        var org = new Mock<IOrgContextResolver>();
        org.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OrgId);
        var reach = new Mock<IHostScopeResolver>();
        reach.Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>(), OrgId, It.IsAny<CancellationToken>())).ReturnsAsync(scope);
        var authorization = new Mock<IAuthorizationService>();
        authorization
            .Setup(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<IEnumerable<IAuthorizationRequirement>>()))
            .ReturnsAsync(financialManage ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        authorization
            .Setup(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<string>()))
            .ReturnsAsync(financialManage ? AuthorizationResult.Success() : AuthorizationResult.Failed());

        var localizer = new Mock<IStringLocalizer<SharedResources>>();
        localizer.Setup(l => l[It.IsAny<string>()]).Returns((string name) => new LocalizedString(name, name, resourceNotFound: true));
        localizer.Setup(l => l[It.IsAny<string>(), It.IsAny<object[]>()]).Returns((string name, object[] _) => new LocalizedString(name, name, resourceNotFound: true));
        var services = new ServiceCollection().AddSingleton(localizer.Object).BuildServiceProvider();
        var identity = new ClaimsIdentity([new Claim("sub", "auth0|solo-alcuni"), new Claim(ClaimTypes.NameIdentifier, "auth0|solo-alcuni")], "test");
        return new OwnerIssuerProfilesController(profiles, org.Object, reach.Object, authorization.Object, NullLogger<OwnerIssuerProfilesController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity), RequestServices = services } },
        };
    }

    /// <summary>An agency over one in-memory database, with encryption, the shipped tax rules, the issuance on and a clock on 20 November 2026.</summary>
    private sealed class World
    {
        private World(AppDbContext db)
        {
            Db = db;
            Flags = new TestFeatureFlags(FeatureFlags.FiscalDocumentIssuance);
            Service = new OwnerIssuerProfileService(db, Flags, TaxRuleCatalogs.Resolver(), new FakeTimeProvider(Now), NullLogger<OwnerIssuerProfileService>.Instance);
        }

        public AppDbContext Db { get; }

        public TestFeatureFlags Flags { get; }

        public OwnerIssuerProfileService Service { get; }

        public static World Create() => new(new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"OwnerIssuers_{Guid.NewGuid()}").Options,
            new IssuerTenant(),
            Provider));

        /// <summary>An owner of the org with the whole of a property in <paramref name="country"/> from 2025 (until <paramref name="end"/> when given).</summary>
        public async Task<Guid> OwnerAsync(string country, DateOnly? end = null, string propertyOwner = "auth0|agenzia")
        {
            var owner = new PropertyOwner { OrgId = OrgId, FirstName = "TEST", LastName = "Proprietario", Email = $"{Guid.NewGuid():N}@example.com" };
            Db.PropertyOwners.Add(owner);
            await Db.SaveChangesAsync();
            await PeriodAsync(owner.Id, country, end, propertyOwner);
            return owner.Id;
        }

        /// <summary>A property of <paramref name="propertyOwner"/> in <paramref name="country"/>, held in full by the owner.</summary>
        public async Task PeriodAsync(Guid owner, string country, DateOnly? end = null, string propertyOwner = "auth0|agenzia")
        {
            var property = new Property { OrgId = OrgId, OwnerId = propertyOwner, Name = "TEST Casa", Address = "Via 1", City = "Como", Country = country };
            Db.Properties.Add(property);
            Db.PropertyOwnerships.Add(new PropertyOwnership
            {
                OrgId = OrgId,
                PropertyId = property.Id,
                PropertyOwnerId = owner,
                SharePercent = 100m,
                OwnershipStart = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                OwnershipEnd = end?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            });
            await Db.SaveChangesAsync();
        }
    }
}

file sealed class IssuerTenant : ITenantContext
{
    public Guid? OrgId => null;

    public bool FilterEnabled => false;
}
