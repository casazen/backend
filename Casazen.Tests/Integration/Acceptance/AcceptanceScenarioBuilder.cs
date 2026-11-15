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

    /// <summary>The first day of the mandate, of the ownership and of the rules of the agency.</summary>
    public static readonly DateOnly FirstOfTheYear = new(2026, 1, 1);

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

    /// <summary>The rule of the agency for the commission of Booking.com (15%), as the wizard saved it.</summary>
    public Guid BookingComRuleId { get; private set; }

    public HostScope WholeOrg => new(OrgId);

    /// <summary>The rules of the documents shipped (<c>FiscalDocuments</c>): the portals that collect, the look-back of the daily run.</summary>
    public static FiscalDocumentRulesOptions DocumentRules => Bind<FiscalDocumentRulesOptions>(FiscalDocumentRulesOptions.SectionName);

    /// <summary>
    /// The agency of the scenario on a database of its own (in memory unless <paramref name="options"/> says otherwise), set up as the wizard «Fatturazione» leaves it.
    /// With <paramref name="agencyHoldsTheBookingAccount"/> the Booking.com account of the flat is the agency's (V2); otherwise the owner's (V1, the default).
    /// </summary>
    public static async Task<AcceptanceScenarioBuilder> CreateAsync(bool agencyHoldsTheBookingAccount = false, DbContextOptions<AppDbContext>? options = null)
    {
        var world = new AcceptanceScenarioBuilder(
            options ?? new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"Acceptance_{Guid.NewGuid():N}").Options);
        await world.SetUpAsync(agencyHoldsTheBookingAccount);
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

    private async Task SetUpAsync(bool agencyHoldsTheBookingAccount)
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
        }
    }

    // ---------------------------------------------------------------- the four origins

    /// <summary>
    /// A reservation of Booking.com as the channel manager gives it: the price of the host (<c>roomGross</c>, the total of the reservation), the commission of the
    /// portal in the breakdown when <paramref name="commission"/> is given, collected by the portal (<c>COMPANY</c>) unless <paramref name="portalCollects"/> is false,
    /// two guests, no tourist tax in it (Booking.com does not collect the one of Ticino), the guest's relay address of the portal.
    /// </summary>
    public OctorateReservation BookingComReservation(DateOnly checkIn, DateOnly checkOut, decimal price, decimal? commission, bool portalCollects = true)
    {
        var id = $"BDC{Interlocked.Increment(ref _sequence):D4}";
        return new OctorateReservation
        {
            Id = id,
            Status = OctorateBookingStatus.Confirmed,
            Product = OctorateRoom,
            Currency = "CHF",
            ChannelId = BookingComChannel,
            ChannelName = "Booking.com",
            CheckIn = Day(checkIn),
            CheckOut = Day(checkOut),
            UpdatedAt = Clock.GetUtcNow().UtcDateTime,
            FirstName = "TEST",
            LastName = "Ospite Booking",
            GuestEmail = $"test.{id.ToLowerInvariant()}@guest.booking.com",
            TotalGuests = 2,
            RoomGross = price,
            TotalGross = price,
            CompanyCollect = portalCollects ? OctorateReservation.CompanyCollectCompany : OctorateReservation.CompanyCollectHotel,
            PriceBreakdown = commission is { } kept ? [new OctoratePriceComponent(OctorateReservation.CommissionType, kept)] : [],
        };
    }

    /// <summary>
    /// A reservation of the agency's own site: its booking engine is a channel of the channel manager (the «direct» channel, <see cref="BookingStateMapper.BookingEngineChannelId"/>),
    /// so it comes in as a direct booking with an Octorate id; the price of the stay and the tourist tax the site charged, no commission, paid outside CasaZen.
    /// </summary>
    public OctorateReservation AgencySiteReservation(DateOnly checkIn, DateOnly checkOut, decimal price, decimal touristTax)
    {
        var id = $"WEB{Interlocked.Increment(ref _sequence):D4}";
        return new OctorateReservation
        {
            Id = id,
            Status = OctorateBookingStatus.Confirmed,
            Product = OctorateRoom,
            Currency = "CHF",
            ChannelId = BookingStateMapper.BookingEngineChannelId,
            ChannelName = "Booking Engine",
            CheckIn = Day(checkIn),
            CheckOut = Day(checkOut),
            UpdatedAt = Clock.GetUtcNow().UtcDateTime,
            FirstName = "TEST",
            LastName = "Ospite Sito",
            GuestEmail = $"test.{id.ToLowerInvariant()}@example.com",
            TotalGuests = 2,
            RoomGross = price,
            TotalGross = price + touristTax,
            TouristTax = touristTax,
        };
    }

    /// <summary>The import of the channel manager reads <paramref name="reservation"/> (the client serves it): the booking it made or changed.</summary>
    public async Task<Guid> ImportFromChannelManagerAsync(OctorateReservation reservation)
    {
        Octorate
            .Setup(c => c.GetReservationsAsync(OctorateAccommodation, It.IsAny<OctorateReservationQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, OctorateReservationQuery query, CancellationToken _) =>
                query.Page == 0 ? OctorateReservationPage.Of(reservation) : OctorateReservationPage.Empty);
        Octorate
            .Setup(c => c.GetReservationAsync(OctorateAccommodation, reservation.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(reservation);

        await using (var scope = Scope())
        {
            var result = await scope.Import.ImportAllAsync(PropertyId, CancellationToken.None);
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
    public async Task<(Guid BookingId, Guid PaymentId)> PayOnTheCasaZenSiteAsync(DateOnly checkIn, DateOnly checkOut, decimal price, decimal cleaningFee, decimal providerFee)
    {
        var paymentIntent = $"pi_test_{Guid.NewGuid():N}";
        var now = Clock.GetUtcNow().UtcDateTime;
        var guest = NewGuest("Ospite Vetrina", "test.ospite.vetrina@example.com");
        var booking = new Booking
        {
            OrgId = OrgId,
            PropertyId = PropertyId,
            GuestId = guest.Id,
            CheckInDate = checkIn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            CheckOutDate = checkOut.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            NumberOfGuests = 2,
            NumberOfAdults = 2,
            Status = BookingStatus.Pending,
            Source = BookingSource.Direct,
            BasePrice = price,
            CleaningFee = cleaningFee,
            TotalPrice = price,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var payment = new Payment
        {
            OrgId = OrgId,
            BookingId = booking.Id,
            Amount = price,
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
        var gross = (long)decimal.Round(price * 100m);
        Stripe
            .Setup(s => s.GetChargeBalanceTransactionAsync(paymentIntent, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StripeBalanceTransactionInfo($"txn_test_{Guid.NewGuid():N}", fee, gross - fee, "chf", now.AddDays(2)));
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
    public async Task<Guid> EnterByHandAsync(DateOnly checkIn, DateOnly checkOut, decimal price, decimal touristTax)
    {
        var now = Clock.GetUtcNow().UtcDateTime;
        var guest = NewGuest("Ospite Telefono", "test.ospite.telefono@example.com");
        var booking = new Booking
        {
            OrgId = OrgId,
            PropertyId = PropertyId,
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
    /// the name of the recipient and prints the address when there is one.
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
    public async Task<MonthEnd> CloseMonthAsync(int year, int month, DateTimeOffset at)
    {
        At(at);
        OwnerPostingResult posting;
        await using (var scope = Scope())
            posting = await scope.Accounting.PostPeriodAsync(OrgId, year, month, AgencyUser);

        Clock.Advance(TimeSpan.FromMinutes(1));
        OwnerStatementVersion generated;
        await using (var scope = Scope())
            generated = await scope.Accounting.GenerateVersionAsync(OrgId, OwnerId, year, month, AgencyUser);

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
            current = (await scope.Accounting.ListVersionsAsync(OrgId, OwnerId, year, month))[0];
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
    public async Task<IReadOnlyList<OwnerLedgerEntry>> LedgerOfAsync(int year, int month)
    {
        await using var scope = Scope();
        var lines = await scope.Db.OwnerLedgerEntries.AsNoTracking()
            .Where(e => e.OrgId == OrgId && e.PropertyOwnerId == OwnerId && e.PeriodYear == year && e.PeriodMonth == month)
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
    public async Task<OwnerCharge> ChargeTheOwnerAsync(decimal amount, DateOnly day, string description)
    {
        OwnerCharge charge;
        await using (var scope = Scope())
        {
            charge = await scope.Accounting.CreateChargeAsync(
                OrgId,
                new OwnerChargeCommand
                {
                    PropertyId = PropertyId,
                    OwnerId = OwnerId,
                    Kind = OwnerChargeKind.Charge,
                    Recurrence = OwnerChargeRecurrence.OneTime,
                    Description = description,
                    Amount = amount,
                    Currency = "CHF",
                    EffectiveFrom = day,
                },
                AgencyUser);
        }

        await using (var scope = Scope())
            return await scope.Accounting.ApproveChargeAsync(OrgId, charge.Id, AgencyUser);
    }

    // ---------------------------------------------------------------- the owner portal, the payout, the wire and the package

    /// <summary>The published statements the owner sees on its portal.</summary>
    public async Task<IReadOnlyList<OwnerPortalStatementInfo>> PortalStatementsAsync()
    {
        await using var scope = Scope();
        return await scope.Portal.GetOwnerStatementsAsync(OwnerUserId);
    }

    /// <summary>The PDF of a published statement the owner downloads from its portal.</summary>
    public async Task<byte[]> PortalStatementPdfAsync(Guid versionId)
    {
        await using var scope = Scope();
        var file = await scope.Portal.OpenOwnerStatementPdfAsync(OwnerUserId, versionId);
        await using var content = file.Content;
        using var copy = new MemoryStream();
        await content.CopyToAsync(copy);
        return copy.ToArray();
    }

    /// <summary>The invoices of the property manager the owner sees on its portal.</summary>
    public async Task<IReadOnlyList<OwnerPortalInvoiceInfo>> PortalInvoicesAsync()
    {
        await using var scope = Scope();
        return await scope.Portal.GetOwnerInvoicesAsync(OwnerUserId);
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
    public async Task<OwnerPayout> PayTheOwnerAsync(OwnerStatementVersion version, DateOnly paidOn)
    {
        await using var scope = Scope();
        var result = await scope.Payouts.ConfirmPayoutAsync(
            OrgId,
            new OwnerPayoutCommand
            {
                IdempotencyKey = $"payout-{version.Id:N}",
                OwnerId = OwnerId,
                StatementVersionId = version.Id,
                GrossAmount = version.ClosingBalance,
                Currency = version.Currency,
                PaidOn = paidOn,
                Reference = "TEST bonifico rendiconto",
                AgencyCollected = true,
                QualifyingShortLet = true,
            },
            AgencyUser);
        return result.Payout;
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
        OwnerFiscal = new OwnerFiscalProfileService(db, clock, NullLogger<OwnerFiscalProfileService>.Instance);
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
        Accounting = new OwnerAccountingService(db, OwnerFiscal, clock, NullLogger<OwnerAccountingService>.Instance, accountingOptions, StatementFiles);
        Mandates = new OwnerMandateService(db, clock, accountingOptions, NullLogger<OwnerMandateService>.Instance);
        PortalAccounts = new PortalAccountSettingsService(db, clock, NullLogger<PortalAccountSettingsService>.Instance);

        // A Swiss document has no FatturaPA XML: the builder of the Italian XML is never asked for one.
        var italianXml = new Mock<IFatturaPaXmlBuilder>(MockBehavior.Strict);
        Documents = new FiscalDocumentService(
            db,
            Issuers,
            InvoiceData,
            new FiscalNumberingService(db, clock, NullLogger<FiscalNumberingService>.Instance),
            italianXml.Object,
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

        Portal = new OwnerPortalService(db, clock, null, flags, new OwnerPortalAccessResolver(db, clock), StatementFiles);
        Payouts = new OwnerPayoutService(db, OwnerFiscal, clock, NullLogger<OwnerPayoutService>.Instance);

        var dashboard = new HostDashboardService(db, configuration, clock);
        var collections = new HostCollectionsReportService(db, configuration, clock);
        var channelCosts = new ChannelCostsReportService(db, new ChannelCommissionSettingsProvider(db, new BookingStateMapper(), environment.Object));
        var touristTax = new TouristTaxSummaryService(db, fiscal, world.Storage, NullLogger<TouristTaxSummaryService>.Instance, clock);
        AgencySummary = new AgencySummaryService(db, dashboard, collections, channelCosts, touristTax, new OwnerReportService(db, dashboard), holders, clock);
        Package = new AccountantPackageService(
            db,
            Documents,
            StatementFiles,
            new StatementExportService(db, pdf, texts, NullLogger<StatementExportService>.Instance),
            AgencySummary,
            collections,
            channelCosts,
            touristTax,
            holders,
            pdf,
            texts,
            Microsoft.Extensions.Options.Options.Create(new DocumentArchiveOptions()),
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

    public AgencySummaryService AgencySummary { get; }

    public AccountantPackageService Package { get; }

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}
