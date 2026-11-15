using System.Globalization;
using System.IO.Compression;
using System.Xml.Schema;
using Casazen.Core.Authorization;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit;
using Casazen.Tests.Unit.Email;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using DocumentKind = Casazen.Core.Entities.Enums.FiscalDocumentKind;

namespace Casazen.Tests.Integration.Acceptance;

/// <summary>
/// The Swiss agency of the acceptance scenario (E19/F01/US01/T01, fatturazione/analisi-tecnica.md §5bis, decisions of the PO of 2026-11-14): an agency of Lugano on the
/// plan Agency whose issuer is CasaZen (registered for the VAT, with its UID, its IBAN and its QR-IBAN, Airbnb collecting the tourist tax of Ticino), one flat in
/// Lugano in francs owned by a private owner with a mandate (commission 20% «+ IVA», cleaning flat fee CHF 80), the rule of the agency for Booking.com (15%) and the
/// listing mapped on the channel manager. Every invoicing setting is saved the way the wizard «Fatturazione» saves it (<see cref="IDocumentIssuerService.SaveSettingsAsync"/>,
/// the mandate and the channel rules through their services).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>The real code, the external services mocked.</b> Each step runs the services the application runs, over one database: the import of the channel manager
/// (<see cref="BookingImportService"/>, the Octorate client mocked), the settlement of a Stripe payment and the capture of its fee (Stripe mocked), the payment the
/// agency records by hand, the daily run of the documents, the outbox events delivered to the consumers of the documents (the e-mail queue records what it is given),
/// the posting of the month, the statement version, the invoice of the property manager, the publication, the owner portal, the payout, the wire file and the
/// package of the accountant. The Italian XML builder is a strict stand-in: a Swiss document never asks for one. The jobs of the police and of the service requests,
/// the push notifications and the scheduler of the refund retries are stand-ins too (nothing of the chain goes through them).</item>
/// <item><b>One scope per step.</b> Every step opens its own <see cref="AppDbContext"/> and the services over it (<see cref="Scope"/>), as a request or a job does, on the
/// same database (in memory, or PostgreSQL for the twin of the main path): nothing a step tracks leaks into the next one.</item>
/// <item><b>The configuration shipped.</b> The tax rules, the tourist tax of Ticino, the portals that collect and the default channel commissions are the ones of
/// <c>Casazen.Web/appsettings.json</c>; the flags the chain needs are on (<c>FiscalDocumentIssuance</c>, <c>OwnerInvoicing</c>, <c>AccountantPackage</c>,
/// <c>AgencyTier</c>).</item>
/// <item><b>Bookings the code writes without a service of this slice.</b> The booking the agency enters by hand and the pending booking the checkout of the CasaZen
/// site writes are rows as those services leave them (with the event of the creation recorded the same way); a change of a booking by the host is the state it leaves
/// with the event <c>BookingModified</c> recorded by <see cref="BookingEvents"/>. Everything after them is the real chain.</item>
/// </list>
/// Everything is invented and obviously test data: the IBANs are valid examples with their check digits, the UID is the example of eCH-0097.
/// </remarks>
internal sealed class AcceptanceScenarioBuilder : IAsyncDisposable
{
    public const string AgencyLegalName = "TEST Quokka Gestioni SA";

    public const string AgencyUid = "CHE-109.322.551";

    /// <summary>The account of the agency that pays the owners (a normal Swiss IBAN).</summary>
    public const string AgencyIban = "CH4308307000289537312";

    /// <summary>The QR-IBAN of the agency (institution 31999): the account of its QR-bills.</summary>
    public const string AgencyQrIban = "CH4431999123000889012";

    /// <summary>The account of the owner, the creditor of the wire.</summary>
    public const string OwnerIban = "CH9300762011623852957";

    public const string OwnerName = "TEST Proprietario";

    public const string OwnerEmail = "test.proprietario.lugano@example.com";

    /// <summary>
    /// The domain of the addresses of the invented guests of the direct bookings. Not <c>example.com</c>: the documents of a stay are never e-mailed to a domain the standards reserve (FIX/RES-DOCS), and
    /// the scenarios check that the guest gets them.
    /// </summary>
    public const string GuestMailDomain = "ospiti-test.ch";

    /// <summary>The BFS number of Lugano, canton Ticino.</summary>
    public const string LuganoMunicipality = "5113";

    public const string PropertyName = "TEST Casa Lago Lugano";

    public const string PublicSiteBaseUrl = "https://app.casazen.test";

    public const string StripeAccount = "acct_test_quokka";

    public const string AgencyUser = "auth0|test-quokka-agency";

    public const string OctorateAccommodation = "585492";

    public const string OctorateRoom = "956528";

    /// <summary>The channel id Octorate gives Booking.com.</summary>
    public const string BookingComChannel = "142";

    /// <summary>The channel id Octorate gives Airbnb.</summary>
    public const string AirbnbChannel = "274";

    /// <summary>The first day of the mandate, of the ownership and of the rules of the agency.</summary>
    public static readonly DateOnly FirstOfTheYear = new(2026, 1, 1);

    // ---------------------------------------------------------------- the Italian agency (E19/F01/US02/T01)

    public const string ItalianAgencyLegalName = "TEST Agenzia Lago di Como Srl";

    /// <summary>The VAT number of the agency, also its fiscal code (a company): a valid Italian number of the tests.</summary>
    public const string ItalianAgencyVat = "01234567897";

    /// <summary>The account of the agency that pays the owners (an invented IBAN with its check digits).</summary>
    public const string ItalianAgencyIban = "IT82Z0100503200000000004321";

    /// <summary>The ISTAT code of Como: the comune of both flats, whose tourist tax the agency collects and remits.</summary>
    public const string ComuneIstatCode = "013075";

    public const string ComuneName = "Como";

    /// <summary>Flat A: a private owner (receipt, withholding), the listing of the flat on the channel manager.</summary>
    public const string FlatAName = "TEST Appartamento A Como";

    public const string OwnerAName = "TEST Mario Rossi";

    public const string OwnerAFiscalCode = "RSSMRA80A01H501U";

    public const string OwnerAIban = "IT60X0542811101000000123456";

    public const string OwnerAEmail = "test.proprietario.a@example.com";

    /// <summary>Flat B: a company (a Srl with a VAT number, FatturaPA with the agency as third-party issuer).</summary>
    public const string FlatBName = "TEST Appartamento B Como";

    public const string OwnerBName = "TEST Verdi Immobiliare Srl";

    /// <summary>The VAT number of the company of flat B, also its fiscal code.</summary>
    public const string OwnerBVat = "09876543217";

    public const string OwnerBIban = "IT87Y0306909606100000063456";

    public const string OwnerBEmail = "test.proprietario.b@example.com";

    /// <summary>The co-owner of flat A in the variant of the shares 60/40, a private person too.</summary>
    public const string CoOwnerName = "TEST Lara Bianchi";

    public const string CoOwnerFiscalCode = "BNCLRA85M41F205C";

    public const string CoOwnerIban = "IT58W0200809432000010345678";

    public const string CoOwnerEmail = "test.comproprietario.a@example.com";

    public const string OctorateAccommodationB = "585493";

    public const string OctorateRoomB = "956529";

    private static readonly Lazy<IConfiguration> Shipped = new(LoadShippedConfiguration, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly IDataProtectionProvider SharedKeys = new EphemeralDataProtectionProvider();

    private readonly DbContextOptions<AppDbContext> _options;
    private readonly ServiceProvider _noServices = new ServiceCollection().BuildServiceProvider();
    private int _sequence;

    private AcceptanceScenarioBuilder(DbContextOptions<AppDbContext> options)
    {
        _options = options;
        Stripe
            .Setup(s => s.CreateRefundAsync(It.IsAny<StripeRefundCreateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((StripeRefundCreateRequest request, CancellationToken _) => new global::Stripe.Refund
            {
                Id = $"re_test_{Guid.NewGuid():N}",
                PaymentIntentId = request.PaymentIntentId,
                Amount = request.AmountCents,
                Status = "succeeded",
            });
    }

    /// <summary>The clock of every service: the scenario moves it from step to step.</summary>
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 9, 15, 8, 0, 0, TimeSpan.Zero));

    public TestFeatureFlags Flags { get; } = new(FeatureFlags.FiscalDocumentIssuance, FeatureFlags.OwnerInvoicing, FeatureFlags.AccountantPackage, FeatureFlags.AgencyTier);

    /// <summary>
    /// The key ring of the encrypted columns and of the links: one for every scenario of the run, so every scope reads what another wrote (and the model of the context,
    /// which is cached by its provider, is built once for the run rather than once for each scenario).
    /// </summary>
    public IDataProtectionProvider Keys => SharedKeys;

    /// <summary>The channel manager.</summary>
    public Mock<IOctorateApiClient> Octorate { get; } = new();

    /// <summary>Stripe: a refund succeeds at once; the fee of a charge is what <see cref="PayOnTheCasaZenSiteAsync"/> says.</summary>
    public Mock<IStripeService> Stripe { get; } = new();

    /// <summary>The e-mail queue: what it was asked to queue.</summary>
    public RecordingEmailQueue Emails { get; } = new();

    /// <summary>The private bucket of the files (the PDF of a published statement).</summary>
    public InMemoryFileStorage Storage { get; } = new();

    /// <summary>The memory of the process (the documents whose e-mail the automatic send queued).</summary>
    public MemoryCache Memory { get; } = new(new MemoryCacheOptions());

    public Guid OrgId { get; } = Guid.NewGuid();

    public Guid PropertyId { get; } = Guid.NewGuid();

    public Guid OwnerId { get; } = Guid.NewGuid();

    /// <summary>The account of the owner on the owner portal.</summary>
    public Guid OwnerUserId { get; } = Guid.NewGuid();

    /// <summary>The Italian agency (<see cref="CreateItalianAsync"/>): two flats, A of a private owner and B of a company.</summary>
    public bool Italy { get; private set; }

    /// <summary>Flat B of the Italian agency: its property, its owner (a company) and the account of the owner on the portal.</summary>
    public Guid PropertyBId { get; } = Guid.NewGuid();

    public Guid OwnerBId { get; } = Guid.NewGuid();

    public Guid OwnerBUserId { get; } = Guid.NewGuid();

    /// <summary>The co-owner of flat A in the variant of the shares 60/40 (<see cref="ItalianSetup.CoOwnersOfFlatA"/>): its registry owner and its account on the portal.</summary>
    public Guid CoOwnerId { get; } = Guid.NewGuid();

    public Guid CoOwnerUserId { get; } = Guid.NewGuid();

    /// <summary>The cleaning company of the agency, the supplier of the requests of the cleanings (a row of the registry: the request names it).</summary>
    public Guid SupplierOrgId { get; } = Guid.NewGuid();

    /// <summary>The flat the steps work on when none is named: the flat of Lugano, or flat A of the Italian agency.</summary>
    public Flat FlatA => new(Italy ? FlatAName : PropertyName, PropertyId, OwnerId, OwnerUserId, OctorateAccommodation, OctorateRoom);

    /// <summary>Flat B of the Italian agency.</summary>
    public Flat FlatB => new(FlatBName, PropertyBId, OwnerBId, OwnerBUserId, OctorateAccommodationB, OctorateRoomB);

    /// <summary>The rule of the agency for the commission of Booking.com (15%), as the wizard saved it.</summary>
    public Guid BookingComRuleId { get; private set; }

    public HostScope WholeOrg => new(OrgId);

    /// <summary>The rules of the documents shipped (<c>FiscalDocuments</c>): the portals that collect, the look-back of the daily run.</summary>
    public static FiscalDocumentRulesOptions DocumentRules => Bind<FiscalDocumentRulesOptions>(FiscalDocumentRulesOptions.SectionName);

    /// <summary>
    /// The agency of the scenario on a database of its own (in memory unless <paramref name="options"/> says otherwise), set up as the wizard «Fatturazione» leaves it.
    /// With <paramref name="agencyHoldsTheBookingAccount"/> the Booking.com account of the flat is the agency's (V2); otherwise the owner's (V1, the default). With
    /// <paramref name="rechargePortalCommissions"/> false the agency does not recharge the commissions of the accounts it holds (⚖ I11; on by default).
    /// </summary>
    public static async Task<AcceptanceScenarioBuilder> CreateAsync(
        bool agencyHoldsTheBookingAccount = false,
        DbContextOptions<AppDbContext>? options = null,
        bool rechargePortalCommissions = true)
    {
        var world = new AcceptanceScenarioBuilder(
            options ?? new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"Acceptance_{Guid.NewGuid():N}").Options);
        await world.SetUpAsync(agencyHoldsTheBookingAccount, rechargePortalCommissions);
        return world;
    }

    /// <summary>
    /// The Italian agency of the scenario (E19/F01/US02/T01) on a database of its own (in memory unless <paramref name="options"/> says otherwise), set up as the
    /// wizard «Fatturazione» and the registry of the owners leave it: an agency of Como with the ordinary VAT regime, two flats under a mandate with representation,
    /// A of a private owner (a receipt in its name, the withholding of 21%) and B of a company (a FatturaPA with the agency as third-party issuer, no withholding),
    /// the Booking.com account held by the owners (V1, the default) or, in the variant, by the agency (V2). The flags of the invoicing chain are on, with
    /// <c>SdiSubmission</c> (no provider is configured: the invoices stay «to send») and <c>TaxReportingExports</c>.
    /// </summary>
    public static async Task<AcceptanceScenarioBuilder> CreateItalianAsync(ItalianSetup? setup = null, DbContextOptions<AppDbContext>? options = null)
    {
        var world = new AcceptanceScenarioBuilder(
            options ?? new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"AcceptanceIt_{Guid.NewGuid():N}").Options)
        {
            Italy = true,
        };
        world.Flags.Set(FeatureFlags.SdiSubmission, true);
        world.Flags.Set(FeatureFlags.TaxReportingExports, true);
        await world.SetUpItalyAsync(setup ?? new ItalianSetup());
        return world;
    }

    /// <summary>A unit of work of its own (a request, a job, a delivery of an event) with the services of the chain over it.</summary>
    public AcceptanceScope Scope() => new(this, new AppDbContext(_options, NullTenantContext.Instance, Keys));

    /// <summary>The instant of <paramref name="hour"/>:<paramref name="minute"/> on <paramref name="day"/> in Rome (and Lugano), in UTC.</summary>
    public static DateTimeOffset InRome(DateOnly day, int hour, int minute = 0)
    {
        var local = day.ToDateTime(new TimeOnly(hour, minute), DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, RomeCalendar.TimeZone), TimeSpan.Zero);
    }

    public void At(DateTimeOffset instant) => Clock.SetUtcNow(instant);

    // ---------------------------------------------------------------- the setup

    private async Task SetUpAsync(bool agencyHoldsTheBookingAccount, bool rechargePortalCommissions)
    {
        await using (var scope = Scope())
        {
            var db = scope.Db;
            db.Orgs.Add(new OrgEntity
            {
                Id = OrgId,
                Name = "TEST Quokka Gestioni",
                Slug = $"quokka-{OrgId:N}"[..28],
                DisplayName = "TEST Quokka Gestioni",
                ContactEmail = "agenzia.quokka@example.com",
                OrgType = OrgType.Host,
                PlanTier = PlanTier.Agency,
                DefaultLanguage = "it",
            });

            // The register of the municipalities is seeded by the migrations on PostgreSQL; the in-memory database starts empty.
            if (!await db.SwissMunicipalities.AnyAsync(m => m.MunicipalityCode == LuganoMunicipality))
                db.SwissMunicipalities.Add(new SwissMunicipality { MunicipalityCode = LuganoMunicipality, CantonCode = "TI", OfficialName = "Lugano" });

            db.Properties.Add(new Property
            {
                Id = PropertyId,
                OrgId = OrgId,
                OwnerId = AgencyUser,
                Name = PropertyName,
                Address = "Riva Paradiso 1",
                City = "Lugano",
                Country = "CH",
                Currency = "CHF",
                SwissMunicipalityCode = LuganoMunicipality,
                MaxGuests = 4,
            });
            db.OwnerUsers.Add(new OwnerUser
            {
                Id = OwnerUserId,
                Auth0Sub = $"auth0|test-owner-{OwnerUserId:N}",
                Email = OwnerEmail,
                FirstName = "TEST",
                LastName = "Proprietario",
                CreatedAt = Clock.GetUtcNow().UtcDateTime,
            });
            db.PropertyOwners.Add(new PropertyOwner
            {
                Id = OwnerId,
                OrgId = OrgId,
                FirstName = "TEST",
                LastName = "Proprietario",
                Email = OwnerEmail,
                EntityKind = OwnerEntityKind.NaturalPerson,
                Iban = OwnerIban,
                BillingStreet = "Via Cantonale",
                BillingCivic = "12",
                BillingPostalCode = "6900",
                BillingCity = "Lugano",
                BillingCountry = "CH",
                SettlementCurrency = "CHF",
                CreatedAt = Clock.GetUtcNow().UtcDateTime,
            });
            db.PropertyOwnerships.Add(new PropertyOwnership
            {
                OrgId = OrgId,
                PropertyId = PropertyId,
                PropertyOwnerId = OwnerId,
                OwnerUserId = OwnerUserId,
                SharePercent = 100m,
                OwnershipStart = FirstOfTheYear.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            });

            // The listing of the flat on the channel manager: the room of the reservations it imports.
            var connection = new OctorateConnection { OrgId = OrgId, Environment = OctorateEnvironment.Test, Status = OctorateConnectionStatus.Connected };
            db.OctorateConnections.Add(connection);
            db.OctorateListingMappings.Add(new OctorateListingMapping
            {
                OrgId = OrgId,
                ConnectionId = connection.Id,
                OctorateAccommodationId = OctorateAccommodation,
                OctorateProductId = OctorateRoom,
                PropertyId = PropertyId,
                Status = OctorateMappingStatus.Confirmed,
                SnapshotName = PropertyName,
            });
            await db.SaveChangesAsync();
        }

        // The wizard «Fatturazione»: who issues, the VAT, the numbering, the tourist tax, the automatic issue and the e-mail to the guest.
        await using (var scope = Scope())
        {
            await scope.Issuers.SaveSettingsAsync(
                OrgId,
                new SaveIssuerSettingsCommand
                {
                    Mode = DocumentIssuerMode.CasaZen,
                    Regime = DocumentRegime.SwissVatRegistered,
                    LegalName = AgencyLegalName,
                    AddressStreet = "Via Nassa",
                    AddressCivic = "5",
                    PostalCode = "6900",
                    City = "Lugano",
                    CountryCode = "CH",
                    VatRegistered = true,
                    SwissVatNumber = AgencyUid,
                    Iban = AgencyIban,
                    QrIban = AgencyQrIban,
                    StayVatTreatment = StayVatTreatment.Standard,
                    CleaningFeeTreatment = CleaningFeeTreatment.FollowsStay,
                    GuestDocumentSeries = "{year}",
                    OwnerInvoiceSeries = "PM{year}",
                    AutoIssueGuestDocuments = true,
                    EmailGuestDocuments = true,
                    PlatformCollectedTouristTaxChannels = [nameof(BookingSource.Airbnb)],
                    Confirmed = true,
                },
                AgencyUser);
        }

        // The owner: an approved rule set of Switzerland (no withholding), its fiscal profile paid in francs, its mandate; the rules of the channels of the agency.
        await using (var scope = Scope())
        {
            var policy = await scope.OwnerFiscal.CreatePolicyAsync(
                OrgId,
                new OwnerAccountingPolicyCommand
                {
                    Jurisdiction = "CH",
                    RuleCode = "TEST-CH",
                    Version = "1",
                    ValidFrom = FirstOfTheYear,
                    SourceUrl = "https://example.com/test-rule-ch",
                    SourceSha256 = new string('d', 64),
                    Parameters = new OwnerAccountingRuleParameters { WithholdingApplies = false, Rounding = "half_even", RetentionYears = 10 },
                },
                AgencyUser);
            await scope.OwnerFiscal.ApprovePolicyAsync(OrgId, policy.Id, "TEST Fiduciario", AgencyUser);
            await scope.OwnerFiscal.SaveProfileAsync(
                OrgId,
                OwnerId,
                new OwnerFiscalProfileCommand
                {
                    ValidFrom = FirstOfTheYear,
                    EntityKind = OwnerEntityKind.NaturalPerson,
                    LegalName = OwnerName,
                    TaxResidenceCountry = "CH",
                    LegalAddress = "Via Cantonale 12, 6900 Lugano",
                    ForeignTaxId = "756123456789",
                    SettlementCurrency = "CHF",
                    BankAccountReference = OwnerIban,
                    RevenueRecognitionRule = "cash",
                    TaxJurisdiction = "CH",
                    AccountingPolicyId = policy.Id,
                },
                AgencyUser);
            await scope.Mandates.CreateVersionAsync(
                WholeOrg,
                OwnerId,
                new OwnerMandateCommand { ValidFrom = FirstOfTheYear, CommissionPercent = 20m, CommissionIncludesVat = false, CleaningFlatRate = 80m },
                AgencyUser);
            var channelRules = await scope.Mandates.SaveChannelRulesAsync(
                OrgId,
                [new ChannelCommissionRuleCommand { Channel = BookingSource.BookingCom, Percent = 15m, ValidFrom = FirstOfTheYear }],
                AgencyUser);
            BookingComRuleId = channelRules.Rules.Single().Id;
            if (agencyHoldsTheBookingAccount)
            {
                await scope.PortalAccounts.SaveAccountHoldersAsync(
                    OrgId,
                    [new ChannelAccountHolderCommand { PropertyId = PropertyId, Channel = BookingSource.BookingCom, Holder = PortalAccountHolder.Agency }],
                    AgencyUser);
            }

            if (!rechargePortalCommissions)
                await scope.PortalAccounts.SaveAsync(OrgId, new PortalAccountSettingsCommand { RechargePortalCommissions = false }, AgencyUser);
        }
    }

    // ---------------------------------------------------------------- the setup of the Italian agency

    private async Task SetUpItalyAsync(ItalianSetup setup)
    {
        var template = Bind<OwnerAccountingOptions>(OwnerAccountingOptions.SectionName).ItalianShortLetRule;

        // The registry: the org with the VAT number and the fiscal code the issuer reads, the two flats of Como, the owners with their structured address (the wire and the
        // recipient of the invoice of the property manager read it), their accounts on the portal, the ownerships and the listings of the flats on the channel manager.
        await using (var scope = Scope())
        {
            var db = scope.Db;
            db.Orgs.Add(new OrgEntity
            {
                Id = OrgId,
                Name = "TEST Agenzia Como",
                Slug = $"como-{OrgId:N}"[..28],
                DisplayName = "TEST Agenzia Como",
                ContactEmail = "agenzia.como@example.com",
                OrgType = OrgType.Host,
                PlanTier = PlanTier.Agency,
                DefaultLanguage = "it",
                HasPartitaIva = true,
                PartitaIvaNumber = ItalianAgencyVat,
                FiscalCode = ItalianAgencyVat,
            });
            db.Orgs.Add(new OrgEntity
            {
                Id = SupplierOrgId,
                Name = "TEST Impresa Pulizie Como",
                Slug = $"pulizie-{SupplierOrgId:N}"[..28],
                DisplayName = "TEST Impresa Pulizie Como",
                ContactEmail = "pulizie.como@example.com",
                OrgType = OrgType.Supplier,
                DefaultLanguage = "it",
            });
            db.Properties.Add(ItalianFlat(PropertyId, FlatAName, "Via Volta 3"));
            db.Properties.Add(ItalianFlat(PropertyBId, FlatBName, "Via Garibaldi 8"));

            db.OwnerUsers.Add(OwnerUserOf(OwnerUserId, "TEST", "Rossi", OwnerAEmail));
            db.OwnerUsers.Add(OwnerUserOf(OwnerBUserId, "TEST", "Verdi Immobiliare", OwnerBEmail));
            db.PropertyOwners.Add(new PropertyOwner
            {
                Id = OwnerId,
                OrgId = OrgId,
                FirstName = "TEST Mario",
                LastName = "Rossi",
                Email = OwnerAEmail,
                EntityKind = OwnerEntityKind.NaturalPerson,
                TaxRegime = OwnerTaxRegime.Private,
                FiscalCode = OwnerAFiscalCode,
                Iban = OwnerAIban,
                TaxResidenceCountry = "IT",
                SettlementCurrency = "EUR",
                BillingStreet = "Via Roma",
                BillingCivic = "1",
                BillingPostalCode = "22100",
                BillingCity = ComuneName,
                BillingProvince = "CO",
                BillingCountry = "IT",
                CreatedAt = Clock.GetUtcNow().UtcDateTime,
            });
            db.PropertyOwners.Add(new PropertyOwner
            {
                Id = OwnerBId,
                OrgId = OrgId,
                FirstName = "TEST",
                LastName = "Verdi Immobiliare",
                LegalName = OwnerBName,
                Email = OwnerBEmail,
                EntityKind = OwnerEntityKind.LegalEntity,
                TaxRegime = OwnerTaxRegime.VatHolder,
                FiscalCode = OwnerBVat,
                VatNumber = OwnerBVat,
                Iban = OwnerBIban,
                TaxResidenceCountry = "IT",
                SettlementCurrency = "EUR",
                BillingStreet = "Via Milano",
                BillingCivic = "5",
                BillingPostalCode = "22100",
                BillingCity = ComuneName,
                BillingProvince = "CO",
                BillingCountry = "IT",
                CreatedAt = Clock.GetUtcNow().UtcDateTime,
            });
            var shareOfA = setup.CoOwnersOfFlatA ? 60m : 100m;
            db.PropertyOwnerships.Add(new PropertyOwnership
            {
                OrgId = OrgId,
                PropertyId = PropertyId,
                PropertyOwnerId = OwnerId,
                OwnerUserId = OwnerUserId,
                SharePercent = shareOfA,
                OwnershipStart = FirstOfTheYear.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            });
            db.PropertyOwnerships.Add(new PropertyOwnership
            {
                OrgId = OrgId,
                PropertyId = PropertyBId,
                PropertyOwnerId = OwnerBId,
                OwnerUserId = OwnerBUserId,
                SharePercent = 100m,
                OwnershipStart = FirstOfTheYear.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            });
            if (setup.CoOwnersOfFlatA)
            {
                db.OwnerUsers.Add(OwnerUserOf(CoOwnerUserId, "TEST", "Bianchi", CoOwnerEmail));
                db.PropertyOwners.Add(new PropertyOwner
                {
                    Id = CoOwnerId,
                    OrgId = OrgId,
                    FirstName = "TEST Lara",
                    LastName = "Bianchi",
                    Email = CoOwnerEmail,
                    EntityKind = OwnerEntityKind.NaturalPerson,
                    TaxRegime = OwnerTaxRegime.Private,
                    FiscalCode = CoOwnerFiscalCode,
                    Iban = CoOwnerIban,
                    TaxResidenceCountry = "IT",
                    SettlementCurrency = "EUR",
                    BillingStreet = "Via Dante",
                    BillingCivic = "2",
                    BillingPostalCode = "22100",
                    BillingCity = ComuneName,
                    BillingProvince = "CO",
                    BillingCountry = "IT",
                    CreatedAt = Clock.GetUtcNow().UtcDateTime,
                });
                db.PropertyOwnerships.Add(new PropertyOwnership
                {
                    OrgId = OrgId,
                    PropertyId = PropertyId,
                    PropertyOwnerId = CoOwnerId,
                    OwnerUserId = CoOwnerUserId,
                    SharePercent = 40m,
                    OwnershipStart = FirstOfTheYear.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                });
            }

            var connection = new OctorateConnection { OrgId = OrgId, Environment = OctorateEnvironment.Test, Status = OctorateConnectionStatus.Connected };
            db.OctorateConnections.Add(connection);
            foreach (var flat in new[] { FlatA, FlatB })
            {
                db.OctorateListingMappings.Add(new OctorateListingMapping
                {
                    OrgId = OrgId,
                    ConnectionId = connection.Id,
                    OctorateAccommodationId = flat.Accommodation,
                    OctorateProductId = flat.Room,
                    PropertyId = flat.PropertyId,
                    Status = OctorateMappingStatus.Confirmed,
                    SnapshotName = flat.Name,
                });
            }

            // Who collects the tourist tax of Como for a stay of a portal (a rule of the platform, with its source): the host for Booking.com; for Airbnb the portal, in the variant.
            db.TouristTaxChannelRules.Add(new TouristTaxChannelRule
            {
                Channel = BookingSource.BookingCom,
                IstatCode = ComuneIstatCode,
                Collector = TouristTaxCollector.Host,
                SourceUrl = "https://example.com/test-regola-tassa-como-booking",
                VerifiedAt = Clock.GetUtcNow().UtcDateTime,
            });
            if (setup.AirbnbCollectsTheTouristTax)
            {
                db.TouristTaxChannelRules.Add(new TouristTaxChannelRule
                {
                    Channel = BookingSource.Airbnb,
                    IstatCode = ComuneIstatCode,
                    Collector = TouristTaxCollector.Channel,
                    SourceUrl = "https://example.com/test-regola-tassa-como-airbnb",
                    VerifiedAt = Clock.GetUtcNow().UtcDateTime,
                });
            }
            else
            {
                db.TouristTaxChannelRules.Add(new TouristTaxChannelRule
                {
                    Channel = BookingSource.Airbnb,
                    IstatCode = ComuneIstatCode,
                    Collector = TouristTaxCollector.Host,
                    SourceUrl = "https://example.com/test-regola-tassa-como-airbnb-host",
                    VerifiedAt = Clock.GetUtcNow().UtcDateTime,
                });
            }

            await db.SaveChangesAsync();
        }

        // The wizard «Fatturazione» of the agency: who issues, the ordinary regime of the VAT, the numbering, the cleaning the agency bills as its own service, the automatic
        // issue and the e-mail to the guest.
        await using (var scope = Scope())
        {
            await scope.Issuers.SaveSettingsAsync(
                OrgId,
                new SaveIssuerSettingsCommand
                {
                    Mode = DocumentIssuerMode.CasaZen,
                    Regime = DocumentRegime.VatOrdinary,
                    LegalName = ItalianAgencyLegalName,
                    AddressStreet = "Via Plinio",
                    AddressCivic = "10",
                    PostalCode = "22100",
                    City = ComuneName,
                    Province = "CO",
                    CountryCode = "IT",
                    VatRegistered = true,
                    Iban = ItalianAgencyIban,
                    StayVatTreatment = StayVatTreatment.Standard,
                    CleaningFeeTreatment = CleaningFeeTreatment.Service,
                    GuestDocumentSeries = "{year}",
                    OwnerInvoiceSeries = "PM{year}",
                    AutoIssueGuestDocuments = true,
                    EmailGuestDocuments = true,
                    PlatformCollectedTouristTaxChannels = [],
                    Confirmed = true,
                },
                AgencyUser);
        }

        // The issuer profiles of the owners (the sezionale of each): the private owner issues a receipt, the company a FatturaPA with the agency as third-party issuer.
        await using (var scope = Scope())
        {
            await scope.OwnerIssuers.SaveAsync(
                OrgId,
                OwnerId,
                new SaveOwnerIssuerProfileCommand
                {
                    Regime = DocumentRegime.PrivatePerson,
                    LegalName = OwnerAName,
                    FiscalCode = OwnerAFiscalCode,
                    AddressStreet = "Via Roma",
                    AddressCivic = "1",
                    PostalCode = "22100",
                    City = ComuneName,
                    Province = "CO",
                    CountryCode = "IT",
                    GuestDocumentSeries = "RIC{year}",
                    StampDutyReference = "AUT-BOLLO-TEST-2026",
                },
                AgencyUser);
            await scope.OwnerIssuers.SaveAsync(
                OrgId,
                OwnerBId,
                new SaveOwnerIssuerProfileCommand
                {
                    Regime = DocumentRegime.VatOrdinary,
                    LegalName = OwnerBName,
                    FiscalCode = OwnerBVat,
                    VatNumber = OwnerBVat,
                    AddressStreet = "Via Milano",
                    AddressCivic = "5",
                    PostalCode = "22100",
                    City = ComuneName,
                    Province = "CO",
                    CountryCode = "IT",
                    StayVatTreatment = setup.StayVatOfFlatB,
                    GuestDocumentSeries = "SRL{year}",
                    StampDutyReference = "AUT-BOLLO-TEST-SRL",
                },
                AgencyUser);
            if (setup.CoOwnersOfFlatA)
            {
                await scope.OwnerIssuers.SaveAsync(
                    OrgId,
                    CoOwnerId,
                    new SaveOwnerIssuerProfileCommand
                    {
                        Regime = DocumentRegime.PrivatePerson,
                        LegalName = CoOwnerName,
                        FiscalCode = CoOwnerFiscalCode,
                        AddressStreet = "Via Dante",
                        AddressCivic = "2",
                        PostalCode = "22100",
                        City = ComuneName,
                        Province = "CO",
                        CountryCode = "IT",
                        GuestDocumentSeries = "RIC{year}",
                        StampDutyReference = "AUT-BOLLO-TEST-2026-B",
                    },
                    AgencyUser);
            }
        }

        // The accounting of the owners: the rule set of Italy approved (the withholding of 21%, code 1919, the draft the configuration ships, never active by itself), the fiscal
        // profile of each owner paid in euro, the mandates, the rules of the channels of the agency and, in V2, the account of Booking.com held by the agency.
        await using (var scope = Scope())
        {
            var policy = await scope.OwnerFiscal.CreatePolicyAsync(
                OrgId,
                new OwnerAccountingPolicyCommand
                {
                    Jurisdiction = template.Jurisdiction,
                    RuleCode = template.RuleCode,
                    Version = template.Version,
                    ValidFrom = FirstOfTheYear,
                    SourceUrl = template.SourceUrl,
                    SourceSha256 = new string('d', 64),
                    Parameters = template.ToParameters(),
                },
                AgencyUser);
            await scope.OwnerFiscal.ApprovePolicyAsync(OrgId, policy.Id, "TEST Commercialista", AgencyUser);
            ItalianPolicyId = policy.Id;

            await SaveItalianProfileAsync(scope, OwnerId, OwnerEntityKind.NaturalPerson, OwnerAName, OwnerAFiscalCode, null, OwnerAIban, "Via Roma 1, 22100 Como CO", policy.Id);
            await SaveItalianProfileAsync(scope, OwnerBId, OwnerEntityKind.LegalEntity, OwnerBName, OwnerBVat, OwnerBVat, OwnerBIban, "Via Milano 5, 22100 Como CO", policy.Id);
            if (setup.CoOwnersOfFlatA)
                await SaveItalianProfileAsync(scope, CoOwnerId, OwnerEntityKind.NaturalPerson, CoOwnerName, CoOwnerFiscalCode, null, CoOwnerIban, "Via Dante 2, 22100 Como CO", policy.Id);

            // Flat A: the cleaning is recharged at the cost of its request (⚖ I13); the co-owners' CU is per share when the variant says so. Flat B: a flat fee for each stay.
            await scope.Mandates.CreateVersionAsync(
                WholeOrg,
                OwnerId,
                new OwnerMandateCommand
                {
                    ValidFrom = FirstOfTheYear,
                    CommissionPercent = 20m,
                    CommissionIncludesVat = false,
                    CleaningRechargeMode = CleaningRechargeMode.ActualCost,
                    CoOwnerTaxReporting = setup.CoOwnersOfFlatA ? CoOwnerTaxReporting.PerShare : null,
                },
                AgencyUser);
            await scope.Mandates.CreateVersionAsync(
                WholeOrg,
                OwnerBId,
                new OwnerMandateCommand { ValidFrom = FirstOfTheYear, CommissionPercent = 20m, CommissionIncludesVat = false, CleaningFlatRate = 70m },
                AgencyUser);
            if (setup.CoOwnersOfFlatA)
            {
                await scope.Mandates.CreateVersionAsync(
                    WholeOrg,
                    CoOwnerId,
                    new OwnerMandateCommand
                    {
                        ValidFrom = FirstOfTheYear,
                        CommissionPercent = 20m,
                        CommissionIncludesVat = false,
                        CleaningRechargeMode = CleaningRechargeMode.ActualCost,
                    },
                    AgencyUser);
            }

            var channelRules = await scope.Mandates.SaveChannelRulesAsync(
                OrgId,
                [new ChannelCommissionRuleCommand { Channel = BookingSource.BookingCom, Percent = 15m, ValidFrom = FirstOfTheYear }],
                AgencyUser);
            BookingComRuleId = channelRules.Rules.Single().Id;
            if (setup.AgencyHoldsTheBookingAccount)
            {
                await scope.PortalAccounts.SaveAccountHoldersAsync(
                    OrgId,
                    [
                        new ChannelAccountHolderCommand { PropertyId = PropertyId, Channel = BookingSource.BookingCom, Holder = PortalAccountHolder.Agency },
                        new ChannelAccountHolderCommand { PropertyId = PropertyBId, Channel = BookingSource.BookingCom, Holder = PortalAccountHolder.Agency },
                    ],
                    AgencyUser);
            }
        }
    }

    /// <summary>The fiscal profile of an Italian owner from the first day of the year, paid in euro on the rule set of Italy.</summary>
    private async Task SaveItalianProfileAsync(
        AcceptanceScope scope,
        Guid ownerId,
        OwnerEntityKind kind,
        string name,
        string fiscalCode,
        string? vatNumber,
        string iban,
        string address,
        Guid policyId)
    {
        await scope.OwnerFiscal.SaveProfileAsync(
            OrgId,
            ownerId,
            new OwnerFiscalProfileCommand
            {
                ValidFrom = FirstOfTheYear,
                EntityKind = kind,
                LegalName = name,
                TaxResidenceCountry = "IT",
                LegalAddress = address,
                FiscalCode = fiscalCode,
                VatNumber = vatNumber,
                SettlementCurrency = "EUR",
                BankAccountReference = iban,
                RevenueRecognitionRule = "cash",
                TaxJurisdiction = "IT",
                AccountingPolicyId = policyId,
            },
            AgencyUser);
    }

    /// <summary>The approved rule set of Italy of the agency (the withholding of 21%, the code 1919).</summary>
    public Guid ItalianPolicyId { get; private set; }

    private Property ItalianFlat(Guid id, string name, string address) => new()
    {
        Id = id,
        OrgId = OrgId,
        OwnerId = AgencyUser,
        Name = name,
        Address = address,
        City = ComuneName,
        PostalCode = "22100",
        Country = "IT",
        Currency = "EUR",
        ComuneIstatCode = ComuneIstatCode,
        MaxGuests = 4,
    };

    private OwnerUser OwnerUserOf(Guid id, string first, string last, string email) => new()
    {
        Id = id,
        Auth0Sub = $"auth0|test-owner-{id:N}",
        Email = email,
        FirstName = first,
        LastName = last,
        CreatedAt = Clock.GetUtcNow().UtcDateTime,
    };

    // ---------------------------------------------------------------- the four origins

    /// <summary>
    /// A reservation of Booking.com as the channel manager gives it: the price of the host (<c>roomGross</c>, the total of the reservation), the commission of the
    /// portal in the breakdown when <paramref name="commission"/> is given, collected by the portal (<c>COMPANY</c>) unless <paramref name="portalCollects"/> is false,
    /// two guests, no tourist tax in it (Booking.com does not collect the one of Ticino), the guest's relay address of the portal.
    /// </summary>
    public OctorateReservation BookingComReservation(
        DateOnly checkIn,
        DateOnly checkOut,
        decimal price,
        decimal? commission,
        bool portalCollects = true,
        decimal touristTax = 0m,
        Flat? flat = null)
    {
        var id = $"BDC{Interlocked.Increment(ref _sequence):D4}";
        return new OctorateReservation
        {
            Id = id,
            Status = OctorateBookingStatus.Confirmed,
            Product = (flat ?? FlatA).Room,
            Currency = Italy ? "EUR" : "CHF",
            ChannelId = BookingComChannel,
            ChannelName = "Booking.com",
            CheckIn = Day(checkIn),
            CheckOut = Day(checkOut),
            UpdatedAt = Clock.GetUtcNow().UtcDateTime,
            FirstName = "TEST",
            LastName = "Ospite Portale",
            GuestEmail = $"test.{id.ToLowerInvariant()}@guest.booking.com",
            TotalGuests = 2,
            RoomGross = price,
            TotalGross = price + touristTax,
            TouristTax = touristTax > 0m ? touristTax : null,
            CompanyCollect = portalCollects ? OctorateReservation.CompanyCollectCompany : OctorateReservation.CompanyCollectHotel,
            PriceBreakdown = commission is { } kept ? [new OctoratePriceComponent(OctorateReservation.CommissionType, kept)] : [],
        };
    }

    /// <summary>
    /// A reservation of Airbnb as the channel manager gives it: the price of the host, the commission of the portal in the breakdown, collected by the portal, two
    /// guests, and the tourist tax of Ticino the portal collected from the guest itself (Airbnb is the portal the issuer confirmed as collecting it in the wizard).
    /// </summary>
    public OctorateReservation AirbnbReservation(DateOnly checkIn, DateOnly checkOut, decimal price, decimal commission, decimal touristTax, Flat? flat = null)
    {
        var id = $"ABB{Interlocked.Increment(ref _sequence):D4}";
        return new OctorateReservation
        {
            Id = id,
            Status = OctorateBookingStatus.Confirmed,
            Product = (flat ?? FlatA).Room,
            Currency = Italy ? "EUR" : "CHF",
            ChannelId = AirbnbChannel,
            ChannelName = "Airbnb",
            CheckIn = Day(checkIn),
            CheckOut = Day(checkOut),
            UpdatedAt = Clock.GetUtcNow().UtcDateTime,
            FirstName = "TEST",
            LastName = "Ospite Lago",
            GuestEmail = $"test.{id.ToLowerInvariant()}@guest.airbnb.com",
            TotalGuests = 2,
            RoomGross = price,
            TotalGross = price + touristTax,
            TouristTax = touristTax,
            CompanyCollect = OctorateReservation.CompanyCollectCompany,
            PriceBreakdown = [new OctoratePriceComponent(OctorateReservation.CommissionType, commission)],
        };
    }

    /// <summary>
    /// A reservation of the agency's own site: its booking engine is a channel of the channel manager (the «direct» channel, <see cref="BookingStateMapper.BookingEngineChannelId"/>),
    /// so it comes in as a direct booking with an Octorate id; the price of the stay and the tourist tax the site charged, no commission, paid outside CasaZen.
    /// </summary>
    public OctorateReservation AgencySiteReservation(DateOnly checkIn, DateOnly checkOut, decimal price, decimal touristTax, Flat? flat = null)
    {
        var id = $"WEB{Interlocked.Increment(ref _sequence):D4}";
        return new OctorateReservation
        {
            Id = id,
            Status = OctorateBookingStatus.Confirmed,
            Product = (flat ?? FlatA).Room,
            Currency = Italy ? "EUR" : "CHF",
            ChannelId = BookingStateMapper.BookingEngineChannelId,
            ChannelName = "Booking Engine",
            CheckIn = Day(checkIn),
            CheckOut = Day(checkOut),
            UpdatedAt = Clock.GetUtcNow().UtcDateTime,
            FirstName = "TEST",
            LastName = "Ospite Sito",
            GuestEmail = $"test.{id.ToLowerInvariant()}@{GuestMailDomain}",
            TotalGuests = 2,
            RoomGross = price,
            TotalGross = price + touristTax,
            TouristTax = touristTax,
        };
    }

    /// <summary>The import of the channel manager reads <paramref name="reservation"/> (the client serves it): the booking it made or changed.</summary>
    public async Task<Guid> ImportFromChannelManagerAsync(OctorateReservation reservation, Flat? flat = null)
    {
        var listing = flat ?? FlatA;
        Octorate
            .Setup(c => c.GetReservationsAsync(listing.Accommodation, It.IsAny<OctorateReservationQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, OctorateReservationQuery query, CancellationToken _) =>
                query.Page == 0 ? OctorateReservationPage.Of(reservation) : OctorateReservationPage.Empty);
        Octorate
            .Setup(c => c.GetReservationAsync(listing.Accommodation, reservation.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(reservation);

        await using (var scope = Scope())
        {
            var result = await scope.Import.ImportAllAsync(listing.PropertyId, CancellationToken.None);
            if (result.Outcome != ImportOutcome.Completed || result.Failed > 0)
                throw new InvalidOperationException($"The import of reservation {reservation.Id} did not complete: {result.Outcome}, {result.Failed} failed.");
        }

        await using var read = Scope();
        return await read.Db.Bookings.AsNoTracking()
            .Where(b => b.OrgId == OrgId && b.OctorateReservationId == reservation.Id)
            .Select(b => b.Id)
            .SingleAsync();
    }

    /// <summary>
    /// A booking of the CasaZen site paid with Stripe: the pending booking and its pending payment as the checkout writes them, the succeeded PaymentIntent settled by
    /// the webhook (<see cref="CheckoutPaymentSettlementService"/>, in the transaction of the event on PostgreSQL), then the fee of the charge read from its balance
    /// transaction (<see cref="PaymentProviderFeeService"/>). The checkout of Switzerland does not compute the tourist tax yet: the booking carries none.
    /// </summary>
    public async Task<(Guid BookingId, Guid PaymentId)> PayOnTheCasaZenSiteAsync(
        DateOnly checkIn,
        DateOnly checkOut,
        decimal price,
        decimal cleaningFee,
        decimal providerFee,
        decimal touristTax = 0m,
        Flat? flat = null)
    {
        var paymentIntent = $"pi_test_{Guid.NewGuid():N}";
        var now = Clock.GetUtcNow().UtcDateTime;
        var guest = NewGuest("Ospite Vetrina", $"test.ospite.vetrina@{GuestMailDomain}");

        // In Italy the checkout calculates the tourist tax and the guest pays it with the stay (BookingService: TouristTax, TouristTaxAmount, the total with it).
        var total = price + touristTax;
        var booking = new Booking
        {
            OrgId = OrgId,
            PropertyId = (flat ?? FlatA).PropertyId,
            GuestId = guest.Id,
            CheckInDate = checkIn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            CheckOutDate = checkOut.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            NumberOfGuests = 2,
            NumberOfAdults = 2,
            Status = BookingStatus.Pending,
            Source = BookingSource.Direct,
            BasePrice = price,
            CleaningFee = cleaningFee,
            TouristTax = touristTax,
            TouristTaxAmount = touristTax,
            TotalPrice = total,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var payment = new Payment
        {
            OrgId = OrgId,
            BookingId = booking.Id,
            Amount = total,
            Status = PaymentStatus.Pending,
            Method = PaymentMethod.CreditCard,
            TransactionId = paymentIntent,
            StripePaymentIntentId = paymentIntent,
            StripeAccountId = StripeAccount,
            Description = "TEST checkout",
            CreatedAt = now,
            UpdatedAt = now,
        };
        await using (var scope = Scope())
        {
            scope.Db.Guests.Add(guest);
            scope.Db.Bookings.Add(booking);
            scope.Db.Payments.Add(payment);
            await scope.Db.SaveChangesAsync();
        }

        await using (var scope = Scope())
        {
            await using var webhookEvent = scope.Db.Database.IsNpgsql() ? await scope.Db.Database.BeginTransactionAsync() : null;
            var settled = await scope.Checkout.SettleSucceededPaymentAsync(paymentIntent, WebhookSource.Connected, StripeAccount);
            if (settled.Outcome != CheckoutPaymentOutcome.Confirmed)
                throw new InvalidOperationException($"The payment of the CasaZen site was not settled: {settled.Outcome}.");
            if (webhookEvent is not null)
                await webhookEvent.CommitAsync();
        }

        var fee = (long)decimal.Round(providerFee * 100m);
        var gross = (long)decimal.Round(total * 100m);
        Stripe
            .Setup(s => s.GetChargeBalanceTransactionAsync(paymentIntent, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StripeBalanceTransactionInfo($"txn_test_{Guid.NewGuid():N}", fee, gross - fee, Italy ? "eur" : "chf", now.AddDays(2)));
        await using (var scope = Scope())
        {
            var captured = await scope.ProviderFees.CaptureAsync(payment.Id);
            if (captured != PaymentProviderFeeOutcome.Captured)
                throw new InvalidOperationException($"The fee of the payment was not captured: {captured}.");
        }

        return (booking.Id, payment.Id);
    }

    /// <summary>
    /// A booking the agency enters by hand (by phone, in person), as <c>BookingService.CreateManualBookingAsync</c> leaves it: confirmed from the start, the price the
    /// agency agreed (cleaning included) and the tourist tax it charges, the event of the creation recorded with it.
    /// </summary>
    public async Task<Guid> EnterByHandAsync(DateOnly checkIn, DateOnly checkOut, decimal price, decimal touristTax, Flat? flat = null)
    {
        var now = Clock.GetUtcNow().UtcDateTime;
        var guest = NewGuest("Ospite Telefono", $"test.ospite.telefono@{GuestMailDomain}");
        var booking = new Booking
        {
            OrgId = OrgId,
            PropertyId = (flat ?? FlatA).PropertyId,
            GuestId = guest.Id,
            CheckInDate = checkIn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            CheckOutDate = checkOut.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            NumberOfGuests = 2,
            NumberOfAdults = 2,
            Status = BookingStatus.Confirmed,
            ConfirmedAt = now,
            Source = BookingSource.Manual,
            BasePrice = price,
            TouristTax = touristTax,
            TouristTaxAmount = touristTax,
            TotalPrice = price + touristTax,
            PriceOverridden = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await using var scope = Scope();
        scope.Db.Guests.Add(guest);
        scope.Db.Bookings.Add(booking);
        BookingEvents.RecordCreated(scope.Events, booking, BookingEventOrigin.Host);
        await scope.Db.SaveChangesAsync();
        return booking.Id;
    }

    /// <summary>Money the agency received outside CasaZen and records on the booking (<see cref="ManualPaymentService"/>): the day it came in, today or before.</summary>
    public async Task<Guid> RecordPaymentByHandAsync(Guid bookingId, decimal amount, PaymentMethod method, DateOnly receivedOn)
    {
        await using var scope = Scope();
        var recorded = await scope.ManualPayments.RecordAsync(
            new ManualPaymentCommand(bookingId, amount, method, receivedOn, "TEST incasso", $"test-{Guid.NewGuid():N}"));
        return recorded.Payment.Id;
    }

    /// <summary>
    /// Who the document of the stay is made out to, as the agency gives it on the booking card (<see cref="InvoiceDataService"/>, source host): a Swiss document needs
    /// the name of the recipient and prints the address when there is one. A booking with no invoice data (a portal sends none) is made out to the name of its guest
    /// (FIX/DOC-RECIPIENT): the Booking.com origin of the scenario gives none, so that the scenario shows it.
    /// </summary>
    public async Task GiveInvoiceDataAsync(Guid bookingId, string name, string countryCode = "CH")
    {
        await using var scope = Scope();
        await scope.InvoiceData.SaveAsync(
            bookingId,
            new SaveInvoiceDataCommand
            {
                RecipientKind = InvoiceRecipientKind.Guest,
                Name = name,
                AddressStreet = "Via del Test",
                AddressCivic = "3",
                PostalCode = "8001",
                City = "Zurigo",
                CountryCode = countryCode,
            },
            InvoiceDataSource.Host,
            AgencyUser);
    }

    /// <summary>The fiscal code of the private guests of the scenario (a valid code of the tests).</summary>
    public const string GuestFiscalCode = "NRIMRA82D50F839C";

    /// <summary>The VAT number of the company guest of the variant of the company tenant (a valid number of the tests).</summary>
    public const string GuestCompanyVat = "12345678903";

    /// <summary>
    /// Who the invoice of a stay is made out to, as the agency gives it on the booking card (<see cref="InvoiceDataService"/>, source host), for an Italian invoice (flat B: the
    /// FatturaPA needs the name, the fiscal code or the VAT number and the address): a private guest by its fiscal code, or, with <paramref name="company"/>, a company by its VAT number.
    /// The receipt of flat A needs the name only (<see cref="GiveInvoiceDataAsync"/> gives it too).
    /// </summary>
    public async Task GiveItalianInvoiceDataAsync(Guid bookingId, string name, bool company = false)
    {
        await using var scope = Scope();
        await scope.InvoiceData.SaveAsync(
            bookingId,
            new SaveInvoiceDataCommand
            {
                RecipientKind = company ? InvoiceRecipientKind.Company : InvoiceRecipientKind.Guest,
                Name = name,
                FiscalCode = company ? null : GuestFiscalCode,
                VatNumber = company ? GuestCompanyVat : null,
                AddressStreet = "Via Mazzini",
                AddressCivic = "7",
                PostalCode = "20121",
                City = "Milano",
                Province = "MI",
                CountryCode = "IT",
            },
            InvoiceDataSource.Host,
            AgencyUser);
    }

    /// <summary>
    /// The cleaning of the stay the agency's supplier did and the agency completed (a request of category cleaning, <c>Completato</c>, with its final amount): what the mandate on
    /// the actual cost (flat A) recharges to the owner, linked to its request (<see cref="OwnerAccountingService"/>, <see cref="CleaningCostSource"/>).
    /// </summary>
    public async Task<Guid> CompleteTheCleaningAsync(Guid bookingId, decimal amount, DateOnly completedOn, Flat? flat = null)
    {
        var completedAt = InRome(completedOn, 12).UtcDateTime;
        var request = new ServiceRequest
        {
            OrgId = OrgId,
            PropertyId = (flat ?? FlatA).PropertyId,
            BookingId = bookingId,
            SupplierOrgId = SupplierOrgId,
            RentalContext = ServiceRequestRentalContext.ShortRent,
            Category = ServiceCategories.Cleaning,
            Status = ServiceRequestStatus.Completato,
            FinalAmountCents = (int)decimal.Round(amount * 100m),
            CompletedAt = completedAt,
            CreatedAt = completedAt.AddDays(-1),
            UpdatedAt = completedAt,
        };
        await using var scope = Scope();
        scope.Db.ServiceRequests.Add(request);
        await scope.Db.SaveChangesAsync();
        return request.Id;
    }

    /// <summary>The booking as it is stored now.</summary>
    public async Task<Booking> BookingAsync(Guid bookingId)
    {
        await using var scope = Scope();
        return await scope.Db.Bookings.AsNoTracking().SingleAsync(b => b.Id == bookingId);
    }

    /// <summary>The address the documents of the stay are e-mailed to: the one of the guest of the booking.</summary>
    public async Task<string> GuestEmailOfAsync(Guid bookingId)
    {
        await using var scope = Scope();
        return await scope.Db.Bookings.AsNoTracking().Where(b => b.Id == bookingId).Select(b => b.Guest.Email).SingleAsync();
    }

    /// <summary>The issuer profile a document is issued on: the one of the owner (its sezionale), or the organization's when <paramref name="ownerId"/> is <c>null</c>.</summary>
    public async Task<Guid> IssuerProfileOfAsync(Guid? ownerId)
    {
        await using var scope = Scope();
        return await scope.Db.OrgFiscalIssuerProfiles.AsNoTracking()
            .Where(p => p.OrgId == OrgId && p.PropertyOwnerId == ownerId)
            .Select(p => p.Id)
            .SingleAsync();
    }

    /// <summary>The payment as it is stored now.</summary>
    public async Task<Payment> PaymentAsync(Guid paymentId)
    {
        await using var scope = Scope();
        return await scope.Db.Payments.AsNoTracking().SingleAsync(p => p.Id == paymentId);
    }

    // ---------------------------------------------------------------- the documents of the guests

    /// <summary>
    /// The daily run of the documents at 05:10 in Rome on <paramref name="day"/> (<c>fiscal-documents-due</c>, with the limit and the look-back of the job), then the
    /// events it recorded delivered to their consumers (the e-mail to the guest).
    /// </summary>
    public async Task<FiscalDocumentsDueResult> RunDocumentsDueAsync(DateOnly day)
    {
        At(InRome(day, 5, 10));
        FiscalDocumentsDueResult result;
        await using (var scope = Scope())
            result = await scope.Documents.ProcessDueDocumentsAsync(OrgId, 500, DocumentRules.DueLookbackDays);

        await DeliverEventsAsync();
        return result;
    }

    /// <summary>The host issues the document of a booking at once (the guest asked for it): the draft and its issue, through the endpoints' path.</summary>
    public async Task<FiscalDocument> IssueNowAsync(Guid bookingId)
    {
        FiscalDocument issued;
        await using (var scope = Scope())
        {
            var draft = await scope.Documents.CreateDraftAsync(bookingId, AgencyUser);
            issued = await scope.Documents.IssueAsync(draft.Id, confirm: true);
        }

        await DeliverEventsAsync();
        return issued;
    }

    /// <summary>The documents of the booking (lines included, the payment state the current one), in the order of their numbers, the drafts last.</summary>
    public async Task<IReadOnlyList<FiscalDocument>> DocumentsOfAsync(Guid bookingId)
    {
        await using var scope = Scope();
        var ids = await scope.Db.FiscalDocuments.AsNoTracking()
            .Where(d => d.BookingId == bookingId && d.Status != FiscalDocumentStatus.Discarded)
            .OrderBy(d => d.SequenceNumber == null)
            .ThenBy(d => d.SequenceNumber)
            .ThenBy(d => d.CreatedAt)
            .Select(d => d.Id)
            .ToListAsync();
        var documents = new List<FiscalDocument>();
        foreach (var id in ids)
            documents.Add((await scope.Documents.GetAsync(id))!);

        return documents;
    }

    /// <summary>What the PDF of a document of CasaZen shows, in Italian (or in <paramref name="language"/>).</summary>
    public async Task<PdfDocumentContent> PdfContentAsync(Guid documentId, string language = DocumentLanguages.Italian)
    {
        await using var scope = Scope();
        return await scope.Documents.PdfContentAsync(documentId, language);
    }

    /// <summary>The PDF of a document of CasaZen, as it is downloaded.</summary>
    public async Task<byte[]> PdfAsync(Guid documentId)
    {
        await using var scope = Scope();
        return await scope.Documents.GetPdfAsync(documentId, DocumentLanguages.Italian);
    }

    // ---------------------------------------------------------------- the changes of a booking (B12)

    /// <summary>
    /// The host changes the booking (<paramref name="change"/>: new dates, the new price): the state the edit of the host leaves, with <c>BookingModified</c> recorded in
    /// the same save by <see cref="BookingEvents"/>, the way every writer of a booking records it.
    /// </summary>
    public async Task ChangeAsHostAsync(Guid bookingId, Action<Booking> change)
    {
        await using var scope = Scope();
        var booking = await scope.Db.Bookings.SingleAsync(b => b.Id == bookingId);
        change(booking);
        booking.UpdatedAt = Clock.GetUtcNow().UtcDateTime;
        await BookingEvents.RecordAsync(scope.Events, scope.Db, DomainEventType.BookingModified, booking, BookingEventOrigin.Host);
        await scope.Db.SaveChangesAsync();
    }

    /// <summary>The host issues every document of a booking at once (the guest asked for them): the drafts of its plan, the owner's document of the stay and the agency's services, issued.</summary>
    public async Task<IReadOnlyList<FiscalDocument>> IssueEveryDocumentNowAsync(Guid bookingId)
    {
        await using (var scope = Scope())
            await scope.Documents.CreateDraftAsync(bookingId, AgencyUser);

        List<Guid> drafts;
        await using (var scope = Scope())
        {
            drafts = await scope.Db.FiscalDocuments.AsNoTracking()
                .Where(d => d.BookingId == bookingId && d.Status == FiscalDocumentStatus.Draft)
                .OrderBy(d => d.ForAgencyServices)
                .Select(d => d.Id)
                .ToListAsync();
        }

        var issued = new List<FiscalDocument>();
        foreach (var draft in drafts)
        {
            await using var scope = Scope();
            issued.Add(await scope.Documents.IssueAsync(draft, confirm: true));
        }

        await DeliverEventsAsync();
        return issued;
    }

    /// <summary>The booking is cancelled (by the host, for the guest): the state of a cancellation with <c>BookingCancelled</c> recorded in the same save.</summary>
    public async Task CancelAsHostAsync(Guid bookingId)
    {
        await using var scope = Scope();
        var booking = await scope.Db.Bookings.SingleAsync(b => b.Id == bookingId);
        booking.Status = BookingStatus.Cancelled;
        booking.CancelledAt = Clock.GetUtcNow().UtcDateTime;
        booking.UpdatedAt = booking.CancelledAt.Value;
        await BookingEvents.RecordAsync(scope.Events, scope.Db, DomainEventType.BookingCancelled, booking, BookingEventOrigin.Host);
        await scope.Db.SaveChangesAsync();
    }

    /// <summary>The host refunds a payment of the CasaZen site through Stripe (<see cref="PaymentRefundService.RefundAsync"/>; Stripe confirms the refund at once).</summary>
    public async Task<PaymentRefund> RefundThroughStripeAsync(Guid paymentId, decimal? amount)
    {
        await using var scope = Scope();
        return await scope.Refunds.RefundAsync(new PaymentRefundRequest(paymentId, amount, "TEST cancellazione", AgencyUser, NotifyGuest: false));
    }

    /// <summary>The host marks the stay as a no-show (<see cref="StayLifecycleService.MarkNoShowAsync"/>).</summary>
    public async Task MarkNoShowAsync(Guid bookingId)
    {
        await using var scope = Scope();
        await scope.Stays.MarkNoShowAsync(bookingId, CancellationToken.None);
    }

    /// <summary>
    /// The outbox delivers the events not delivered yet, the oldest first, to the consumers of the chain: the corrections of the documents
    /// (<see cref="FiscalDocumentBookingChangeHandler"/>) and the e-mail to the guest (<see cref="FiscalDocumentEmailHandler"/>), each delivery in a scope of its own; an
    /// event a consumer asks to see again (a refund still under way) stays for the next round, as the dispatcher retries it. Returns the events delivered.
    /// </summary>
    public async Task<int> DeliverEventsAsync()
    {
        var delivered = 0;
        for (var round = 0; round < 5; round++)
        {
            List<OutboxEvent> pending;
            await using (var read = Scope())
            {
                pending = await read.Db.OutboxEvents.AsNoTracking()
                    .Where(e => e.OrgId == OrgId && e.DispatchedAt == null)
                    .OrderBy(e => e.OccurredAt)
                    .ThenBy(e => e.Id)
                    .ToListAsync();
            }

            var progressed = false;
            foreach (var row in pending)
            {
                var domainEvent = new DomainEvent(row.Id, row.OrgId, row.EventType, row.EntityId, row.OccurredAt, row.PayloadJson);
                try
                {
                    await using (var scope = Scope())
                    {
                        await scope.FiscalDocumentChanges.HandleAsync(domainEvent, CancellationToken.None);
                        await scope.FiscalDocumentEmails.HandleAsync(domainEvent, CancellationToken.None);
                    }
                }
                catch (FiscalDocumentChangePendingException)
                {
                    continue;
                }

                await using (var mark = Scope())
                {
                    var stored = await mark.Db.OutboxEvents.SingleAsync(e => e.Id == row.Id);
                    stored.DispatchedAt = Clock.GetUtcNow().UtcDateTime;
                    stored.Attempts++;
                    await mark.Db.SaveChangesAsync();
                }

                delivered++;
                progressed = true;
            }

            if (!progressed)
                break;
        }

        return delivered;
    }

    // ---------------------------------------------------------------- the month of the owner

    /// <summary>
    /// The end of the month of the agency, at <paramref name="at"/>: the month posted, the version of the owner generated, the invoice of the property manager made from it
    /// and issued (its VAT posted to the ledger, the version generated again with it), the version approved and published (its PDF stored, the owner told).
    /// </summary>
    public async Task<MonthEnd> CloseMonthAsync(int year, int month, DateTimeOffset at, Flat? flat = null)
    {
        var posting = await PostMonthAsync(year, month, at);
        return await CloseOwnerMonthAsync(posting, year, month, flat);
    }

    /// <summary>
    /// The end of the month of an agency with several owners: the month posted once for all of them, then, for each one in the order given, its version generated, the invoice of
    /// the property manager made from it and issued, the version approved and published.
    /// </summary>
    public async Task<IReadOnlyList<MonthEnd>> CloseMonthForAsync(int year, int month, DateTimeOffset at, params Flat[] flats)
    {
        var posting = await PostMonthAsync(year, month, at);
        var ends = new List<MonthEnd>();
        foreach (var flat in flats)
            ends.Add(await CloseOwnerMonthAsync(posting, year, month, flat));

        return ends;
    }

    /// <summary>What follows the posting of the month for one owner: its version, its invoice of the property manager (made and issued), the version approved and published.</summary>
    private async Task<MonthEnd> CloseOwnerMonthAsync(OwnerPostingResult posting, int year, int month, Flat? flat)
    {
        var ownerId = (flat ?? FlatA).OwnerId;
        Clock.Advance(TimeSpan.FromMinutes(1));
        OwnerStatementVersion generated;
        await using (var scope = Scope())
            generated = await scope.Accounting.GenerateVersionAsync(OrgId, ownerId, year, month, AgencyUser);

        Clock.Advance(TimeSpan.FromMinutes(1));
        FiscalDocument draft;
        await using (var scope = Scope())
            draft = await scope.OwnerInvoices.CreateDraftAsync(OrgId, generated.Id, AgencyUser);

        Clock.Advance(TimeSpan.FromMinutes(1));
        FiscalDocument invoice;
        await using (var scope = Scope())
            invoice = await scope.OwnerInvoices.IssueAsync(OrgId, draft.Id, confirm: true, AgencyUser);

        Clock.Advance(TimeSpan.FromMinutes(1));
        OwnerStatementVersion current;
        await using (var scope = Scope())
            current = (await scope.Accounting.ListVersionsAsync(OrgId, ownerId, year, month))[0];
        await using (var scope = Scope())
            await scope.Accounting.ApproveVersionAsync(OrgId, current.Id, AgencyUser);

        Clock.Advance(TimeSpan.FromMinutes(1));
        OwnerStatementVersion published;
        await using (var scope = Scope())
            published = await scope.Accounting.PublishVersionAsync(OrgId, current.Id, AgencyUser);

        FiscalDocument issued;
        await using (var scope = Scope())
            issued = (await scope.Documents.GetAsync(invoice.Id))!;

        return new MonthEnd(posting, generated, draft, issued, published);
    }

    /// <summary>The month posted at <paramref name="at"/> (the sources of the month written to the ledger of the owner), and nothing else.</summary>
    public async Task<OwnerPostingResult> PostMonthAsync(int year, int month, DateTimeOffset at)
    {
        At(at);
        await using var scope = Scope();
        return await scope.Accounting.PostPeriodAsync(OrgId, year, month, AgencyUser);
    }

    /// <summary>The lines of the ledger of the owner in the month that count: neither the reversals nor the lines they reverse.</summary>
    public async Task<IReadOnlyList<OwnerLedgerEntry>> LedgerOfAsync(int year, int month, Flat? flat = null)
    {
        var ownerId = (flat ?? FlatA).OwnerId;
        await using var scope = Scope();
        var lines = await scope.Db.OwnerLedgerEntries.AsNoTracking()
            .Where(e => e.OrgId == OrgId && e.PropertyOwnerId == ownerId && e.PeriodYear == year && e.PeriodMonth == month)
            .ToListAsync();
        var reversed = lines.Where(e => e.ReversesEntryId is not null).Select(e => e.ReversesEntryId!.Value).ToHashSet();
        return lines.Where(e => e.ReversesEntryId is null && !reversed.Contains(e.Id)).ToList();
    }

    /// <summary>What the statement of the version says (the lines of its parts, the box of the tourist tax), as its PDF and the owner portal show it.</summary>
    public async Task<OwnerStatementContent> StatementOfAsync(Guid versionId)
    {
        await using var scope = Scope();
        return await scope.StatementFiles.GetContentAsync(OrgId, versionId);
    }

    /// <summary>An approved charge of the owner (a repair the agency paid and recharges): posted with its month.</summary>
    public async Task<OwnerCharge> ChargeTheOwnerAsync(decimal amount, DateOnly day, string description, Flat? flat = null)
    {
        var charged = flat ?? FlatA;
        OwnerCharge charge;
        await using (var scope = Scope())
        {
            charge = await scope.Accounting.CreateChargeAsync(
                OrgId,
                new OwnerChargeCommand
                {
                    PropertyId = charged.PropertyId,
                    OwnerId = charged.OwnerId,
                    Kind = OwnerChargeKind.Charge,
                    Recurrence = OwnerChargeRecurrence.OneTime,
                    Description = description,
                    Amount = amount,
                    Currency = Italy ? "EUR" : "CHF",
                    EffectiveFrom = day,
                },
                AgencyUser);
        }

        await using (var scope = Scope())
            return await scope.Accounting.ApproveChargeAsync(OrgId, charge.Id, AgencyUser);
    }

    // ---------------------------------------------------------------- the owner portal, the payout, the wire and the package

    /// <summary>The published statements the owner sees on its portal.</summary>
    public async Task<IReadOnlyList<OwnerPortalStatementInfo>> PortalStatementsAsync(Flat? flat = null)
    {
        await using var scope = Scope();
        return await scope.Portal.GetOwnerStatementsAsync((flat ?? FlatA).OwnerUserId);
    }

    /// <summary>
    /// A published statement as its owner reads it on the portal (<c>GET api/owner-portal/statements/{id}</c>, the lines and their labels), under the tenant filter of
    /// the request of an owner: on, and with no org (the person of the portal has none), so a read that leaves a row to the filter finds nothing there.
    /// </summary>
    public async Task<OwnerPortalStatementDetailInfo> PortalStatementAsTheOwnerAsync(Guid versionId, Flat? flat = null)
    {
        await using var scope = new AcceptanceScope(this, new AppDbContext(_options, new OwnerRequestTenant(), Keys));
        return await scope.Portal.GetOwnerStatementAsync((flat ?? FlatA).OwnerUserId, versionId);
    }

    /// <summary>The PDF of a published statement the owner downloads from its portal.</summary>
    public async Task<byte[]> PortalStatementPdfAsync(Guid versionId, Flat? flat = null)
    {
        await using var scope = Scope();
        var file = await scope.Portal.OpenOwnerStatementPdfAsync((flat ?? FlatA).OwnerUserId, versionId);
        await using var content = file.Content;
        using var copy = new MemoryStream();
        await content.CopyToAsync(copy);
        return copy.ToArray();
    }

    /// <summary>The invoices of the property manager the owner sees on its portal.</summary>
    public async Task<IReadOnlyList<OwnerPortalInvoiceInfo>> PortalInvoicesAsync(Flat? flat = null)
    {
        await using var scope = Scope();
        return await scope.Portal.GetOwnerInvoicesAsync((flat ?? FlatA).OwnerUserId);
    }

    /// <summary>The stored PDF of the published version, as the publication wrote it.</summary>
    public async Task<byte[]> StoredStatementPdfAsync(Guid versionId)
    {
        await using var scope = Scope();
        var stored = await scope.Db.OwnerDocumentVersions.AsNoTracking().SingleAsync(v => v.OwnerDocumentId == versionId);
        await using var content = (await Storage.OpenReadAsync(StorageBucket.Private, stored.StorageKey))!;
        using var copy = new MemoryStream();
        await content.CopyToAsync(copy);
        return copy.ToArray();
    }

    /// <summary>The agency pays the owner what the published version owes it, on <paramref name="paidOn"/> (the payout of the statement, E06/F05/US02/T03).</summary>
    public async Task<OwnerPayout> PayTheOwnerAsync(OwnerStatementVersion version, DateOnly paidOn, Flat? flat = null) =>
        (await PayTheOwnerWithEvidenceAsync(version, paidOn, flat)).Payout;

    /// <summary>
    /// The same payout with the evidence of its withholding (E18/F01/US01/T01): the decision on the facts of the ledger, the reason when nothing is withheld. A second call with the
    /// same key is the same payout (<see cref="OwnerPayoutResult.Replayed"/>).
    /// </summary>
    public async Task<OwnerPayoutResult> PayTheOwnerWithEvidenceAsync(OwnerStatementVersion version, DateOnly paidOn, Flat? flat = null)
    {
        await using var scope = Scope();
        return await scope.Payouts.ConfirmPayoutAsync(
            OrgId,
            new OwnerPayoutCommand
            {
                IdempotencyKey = $"payout-{version.Id:N}",
                OwnerId = (flat ?? FlatA).OwnerId,
                StatementVersionId = version.Id,
                GrossAmount = version.ClosingBalance,
                Currency = version.Currency,
                PaidOn = paidOn,
                Reference = "TEST bonifico rendiconto",
            },
            AgencyUser);
    }

    /// <summary>What a payout of the version paid on <paramref name="paidOn"/> would be, read only (the dialog of the agency shows it before the confirmation).</summary>
    public async Task<OwnerPayoutPreview> PreviewThePayoutAsync(OwnerStatementVersion version, DateOnly paidOn)
    {
        await using var scope = Scope();
        return await scope.Payouts.PreviewPayoutAsync(OrgId, version.Id, paidOn);
    }

    /// <summary>
    /// The wire file of the payouts (<c>pain.001.001.09.ch.03</c> for francs), validated against <paramref name="schemas"/>: the official schemas of the build when it is
    /// <c>null</c> (422 <c>wire_batch_schema_unavailable</c> while they are not in the repository).
    /// </summary>
    public async Task<(WireBatch Batch, string Xml)> WireAsync(IReadOnlyList<Guid> payoutIds, DateOnly executionDate, Func<string, XmlSchemaSet>? schemas)
    {
        await using var scope = Scope();
        var wires = new WireTransferService(scope.Db, NullLogger<WireTransferService>.Instance, schemas, Clock);
        return await wires.CreateBatchAsync(OrgId, payoutIds, executionDate);
    }

    /// <summary>The ZIP of the accountant for <paramref name="period"/>, for the agency (the whole organization, the owner accounting included), file by file.</summary>
    public async Task<Dictionary<string, byte[]>> PackageAsync(AccountantPackagePeriod period)
    {
        using var output = new MemoryStream();
        await using (var scope = Scope())
            await scope.Package.BuildAsync(WholeOrg, AccountantPackageAccess.Full, period, output);

        using var archive = new ZipArchive(new MemoryStream(output.ToArray()), ZipArchiveMode.Read);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            using var content = new MemoryStream();
            await using (var stream = entry.Open())
                await stream.CopyToAsync(content);

            files[entry.FullName] = content.ToArray();
        }

        return files;
    }

    /// <summary>The monthly report of the agency (the summary of the package) for <paramref name="period"/>.</summary>
    public async Task<AgencySummary> AgencySummaryAsync(AccountantPackagePeriod period)
    {
        await using var scope = Scope();
        return await scope.AgencySummary.GetAsync(WholeOrg, AccountantPackageAccess.Full, period);
    }

    // ---------------------------------------------------------------- Italy: the SDI, the self-billed invoices, the withholding, the year end

    /// <summary>Where an invoice is with the SDI: the state the host reads (<c>GET api/fiscal/documents/{id}/sdi</c>).</summary>
    public async Task<FiscalDocumentSdiState> SdiStateOfAsync(Guid documentId)
    {
        await using var scope = Scope();
        return (await scope.Sdi.GetStateAsync(documentId))!;
    }

    /// <summary>The submission of an invoice to the SDI through the channel: with no provider configured it answers «not configured» and changes nothing.</summary>
    public async Task<SdiSubmitResult> SubmitToSdiAsync(Guid documentId)
    {
        await using var scope = Scope();
        return await scope.Sdi.SubmitAsync(documentId);
    }

    /// <summary>The issuer sent the file through its own channel (the portal of the Agenzia delle Entrate) and declares it: the day and the reference of the sending.</summary>
    public async Task<FiscalDocumentSdiState> DeclareSentToSdiAsync(Guid documentId, DateOnly day, string reference)
    {
        await using var scope = Scope();
        return await scope.Sdi.DeclareManuallyAsync(documentId, new SdiDeclaration(SdiDeclarationKind.Sent, day, reference));
    }

    /// <summary>
    /// The self-billed invoices (TD17) of a month for the holders of the portal accounts that need one, made and issued at <paramref name="at"/> (the month is over): the generation
    /// of the drafts, then the issue of each one (the estimate is accepted only when asked).
    /// </summary>
    public async Task<(SelfBillingGeneration Generation, IReadOnlyList<SelfBillingDetail> Issued)> SelfBillMonthAsync(int year, int month, DateTimeOffset at, bool acceptEstimate = false)
    {
        At(at);
        SelfBillingGeneration generation;
        await using (var scope = Scope())
            generation = await scope.SelfBilling.CreateDraftsForPeriodAsync(OrgId, year, month, AgencyUser);

        var issued = new List<SelfBillingDetail>();
        foreach (var draft in generation.Drafts)
        {
            await using var scope = Scope();
            issued.Add(await scope.SelfBilling.IssueAsync(OrgId, draft.DocumentId, confirm: true, AgencyUser, acceptEstimate: acceptEstimate));
        }

        return (generation, issued);
    }

    /// <summary>The self-billed invoices of the agency as the list of the screen reads them.</summary>
    public async Task<IReadOnlyList<SelfBillingRow>> SelfBilledInvoicesAsync(int year, int month)
    {
        await using var scope = Scope();
        return await scope.SelfBilling.ListAsync(OrgId, new SelfBillingCriteria(year, month));
    }

    /// <summary>The F24 data of a month to export (the lines of the section Erario and the breakdown per owner and payout), read only.</summary>
    public async Task<F24MonthData> F24DataAsync(int year, int month)
    {
        await using var scope = Scope();
        return await scope.Withholding.GetF24DataAsync(OrgId, year, month);
    }

    /// <summary>The F24 record of a month generated from the evidence (generating it is never filing it).</summary>
    public async Task<F24Record> GenerateF24Async(int year, int month)
    {
        await using var scope = Scope();
        return await scope.Withholding.GenerateF24Async(OrgId, month, year);
    }

    /// <summary>The annual Certificazione Unica data of the year generated from the evidence of the payouts (while it is only generated), and read as the export reads it.</summary>
    public async Task<StoredCuDataset> CuOfTheYearAsync(int year)
    {
        await using (var scope = Scope())
            await scope.Payouts.GenerateAnnualDatasetAsync(OrgId, year, AgencyUser);

        await using var read = Scope();
        return await read.Withholding.GetCuDatasetAsync(OrgId, year);
    }

    /// <summary>The data of the 770 of the year: the withholdings made, month by month and tax code, with the F24 they were paid with.</summary>
    public async Task<Withholding770Data> Tax770Async(int year)
    {
        await using var scope = Scope();
        return await scope.Withholding.Get770DataAsync(OrgId, year);
    }

    /// <summary>The contracts of the year to communicate by 30 June: the qualifying short lets the agency handled and did not withhold on.</summary>
    public async Task<ContractsCommunication> ContractsOfTheYearAsync(int year)
    {
        await using var scope = Scope();
        return await scope.Withholding.GetContractsCommunicationAsync(OrgId, year);
    }

    /// <summary>The annual declaration data of the tourist tax per comune (the stays, the tax owed, the tax remitted, the exemptions by reason).</summary>
    public async Task<TouristTaxDeclaration> TouristTaxDeclarationAsync(int year, bool withExemptionReasons = true)
    {
        await using var scope = Scope();
        return await scope.TouristTax.GetDeclarationAsync(WholeOrg, year, withExemptionReasons);
    }

    /// <summary>The account of the agent for the year (the tax collected and remitted by month or quarter, the line of the year, every payment).</summary>
    public async Task<TouristTaxAgentAccount> TouristTaxAccountAsync(int year, TouristTaxAccountPeriod by)
    {
        await using var scope = Scope();
        return await scope.TouristTax.GetAgentAccountAsync(WholeOrg, year, by);
    }

    /// <summary>The agency pays the tourist tax of a period to the comune and records it (the day it paid and its reference).</summary>
    public async Task<TouristTaxRemittanceLine> RemitTouristTaxAsync(DateOnly from, DateOnly to, decimal amount, DateOnly paidOn, string reference)
    {
        await using var scope = Scope();
        return await scope.TouristTax.RecordRemittanceAsync(
            WholeOrg,
            new RecordRemittanceRequest(ComuneName, from, to, amount, paidOn, reference, null, AgencyUser),
            receipt: null);
    }

    /// <summary>The documents issued in the name of the owner of <paramref name="flat"/>, as its portal lists them (the account of the owner, accounting.read).</summary>
    public async Task<IReadOnlyList<OwnerPortalIssuedDocumentInfo>> PortalIssuedDocumentsAsync(Flat flat, AccountantPackagePeriod? period = null)
    {
        await using var scope = new AcceptanceScope(this, new AppDbContext(_options, new OwnerRequestTenant(), Keys));
        return await scope.Portal.GetOwnerIssuedDocumentsAsync(flat.OwnerUserId, period);
    }

    /// <summary>The ZIP of the documents issued in the name of the owner of <paramref name="flat"/> for its accountant, file by file (the owner reads it from its portal).</summary>
    public async Task<Dictionary<string, byte[]>> PortalIssuedPackageAsync(Flat flat, AccountantPackagePeriod period)
    {
        using var output = new MemoryStream();
        await using (var scope = new AcceptanceScope(this, new AppDbContext(_options, new OwnerRequestTenant(), Keys)))
            await scope.Portal.ExportOwnerIssuedDocumentsAsync(flat.OwnerUserId, period, output);

        return ReadZip(output.ToArray());
    }

    /// <summary>The withholding of the owner of <paramref name="flat"/> in the year as its portal shows it (the evidence, the CU once reviewed).</summary>
    public async Task<OwnerPortalWithholdingInfo> PortalWithholdingAsync(Flat flat, int year)
    {
        await using var scope = new AcceptanceScope(this, new AppDbContext(_options, new OwnerRequestTenant(), Keys));
        return await scope.Portal.GetOwnerWithholdingAsync(flat.OwnerUserId, year);
    }

    /// <summary>The check of the limit of units of the owner of <paramref name="flat"/> on a day (the apartments the agency manages for it and the ones it declared outside).</summary>
    public async Task<OwnerUnitsCheck> UnitsOfTheOwnerAsync(Flat flat, DateOnly day)
    {
        await using var scope = Scope();
        return (await scope.TaxChecks.CheckUnitsAsync(OrgId, [flat.OwnerId], day))[flat.OwnerId];
    }

    /// <summary>The owner declares how many apartments it lets short outside the agency: three in all make it presumed a business from the third.</summary>
    public async Task<OwnerUnitsCheck> DeclareExternalUnitsAsync(Flat flat, int units)
    {
        await using var scope = Scope();
        return await scope.TaxChecks.DeclareExternalUnitsAsync(WholeOrg, flat.OwnerId, units, AgencyUser);
    }

    /// <summary>The «Da fare» of the stay of a company tenant (raised once), and whether it was raised now.</summary>
    public async Task<bool> RaiseCompanyTenantDecisionAsync(Guid bookingId)
    {
        await using var scope = Scope();
        return await scope.TaxChecks.RaiseCompanyTenantDecisionAsync(OrgId, bookingId);
    }

    /// <summary>The stays of a company tenant in the property of a private owner and their decision, as the screen lists them.</summary>
    public async Task<IReadOnlyList<CompanyTenantCase>> CompanyTenantCasesAsync(CompanyTenantCaseState? state = null)
    {
        await using var scope = Scope();
        return await scope.TaxChecks.ListCompanyTenantCasesAsync(WholeOrg, state);
    }

    /// <summary>The agency records the regime of the stay of a company tenant, with its reason: a person decides, the regime is never changed by itself.</summary>
    public async Task<ShortLetTaxDecision> DecideTheCompanyTenantAsync(Guid bookingId, ShortLetTaxRegime regime, string reason)
    {
        await using var scope = Scope();
        return await scope.TaxChecks.DecideAsync(WholeOrg, new DecideShortLetTaxCommand { BookingId = bookingId, Regime = regime, Reason = reason }, AgencyUser);
    }

    /// <summary>The credit note of an issued document (the whole of it, or an amount), issued at once, as the agency makes it from the list.</summary>
    public async Task<FiscalDocument> CreditTheDocumentAsync(Guid documentId, decimal? amount, string reason)
    {
        await using var scope = Scope();
        return await scope.Documents.CreateCreditNoteAsync(documentId, amount, reason, confirm: true, AgencyUser);
    }

    private static Dictionary<string, byte[]> ReadZip(byte[] bytes)
    {
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            using var content = new MemoryStream();
            using (var stream = entry.Open())
                stream.CopyTo(content);

            files[entry.FullName] = content.ToArray();
        }

        return files;
    }

    public async ValueTask DisposeAsync()
    {
        Memory.Dispose();
        await _noServices.DisposeAsync();
    }

    internal IServiceScopeFactory NoServices => _noServices.GetRequiredService<IServiceScopeFactory>();

    internal static IConfiguration Configuration => Shipped.Value;

    internal static T Bind<T>(string section)
        where T : new()
    {
        var options = new T();
        Shipped.Value.GetSection(section).Bind(options);
        return options;
    }

    private Guest NewGuest(string lastName, string email) => new()
    {
        OrgId = OrgId,
        FirstName = "TEST",
        LastName = lastName,
        Email = email,
        CreatedAt = Clock.GetUtcNow().UtcDateTime,
        UpdatedAt = Clock.GetUtcNow().UtcDateTime,
    };

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The configuration <c>Casazen.Web/appsettings.json</c> ships, as the application reads it.</summary>
    private static IConfiguration LoadShippedConfiguration()
    {
        var directory = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        var root = directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
        return new ConfigurationBuilder().AddJsonFile(Path.Combine(root, "Casazen.Web", "appsettings.json"), optional: false).Build();
    }
}

/// <summary>A flat of the scenario: its property, its owner, the account of its owner on the portal and its listing on the channel manager.</summary>
internal sealed record Flat(string Name, Guid PropertyId, Guid OwnerId, Guid OwnerUserId, string Accommodation, string Room);

/// <summary>The variants of the Italian agency of the scenario (<see cref="AcceptanceScenarioBuilder.CreateItalianAsync"/>): V1 and no co-owners unless said.</summary>
internal sealed record ItalianSetup
{
    /// <summary>The agency holds the Booking.com account of both flats (V2); by default the owners do (V1).</summary>
    public bool AgencyHoldsTheBookingAccount { get; init; }

    /// <summary>Flat A has two co-owners, 60% and 40%, and the mandate says that the CU is made per share.</summary>
    public bool CoOwnersOfFlatA { get; init; }

    /// <summary>The VAT of the stay of the company of flat B: 10% by default, or exempt (art. 10 n. 8, nature N4).</summary>
    public StayVatTreatment StayVatOfFlatB { get; init; } = StayVatTreatment.Standard;

    /// <summary>Airbnb collects the tourist tax of Como itself (no line on the document); by default the host does.</summary>
    public bool AirbnbCollectsTheTouristTax { get; init; }
}

/// <summary>What the request of a person of the owner portal has: an authenticated caller, so the tenant filter is on, and no org of its own (fail-closed).</summary>
internal sealed class OwnerRequestTenant : ITenantContext
{
    public Guid? OrgId => null;

    public bool FilterEnabled => true;
}

/// <summary>What the end of a month left: the posting, the first version, the draft and the issued invoice of the property manager, and the published version.</summary>
internal sealed record MonthEnd(
    OwnerPostingResult Posting,
    OwnerStatementVersion FirstVersion,
    FiscalDocument InvoiceDraft,
    FiscalDocument Invoice,
    OwnerStatementVersion Published);

/// <summary>
/// One unit of work of the scenario (a request, a job, a delivery of an event): a context of its own over the database of the scenario and the services of the chain
/// over it, built as the container builds them (the real implementations; only the external services and the schedulers are the stand-ins of the builder).
/// </summary>
internal sealed class AcceptanceScope : IAsyncDisposable
{
    public AcceptanceScope(AcceptanceScenarioBuilder world, AppDbContext db)
    {
        Db = db;
        var clock = world.Clock;
        var flags = world.Flags;
        var texts = new ResxDocumentTexts();
        var pdf = new MigraDocPdfDocumentRenderer();
        var rules = TaxRuleCatalogs.Resolver();
        var configuration = AcceptanceScenarioBuilder.Configuration;
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Test");
        var links = EmailTestHelpers.Links(AcceptanceScenarioBuilder.PublicSiteBaseUrl);
        var accountingOptions = Microsoft.Extensions.Options.Options.Create(AcceptanceScenarioBuilder.Bind<OwnerAccountingOptions>(OwnerAccountingOptions.SectionName));
        var holders = new PortalAccountHolderResolver(db);

        Events = new DomainEventRecorder(db, NullTenantContext.Instance, clock);
        Issuers = new DocumentIssuerService(db, flags, rules, environment.Object, clock, NullLogger<DocumentIssuerService>.Instance);
        InvoiceData = new InvoiceDataService(db, Events, clock, NullLogger<InvoiceDataService>.Instance);
        var activities = new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, clock);
        var shortStay = Microsoft.Extensions.Options.Options.Create(new ShortStayFiscalOptions());
        TaxChecks = new OwnerTaxChecks(db, activities, flags, shortStay, clock, NullLogger<OwnerTaxChecks>.Instance);
        OwnerFiscal = new OwnerFiscalProfileService(db, clock, NullLogger<OwnerFiscalProfileService>.Instance, accountingOptions);
        StatementFiles = new OwnerStatementDocuments(
            db,
            pdf,
            texts,
            world.Storage,
            new OwnerFileContentScanner(),
            flags,
            world.Emails,
            links,
            activities,
            clock,
            NullLogger<OwnerStatementDocuments>.Instance,
            holders);
        Accounting = new OwnerAccountingService(
            db,
            OwnerFiscal,
            clock,
            NullLogger<OwnerAccountingService>.Instance,
            accountingOptions,
            StatementFiles,
            TaxChecks,
            new CleaningCostSource(db),
            activities);
        Mandates = new OwnerMandateService(db, clock, accountingOptions, NullLogger<OwnerMandateService>.Instance);
        PortalAccounts = new PortalAccountSettingsService(db, clock, NullLogger<PortalAccountSettingsService>.Instance);

        // The Italian electronic invoices are written by the real builder and validated against the official schema; a Swiss document has no XML, so it is never asked for one.
        var xmlBuilder = new FatturaPaXmlBuilder(new FatturaPaSchemaProvider(), NullLogger<FatturaPaXmlBuilder>.Instance);
        Documents = new FiscalDocumentService(
            db,
            Issuers,
            InvoiceData,
            new FiscalNumberingService(db, clock, NullLogger<FiscalNumberingService>.Instance),
            xmlBuilder,
            Events,
            pdf,
            texts,
            rules,
            clock,
            NullLogger<FiscalDocumentService>.Instance,
            new GuestDocumentLineBuilder(
                db,
                texts,
                Microsoft.Extensions.Options.Options.Create(AcceptanceScenarioBuilder.DocumentRules),
                Microsoft.Extensions.Options.Options.Create(AcceptanceScenarioBuilder.Bind<SwissTouristTaxOptions>(SwissTouristTaxOptions.SectionName))),
            activities,
            Mock.Of<IPushNotificationService>(),
            flags,
            billingSetup: null,
            ownerAccounting: Accounting);
        OwnerInvoices = new OwnerInvoiceService(db, Documents, Issuers, rules, holders, texts, clock, NullLogger<OwnerInvoiceService>.Instance);
        OwnerIssuers = new OwnerIssuerProfileService(db, flags, rules, clock, NullLogger<OwnerIssuerProfileService>.Instance);

        // No provider of the SDI is configured: a submission answers «not configured» and changes nothing, the invoices stay «to send» and the issuer declares what it sent by hand.
        Sdi = new FiscalDocumentSdiService(db, new UnconfiguredSdiEInvoiceProvider(), Events, clock, NullLogger<FiscalDocumentSdiService>.Instance, flags);
        SelfBilling = new SelfBillingService(
            db,
            Documents,
            Issuers,
            rules,
            holders,
            texts,
            Microsoft.Extensions.Options.Options.Create(AcceptanceScenarioBuilder.Bind<SelfBillingOptions>(SelfBillingOptions.SectionName)),
            clock,
            NullLogger<SelfBillingService>.Instance);
        Corrections = new FiscalDocumentCorrectionService(db, Events, clock, NullLogger<FiscalDocumentCorrectionService>.Instance, activities, Documents);

        Delivery = new FiscalDocumentDeliveryService(
            db,
            world.Emails,
            EmailTestHelpers.ConfiguredEmail(),
            links,
            new FiscalDocumentLinkTokens(world.Keys),
            world.NoServices,
            world.Memory,
            clock,
            NullLogger<FiscalDocumentDeliveryService>.Instance);
        FiscalDocumentEmails = new FiscalDocumentEmailHandler(Delivery, flags, NullLogger<FiscalDocumentEmailHandler>.Instance);
        FiscalDocumentChanges = new FiscalDocumentBookingChangeHandler(Documents, flags, NullLogger<FiscalDocumentBookingChangeHandler>.Instance);

        Import = new BookingImportService(
            db,
            world.Octorate.Object,
            new OctorateOrgScope(NullTenantContext.Instance),
            new OctorateRateLimiter(clock),
            new BookingStateMapper(),
            Events,
            clock,
            NullLogger<BookingImportService>.Instance);

        var fiscal = new FiscalService(db, pdf, Microsoft.Extensions.Options.Options.Create(new ShortStayFiscalOptions()), clock);
        ManualPayments = new ManualPaymentService(db, fiscal, NullLogger<ManualPaymentService>.Instance, clock, Events, world.Stripe.Object);
        var retries = Mock.Of<IPaymentRefundRetryScheduler>();
        Refunds = new PaymentRefundService(db, world.Stripe.Object, retries, world.Emails, NullLogger<PaymentRefundService>.Instance, clock, Events);
        Checkout = new CheckoutPaymentSettlementService(
            db,
            new PaymentRepository(db),
            Refunds,
            retries,
            new BookingNotifier(db, world.Emails, links, Mock.Of<IPushNotificationService>(), NullLogger<BookingNotifier>.Instance),
            configuration,
            NullLogger<CheckoutPaymentSettlementService>.Instance,
            clock,
            Events);
        ProviderFees = new PaymentProviderFeeService(db, world.Stripe.Object, clock, NullLogger<PaymentProviderFeeService>.Instance);
        Stays = new StayLifecycleService(
            db,
            Mock.Of<IAlloggiatiWebService>(),
            Mock.Of<IAlloggiatiReportScheduler>(),
            Mock.Of<IServiceRequestService>(),
            NullLogger<StayLifecycleService>.Instance,
            clock,
            Events);

        var documentArchive = Microsoft.Extensions.Options.Options.Create(new DocumentArchiveOptions());
        OwnerIssuedDocuments = new OwnerIssuedDocumentsExport(db, StatementFiles, pdf, texts, documentArchive, NullLogger<OwnerIssuedDocumentsExport>.Instance);
        Portal = new OwnerPortalService(db, clock, null, flags, new OwnerPortalAccessResolver(db, clock), StatementFiles, OwnerIssuedDocuments);
        Payouts = new OwnerPayoutService(
            db, OwnerFiscal, clock, NullLogger<OwnerPayoutService>.Instance, new OwnerPayoutFacts(db, TaxChecks, holders, shortStay, accountingOptions));
        Withholding = new WithholdingService(db, clock);

        var dashboard = new HostDashboardService(db, configuration, clock);
        var collections = new HostCollectionsReportService(db, configuration, clock);
        var channelCosts = new ChannelCostsReportService(db, new ChannelCommissionSettingsProvider(db, new BookingStateMapper(), environment.Object));
        TouristTax = new TouristTaxSummaryService(db, fiscal, world.Storage, NullLogger<TouristTaxSummaryService>.Instance, clock);
        AgencySummary = new AgencySummaryService(db, dashboard, collections, channelCosts, TouristTax, new OwnerReportService(db, dashboard), holders, clock);
        Package = new AccountantPackageService(
            db,
            Documents,
            StatementFiles,
            new StatementExportService(db, pdf, texts, NullLogger<StatementExportService>.Instance),
            AgencySummary,
            collections,
            channelCosts,
            TouristTax,
            holders,
            pdf,
            texts,
            documentArchive,
            NullLogger<AccountantPackageService>.Instance);
    }

    public AppDbContext Db { get; }

    public DomainEventRecorder Events { get; }

    public DocumentIssuerService Issuers { get; }

    public InvoiceDataService InvoiceData { get; }

    public FiscalDocumentService Documents { get; }

    public FiscalDocumentDeliveryService Delivery { get; }

    public FiscalDocumentEmailHandler FiscalDocumentEmails { get; }

    public FiscalDocumentBookingChangeHandler FiscalDocumentChanges { get; }

    public BookingImportService Import { get; }

    public ManualPaymentService ManualPayments { get; }

    public PaymentRefundService Refunds { get; }

    public CheckoutPaymentSettlementService Checkout { get; }

    public PaymentProviderFeeService ProviderFees { get; }

    public StayLifecycleService Stays { get; }

    public OwnerFiscalProfileService OwnerFiscal { get; }

    public OwnerStatementDocuments StatementFiles { get; }

    public OwnerAccountingService Accounting { get; }

    public OwnerMandateService Mandates { get; }

    public PortalAccountSettingsService PortalAccounts { get; }

    public OwnerInvoiceService OwnerInvoices { get; }

    public OwnerPortalService Portal { get; }

    public OwnerPayoutService Payouts { get; }

    public WithholdingService Withholding { get; }

    public OwnerTaxChecks TaxChecks { get; }

    public OwnerIssuerProfileService OwnerIssuers { get; }

    public FiscalDocumentSdiService Sdi { get; }

    public SelfBillingService SelfBilling { get; }

    public FiscalDocumentCorrectionService Corrections { get; }

    public OwnerIssuedDocumentsExport OwnerIssuedDocuments { get; }

    public TouristTaxSummaryService TouristTax { get; }

    public AgencySummaryService AgencySummary { get; }

    public AccountantPackageService Package { get; }

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}
